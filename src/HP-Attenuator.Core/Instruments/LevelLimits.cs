namespace HpAttenuator.Instruments
{
    /// <summary>
    /// The absolute-level window (dBm) a given measurement path can actually measure, taken from
    /// the instrument specifications rather than bench guesswork (issue #21). Used to keep the
    /// sweep from commanding attenuation whose resulting level falls outside the path's range —
    /// below the floor the receiver does not merely lose accuracy, it stops tracking the signal
    /// altogether and raises a genuine Error 01 (signal out of IF range).
    /// </summary>
    public sealed class LevelWindow
    {
        /// <summary>Highest level the path can measure, dBm.</summary>
        public double MaxDbm { get; }

        /// <summary>Lowest level the path can measure, dBm — the hard measurement floor.</summary>
        public double MinDbm { get; }

        /// <summary>Human name of the path this window describes (for reporting).</summary>
        public string PathName { get; }

        /// <summary>Where the numbers come from, so a reader can check them against the manual.</summary>
        public string Citation { get; }

        public LevelWindow(string pathName, double maxDbm, double minDbm, string citation)
        {
            PathName = pathName;
            MaxDbm = maxDbm;
            MinDbm = minDbm;
            Citation = citation;
        }

        /// <summary>Total measurable dynamic range of the path, dB.</summary>
        public double RangeDb => MaxDbm - MinDbm;

        /// <summary>
        /// The deepest attenuation (dB) that can be measured relative to a 0 dB reference sitting at
        /// <paramref name="referenceDbm"/>: attenuating further pushes the level below <see cref="MinDbm"/>.
        /// </summary>
        public double UsableDepthDb(double referenceDbm) => referenceDbm - MinDbm;

        /// <summary>True if <paramref name="levelDbm"/> lies within this path's measurable window.</summary>
        public bool Contains(double levelDbm) => levelDbm >= MinDbm && levelDbm <= MaxDbm;
    }

    /// <summary>
    /// Spec-derived measurable level limits per measurement path (issue #21).
    ///
    /// These replace the previously guessed flat −98 dBm floor with the numbers the manuals actually
    /// state, keyed on the path in use. They differ by more than 25 dB between paths, so a single
    /// global threshold cannot be right for both.
    /// </summary>
    public static class LevelLimits
    {
        /// <summary>
        /// Ceiling of the 8902A Tuned RF Level measurement, dBm. Operation &amp; Calibration manual,
        /// Tuned RF Level: the stated measurement range is "0 to −127 dBm".
        /// </summary>
        public const double TunedRfLevelMaxDbm = 0.0;

        /// <summary>
        /// 8902A direct-path floor with the IF AVERAGE detector (SF 4.4), dBm. O&amp;C Table 1-1
        /// Specifications, Tuned RF Level, footnote 12, verbatim: "The Tuned RF Level measurement
        /// sensitivity when using the IF average detector is -100 dBm."
        /// </summary>
        public const double DirectAverageMinDbm = -100.0;

        /// <summary>
        /// 8902A direct-path floor with the IF SYNCHRONOUS detector (SF 4.0), dBm. O&amp;C General
        /// Information: "The Measuring Receiver has a minimum sensitivity of -127 dBm"; the Tuned RF
        /// Level measurement range is stated as "0 to -127 dBm".
        /// </summary>
        public const double DirectSynchronousMinDbm = -127.0;

        /// <summary>
        /// 11793A converted-path floor, dBm. 8902A Microwave Product Note, verbatim: "any power level
        /// may be measured between +0 dBm and -100 dBm without further calibration", and "low level
        /// power measurements down to -100 dBm".
        ///
        /// This applies to the converted path REGARDLESS of detector: the −127 dBm synchronous figure
        /// is a direct-path sensitivity and never applies through the converter. That is confirmed on
        /// this bench — the synchronous detector loses lock (Error 96) on the converted signal below
        /// roughly −100 dBm, recorded as a dead end in SharedMemory.md.
        /// </summary>
        public const double ConvertedMinDbm = -100.0;

        /// <summary>
        /// The measurable level window for the given path. <paramref name="detector"/> only affects
        /// the direct path; the converted path floors at −100 dBm either way (see <see cref="ConvertedMinDbm"/>).
        /// </summary>
        public static LevelWindow For(MeasurementRegime regime, TrflDetector detector)
        {
            if (regime == MeasurementRegime.Converted)
            {
                return new LevelWindow(
                    "11793A converted",
                    TunedRfLevelMaxDbm,
                    ConvertedMinDbm,
                    "8902A Microwave Product Note: +0 dBm to -100 dBm");
            }

            if (detector == TrflDetector.Synchronous)
            {
                return new LevelWindow(
                    "8902A direct (IF synchronous)",
                    TunedRfLevelMaxDbm,
                    DirectSynchronousMinDbm,
                    "8902A O&C Table 1-1: TRFL range 0 to -127 dBm");
            }

            return new LevelWindow(
                "8902A direct (IF average)",
                TunedRfLevelMaxDbm,
                DirectAverageMinDbm,
                "8902A O&C Table 1-1 fn.12: IF average detector sensitivity -100 dBm");
        }
    }
}
