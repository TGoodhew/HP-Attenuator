using System.Collections.Generic;
using System.Threading;
using HpAttenuator.Instruments;

namespace HpAttenuator.Measurement
{
    /// <summary>
    /// Runs the attenuation-vs-frequency measurement: for each source frequency it
    /// establishes the measurement regime (direct or via the 11793A + LO), then steps
    /// the attenuator and records the measured attenuation relative to the 0 dB point.
    /// </summary>
    public sealed class MeasurementEngine
    {
        private readonly ISignalSource _source;
        private readonly ILocalOscillator _lo;
        private readonly IStepAttenuator _attenuator;
        private readonly IMeasuringReceiver _receiver;
        private readonly SweepOptions _options;

        public MeasurementEngine(ISignalSource source, ILocalOscillator lo,
                                 IStepAttenuator attenuator, IMeasuringReceiver receiver,
                                 SweepOptions options)
        {
            _source = source;
            _lo = lo;
            _attenuator = attenuator;
            _receiver = receiver;
            _options = options;
        }

        /// <summary>Measures every frequency, yielding a result as each completes.</summary>
        public IEnumerable<FreqPointResult> RunSweep(CancellationToken cancel = default)
        {
            _source.SetPowerDbm(_options.SourcePowerDbm);
            _source.RfOn();
            _receiver.PrepareTunedRfLevel();

            foreach (double freq in _options.Frequencies())
            {
                if (cancel.IsCancellationRequested) yield break;
                yield return MeasureFrequency(freq);
            }
        }

        /// <summary>Measures a single frequency across the attenuation range.</summary>
        public FreqPointResult MeasureFrequency(double freqMHz)
        {
            var plan = MicrowaveConverter.Plan(freqMHz, _lo.MinFrequencyMHz, _lo.MaxFrequencyMHz);

            _source.SetFrequencyMHz(freqMHz);
            _source.SetPowerDbm(_options.SourcePowerDbm);
            _source.RfOn();

            if (plan.Regime == MeasurementRegime.Converted)
            {
                _lo.SetFrequencyMHz(plan.LoMHz);
                _lo.SetPowerDbm(_options.LoPowerDbm);
                _lo.RfOn();
                _receiver.ConfigureConverted(freqMHz, plan.LoMHz);
            }
            else
            {
                _lo.RfOff();
                _receiver.ConfigureDirect(freqMHz);
            }

            var result = new FreqPointResult
            {
                FreqMHz = freqMHz,
                Regime = plan.Regime,
                LoMHz = plan.LoMHz,
                IfMHz = plan.IfMHz,
                Warning = plan.Warning
            };

            double reference = 0;
            bool haveReference = false;

            foreach (int atten in _options.AttenuationSteps())
            {
                string command = _attenuator.SetAttenuationDb(atten);
                if (_options.SettleMs > 0) Thread.Sleep(_options.SettleMs);

                double power = _receiver.ReadLevelDbm();
                if (!haveReference)
                {
                    reference = power;
                    haveReference = true;
                    result.ReferencePowerDbm = power;
                }

                double measuredAtten = reference - power;
                double expected = atten - _options.AttenStartDb;

                result.Points.Add(new AttenPointResult
                {
                    CommandedDb = atten,
                    Command = command,
                    MeasuredPowerDbm = power,
                    MeasuredAttenuationDb = measuredAtten,
                    ExpectedAttenuationDb = expected,
                    ErrorDb = measuredAtten - expected
                });
            }

            return result;
        }
    }
}
