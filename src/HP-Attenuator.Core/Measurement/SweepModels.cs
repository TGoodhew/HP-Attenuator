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

        /// <summary>Target for the leveled 0 dB reference at the 8902A, dBm. Just under the 0 dBm
        /// ceiling with margin against drift (manual guidance −1 to −3 dBm).</summary>
        public double TargetReferenceDbm { get; set; } = -2.0;

        /// <summary>Accept the reference without stepping when it is within this of the target, dB.</summary>
        public double LevelToleranceDb { get; set; } = 1.0;

        /// <summary>Max source-power adjustment iterations per frequency (best-effort; clamps out).</summary>
        public int MaxLevelIterations { get; set; } = 5;

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

        /// <summary>#13: absolute level (dBm) at/below which a reading is treated as sitting on the
        /// converter floor. The 11793A path floors near −100 dBm and readings saturate ~−98.7 dBm
        /// (SharedMemory.md); default −98 with <see cref="FloorMarginDb"/> of headroom.</summary>
        public double FloorDbm { get; set; } = -98.0;

        /// <summary>#13: dB band used by the floor classifier — the headroom above <see cref="FloorDbm"/>
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

        public IEnumerable<int> AttenuationSteps()
        {
            for (int a = AttenStartDb; a <= AttenStopDb; a += AttenStepDb)
                yield return a;
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
                    if (!p.FloorLimited && System.Math.Abs(p.ErrorDb) > m) m = System.Math.Abs(p.ErrorDb);
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
                    if (!p.FloorLimited && p.Error == null && !double.IsNaN(p.MeasuredAttenuationDb))
                        d = double.IsNaN(d) ? p.MeasuredAttenuationDb : System.Math.Max(d, p.MeasuredAttenuationDb);
                return d;
            }
        }
    }
}
