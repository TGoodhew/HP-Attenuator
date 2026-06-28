using System;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using HpAttenuator.Visa;

namespace HpAttenuator.Instruments
{
    /// <summary>
    /// Driver for the HP 8902A Measuring Receiver. HP-IB codes (8902A Operation
    /// manual): Tuned RF Level = S4, tune via &lt;val&gt; MZ, settled trigger = T3,
    /// special functions = &lt;n&gt; SP, Frequency-Offset (converter) mode enter =
    /// 27.3 SP &lt;LO_MHz&gt; MZ, exit = 27.0 SP. Measured data is returned in WATTS
    /// regardless of the front-panel display, so it is converted to dBm here.
    /// </summary>
    public sealed class Hp8902A : IMeasuringReceiver
    {
        private readonly IInstrumentLink _link;

        /// <summary>Extra settle time after a trigger, on top of the 8902A's own T3 settling.</summary>
        public int SettleMilliseconds { get; set; } = 0;

        public Hp8902A(IInstrumentLink link) => _link = link ?? throw new ArgumentNullException(nameof(link));

        public string ResourceName => _link.ResourceName;

        public void PrepareTunedRfLevel()
        {
            _link.Write("IP");      // Instrument Preset to a known state
            _link.Write("S4");      // Tuned RF Level measurement mode
            _link.Write("4.1SP");   // 10 s averaging time for best level accuracy
        }

        public void ConfigureDirect(double rfMHz)
        {
            _link.Write("27.0SP");  // exit Frequency-Offset mode (normal/direct)
            TuneMHz(rfMHz);
        }

        public void ConfigureConverted(double rfMHz, double loMHz)
        {
            // Enter Frequency-Offset mode and tell the 8902A the external LO frequency,
            // then tune to the expected RF; it measures the down-converted IF.
            _link.Write("27.3SP " + Fmt(loMHz) + " MZ");
            TuneMHz(rfMHz);
        }

        public double ReadLevelDbm()
        {
            string raw = _link.Query("T3"); // settled trigger, then read
            if (SettleMilliseconds > 0) Thread.Sleep(SettleMilliseconds);
            double watts = ParseReading(raw);
            return Rf.WattsToDbm(watts);
        }

        private void TuneMHz(double rfMHz) =>
            _link.Write(Fmt(rfMHz) + " MZ");

        private static string Fmt(double mhz) => mhz.ToString("0.######", CultureInfo.InvariantCulture);

        /// <summary>
        /// Parses the 8902A's ASCII reading (fundamental SI units = watts). The
        /// instrument sends a 17-char implicit-point exponential form, but it also
        /// reads back as a normal floating-point/exponential string, so accept both.
        /// </summary>
        internal static double ParseReading(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                throw new FormatException("Empty reading from 8902A.");

            string s = raw.Trim();

            // Plain float / exponential (e.g. "+0096921346E+01" or "1.234E-06").
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                return v;

            // Fall back: strip any non-numeric prefix label and retry.
            Match m = Regex.Match(s, @"[-+]?[0-9]*\.?[0-9]+([eE][-+]?[0-9]+)?");
            if (m.Success && double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                return v;

            throw new FormatException($"Unrecognized 8902A reading: '{raw}'.");
        }
    }
}
