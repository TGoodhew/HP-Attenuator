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

        /// <summary>
        /// Nominal external-LO frequency (MHz) used only to <b>activate</b> Frequency-Offset
        /// mode while the offset cal-factor table is written. The table is keyed by each
        /// entry's RF frequency, so this value does not affect what gets stored; the real
        /// measurement LO is set later by <see cref="BeginRfPowerMeasurement"/>. It must be
        /// a valid, non-zero, in-range LO — see <see cref="LoadCalFactors"/>.
        /// </summary>
        private const double CalLoadLoMHz = 2120.53;   // ~2 GHz RF + 120.53 MHz IF

        /// <summary>
        /// Loads BOTH cal-factor tables the 8902A needs for RF Power measurements — the
        /// Normal table (direct, incl. the 50 MHz sensor-cal reference) and the
        /// Frequency-Offset table (converter path) — in a single pass.
        /// <para>
        /// SF 37 (cal-factor entry) targets whichever of the two tables the Frequency-Offset
        /// mode (SF 27) status selects (8902A Operation manual: "the table being used is
        /// determined by the status of the Frequency Offset mode"). <c>37.9SP</c> clears
        /// ALL cal-factor storage, so this clears <b>once</b> then fills both tables.
        /// </para>
        /// <para>
        /// Offset mode must be genuinely ACTIVE for the offset entries to land in the offset
        /// table, and the only way to activate it is <c>27.3SP&lt;LO&gt;MZ</c> with a valid LO
        /// frequency. <c>27.1SP</c> merely <i>re-enters</i> with a previously set LO, and
        /// <c>27.3SP0MZ</c> (LO = 0) never activates it — in either case the offset entries
        /// fall back into the Normal table, leaving the offset table empty and producing
        /// Error 15 at measurement time. Loading more than once re-clears a filled table for
        /// the same reason. Leaves the receiver in Normal mode for the sensor zero/calibrate.
        /// </para>
        /// </summary>
        public void LoadCalFactors(double referenceCf, IReadOnlyList<CalFactor> table)
        {
            Send("M4");        // RF Power
            Send("37.9SP");    // clear ALL cal-factor storage (both tables) — once

            Send("27.0SP");    // Normal mode -> 37.x entries target the Normal table
            WriteCalFactorTable(referenceCf, table);

            // Activate Frequency-Offset mode with a valid LO so 37.x targets the Offset table.
            Send("27.3SP" + Fmt(CalLoadLoMHz) + "MZ");
            WriteCalFactorTable(referenceCf, table);

            Send("27.0SP");    // exit offset mode -> back to Normal for the sensor zero/cal
        }

        private void WriteCalFactorTable(double referenceCf, IReadOnlyList<CalFactor> table)
        {
            // The cal-factor entry terminator is D2 (the "% CAL FACTOR" key, blue-shifted MHz).
            // NOT "CF" — that is the CALIBRATE-Off command, which silently fails to store the
            // factor (the entry never commits, so RF POWER then shows Error 15).
            Send("37.3SP" + Fmt(referenceCf) + "D2");        // REF CF (50 MHz)
            foreach (var c in table)
                Send("37.3SP" + Fmt(c.FreqMHz) + "MZ" + Fmt(c.Cf) + "D2");
            Send("37.0SP");                                  // automatic cal-factor selection
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
