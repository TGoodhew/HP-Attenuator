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
    /// ATTEN X vs ATTEN Y by probing each bank with its first two sections only.
    /// <para>
    /// We deliberately do NOT engage the full bank: a full 8496 is 110 dB, which at a
    /// 0 dBm source drops the level to ≈ −110 dBm — at/below the 8902A Tuned RF Level
    /// acquisition floor, so the read never returns and the VISA call times out.
    /// Probing the first two sections gives 8494 → 1+2 = 3 dB and 8496 → 10+20 = 30 dB:
    /// both levels are comfortably measurable and the 10× separation still identifies
    /// the banks unambiguously.
    /// </para>
    /// </summary>
    public static class AttenuatorIdentifier
    {
        // First two sections of each bank (digits 1-4 = ATTEN X, 5-8 = ATTEN Y).
        private static readonly int[] BankXProbe = { 1, 2 };
        private static readonly int[] BankYProbe = { 5, 6 };

        private const double FineProbeDb = 3.0;    // 8494 first two sections: 1 + 2
        private const double CoarseProbeDb = 30.0; // 8496 first two sections: 10 + 20
        private const double Midpoint = (FineProbeDb + CoarseProbeDb) / 2.0;

        /// <summary>
        /// Measures both banks at <paramref name="idFreqMHz"/> (kept in the direct
        /// regime, default 100 MHz) and returns the resolved configuration. Reads are
        /// timeout-resilient: a failed read yields NaN rather than throwing, so a flaky
        /// or unmeasurable point degrades confidence instead of crashing the run.
        /// </summary>
        public static IdentificationResult Identify(
            ISignalSource source, IStepAttenuator attenuator, IMeasuringReceiver receiver,
            double sourcePowerDbm, double idFreqMHz = 100.0, int settleMs = 0)
        {
            source.SetFrequencyMHz(idFreqMHz);
            source.SetPowerDbm(sourcePowerDbm);
            source.RfOn();
            receiver.Reset();
            receiver.BeginAttenuationMeasurement(idFreqMHz, MeasurementRegime.Direct, 0);

            // 0 dB reference, then read each bank's probe attenuation as relative dB.
            attenuator.SetEngaged(Array.Empty<int>());
            Settle(settleMs);
            receiver.SetReference();

            double probeX = ReadAttenuation(receiver, attenuator, BankXProbe, settleMs);
            double probeY = ReadAttenuation(receiver, attenuator, BankYProbe, settleMs);

            attenuator.SetEngaged(Array.Empty<int>()); // leave at 0 dB

            bool bothRead = !double.IsNaN(probeX) && !double.IsNaN(probeY);

            bool xIs8494;
            bool confident;
            if (bothRead)
            {
                double errDefault = Sq(probeX - FineProbeDb) + Sq(probeY - CoarseProbeDb); // X = 8494
                double errSwapped = Sq(probeX - CoarseProbeDb) + Sq(probeY - FineProbeDb); // X = 8496
                xIs8494 = errDefault <= errSwapped;
                double ratio = Math.Min(errDefault, errSwapped) /
                               Math.Max(Math.Max(errDefault, errSwapped), 1e-9);
                confident = ratio < 0.1 && Math.Abs(probeX - probeY) > 10;
            }
            else if (!double.IsNaN(probeX)) { xIs8494 = probeX < Midpoint; confident = false; }
            else if (!double.IsNaN(probeY)) { xIs8494 = probeY > Midpoint; confident = false; }
            else { xIs8494 = true; confident = false; }

            var config = AttenuatorConfig.Build(xIs8494);

            string Show(double v) => double.IsNaN(v) ? "no read" : $"{v:0.0} dB";
            string summary =
                $"ATTEN X probe = {Show(probeX)}, ATTEN Y probe = {Show(probeY)} " +
                $"(first 2 sections; 8494≈{FineProbeDb:0}, 8496≈{CoarseProbeDb:0})  =>  " +
                $"X is {config.XModel}, Y is {config.YModel}";

            return new IdentificationResult(config, probeX, probeY, confident, summary);
        }

        /// <summary>
        /// Engages the given sections and reads the relative attenuation (dB). Returns
        /// NaN on any instrument error or VISA timeout, clearing the error so the caller
        /// can continue.
        /// </summary>
        private static double ReadAttenuation(
            IMeasuringReceiver receiver, IStepAttenuator attenuator, int[] digits, int settleMs)
        {
            attenuator.SetEngaged(digits);
            Settle(settleMs);
            try
            {
                return -receiver.ReadRelativeDb();
            }
            catch (Exception)
            {
                try { receiver.ClearError(); } catch { /* best effort */ }
                return double.NaN;
            }
        }

        private static double Sq(double v) => v * v;

        private static void Settle(int ms)
        {
            if (ms > 0) System.Threading.Thread.Sleep(ms);
        }
    }
}
