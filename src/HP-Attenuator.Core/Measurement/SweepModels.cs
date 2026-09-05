using System.Collections.Generic;
using HpAttenuator.Instruments;

namespace HpAttenuator.Measurement
{
    /// <summary>Parameters for an attenuation-vs-frequency sweep.</summary>
    public sealed class SweepOptions
    {
        public double FreqStartMHz { get; set; } = 1.0;
        public double FreqStopMHz { get; set; } = 18000.0;
        public double FreqStepMHz { get; set; } = 10.0;

        // 0 dBm source lands the 0 dB reference ~-1 dBm at the 8902A at 3 GHz — just under its 0 dBm
        // relative-measurement ceiling. +10 dBm (used to prove the floor moves with level) over-drives
        // it to ~+9 dBm at 3 GHz, over-ranging the reference and hanging the first range boundary
        // (~12 dB). NB the ideal level is frequency-dependent (converter loss varies) — a multi-freq
        // sweep needs per-frequency leveling to keep the reference just below 0 dBm.
        public double SourcePowerDbm { get; set; } = 0.0;
        public double LoPowerDbm { get; set; } = 8.0; // 11793A wants +8 dBm LO drive (2-18 GHz)

        // --- Adaptive reference leveling (#16) ---
        // Before SET REF at each frequency, measure the absolute 0 dB reference level and nudge the
        // source so it lands just under the 8902A's 0 dBm relative-measurement ceiling. The ideal
        // source power is frequency-dependent (11793A converter loss varies with frequency), so one
        // fixed SourcePowerDbm can't serve a multi-frequency / --full sweep — too hot over-ranges and
        // hangs the reference, too cold makes a shallow floor. Leveling keeps the reference in range
        // at every frequency and maximises usable depth. Prerequisite for #14 (segmented sweep).

        /// <summary>Measure and level the 0 dB reference per frequency before SET REF (#16).</summary>
        public bool AdaptiveLevel { get; set; } = true;

        /// <summary>
        /// Target for the leveled 0 dB reference at the 8902A, dBm. Levelling the source until the
        /// receiver reads exactly 0 dBm makes the reference an absolute anchor, so the signal
        /// generator's own power error and the cabling loss drop out of the result entirely — the
        /// measured attenuation is then just the negated reading. It also buys the most dynamic range,
        /// since every dB of reference below 0 is a dB of usable depth lost above the floor.
        ///
        /// NOTE: 0 dBm is the 8902A's Tuned RF Level ceiling (<see cref="LevelLimits.TunedRfLevelMaxDbm"/>),
        /// so there is no headroom above it — the leveller therefore converges from BELOW and never
        /// accepts a reading above the target (see the engine's LevelReference).
        /// </summary>
        public double TargetReferenceDbm { get; set; } = 0.0;

        /// <summary>
        /// Source-power increment used on the final approach to the target, dB — one step of the
        /// source's own amplitude grid. The HP 8340B's power resolution is <b>0.05 dB</b> (8340B/41B
        /// User manual: "resolution of 0.05 dB"), so this is its full granularity and the finest the
        /// reference can be placed. Commanding finer than this does nothing: 0.01 dB steps were
        /// observed to quantize into a single 0.049 dB jump (source 1.12 dBm read −0.040, 1.13 dBm
        /// read +0.009), which made the leveller oscillate instead of converging.
        /// </summary>
        public double LevelFineStepDb { get; set; } = 0.05;

        /// <summary>Remaining error (dB) below which the leveller switches from a single corrective jump
        /// to <see cref="LevelFineStepDb"/> creeping. The coarse jump deliberately lands one fine step
        /// SHORT of the target, so the final approach is always upward from below.</summary>
        public double LevelFineWindowDb { get; set; } = 0.15;

        /// <summary>Max source-power adjustment iterations per frequency (best-effort; clamps out).
        /// Generous, since the fine approach can take several 0.01 dB steps.</summary>
        public int MaxLevelIterations { get; set; } = 30;

        /// <summary>Lower clamp on the leveled source power, dBm (8340B usable range / safety).</summary>
        public double SourcePowerMinDbm { get; set; } = -15.0;

        /// <summary>Upper clamp on the leveled source power, dBm (keep the reference ≤ 0 dBm-safe).</summary>
        public double SourcePowerMaxDbm { get; set; } = 15.0;

        public int AttenStartDb { get; set; } = 0;
        public int AttenStopDb { get; set; } = 110;
        public int AttenStepDb { get; set; } = 10;

        /// <summary>Settle time per attenuator step, in ms (on top of the 8902A's T3 settling).</summary>
        public int SettleMs { get; set; } = 100;

        /// <summary>Run the 8902A 3-point range-calibration pass before measuring (hardware).</summary>
        public bool RangeCalibrate { get; set; } = true;

        /// <summary>Attenuation step (dB) used only for the range-calibration pass.</summary>
        public int CalStepDb { get; set; } = 10;

        /// <summary>
        /// Force a per-RF-range CALIBRATE during the pre-SET-REF descent even when the 8902A does
        /// NOT raise RECAL/UNCAL (issue #17). Resident range factors suppress the natural RECAL, so
        /// the default UNCAL-gated descent can fire zero CALIBRATEs and silently ride stale factors;
        /// when set, one unconditional CALIBRATE is issued per range at the approximate boundary
        /// depths. Off by default (preserves the validated Average-detector path); opt-in for the
        /// bench A/B that tells us whether a genuine fresh calibration improves the 80–95 dB accuracy.
        /// </summary>
        public bool ForceRangeCal { get; set; } = false;

        /// <summary>#13: flag deep points that saturated at the converter floor (they stop tracking the
        /// attenuation) as FLOOR instead of counting them as measurement errors. On by default.</summary>
        public bool FloorDetect { get; set; } = true;

        /// <summary>
        /// #21: explicit floor override (dBm) from <c>--floor-dbm</c>. NaN — the default — means "use
        /// the spec-derived floor for the path actually in use" (see <see cref="LevelLimits"/>). The
        /// previous flat −98 dBm default was a bench guess; it is a reasonable approximation for the
        /// 11793A converted path but wrong by 27 dB for the 8902A direct synchronous path.
        /// </summary>
        public double FloorDbmOverride { get; set; } = double.NaN;

        /// <summary>
        /// #21: skip sweep points whose predicted level falls below the measurable floor of the path
        /// in use, instead of measuring them and reporting the under-read as an error. On by default:
        /// below the floor the 8902A cannot track the signal at all — it raises a genuine Error 01
        /// (signal out of IF range) — so a reading there is not a datum, and commanding it drives the
        /// receiver into an error state it can never resolve.
        /// </summary>
        public bool EnforceLevelLimits { get; set; } = true;

        /// <summary>The spec-derived measurable level window for <paramref name="regime"/> with the
        /// detector currently selected (#21).</summary>
        public LevelWindow LevelWindowFor(MeasurementRegime regime) =>
            LevelLimits.For(regime, Detector);

        /// <summary>
        /// The floor (dBm) to apply for <paramref name="regime"/>: the <c>--floor-dbm</c> override when
        /// one was given, otherwise the spec limit for that path (#21).
        /// </summary>
        public double EffectiveFloorDbm(MeasurementRegime regime) =>
            double.IsNaN(FloorDbmOverride) ? LevelWindowFor(regime).MinDbm : FloorDbmOverride;

        /// <summary>#13: dB band used by the floor classifier — the headroom above the effective floor
        /// counted as "at floor", the plateau non-advance threshold, and the under-read threshold a
        /// point must exceed (measured &lt; target − this) before it can be flagged FLOOR.</summary>
        public double FloorMarginDb { get; set; } = 1.0;

        /// <summary>
        /// Which 8902A IF detector the Tuned RF Level sweep uses. Average (default, floor ≈ −100 dBm)
        /// is robust through the converter/LO path; Synchronous (floor ≈ −127 dBm) is needed to reach
        /// the full 110 dB but can lose lock on a drifty signal (#14).
        /// </summary>
        public TrflDetector Detector { get; set; } = TrflDetector.Average;

        /// <summary>
        /// Use 8902A Track Mode (SF 32.9) for the sweep — the Microwave Product Note's low-level
        /// converter method, which keeps the receiver locked onto the drifting converted signal so it
        /// can hold down toward the ~−100 dBm converter floor instead of losing lock partway (#14).
        /// Track Mode implies the Average detector.
        /// </summary>
        public bool TrackMode { get; set; } = false;

        /// <summary>Manual (default) or automatic signal acquisition for Tuned RF Level (#3). Manual
        /// tunes to the commanded frequency directly; Auto searches/acquires first, then holds.</summary>
        public TrflTuning Tuning { get; set; } = TrflTuning.Manual;

        public IEnumerable<double> Frequencies()
        {
            int n = (int)System.Math.Round((FreqStopMHz - FreqStartMHz) / FreqStepMHz) + 1;
            for (int i = 0; i < n; i++)
                yield return FreqStartMHz + i * FreqStepMHz;
        }

        /// <summary>
        /// #22: attenuation (dB) at which the sweep switches from <see cref="AttenStepDb"/> to the
        /// finer <see cref="FineStepDb"/>. Negative — the default — disables it (uniform coarse step).
        /// Used to sample the approach to the measurement floor densely, where accuracy degrades as the
        /// signal nears the noise, while the well-behaved shallow region stays cheap.
        /// </summary>
        public int FineFromDb { get; set; } = -1;

        /// <summary>#22: step (dB) used from <see cref="FineFromDb"/> through <see cref="FineToDb"/>.</summary>
        public int FineStepDb { get; set; } = 1;

        /// <summary>#22: attenuation (dB) at which the fine region ends and the coarse grid resumes.
        /// Negative = stay fine all the way to <see cref="AttenStopDb"/>.</summary>
        public int FineToDb { get; set; } = -1;

        /// <summary>#23: how the attenuation points are chosen — a fixed grid, or a plan derived per
        /// frequency from the achieved reference and the path's measurable floor.</summary>
        public AttenStepPlan StepPlan { get; set; } = AttenStepPlan.Uniform;

        /// <summary>#23: full range of the FINE step attenuator (the 8494's 1+2+4+4 = 11 dB), swept one
        /// step at a time at the top of an adaptive plan so every one of its steps is characterized.
        /// Set from the resolved <c>AttenuatorConfig</c>.</summary>
        public int FineAttenuatorMaxDb { get; set; } = 11;

        /// <summary>#23: how far above the measurable limit the adaptive plan leaves the coarse ladder
        /// and approaches the floor in 1 dB steps.</summary>
        public int FloorApproachDb { get; set; } = 10;

        /// <summary>#25: readings taken at each attenuation point. >1 gives a per-point spread (standard
        /// deviation), which is what separates a genuinely noisy measurement from a biased one — the
        /// question behind the noise-correction experiment.</summary>
        public int RepeatsPerPoint { get; set; } = 1;

        /// <summary>#25: enable 8902A noise correction (SF 31.1) — an extra Range 3 (−60 to −100 dBm)
        /// calibration factor compensating residual system noise. AVG detector + Sensor Module only, and
        /// it does nothing unless a Range 3 CALIBRATE actually fires (see <see cref="ForceRangeCal"/>).</summary>
        public bool NoiseCorrection { get; set; } = false;

        /// <summary>
        /// The attenuation points to measure, ascending and without duplicates. Normally a uniform
        /// <see cref="AttenStepDb"/> grid; when <see cref="FineFromDb"/> is set the sweep runs coarse up
        /// to it, fine (<see cref="FineStepDb"/>) through <see cref="FineToDb"/>, then rejoins the
        /// ORIGINAL coarse grid beyond it — so enabling the fine region never shifts the coarse points.
        /// </summary>
        public IEnumerable<int> AttenuationSteps()
        {
            if (FineFromDb < 0 || FineStepDb <= 0)
            {
                for (int a = AttenStartDb; a <= AttenStopDb; a += AttenStepDb)
                    yield return a;
                yield break;
            }

            int fineEnd = FineToDb >= 0 ? System.Math.Min(FineToDb, AttenStopDb) : AttenStopDb;
            int last = int.MinValue;

            // Coarse approach, up to (not including) the fine region.
            for (int a = AttenStartDb; a <= AttenStopDb && a < FineFromDb; a += AttenStepDb)
            {
                yield return a;
                last = a;
            }

            // Fine region.
            for (int a = System.Math.Max(FineFromDb, AttenStartDb); a <= fineEnd; a += FineStepDb)
            {
                if (a <= last) continue;
                yield return a;
                last = a;
            }

            // Rejoin the original coarse grid past the fine region (keeps 110 dB on a 0/10 grid).
            for (int a = AttenStartDb; a <= AttenStopDb; a += AttenStepDb)
            {
                if (a <= last) continue;
                yield return a;
                last = a;
            }
        }

        /// <summary>Number of points <see cref="AttenuationSteps"/> yields (the step grid is no longer
        /// uniform once #22's fine region is enabled, so it must be counted, not calculated).</summary>
        public int AttenuationStepCount()
        {
            int n = 0;
            foreach (var _ in AttenuationSteps()) n++;
            return n;
        }

        /// <summary>Coarser attenuation points used only for the range-calibration pass.</summary>
        public IEnumerable<int> CalSteps()
        {
            for (int a = AttenStartDb; a <= AttenStopDb; a += CalStepDb)
                yield return a;
        }

        public int FrequencyCount()
        {
            int n = 0;
            foreach (var _ in Frequencies()) n++;
            return n;
        }
    }

    /// <summary>
    /// One attenuation setting to exercise on its own: which section digits to engage, the
    /// dB it should produce, and the attenuator it belongs to (for grouped reporting).
    /// </summary>
    public sealed class AttenSetting
    {
        public string Group { get; }
        public int ExpectedDb { get; }
        public System.Collections.Generic.IReadOnlyList<int> Digits { get; }

        public AttenSetting(string group, int expectedDb, System.Collections.Generic.IReadOnlyList<int> digits)
        {
            Group = group;
            ExpectedDb = expectedDb;
            Digits = digits;
        }
    }

    /// <summary>One attenuator setting measured at one frequency.</summary>
    public sealed class AttenPointResult
    {
        public int CommandedDb { get; set; }
        public string Command { get; set; }
        public string Group { get; set; }                 // which attenuator (per-attenuator test)
        public double MeasuredRelativeDb { get; set; }   // 8902A reading, dB rel to 0 dB ref (≤ 0)
        public double MeasuredAttenuationDb { get; set; } // = -MeasuredRelativeDb
        public double ExpectedAttenuationDb { get; set; }
        public double ErrorDb { get; set; }
        public string Error { get; set; }                 // set if the 8902A reported an error

        /// <summary>
        /// #13: this point saturated at the converter floor — the reading stopped tracking the
        /// attenuation (a deep point reads ~the floor and so under-reads its target). It's the
        /// measurement floor, not a DUT/sweep fault, so it's excluded from the accuracy verdict and
        /// reported as FLOOR rather than a failure. Set by the floor/plateau classifier.
        /// </summary>
        public bool FloorLimited { get; set; }

        /// <summary>
        /// #21: this point was NOT attempted — its predicted level (<see cref="PredictedLevelDbm"/>)
        /// falls outside the measurable window of the path in use, so commanding it could only produce
        /// a meaningless under-read and an Error 01 on the receiver. Excluded from the accuracy verdict
        /// and reported as out-of-range rather than as a measurement failure.
        /// </summary>
        public bool OutOfRange { get; set; }

        /// <summary>#21: the absolute level this point would have produced, dBm (reference − target
        /// attenuation). NaN when the reference level is unknown, in which case limits aren't enforced.</summary>
        public double PredictedLevelDbm { get; set; } = double.NaN;

        /// <summary>
        /// The dB the signal ACTUALLY moved between the previous measured point and this one (#24) —
        /// the increment this attenuator step contributed. NaN on the first measured point of a sweep
        /// and on any point that produced no reading. This is what answers "does each step apply its
        /// nominal value", which the cumulative error column cannot: a cumulative error carries every
        /// earlier step's contribution with it.
        /// </summary>
        public double StepDeltaDb { get; set; } = double.NaN;

        /// <summary>#24: the increment this step was COMMANDED to make, dB (this point's target minus
        /// the previous measured point's target).</summary>
        public double NominalStepDb { get; set; } = double.NaN;

        /// <summary>#24: <see cref="StepDeltaDb"/> − <see cref="NominalStepDb"/> — how far this single
        /// step missed its own nominal value, independent of everything before it.</summary>
        public double StepErrorDb { get; set; } = double.NaN;

        /// <summary>#25: every reading taken at this point (one per repeat), in order.</summary>
        public System.Collections.Generic.List<double> Repeats { get; } = new System.Collections.Generic.List<double>();

        /// <summary>#25: standard deviation of <see cref="Repeats"/>, dB. NaN with fewer than 2 readings.
        /// This is the measurement's repeatability at this level — high sd means noise, while a large
        /// error with a low sd means bias.</summary>
        public double StdDevDb { get; set; } = double.NaN;

        /// <summary>True if this point yielded no usable measurement — never attempted (#21), flagged at
        /// the floor (#13), errored, or unreadable. Such points are excluded from the accuracy verdict.</summary>
        public bool Excluded =>
            OutOfRange || FloorLimited || Error != null || double.IsNaN(MeasuredAttenuationDb);
    }

    /// <summary>Result of a signal-presence check at one frequency.</summary>
    public sealed class DetectResult
    {
        public double FreqMHz { get; set; }
        public MeasurementRegime Regime { get; set; }
        public double LoMHz { get; set; }
        public double IfMHz { get; set; }
        public double MeasuredFreqMHz { get; set; } = double.NaN; // 8902A M5 with RF on
        public bool SignalWithRfOn { get; set; }
        public bool SignalWithRfOff { get; set; }
        public bool Detected { get; set; }
        public string Warning { get; set; }
        public string Note { get; set; }
    }

    /// <summary>Result of a single-point absolute RF power readback (Test 1).</summary>
    public sealed class RfPowerResult
    {
        public double FreqMHz { get; set; }
        public MeasurementRegime Regime { get; set; }
        public double LoMHz { get; set; }
        public double IfMHz { get; set; }
        public double SourcePowerDbm { get; set; }
        public int AttenuationDb { get; set; }
        public double MeasuredPowerDbm { get; set; } = double.NaN;

        /// <summary>source − attenuation − measured: the path/insertion loss implied by the reading.</summary>
        public double ImpliedPathLossDb { get; set; } = double.NaN;

        public string Warning { get; set; }
        public string Error { get; set; }    // set if the 8902A reported an error
    }

    /// <summary>All attenuator measurements at one frequency.</summary>
    public sealed class FreqPointResult
    {
        public double FreqMHz { get; set; }
        public MeasurementRegime Regime { get; set; }
        public double LoMHz { get; set; }
        public double IfMHz { get; set; }
        public string Warning { get; set; }

        /// <summary>Absolute 0 dB reference level measured at the 8902A after leveling, dBm
        /// (NaN if leveling was off or the level couldn't be read). See #16.</summary>
        public double ReferencePowerDbm { get; set; } = double.NaN;

        /// <summary>Source power the leveler settled on for this frequency, dBm (#16).</summary>
        public double LeveledSourcePowerDbm { get; set; } = double.NaN;

        public List<AttenPointResult> Points { get; } = new List<AttenPointResult>();

        /// <summary>Worst |error| over the ACCURATE points — floor-limited points (#13) are excluded, as
        /// they read the measurement floor rather than the attenuation and would otherwise dominate.</summary>
        public double MaxAbsErrorDb
        {
            get
            {
                double m = 0;
                foreach (var p in Points)
                    if (!p.Excluded && System.Math.Abs(p.ErrorDb) > m) m = System.Math.Abs(p.ErrorDb);
                return m;
            }
        }

        /// <summary>Count of points flagged as floor-limited (#13) at this frequency.</summary>
        public int FloorLimitedCount
        {
            get
            {
                int n = 0;
                foreach (var p in Points) if (p.FloorLimited) n++;
                return n;
            }
        }

        /// <summary>Deepest attenuation (dB) actually tracked — the largest measured attenuation among
        /// points NOT flagged floor-limited. NaN if none measured. The honest usable depth at this freq.</summary>
        public double DeepestMeasuredDb
        {
            get
            {
                double d = double.NaN;
                foreach (var p in Points)
                    if (!p.Excluded)
                        d = double.IsNaN(d) ? p.MeasuredAttenuationDb : System.Math.Max(d, p.MeasuredAttenuationDb);
                return d;
            }
        }

        /// <summary>#21: count of points skipped as outside the path's measurable level window.</summary>
        public int OutOfRangeCount
        {
            get
            {
                int n = 0;
                foreach (var p in Points) if (p.OutOfRange) n++;
                return n;
            }
        }

        /// <summary>#21: the measurable level window of the path used at this frequency (null if not
        /// evaluated — e.g. the reference level was never read).</summary>
        public LevelWindow LevelWindow { get; set; }

        /// <summary>#21: deepest attenuation (dB) the path could measure from the achieved reference —
        /// reference − floor. NaN when the reference or window is unknown.</summary>
        public double UsableDepthDb =>
            LevelWindow == null || double.IsNaN(ReferencePowerDbm)
                ? double.NaN
                : LevelWindow.UsableDepthDb(ReferencePowerDbm);
    }
}
