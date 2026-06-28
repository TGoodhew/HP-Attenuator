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

        public double SourcePowerDbm { get; set; } = 0.0;
        public double LoPowerDbm { get; set; } = 8.0; // 11793A wants +8 dBm LO drive (2-18 GHz)

        public int AttenStartDb { get; set; } = 0;
        public int AttenStopDb { get; set; } = 110;
        public int AttenStepDb { get; set; } = 10;

        /// <summary>Settle time per attenuator step, in ms (on top of the 8902A's T3 settling).</summary>
        public int SettleMs { get; set; } = 100;

        /// <summary>Run the 8902A 3-point range-calibration pass before measuring (hardware).</summary>
        public bool RangeCalibrate { get; set; } = true;

        /// <summary>Attenuation step (dB) used only for the range-calibration pass.</summary>
        public int CalStepDb { get; set; } = 10;

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

    /// <summary>One attenuator setting measured at one frequency.</summary>
    public sealed class AttenPointResult
    {
        public int CommandedDb { get; set; }
        public string Command { get; set; }
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

    /// <summary>All attenuator measurements at one frequency.</summary>
    public sealed class FreqPointResult
    {
        public double FreqMHz { get; set; }
        public MeasurementRegime Regime { get; set; }
        public double LoMHz { get; set; }
        public double IfMHz { get; set; }
        public string Warning { get; set; }
        public double ReferencePowerDbm { get; set; }
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
