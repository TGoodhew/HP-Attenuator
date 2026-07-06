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

        public double MaxAbsErrorDb
        {
            get
            {
                double m = 0;
                foreach (var p in Points)
                    if (System.Math.Abs(p.ErrorDb) > m) m = System.Math.Abs(p.ErrorDb);
                return m;
            }
        }
    }
}
