using System.Collections.Generic;
using HpAttenuator.Instruments;
using HpAttenuator.Measurement;
using HpAttenuator.Model;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HpAttenuator.Tests
{
    /// <summary>
    /// "Never step the attenuator or change the source until calibration has been positively
    /// confirmed" (#41): a CALIBRATE that cannot be confirmed must stop the descent and the sweep where
    /// they are. Runs the real engine over the simulated bench with a receiver that fails on demand.
    /// </summary>
    [TestClass]
    public class EngineCalibrationStopTests
    {
        private static (MeasurementEngine engine, FailingReceiver rx, RecordingAttenuator atten)
            Build(SweepOptions options)
        {
            var sim = new SimulatedBench();
            var rx = new FailingReceiver(new SimulatedReceiver(sim));
            var atten = new RecordingAttenuator(new SimulatedAttenuator(sim, AttenuatorConfig.Default()), rx);
            var engine = new MeasurementEngine(new SimulatedSource(sim), new SimulatedLo(sim), atten, rx, options);
            return (engine, rx, atten);
        }

        private static SweepOptions Options(bool force) => new SweepOptions
        {
            FreqStartMHz = 3000, FreqStopMHz = 3000,
            AttenStartDb = 0, AttenStopDb = 60, AttenStepDb = 10,
            SettleMs = 0,
            RangeCalibrate = true,
            ForceRangeCal = force,
        };

        [TestMethod]
        public void AFailedCalibrateInTheDescent_StopsBeforeTheNextStep()
        {
            var (engine, rx, atten) = Build(Options(force: true));
            rx.FailOnCalibrate = 2;                       // the forced CALIBRATE at 20 dB

            Assert.ThrowsException<TrflCalibrationFailedException>(() => engine.MeasureFrequency(3000));

            Assert.IsTrue(rx.Failed);
            Assert.AreEqual(0, atten.CommandsAfterFailure,
                "the attenuator was stepped after an unconfirmed CALIBRATE: " + string.Join(", ", atten.AfterFailure));
        }

        [TestMethod]
        public void AFailedBoundaryCalibrateInTheSweep_StopsTheFrequencyThere()
        {
            var (engine, rx, atten) = Build(Options(force: false));
            rx.UncalOnRelativeRead = 3;                   // after SET REF: 0 dB, 10 dB, then UNCAL at 20 dB
            rx.FailAfterUncal = true;                     // ...and the boundary CALIBRATE it triggers fails

            var result = engine.MeasureFrequency(3000);

            Assert.IsTrue(rx.Failed, "the boundary CALIBRATE was never attempted");
            Assert.AreEqual(0, atten.CommandsAfterFailure,
                "the attenuator was stepped after an unconfirmed CALIBRATE: " + string.Join(", ", atten.AfterFailure));
            StringAssert.Contains(result.Warning, "could not be confirmed");
            Assert.AreEqual(3, result.Points.Count, "0, 10 and the failed 20 dB point; nothing after");
        }

        /// <summary>Delegates to the simulated receiver, except: CALIBRATE number
        /// <see cref="FailOnCalibrate"/> throws (or, with <see cref="FailAfterUncal"/>, the first one
        /// after the scripted UNCAL), and relative read number <see cref="UncalOnRelativeRead"/>,
        /// counted from SET REF, returns UNCAL.</summary>
        private sealed class FailingReceiver : IMeasuringReceiver
        {
            private readonly IMeasuringReceiver _inner;
            private int _cals, _relReads;
            public int FailOnCalibrate = -1;
            public int UncalOnRelativeRead = -1;
            public bool FailAfterUncal;
            private bool _uncalFired, _afterRef;
            public bool Failed;

            public FailingReceiver(IMeasuringReceiver inner) => _inner = inner;

            public void Calibrate()
            {
                if (++_cals == FailOnCalibrate || (FailAfterUncal && _uncalFired))
                {
                    Failed = true;
                    throw new TrflCalibrationFailedException(new TrflCalibrationResult
                    {
                        Outcome = TrflCalibrationOutcome.Error,
                        Message = "scripted Error 33",
                    });
                }
                _inner.Calibrate();
            }

            public double ReadRelativeDb()
            {
                // Counted from SET REF, so the scripted UNCAL lands in the sweep, not the descent.
                if (_afterRef && ++_relReads == UncalOnRelativeRead) { _uncalFired = true; throw Hp8902AException.Uncal(); }
                return _inner.ReadRelativeDb();
            }

            public string ResourceName => _inner.ResourceName;
            public void Initialize() => _inner.Initialize();
            public void Reset() => _inner.Reset();
            public void SelectRfPower() => _inner.SelectRfPower();
            public void LoadCalFactors(double referenceCf, IReadOnlyList<CalFactor> table) => _inner.LoadCalFactors(referenceCf, table);
            public double ZeroSensor() => _inner.ZeroSensor();
            public double CalibrateSensor() => _inner.CalibrateSensor();
            public double ReadFirmwareDateCode() => _inner.ReadFirmwareDateCode();
            public double ReadTrflCalFactor(int range) => _inner.ReadTrflCalFactor(range);
            public void ClearTrflCalFactors() => _inner.ClearTrflCalFactors();
            public double ReadAmDepthPercent() => _inner.ReadAmDepthPercent();
            public double ReadFmDeviationHz() => _inner.ReadFmDeviationHz();
            public void BeginRfPowerMeasurement(double rfMHz, MeasurementRegime regime, double loMHz) => _inner.BeginRfPowerMeasurement(rfMHz, regime, loMHz);
            public double ReadRfPowerDbm() => _inner.ReadRfPowerDbm();
            public void BeginAttenuationMeasurement(double rfMHz, MeasurementRegime regime, double loMHz,
                TrflDetector detector = TrflDetector.Average, bool trackMode = false,
                TrflTuning tuning = TrflTuning.Manual, bool noiseCorrection = false) =>
                _inner.BeginAttenuationMeasurement(rfMHz, regime, loMHz, detector, trackMode, tuning, noiseCorrection);
            public void BeginRangeCalibration() => _inner.BeginRangeCalibration();
            public void EnableRecalStatus() => _inner.EnableRecalStatus();
            public bool RecalRequested() => _inner.RecalRequested();
            public int PollStatusByte() => _inner.PollStatusByte();
            public void ClearError() => _inner.ClearError();
            public void RetuneToSignal() => _inner.RetuneToSignal();
            public void ReleaseBus() => _inner.ReleaseBus();
            public void SetReference() { _afterRef = true; _inner.SetReference(); }
            public double ReadTunedLevelDbm() => _inner.ReadTunedLevelDbm();
            public double ReadSignalFrequencyMHz() => _inner.ReadSignalFrequencyMHz();
        }

        /// <summary>Records every attenuator command issued after the receiver's CALIBRATE failed.</summary>
        private sealed class RecordingAttenuator : IStepAttenuator
        {
            private readonly IStepAttenuator _inner;
            private readonly FailingReceiver _rx;
            public readonly List<string> AfterFailure = new List<string>();
            public int CommandsAfterFailure => AfterFailure.Count;

            public RecordingAttenuator(IStepAttenuator inner, FailingReceiver rx) { _inner = inner; _rx = rx; }

            public string ResourceName => _inner.ResourceName;
            public AttenuatorConfig Config => _inner.Config;
            public void Initialize() => _inner.Initialize();

            public string SetAttenuationDb(int db)
            {
                if (_rx.Failed) AfterFailure.Add($"{db} dB");
                return _inner.SetAttenuationDb(db);
            }

            public string SetEngaged(IEnumerable<int> digits)
            {
                if (_rx.Failed) AfterFailure.Add("digits " + string.Join("", digits));
                return _inner.SetEngaged(digits);
            }
        }
    }
}
