using System;
using System.Collections.Generic;
using System.Linq;
using HpAttenuator.Model;

namespace HpAttenuator.Instruments
{
    /// <summary>
    /// Shared mutable state for a simulated bench. The simulated source/LO/attenuator
    /// write into it and the simulated receiver computes a measured level from it, so
    /// the whole harness can run with no hardware attached.
    /// </summary>
    public sealed class SimulatedBench
    {
        public double SourceFreqMHz;
        public double SourcePowerDbm = -200;
        public bool SourceRfOn;

        public double LoFreqMHz;
        public double LoPowerDbm = -200;
        public bool LoRfOn;

        /// <summary>The TRUE dB weight of each relay digit (1-8) as physically wired.</summary>
        public Dictionary<int, double> TrueSectionDb { get; }

        /// <summary>Relay digits currently engaged.</summary>
        public HashSet<int> EngagedDigits { get; } = new HashSet<int>();

        /// <summary>Receiver noise floor in dBm; readings clamp up from here.</summary>
        public double NoiseFloorDbm = -130.0;

        /// <summary>Peak measurement noise amplitude in dB.</summary>
        public double NoiseDb = 0.03;

        private readonly Random _rng;

        /// <param name="xIs8494">
        /// True: ATTEN X (digits 1-4) is the 8494 (1 dB) and ATTEN Y (5-8) is the 8496
        /// (10 dB). False: the two are swapped. This is the ground truth the harness's
        /// identification step is meant to discover.
        /// </param>
        public SimulatedBench(bool xIs8494 = true, int seed = 12345)
        {
            _rng = new Random(seed);
            double[] fine = { 1, 2, 4, 4 };
            double[] coarse = { 10, 20, 40, 40 };
            double[] x = xIs8494 ? fine : coarse;
            double[] y = xIs8494 ? coarse : fine;
            TrueSectionDb = new Dictionary<int, double>();
            for (int i = 0; i < 4; i++) TrueSectionDb[i + 1] = x[i]; // digits 1-4
            for (int i = 0; i < 4; i++) TrueSectionDb[i + 5] = y[i]; // digits 5-8
        }

        /// <summary>True total attenuation from the engaged sections.</summary>
        public double TrueAttenuationDb =>
            EngagedDigits.Sum(d => TrueSectionDb.TryGetValue(d, out var v) ? v : 0.0);

        /// <summary>
        /// Models the level the receiver would read: source power minus attenuation,
        /// plus a small frequency-dependent insertion loss and measurement noise. The
        /// insertion loss is constant across an attenuation sweep at one frequency, so
        /// it cancels out of the relative attenuation result.
        /// </summary>
        public double MeasuredLevelDbm()
        {
            if (!SourceRfOn)
                return NoiseFloorDbm + Noise();

            double pathLoss = 0.5 + 0.4 * Math.Log10(1.0 + SourceFreqMHz / 1000.0);
            double level = SourcePowerDbm - TrueAttenuationDb - pathLoss + Noise();
            return Math.Max(level, NoiseFloorDbm + Noise());
        }

        private double Noise() => (_rng.NextDouble() * 2.0 - 1.0) * NoiseDb;
    }

    /// <summary>Simulated CW source backed by a <see cref="SimulatedBench"/>.</summary>
    public sealed class SimulatedSource : ISignalSource
    {
        private readonly SimulatedBench _bench;
        public SimulatedSource(SimulatedBench bench) => _bench = bench;
        public string ResourceName => "SIM:SOURCE";
        public void Initialize() { _bench.SourceRfOn = false; }
        public void Preset() { _bench.SourceRfOn = false; }
        public void SetFrequencyMHz(double mhz) => _bench.SourceFreqMHz = mhz;
        public void SetPowerDbm(double dbm) => _bench.SourcePowerDbm = dbm;
        public void RfOn() => _bench.SourceRfOn = true;
        public void RfOff() => _bench.SourceRfOn = false;
    }

    /// <summary>Simulated external LO backed by a <see cref="SimulatedBench"/>.</summary>
    public sealed class SimulatedLo : ILocalOscillator
    {
        private readonly SimulatedBench _bench;
        public SimulatedLo(SimulatedBench bench) => _bench = bench;
        public string ResourceName => "SIM:LO";
        public double MinFrequencyMHz => 2000.0;
        public double MaxFrequencyMHz => 26500.0;
        public void Initialize() { _bench.LoRfOn = false; }
        public void Preset() { _bench.LoRfOn = false; }
        public void SetFrequencyMHz(double mhz) => _bench.LoFreqMHz = mhz;
        public void SetPowerDbm(double dbm) => _bench.LoPowerDbm = dbm;
        public void RfOn() => _bench.LoRfOn = true;
        public void RfOff() => _bench.LoRfOn = false;
    }

    /// <summary>Simulated 11713A attenuator backed by a <see cref="SimulatedBench"/>.</summary>
    public sealed class SimulatedAttenuator : IStepAttenuator
    {
        private readonly SimulatedBench _bench;

        public SimulatedAttenuator(SimulatedBench bench, AttenuatorConfig config)
        {
            _bench = bench;
            Config = config;
        }

        public string ResourceName => "SIM:11713A";
        public AttenuatorConfig Config { get; }

        public void Initialize() => _bench.EngagedDigits.Clear();

        public string SetAttenuationDb(int db)
        {
            var engaged = CommandBuilder.Solve(Config.AllSections.ToList(), db);
            if (engaged == null)
                throw new ArgumentOutOfRangeException(nameof(db),
                    $"{db} dB is not achievable (range 0-{Config.MaxDecibels} dB).");
            return SetEngaged(engaged);
        }

        public string SetEngaged(IEnumerable<int> digits)
        {
            var set = new HashSet<int>(digits);
            _bench.EngagedDigits.Clear();
            foreach (var d in set) _bench.EngagedDigits.Add(d);
            return CommandBuilder.BuildString(Config.AllSections, set);
        }
    }

    /// <summary>
    /// Simulated 8902A measuring receiver backed by a <see cref="SimulatedBench"/>.
    /// Models the relative Tuned RF Level workflow: a 0 dB reference is captured at
    /// SetReference and subsequent reads return dB relative to it. Throws
    /// <see cref="Hp8902AException"/> (96) when no signal is present, mirroring real
    /// hardware.
    /// </summary>
    public sealed class SimulatedReceiver : IMeasuringReceiver
    {
        private readonly SimulatedBench _bench;
        private double _referenceDbm;
        private bool _haveReference;
        private double _tunedMHz;

        public SimulatedReceiver(SimulatedBench bench) => _bench = bench;
        public string ResourceName => "SIM:8902A";

        public void Initialize() { _haveReference = false; }
        public void Reset() { _haveReference = false; }
        public void SelectRfPower() { }
        public void LoadCalFactors(double referenceCf, System.Collections.Generic.IReadOnlyList<CalFactor> table) { }
        public double ZeroSensor() => 2e-10;     // a near-zero residual, watts
        public double CalibrateSensor() => 1.0e-3; // 1.000 mW reference, watts

        public void BeginAttenuationMeasurement(double rfMHz, MeasurementRegime regime, double loMHz)
        {
            _tunedMHz = rfMHz;
            _haveReference = false;
        }

        public void BeginRfPowerMeasurement(double rfMHz, MeasurementRegime regime, double loMHz)
        {
            _tunedMHz = rfMHz;
        }

        public double ReadRfPowerDbm()
        {
            if (!_bench.SourceRfOn) throw new Hp8902AException(96, Hp8902AException.Describe(96));
            return _bench.MeasuredLevelDbm();   // absolute level: source - attenuation - path loss
        }

        public void BeginRangeCalibration() { }
        public void EnableRecalStatus() { }
        public bool RecalRequested() => false; // simulated receiver never needs range calibration
        public int PollStatusByte() => 0;
        public void Calibrate() { }
        public void ClearError() { }
        public void RetuneToSignal() { }   // sim never loses lock (Error 96 only when RF is off)

        public void SetReference()
        {
            _referenceDbm = _bench.MeasuredLevelDbm();
            _haveReference = true;
        }

        public double ReadRelativeDb()
        {
            if (!_bench.SourceRfOn) throw new Hp8902AException(96, Hp8902AException.Describe(96));
            double level = _bench.MeasuredLevelDbm();
            double reference = _haveReference ? _referenceDbm : level;
            return level - reference;   // ≤ 0 as attenuation increases
        }

        public double ReadSignalFrequencyMHz()
        {
            if (!_bench.SourceRfOn) throw new Hp8902AException(96, Hp8902AException.Describe(96));
            return _tunedMHz;
        }
    }
}
