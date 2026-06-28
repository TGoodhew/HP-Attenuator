using System;
using HpAttenuator.Instruments;
using HpAttenuator.Model;

namespace HpAttenuator.Measurement
{
    /// <summary>Outcome of identifying which physical attenuator is on which 11713A port.</summary>
    public sealed class IdentificationResult
    {
        public AttenuatorConfig Config { get; }
        public double FullXAttenuationDb { get; }
        public double FullYAttenuationDb { get; }
        public bool Confident { get; }
        public string Summary { get; }

        public IdentificationResult(AttenuatorConfig config, double fullX, double fullY,
                                    bool confident, string summary)
        {
            Config = config;
            FullXAttenuationDb = fullX;
            FullYAttenuationDb = fullY;
            Confident = confident;
            Summary = summary;
        }
    }

    /// <summary>
    /// Determines which step attenuator (8494 = 1 dB, 8496 = 10 dB) is cabled to
    /// ATTEN X vs ATTEN Y by measuring the full-scale attenuation of each bank.
    /// Full 8494 ≈ 11 dB; full 8496 ≈ 110 dB — easily distinguished.
    /// </summary>
    public static class AttenuatorIdentifier
    {
        private static readonly int[] BankXDigits = { 1, 2, 3, 4 };
        private static readonly int[] BankYDigits = { 5, 6, 7, 8 };

        /// <summary>
        /// Measures both banks at <paramref name="idFreqMHz"/> (kept in the direct
        /// regime, default 100 MHz) and returns the resolved configuration.
        /// </summary>
        public static IdentificationResult Identify(
            ISignalSource source, IStepAttenuator attenuator, IMeasuringReceiver receiver,
            double sourcePowerDbm, double idFreqMHz = 100.0, int settleMs = 0)
        {
            source.SetFrequencyMHz(idFreqMHz);
            source.SetPowerDbm(sourcePowerDbm);
            source.RfOn();
            receiver.PrepareTunedRfLevel();
            receiver.ConfigureDirect(idFreqMHz);

            attenuator.SetEngaged(Array.Empty<int>());
            Settle(settleMs);
            double p0 = receiver.ReadLevelDbm();

            attenuator.SetEngaged(BankXDigits);
            Settle(settleMs);
            double px = receiver.ReadLevelDbm();

            attenuator.SetEngaged(BankYDigits);
            Settle(settleMs);
            double py = receiver.ReadLevelDbm();

            attenuator.SetEngaged(Array.Empty<int>()); // leave at 0 dB

            double fullX = p0 - px;
            double fullY = p0 - py;

            // Compare against the two candidate wirings.
            double errDefault = Sq(fullX - 11) + Sq(fullY - 110); // X = 8494, Y = 8496
            double errSwapped = Sq(fullX - 110) + Sq(fullY - 11); // X = 8496, Y = 8494

            bool xIs8494 = errDefault <= errSwapped;
            var config = AttenuatorConfig.Build(xIs8494);

            // Confident when the chosen hypothesis fits far better and the magnitudes
            // are in the right ballpark (the two banks differ by ~10x).
            double ratio = Math.Min(errDefault, errSwapped) / Math.Max(Math.Max(errDefault, errSwapped), 1e-9);
            bool confident = ratio < 0.1 && Math.Abs(fullX - fullY) > 20;

            string summary =
                $"ATTEN X full = {fullX:0.0} dB, ATTEN Y full = {fullY:0.0} dB  =>  " +
                $"X is {config.XModel}, Y is {config.YModel}";

            return new IdentificationResult(config, fullX, fullY, confident, summary);
        }

        private static double Sq(double v) => v * v;

        private static void Settle(int ms)
        {
            if (ms > 0) System.Threading.Thread.Sleep(ms);
        }
    }
}
