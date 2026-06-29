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
        private readonly IInstrumentLink _link;

        /// <summary>Settle delay after CALIBRATE / SET REF, ms. The settled read uses T3.</summary>
        public int SettleMilliseconds { get; set; } = 0;

        /// <summary>Delay after ZERO before reading back, ms.</summary>
        public int ZeroSettleMs { get; set; } = 5000;

        /// <summary>Delay after CALIBRATE-on before reading the reference, ms.</summary>
        public int CalSettleMs { get; set; } = 3000;

        public Hp8902A(IInstrumentLink link) => _link = link ?? throw new ArgumentNullException(nameof(link));

        public string ResourceName => _link.ResourceName;

        public void Initialize()
        {
            // Device clear drops pending I/O and deasserts a latched SRQ; IP presets to a
            // known state (Free-Run T0, SRQ mask = HP-IB code error only).
            _link.Clear();
            _link.Write("IP");
        }

        public void Reset() => _link.Write("IP");

        public void SelectRfPower() => _link.Write("M4");

        /// <summary>
        /// Loads BOTH cal-factor tables the 8902A needs for RF Power measurements — the
        /// Normal table (direct, incl. the 50 MHz sensor-cal reference) and the
        /// Frequency-Offset table (converter path) — in a single pass.
        /// <para>
        /// <c>37.9SP</c> clears ALL cal-factor storage, so this clears <b>once</b> and
        /// then fills both tables. Loading more than once — or mixing this with a separate
        /// offset load — re-clears a table you just filled and leaves the offset table
        /// empty, which the receiver reports as Error 15 at measurement time.
        /// </para>
        /// Per the 8902A Microwave Product Note: clear (37.9SP), enter REF CF + per-freq
        /// CFs in Normal mode, then <c>27.1SP</c> (enter Frequency-Offset mode) and repeat
        /// for the offset table. Note 27.1SP — NOT 27.3SP&lt;LO&gt;MZ, which enables the
        /// external LO for a measurement and does not select the offset table for editing.
        /// Leaves the receiver in Normal mode, ready for the sensor zero/calibrate.
        /// </summary>
        public void LoadCalFactors(double referenceCf, IReadOnlyList<CalFactor> table)
        {
            _link.Write("M4");        // RF Power
            _link.Write("37.9SP");    // clear ALL cal-factor storage (both tables) — once

            _link.Write("27.0SP");    // Normal mode -> 37.x entries target the Normal table
            WriteCalFactorTable(referenceCf, table);

            _link.Write("27.1SP");    // enter Frequency-Offset mode -> 37.x targets the Offset table
            WriteCalFactorTable(referenceCf, table);

            _link.Write("27.0SP");    // back to Normal mode for the sensor zero/calibrate
        }

        private void WriteCalFactorTable(double referenceCf, IReadOnlyList<CalFactor> table)
        {
            _link.Write("37.3SP" + Fmt(referenceCf) + "CF");        // REF CF (50 MHz)
            foreach (var c in table)
                _link.Write("37.3SP" + Fmt(c.FreqMHz) + "MZ" + Fmt(c.Cf) + "CF");
            _link.Write("37.0SP");                                  // automatic cal-factor selection
        }

        public double ZeroSensor()
        {
            _link.Write("M4");        // RF Power
            _link.Write("C0");        // calibrator off — no reference power while zeroing
            _link.Write("ZR");        // zero the sensor
            if (ZeroSettleMs > 0) Thread.Sleep(ZeroSettleMs);
            return ReadMeasurement(); // watts, ~0
        }

        public double CalibrateSensor()
        {
            _link.Write("M4");        // ensure RF Power mode (continues from the zero step)
            _link.Write("C1");        // calibrator on: 50 MHz / 1 mW reference
            if (CalSettleMs > 0) Thread.Sleep(CalSettleMs);

            // Settled read with the calibrator on (manual: C1 T3 SC). If the reference
            // isn't ~1 mW (0 dBm), the sensor is not on the CALIBRATION RF POWER OUTPUT;
            // do NOT save — saving here would corrupt the sensor cal.
            double pre = ReadMeasurement();
            double preDbm = Rf.WattsToDbm(pre);
            if (preDbm < -10.0)
            {
                _link.Write("C0");
                throw new Hp8902AException(18,
                    $"reference reads {preDbm:0.0} dBm, not ~0 dBm — {Hp8902AException.Describe(18)}");
            }

            _link.Write("SC");        // save cal — scales the reference to read 1.000 mW
            double reference = ReadMeasurement();
            _link.Write("C0");        // calibrator off
            return reference;         // watts, ≈ 1e-3
        }

        public void BeginAttenuationMeasurement(double rfMHz, MeasurementRegime regime, double loMHz)
        {
            _link.Write("S4");      // Tuned RF Level
            if (regime == MeasurementRegime.Converted)
                _link.Write("27.3SP" + Fmt(loMHz) + "MZ");  // frequency-offset: external LO
            else
                _link.Write("27.0SP");                      // direct / normal mode
            _link.Write(Fmt(rfMHz) + "MZ");   // manual tune to the fixed frequency
            _link.Write("4.0SP");             // IF synchronous detector (floor -127 dBm)
            _link.Write("1.0SP");             // auto RF attenuation (keep fixed after cal)
            _link.Write("LG");                // dB display -> bus returns dB
            _link.Write("32.1SP");            // 0.001 dB resolution
        }

        public void BeginRfPowerMeasurement(double rfMHz, MeasurementRegime regime, double loMHz)
        {
            _link.Write("M4");      // RF Power (power sensor)
            if (regime == MeasurementRegime.Converted)
                _link.Write("27.3SP" + Fmt(loMHz) + "MZ");  // frequency-offset: external LO
            else
                _link.Write("27.0SP");                      // direct / normal mode
            _link.Write("37.0SP");  // automatic cal-factor selection (table loaded separately)
        }

        public double ReadRfPowerDbm()
        {
            // RF Power fundamental unit = watts; convert to dBm for reporting.
            return Rf.WattsToDbm(ReadMeasurement());
        }

        public void Calibrate()
        {
            _link.Write("C1");
            Settle();
        }

        public void SetReference()
        {
            _link.Write("RF");   // SET REF (special function 26) at the current level
            Settle();
        }

        public void ClearError() => _link.Write("CL");   // CLEAR key — clears a displayed error

        public double ReadRelativeDb() => ReadMeasurement();   // dB in LOG relative mode

        public double ReadSignalFrequencyMHz()
        {
            _link.Write("M5");                       // RF Frequency measurement
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
