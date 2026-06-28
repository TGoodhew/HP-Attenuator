using System;

namespace HpAttenuator.Instruments
{
    /// <summary>How a given RF frequency is measured by the 8902A.</summary>
    public enum MeasurementRegime
    {
        /// <summary>Measured directly on the 8902A (no converter, no LO).</summary>
        Direct,

        /// <summary>Measured through the 11793A converter with an external LO.</summary>
        Converted
    }

    /// <summary>The LO/IF plan for measuring one RF frequency through the 11793A.</summary>
    public sealed class LoPlan
    {
        public double RfMHz { get; }
        public MeasurementRegime Regime { get; }
        public double LoMHz { get; }
        public double IfMHz { get; }

        /// <summary>True if the LO is set above the signal (f_RF = f_LO - f_IF, the normal case).</summary>
        public bool LoAboveSignal { get; }

        /// <summary>Set when the plan required an unusual choice that should be verified on hardware.</summary>
        public string Warning { get; }

        public LoPlan(double rfMHz, MeasurementRegime regime, double loMHz, double ifMHz,
                      bool loAboveSignal, string warning)
        {
            RfMHz = rfMHz;
            Regime = regime;
            LoMHz = loMHz;
            IfMHz = ifMHz;
            LoAboveSignal = loAboveSignal;
            Warning = warning;
        }
    }

    /// <summary>
    /// Plans the LO frequency / IF for the HP 11793A Microwave Converter driving an
    /// HP 8902A. Fundamental mixing (N = 1): f_RF = f_LO ± f_IF. The preferred IF is
    /// 120.53 MHz with the LO set above the signal (8902A Microwave Product Note).
    /// IF must stay within the converter's 10-700 MHz window and the LO within the
    /// generator's range.
    /// </summary>
    public static class MicrowaveConverter
    {
        /// <summary>Below this RF frequency the 8902A measures directly (no converter).</summary>
        public const double CrossoverMHz = 1300.0;

        /// <summary>Recommended IF for best accuracy (8902A Microwave Product Note).</summary>
        public const double PreferredIfMHz = 120.53;

        public const double IfMinMHz = 10.0;
        public const double IfMaxMHz = 700.0;

        /// <summary>
        /// Builds the measurement plan for <paramref name="rfMHz"/> given the LO's
        /// frequency limits. Throws if no valid LO/IF combination exists.
        /// </summary>
        public static LoPlan Plan(double rfMHz, double loMinMHz, double loMaxMHz)
        {
            if (rfMHz < CrossoverMHz)
                return new LoPlan(rfMHz, MeasurementRegime.Direct, 0, 0, true, null);

            // LO above signal: LO = rf + IF. Keep loMin <= LO <= loMax and IF in window.
            double aboveLow = Math.Max(IfMinMHz, loMinMHz - rfMHz);
            double aboveHigh = Math.Min(IfMaxMHz, loMaxMHz - rfMHz);
            if (aboveLow <= aboveHigh)
            {
                double ifMHz = Clamp(PreferredIfMHz, aboveLow, aboveHigh);
                return new LoPlan(rfMHz, MeasurementRegime.Converted, rfMHz + ifMHz, ifMHz, true, null);
            }

            // Fallback: LO below signal: LO = rf - IF. (8902A offset-mode sign handling
            // for LO-below should be verified on hardware.)
            double belowLow = Math.Max(IfMinMHz, rfMHz - loMaxMHz);
            double belowHigh = Math.Min(IfMaxMHz, rfMHz - loMinMHz);
            if (belowLow <= belowHigh)
            {
                double ifMHz = Clamp(PreferredIfMHz, belowLow, belowHigh);
                return new LoPlan(rfMHz, MeasurementRegime.Converted, rfMHz - ifMHz, ifMHz, false,
                    "LO set below signal (f_RF = f_LO + f_IF); verify 8902A offset sign on hardware.");
            }

            throw new ArgumentOutOfRangeException(nameof(rfMHz),
                $"No valid LO/IF for {rfMHz:0.###} MHz with LO range {loMinMHz:0}-{loMaxMHz:0} MHz.");
        }

        private static double Clamp(double v, double lo, double hi) => Math.Min(Math.Max(v, lo), hi);
    }
}
