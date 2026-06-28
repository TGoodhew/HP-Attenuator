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

        public Hp8902A(IInstrumentLink link) => _link = link ?? throw new ArgumentNullException(nameof(link));

        public string ResourceName => _link.ResourceName;

        public void Reset() => _link.Write("IP");

        public void LoadOffsetCalFactors(double referenceCf, IReadOnlyList<CalFactor> table)
        {
            // The 37.x table must be edited in Frequency-Offset mode so it hits the
            // offset RF-Power table (27.3SP0MZ = enter offset mode, 0 MHz offset).
            _link.Write("27.3SP0MZ");
            _link.Write("37.9SP");                                  // clear offset table
            _link.Write("37.3SP" + Fmt(referenceCf) + "CF");        // REF CF (50 MHz)
            foreach (var c in table)
                _link.Write("37.3SP" + Fmt(c.FreqMHz) + "MZ" + Fmt(c.Cf) + "CF");
            _link.Write("37.0SP");                                  // automatic cal-factor selection
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
