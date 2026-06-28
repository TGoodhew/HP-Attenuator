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
        public double MeasuredPowerDbm { get; set; }
        public double MeasuredAttenuationDb { get; set; }
        public double ExpectedAttenuationDb { get; set; }
        public double ErrorDb { get; set; }
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
