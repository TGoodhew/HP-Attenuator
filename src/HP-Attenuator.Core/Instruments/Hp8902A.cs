using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using HpAttenuator.Visa;

namespace HpAttenuator.Instruments
{
    /// <summary>
    /// Driver for the HP 8902A Measuring Receiver, measuring attenuation as a relative
    /// Tuned RF Level in dB (the manual-recommended method — see the attenuation
    /// procedure note). HP-IB codes (8902A Operation manual): S4 Tuned RF Level,
    /// 4.0SP IF synchronous detector, 1.0SP auto RF atten, LG dB display, 32.1SP fine
    /// resolution, RF = SET REF, C1 = CALIBRATE, M5 = RF frequency, 27.x SP = frequency
    /// offset, 37.x SP = cal factors. Readings are the 17-char implicit-point form; a
    /// value ≥ 9e10 is an error sentinel +900000NNNNE+01.
    /// </summary>
    public sealed class Hp8902A : IMeasuringReceiver
    {
        /// <summary>
        /// When set, every command sent to the 8902A is traced here together with the status
        /// byte read back immediately after, so the command that triggers an instrument error
        /// (status bit 0x04 — e.g. Error 35) can be identified. Gated by the harness --debug
        /// flag; null = no tracing (and no per-command serial poll).
        /// </summary>
        public static Action<string> DebugLog;

        private readonly IInstrumentLink _link;

        /// <summary>Settle delay after CALIBRATE / SET REF, ms. The settled read uses T3.</summary>
        public int SettleMilliseconds { get; set; } = 0;

        /// <summary>Delay after ZERO before reading back, ms.</summary>
        public int ZeroSettleMs { get; set; } = 5000;

        /// <summary>Delay after CALIBRATE-on before reading the reference, ms.</summary>
        public int CalSettleMs { get; set; } = 3000;

        public Hp8902A(IInstrumentLink link) => _link = link ?? throw new ArgumentNullException(nameof(link));

        public string ResourceName => _link.ResourceName;

        /// <summary>
        /// Sends a command and, when <see cref="DebugLog"/> is set, serial-polls the status
        /// byte immediately after and traces both — flagging the instrument-error bit (0x04)
        /// so the offending command (e.g. the one raising Error 35) is obvious.
        /// </summary>
        private void Send(string command)
        {
            _link.Write(command);
            if (DebugLog == null) return;
            int sb = -1;
            try { sb = _link.SerialPoll(); } catch { /* ignore poll failure */ }
            string flags = sb < 0 ? "?" : $"0x{sb:X2}";
            string note = (sb & 0x04) != 0 ? "  <-- INSTRUMENT ERROR (0x04)"
                        : (sb & 0x20) != 0 ? "  (RECAL/UNCAL 0x20)" : "";
            DebugLog($"8902A < {command,-14} SB={flags}{note}");
        }

        public void Initialize()
        {
            // Device clear drops pending I/O and deasserts a latched SRQ; IP presets to a
            // known state (Free-Run T0, SRQ mask = HP-IB code error only).
            _link.Clear();
            Send("IP");
        }

        public void Reset() => Send("IP");

        public void SelectRfPower() => Send("M4");

        /// <summary>Table #1 (primary / Normal) holds at most this many freq/CF pairs, plus the
        /// separate Reference Cal Factor (8902A Operation manual, RF Power specifications).</summary>
        public const int NormalTableMaxPairs = 16;

        /// <summary>Table #2 (frequency offset) holds at most this many freq/CF pairs, plus REF CF.</summary>
        public const int OffsetTableMaxPairs = 22;

        /// <summary>LO (MHz) used only to genuinely ENTER Frequency Offset mode while loading /
        /// reading the second cal-factor table. The Operation manual: "Every time you use the
        /// second table, you must enter the external LO value" — <c>27.1SP</c> only re-enters with
        /// a previously-set LO, so with no prior LO the offset table is never truly active and the
        /// measurement (which enters offset via <c>27.3SP&lt;LO&gt;MZ</c>) sees an empty table →
        /// Error 15. The exact value is arbitrary; any valid LO enables the table.</summary>
        private const double OffsetTableLoadLoMHz = 5120.53;

        /// <summary>
        /// Loads BOTH cal-factor tables the 8902A needs for RF Power measurements — the Normal
        /// table (direct, <c>27.0SP</c>) and the Frequency-Offset table (converter path,
        /// <c>27.1SP</c>). Per table: <c>M4T0</c> (RF Power + free-run trigger), select the table,
        /// clear it (<c>37.9SP</c>), set the <b>Reference Cal Factor</b> — a separate store entered
        /// value-only as <c>37.3SP{cf}CF</c> (no <c>MZ</c>); this is what actually clears Error 15
        /// ("no Cal Factors stored") — then write each freq/CF pair as <c>37.3SP{freqMHz}MZ{cf}CF</c>
        /// (<c>CF</c> = the "% CAL FACTOR" terminator; values fixed-2-decimal). Pairs are capped to
        /// the table's documented capacity (<see cref="NormalTableMaxPairs"/> / <see cref="OffsetTableMaxPairs"/>);
        /// entries beyond the cap won't fit (they're unreachable in the low-frequency direct regime
        /// anyway). The <c>T0</c> free-run state is required for the entries to commit.
        /// </summary>
        public void LoadCalFactors(double referenceCf, IReadOnlyList<CalFactor> table)
        {
            WriteCalFactorTable(useOffsetTable: false, referenceCf, table);   // Normal table
            WriteCalFactorTable(useOffsetTable: true, referenceCf, table);    // Frequency-Offset table
            Send("27.0SP");   // leave in Normal mode (not offset-with-no-LO, which re-flags Error 15)
        }

        private void WriteCalFactorTable(bool useOffsetTable, double referenceCf, IReadOnlyList<CalFactor> table)
        {
            Send("M4T0");                                    // RF Power + free-run trigger
            // Select the table. The offset table is only genuinely active when offset mode is
            // ENTERED with an LO (27.3SP<LO>MZ); 27.1SP alone (no prior LO) does not activate it,
            // so the entries never reach the table the measurement consults → Error 15.
            Send(useOffsetTable
                ? "27.3SP" + Fmt(OffsetTableLoadLoMHz) + "MZ"
                : "27.0SP");                                 // Normal (direct) table
            Send("37.9SP");                                  // clear the selected table

            // The Reference Cal Factor is a SEPARATE store, entered value-only with NO frequency:
            // "37.3 SPCL, REF CF value, BLUE, MHz" (Microwave Product Note p.3). Entering it is what
            // clears the idle "no cal factors" Error 15 — a plain 50 MHz *pair* does NOT set the REF CF.
            Send("37.3SP" + string.Format(CultureInfo.InvariantCulture, "{0:F2}CF", referenceCf));

            // The pairs, capped to the table's capacity (Table #1 = 16, Table #2 = 22). Lead with a
            // 50 MHz anchor PAIR: the Operation manual (11792A) — "enter the reference cal factor as
            // an entry in the table at 50 MHz. If this pair is not entered, the instrument will not
            // measure power at frequencies less than the lowest frequency entered" (2 GHz here). This
            // lets the direct regime (<1.3 GHz) measure by interpolating up to the 2 GHz entry.
            int max = useOffsetTable ? OffsetTableMaxPairs : NormalTableMaxPairs;
            int written = 0;
            WriteEntry(ReferenceCfFreqMHz, referenceCf);   // 50 MHz low-frequency anchor pair
            written++;
            foreach (var c in table)
            {
                if (written >= max) break;
                WriteEntry(c.FreqMHz, c.Cf);
                written++;
            }
        }

        /// <summary>The 50 MHz calibrator frequency — entered as a table pair to anchor the low end.</summary>
        private const double ReferenceCfFreqMHz = 50.0;

        private void WriteEntry(double freqMHz, double calFactorPercent) =>
            Send("37.3SP" + string.Format(CultureInfo.InvariantCulture, "{0:F2}MZ{1:F2}CF", freqMHz, calFactorPercent));

        /// <summary>
        /// Reads back BOTH cal-factor tables — Normal (27.0SP) and Frequency-Offset (27.1SP) — to
        /// verify a load committed: the freq/CF pair count (37.4SP) and the Reference Cal Factor
        /// (37.5SP) of each. Leaves Normal mode. A count is -1 / a REF CF is NaN when unreadable.
        /// </summary>
        public (int normalPairs, int offsetPairs, double normalRefCf, double offsetRefCf) ReadCalFactorTables()
        {
            _link.Write("27.0SP"); int normal = TryReadTableSize(); double normalRef = TryReadRefCf();
            // Enter offset mode with an LO so the ACTIVE second table is read (not a 27.1SP no-LO state).
            _link.Write("27.3SP" + Fmt(OffsetTableLoadLoMHz) + "MZ");
            int offset = TryReadTableSize(); double offsetRef = TryReadRefCf();
            _link.Write("27.0SP");                       // leave in Normal mode
            return (normal, offset, normalRef, offsetRef);
        }

        private int TryReadTableSize()
        {
            try
            {
                string raw = _link.Query("37.4SP").Trim();
                if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                    return (int)Math.Round(v);
            }
            catch { /* unreadable */ }
            return -1;
        }

        /// <summary>Recalls the Reference Cal Factor of the currently-selected table: 37.5SP makes
        /// it the "current" cal factor, then the CF query reads that value back (a bare read after
        /// 37.5SP returns the live measurement, not the recalled factor).</summary>
        private double TryReadRefCf()
        {
            try
            {
                _link.Write("37.5SP");                       // recall reference cal factor to "current"
                string raw = _link.Query("CF").Trim();       // read current calibration factor
                if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                    return v;
            }
            catch { /* unreadable */ }
            return double.NaN;
        }

        public double ZeroSensor()
        {
            Send("M4");        // RF Power
            Send("C0");        // calibrator off — no reference power while zeroing
            Send("ZR");        // zero the sensor
            if (ZeroSettleMs > 0) Thread.Sleep(ZeroSettleMs);
            return ReadMeasurement(); // watts, ~0
        }

        public double CalibrateSensor()
        {
            Send("M4");        // ensure RF Power mode (continues from the zero step)
            Send("C1");        // calibrator on: 50 MHz / 1 mW reference
            if (CalSettleMs > 0) Thread.Sleep(CalSettleMs);

            // Settled read with the calibrator on (manual: C1 T3 SC). If the reference
            // isn't ~1 mW (0 dBm), the sensor is not on the CALIBRATION RF POWER OUTPUT;
            // do NOT save — saving here would corrupt the sensor cal.
            double pre = ReadMeasurement();
            double preDbm = Rf.WattsToDbm(pre);
            if (preDbm < -10.0)
            {
                Send("C0");
                throw new Hp8902AException(18,
                    $"reference reads {preDbm:0.0} dBm, not ~0 dBm — {Hp8902AException.Describe(18)}");
            }

            Send("SC");        // save cal — scales the reference to read 1.000 mW
            double reference = ReadMeasurement();
            Send("C0");        // calibrator off
            return reference;         // watts, ≈ 1e-3
        }

        public void BeginAttenuationMeasurement(double rfMHz, MeasurementRegime regime, double loMHz)
        {
            // Manual "Attenuator Measurements" (relative Tuned RF Level, O&C 3-115): S4, tune, pick
            // the detector, then SET REF (done by the engine). NO Track Mode — its LO feedback loop
            // keeps adjusting during a CALIBRATE, so the level isn't steady and CALIBRATE fails with
            // Error 35 ("maintain signal stability during calibration"). The engine calibrates each
            // range-to-range boundary once, on RECAL, with the attenuator held steady.
            Send("S4");      // Tuned RF Level
            if (regime == MeasurementRegime.Converted)
                Send("27.3SP" + Fmt(loMHz) + "MZ");  // frequency-offset: external LO
            else
                Send("27.0SP");                      // direct / normal mode
            Send(Fmt(rfMHz) + "MZ");   // manual tune to the fixed frequency
            Send("4.4SP");             // IF AVERAGE detector (30 kHz BW, to -100 dBm) — the manual's
                                       // low-level detector; range-to-range CALIBRATE needs the sensor
                                       // module (the 11792A, which is in the chain)
            Send("1.0SP");             // auto RF attenuation (keep fixed after cal)
            Send("LG");                // dB display -> bus returns dB
            Send("32.1SP");            // 0.001 dB resolution
            UnmaskMeasurementStatus(); // so ReadMeasurement's completion poll can see Data Ready
        }

        public void BeginRfPowerMeasurement(double rfMHz, MeasurementRegime regime, double loMHz)
        {
            Send("M4");      // RF Power (power sensor)
            if (regime == MeasurementRegime.Converted)
                Send("27.3SP" + Fmt(loMHz) + "MZ");  // frequency-offset: external LO
            else
                Send("27.0SP");                      // direct / normal mode
            Send(Fmt(rfMHz) + "MZ");   // tune to the RF frequency: the automatic cal factor is
                                       // selected only AFTER the receiver has tuned (Operation
                                       // manual, RF Power) — without it, RF POWER raises Error 15.
            Send("37.0SP");  // automatic cal-factor selection (table loaded separately)
            UnmaskMeasurementStatus(); // so ReadMeasurement's completion poll can see Data Ready
        }

        /// <summary>
        /// Unmasks the Status Byte bits the completion handshake polls for — Data Ready (bit 0),
        /// Instrument Error (bit 2) and RECAL/UNCAL (bit 5), i.e. SF 22.37 (1+4+32; the HP-IB error
        /// bit 1/weight 2 is permanently set). REQUIRED before any measurement: at power-up / IP the
        /// 8902A masks every Status Byte bit except HP-IB error (O&amp;C 3-25), so without this a serial
        /// poll reads 0x00 and <see cref="ReadMeasurement"/> never sees Data Ready — it would burn the
        /// whole budget on every read.
        /// </summary>
        private void UnmaskMeasurementStatus() => Send("22.37SP");

        public double ReadRfPowerDbm()
        {
            // RF Power fundamental unit = watts; convert to dBm for reporting.
            return Rf.WattsToDbm(ReadMeasurement());
        }

        /// <summary>RECAL/UNCAL condition weight in the 8902A status byte (Special Function 22).</summary>
        private const byte RecalStatusBit = 0x20;   // 32 = "Recal or Uncal"

        /// <summary>Data Ready bit in the 8902A status byte — set when a measurement result is ready.</summary>
        private const byte DataReadyBit = 0x01;

        /// <summary>Instrument-error bit in the 8902A status byte (e.g. Error 96 no-signal).</summary>
        private const byte InstrErrorBit = 0x04;

        /// <summary>Serial-poll interval while waiting for Data Ready, ms.</summary>
        private const int DataReadyPollMs = 250;

        /// <summary>How long to watch the status byte for a completed measurement before giving up, ms.
        /// Well above the longest legitimate settled read (~6 s typical, ~12 s near the floor) so a
        /// real measurement always finishes first; a stalled read past this propagates as a timeout so
        /// the caller can release the bus (#11) — kept modest so a genuine hang recovers promptly.</summary>
        private const int DataReadyBudgetMs = 30000;

        public void BeginRangeCalibration()
        {
            UnmaskMeasurementStatus();   // Data Ready + Instr Error + Recal/Uncal in the status byte
            // Free-run trigger so the receiver keeps measuring (and updating RECAL) as the
            // attenuator steps down, rather than waiting for a settled trigger.
            Send("T0");
        }

        /// <summary>Unmask RECAL/UNCAL (and Data Ready + Instr Error) in the status byte WITHOUT the
        /// free-run trigger, so a serial poll reflects RECAL but the receiver keeps its settled (T3)
        /// ranging instead of auto-ranging.</summary>
        public void EnableRecalStatus() => UnmaskMeasurementStatus();

        public bool RecalRequested() => (_link.SerialPoll() & RecalStatusBit) != 0;

        public int PollStatusByte() => _link.SerialPoll();

        public void Calibrate()
        {
            Send("C1");
            Settle();
        }

        public void SetReference()
        {
            Send("RF");   // SET REF (special function 26) at the current level
            Settle();
        }

        public void ClearError() => Send("CL");   // CLEAR key — clears a displayed error

        public void RetuneToSignal()
        {
            // Blue Key + CLEAR (O&C 3-116): forces a VCO retune during Tuned RF Level and recaptures
            // the signal if it hasn't drifted more than 5 MHz. The remedy for a lost-lock Error 96 at
            // a range boundary — CL alone clears the error but does NOT re-acquire the signal.
            Send("BC");
            Settle();
        }

        public void ReleaseBus()
        {
            // GPIB device clear (SDC). Aborts a measurement cycle that is holding the bus handshake
            // (O&C 3-22) and frees the bus so the next write can't collide. Best-effort: _link.Clear()
            // swallows a device that ignores clear. Resets the 8902A to preset — caller must re-setup.
            _link.Clear();
        }

        public double ReadRelativeDb()
        {
            // LOG relative mode returns dB. ReadMeasurement's completion handshake surfaces a UNCAL
            // range (RECAL 0x20, or a 'CCCC'/'AAAA' fill) as an Hp8902AException so the caller can
            // CALIBRATE; a valid level parses to dB.
            return ReadMeasurement();
        }

        public double ReadTunedLevelDbm()
        {
            // Before SET REF the S4/LG Tuned RF Level reading IS the absolute level in dBm (SET REF
            // later re-zeroes it to relative dB). Same settled-read path as ReadRelativeDb; the only
            // difference is the caller reads it BEFORE taking the reference, for #16 leveling.
            return ReadMeasurement();
        }

        public double ReadSignalFrequencyMHz()
        {
            Send("M5");                       // RF Frequency measurement
            double hz = ReadMeasurement();           // fundamental units = Hz
            return hz / 1e6;
        }

        /// <summary>
        /// Triggers a settled measurement and retrieves the result using the completion handshake:
        /// write the trigger, then poll the status byte until the measurement produces something to
        /// read — Data Ready (0x01), an instrument error (0x04), or RECAL/UNCAL (0x20) — and only then
        /// read. This replaces the old single blocking <c>Query("T3")</c>, which at deep levels could
        /// time out WITHOUT delivering the result even though the receiver had set Data Ready, and
        /// which never surfaced the RECAL that drives the range-boundary CALIBRATE. On RECAL with no
        /// result it throws UNCAL; otherwise <see cref="ParseReading"/> turns the response into a value
        /// or the appropriate error. If nothing completes within <see cref="DataReadyBudgetMs"/> the
        /// read is attempted anyway and any GPIB timeout propagates so the caller can release the bus
        /// (#11). Verified on hardware via the former <c>--handshake-probe</c>; ~6 s per settled read.
        /// </summary>
        private double ReadMeasurement()
        {
            _link.Write("T3");                       // trigger; do NOT block-read yet
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int sb = -1;
            bool ready = false;
            while (sw.ElapsedMilliseconds < DataReadyBudgetMs)
            {
                try { sb = _link.SerialPoll(); } catch { sb = -1; }
                if (sb >= 0 && (sb & RecalStatusBit) != 0) break;                     // RECAL/UNCAL
                if (sb >= 0 && (sb & (DataReadyBit | InstrErrorBit)) != 0) { ready = true; break; }
                Thread.Sleep(DataReadyPollMs);
            }
            DebugLog?.Invoke($"8902A DataReady {(ready ? "SET" : "NOT set")} after " +
                             $"{sw.ElapsedMilliseconds / 1000.0:0.0} s (SB=0x{(sb < 0 ? 0 : sb):X2})");

            // RECAL set but no result produced → the range needs calibrating at this level.
            if (sb >= 0 && (sb & RecalStatusBit) != 0 && !ready) throw Hp8902AException.Uncal();

            string raw = _link.Read();               // retrieve the now-ready result
            DebugLog?.Invoke($"8902A read after DataReady: '{raw}'");
            Settle();
            try { return ParseReading(raw); }
            catch (FormatException)
            {
                if ((_link.SerialPoll() & RecalStatusBit) != 0) throw Hp8902AException.Uncal();
                throw;
            }
        }

        private void Settle()
        {
            if (SettleMilliseconds > 0) Thread.Sleep(SettleMilliseconds);
        }

        private static string Fmt(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

        /// <summary>
        /// Parses an 8902A reading. Values ≥ 9e10 are error sentinels (+900000NNNNE+01);
        /// these throw <see cref="Hp8902AException"/>. Otherwise returns the value in the
        /// measurement's fundamental units.
        /// </summary>
        internal static double ParseReading(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                throw new FormatException("Empty reading from 8902A.");

            string s = raw.Trim();

            // UNCAL fill: instead of a number the 8902A returns a run of repeated letters when the
            // current RF range is uncalibrated — 'CCCC…' (RF Power) or 'AAAA…'/'aaaa…' (Tuned RF
            // Level). This does NOT reliably set the RECAL status bit, so recognise it from the
            // response itself and surface it as UNCAL so the caller can CALIBRATE at this level.
            if (Regex.IsMatch(s, "^[A-Za-z]+$"))
                throw Hp8902AException.Uncal();

            double v;
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
            {
                Match m = Regex.Match(s, @"[-+]?[0-9]*\.?[0-9]+([eE][-+]?[0-9]+)?");
                if (!m.Success || !double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                    throw new FormatException($"Unrecognized 8902A reading: '{raw}'.");
            }

            // Error sentinel: +900000NNNNE+01  =>  code = (value - 9e10) / 1000.
            if (v >= 9e10)
            {
                int code = (int)Math.Round((v - 9e10) / 1000.0);
                throw new Hp8902AException(code, Hp8902AException.Describe(code));
            }
            return v;
        }
    }
}
