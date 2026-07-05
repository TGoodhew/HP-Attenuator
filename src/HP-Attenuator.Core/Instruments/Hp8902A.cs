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
            Send("S4");      // Tuned RF Level
            if (regime == MeasurementRegime.Converted)
                Send("27.3SP" + Fmt(loMHz) + "MZ");  // frequency-offset: external LO
            else
                Send("27.0SP");                      // direct / normal mode
            Send(Fmt(rfMHz) + "MZ");   // manual tune to the fixed frequency
            Send("4.0SP");             // IF synchronous detector (floor -127 dBm)
            Send("1.0SP");             // auto RF attenuation (keep fixed after cal)
            Send("LG");                // dB display -> bus returns dB
            Send("32.1SP");            // 0.001 dB resolution
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
        }

        public double ReadRfPowerDbm()
        {
            // RF Power fundamental unit = watts; convert to dBm for reporting.
            return Rf.WattsToDbm(ReadMeasurement());
        }

        /// <summary>RECAL/UNCAL condition weight in the 8902A status byte (Special Function 22).</summary>
        private const byte RecalStatusBit = 0x20;   // 32 = "Recal or Uncal"

        public void BeginRangeCalibration()
        {
            // Enable Data Ready (1) + Recal/Uncal (32) in the status byte so a serial poll
            // reflects when a range needs calibrating (SF 22.NN sums the weighted conditions).
            Send("22.33SP");
            // Free-run trigger so the receiver keeps measuring (and updating RECAL) as the
            // attenuator steps down, rather than waiting for a settled trigger.
            Send("T0");
        }

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

        public double ReadRelativeDb()
        {
            // LOG relative mode returns dB. When the current level is UNCAL the 8902A does not
            // return a number — it sends a row of 'C's or an empty/short response — which fails
            // to parse. Distinguish the two cases by the status byte: RECAL/UNCAL set means the
            // receiver needs calibrating at this level (surface as IsUncal so the caller can
            // CALIBRATE); otherwise it's a transient (e.g. read before Data-Ready) — rethrow so
            // the caller simply re-reads.
            try { return ReadMeasurement(); }
            catch (FormatException)
            {
                if ((_link.SerialPoll() & RecalStatusBit) != 0) throw Hp8902AException.Uncal();
                throw;
            }
        }

        public double ReadSignalFrequencyMHz()
        {
            Send("M5");                       // RF Frequency measurement
            double hz = ReadMeasurement();           // fundamental units = Hz
            return hz / 1e6;
        }

        private double ReadMeasurement()
        {
            string raw = _link.Query("T3");          // settled trigger, then read
            Settle();
            return ParseReading(raw);
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
