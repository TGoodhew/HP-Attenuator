using System;
using System.Collections.Generic;
using System.Threading;
using HpAttenuator.Instruments;

namespace HpAttenuator.Measurement
{
    /// <summary>
    /// Runs the attenuation-vs-frequency measurement. For each source frequency it
    /// establishes the regime (direct or via the 11793A + LO), optionally runs the
    /// 8902A 3-point range calibration, sets a 0 dB reference, then steps the
    /// attenuator and records the measured attenuation as the negated relative dB.
    /// </summary>
    public sealed class MeasurementEngine
    {
        /// <summary>Stop a sweep after this many consecutive below-floor read failures.</summary>
        private const int FloorStopCount = 4;

        /// <summary>Read attempts for the 0 dB reference point (it anchors the whole sweep).</summary>
        private const int ReferenceReadAttempts = 5;

        /// <summary>Read attempts for each ordinary sweep point (transient empty/garbled reads at an
        /// RF-range auto-range boundary can take a few settled re-reads to clear).</summary>
        private const int StepReadAttempts = 6;

        /// <summary>Wait between read retries, ms. Longer than a normal settle so an RF-range
        /// auto-range (which briefly returns empty/garbled reads) has time to settle before re-read.</summary>
        private const int TransientReadSettleMs = 1200;

        /// <summary>
        /// How far below the source level the range CALIBRATE may go, in dB. CALIBRATE needs
        /// the signal still measurable; past ~this depth it raises Error 35 ("level error
        /// during calibration"). --cal-probe read cleanly to ~-90 dB; 80 keeps a margin. The
        /// receiver then measures DEEPER than this on the calibration it established.
        /// </summary>
        private const int RangeCalReachDb = 80;

        /// <summary>
        /// Step size (dB) for the range-cal pass. Fine enough to land on each RF-range boundary
        /// so RECAL is caught and that range calibrated; a coarse step skips boundaries and
        /// leaves bands uncalibrated (UNCAL) during measurement.
        /// </summary>
        private const int RangeCalStepDb = 2;

        /// <summary>
        /// A step-to-step change in the relative reading that deviates from the expected
        /// per-step attenuation by more than this (dB) means the receiver crossed into an
        /// UNCALIBRATED RF range (the reading jumps by the range factor). It's the reliable
        /// range-change signal — the RECAL status bit is polled but is often missed — so we
        /// CALIBRATE that range and re-read when the reading jumps this far off trend.
        /// </summary>
        private const double RangeStepThresholdDb = 2.0;

        /// <summary>
        /// Maximum range-to-range CALIBRATEs per frequency during the sweep. The 8902A stores
        /// exactly <b>two</b> input range-to-range calibration factors per IF detector (O&amp;C 3-115,
        /// "Two input range-to-range calibration factors are stored for each of the two IF
        /// detectors"), so at most two boundaries ever need calibrating on the way down. The cap
        /// is also a safety net: it stops a runaway deep/weak CALIBRATE, which stores a bad factor
        /// (the CAUTION — the AVG detector needs the sensor module to reference a range cal, and a
        /// deep/weak level it can't reference is the ~4 dB error chased in bf6ba51).
        /// </summary>
        private const int MaxBoundaryCalibrations = 2;

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

        public IEnumerable<FreqPointResult> RunSweep(CancellationToken cancel = default)
        {
            _source.SetPowerDbm(_options.SourcePowerDbm);
            _source.RfOn();
            _receiver.Reset();

            foreach (double freq in _options.Frequencies())
            {
                if (cancel.IsCancellationRequested) yield break;
                yield return MeasureFrequency(freq);
            }
        }

        /// <summary>Confirms the chain can see the source at one frequency (RF on vs off).</summary>
        public DetectResult DetectSignal(double freqMHz, double thresholdDb = 10.0)
        {
            var plan = Prepare(freqMHz);
            _receiver.BeginAttenuationMeasurement(freqMHz, plan.Regime, plan.LoMHz);
            _attenuator.SetAttenuationDb(0);

            var r = new DetectResult
            {
                FreqMHz = freqMHz, Regime = plan.Regime, LoMHz = plan.LoMHz,
                IfMHz = plan.IfMHz, Warning = plan.Warning
            };

            _source.RfOn();
            Settle();
            try { r.MeasuredFreqMHz = _receiver.ReadSignalFrequencyMHz(); r.SignalWithRfOn = true; }
            catch (Hp8902AException) { r.SignalWithRfOn = false; }

            _source.RfOff();
            Settle();
            try { _receiver.ReadSignalFrequencyMHz(); r.SignalWithRfOff = true; }
            catch (Hp8902AException) { r.SignalWithRfOff = false; }

            _source.RfOn();   // leave the path live

            // Detected: signal seen with RF on, gone with RF off, and frequency ~ expected.
            bool freqOk = !double.IsNaN(r.MeasuredFreqMHz) &&
                          Math.Abs(r.MeasuredFreqMHz - freqMHz) <= Math.Max(1.0, freqMHz * 0.001);
            r.Detected = r.SignalWithRfOn && !r.SignalWithRfOff && freqOk;
            return r;
        }

        /// <summary>
        /// Test 1: single-point absolute RF power readback. Sets the attenuator to a fixed
        /// value (default 0 dB), sources <paramref name="freqMHz"/> at the configured power,
        /// and reads the absolute power in dBm via the 8902A RF Power measurement (through
        /// the 11793A + LO when above the crossover). The RF-Power cal-factor table should
        /// already be loaded for converter-path accuracy.
        /// </summary>
        public RfPowerResult MeasureRfPower(double freqMHz, int attenuationDb = 0)
        {
            var plan = Prepare(freqMHz);
            _attenuator.SetAttenuationDb(attenuationDb);
            _receiver.BeginRfPowerMeasurement(freqMHz, plan.Regime, plan.LoMHz);
            Settle();

            var result = new RfPowerResult
            {
                FreqMHz = freqMHz, Regime = plan.Regime, LoMHz = plan.LoMHz, IfMHz = plan.IfMHz,
                Warning = plan.Warning, SourcePowerDbm = _options.SourcePowerDbm,
                AttenuationDb = attenuationDb
            };

            try
            {
                double dbm = _receiver.ReadRfPowerDbm();
                result.MeasuredPowerDbm = dbm;
                result.ImpliedPathLossDb = _options.SourcePowerDbm - attenuationDb - dbm;
            }
            catch (System.Exception ex)
            {
                result.Error = ex is Hp8902AException ? ex.Message : "read failed: " + ex.GetType().Name;
                try { _receiver.ClearError(); } catch { /* keep going */ }
            }
            return result;
        }

        public FreqPointResult MeasureFrequency(double freqMHz,
            System.Action<int, int, AttenPointResult> onPoint = null)
        {
            int total = (_options.AttenStopDb - _options.AttenStartDb) / _options.AttenStepDb + 1;
            int index = 0;
            var plan = Prepare(freqMHz);
            _receiver.BeginAttenuationMeasurement(freqMHz, plan.Regime, plan.LoMHz);

            var result = new FreqPointResult
            {
                FreqMHz = freqMHz, Regime = plan.Regime, LoMHz = plan.LoMHz,
                IfMHz = plan.IfMHz, Warning = plan.Warning
            };

            // Establish the 0 dB reference and run the range calibration. The 8902A needs
            // each RF range calibrated to hold lock as the level drops; without it the tuned
            // receiver loses lock after only a few dB (Error 96). This is the sequence proven
            // by --cal-probe on hardware (reads cleanly to ~-90 dB): SET REF at 0 dB FIRST,
            // then step the level down and CALIBRATE at each coarse step. The CALIBRATE is
            // CAPPED so it never goes deeper than the signal can be measured — calibrating
            // past that point is the "level error during calibration" (Error 35).
            RunRangeCalibration(() => _attenuator.SetAttenuationDb(_options.AttenStartDb));

            // The 8902A SET REF can leave a small residual offset, so we also normalise in
            // software: the reading at the start attenuation defines 0 dB and every reading
            // is reported relative to it (substitution method, attenuation = reading0 −
            // reading_i). This guarantees the first point is exactly 0 dB — only the
            // attenuation shows, with no path-loss / reference offset.
            bool haveBaseline = false;
            double baselineRelDb = 0.0;
            int consecutiveTimeouts = 0;
            int boundaryCals = 0;   // range-to-range CALIBRATEs done this frequency (cap: MaxBoundaryCalibrations)

            foreach (int atten in _options.AttenuationSteps())
            {
                string command = _attenuator.SetAttenuationDb(atten);
                Settle();

                double expected = atten - _options.AttenStartDb;
                var point = new AttenPointResult
                {
                    CommandedDb = atten,
                    Command = command,
                    ExpectedAttenuationDb = expected
                };

                try
                {
                    // Retry transient instrument errors (e.g. Error 96 "no signal sensed") —
                    // and give the 0 dB reference extra attempts, since it anchors every other
                    // point. Timeouts (the receiver floor) are not retried here.
                    //
                    // Per the manual's Attenuator Measurements procedure (O&C 3-115), the stepping
                    // points CALIBRATE each RF input range-to-range boundary the first time RECAL
                    // appears — see ReadStepWithBoundaryCal. Only ~2 boundaries exist and they're
                    // calibrated at the shallow level where RECAL naturally lights (strong enough for
                    // the sensor module to reference), NOT at a fixed deep step (which stored the bad
                    // factor removed in bf6ba51). The 0 dB reference reads without boundary cal — its
                    // top range is already calibrated by RunRangeCalibration + SET REF.
                    bool isReference = atten == _options.AttenStartDb;
                    double relDb = isReference
                        ? ReadRelativeDbWithRetry(ReferenceReadAttempts)
                        : ReadStepWithBoundaryCal(StepReadAttempts, ref boundaryCals);

                    // Capture the start-attenuation reading as the software zero reference.
                    if (atten == _options.AttenStartDb) { baselineRelDb = relDb; haveBaseline = true; }
                    double normRelDb = haveBaseline ? relDb - baselineRelDb : relDb;

                    point.MeasuredRelativeDb = normRelDb;
                    point.MeasuredAttenuationDb = -normRelDb;
                    point.ErrorDb = point.MeasuredAttenuationDb - expected;
                    consecutiveTimeouts = 0;
                }
                catch (System.Exception ex)
                {
                    // Capture the full detail — for a FormatException ex.Message holds the raw
                    // 8902A response, which is exactly what we need to diagnose. Also append
                    // the status byte so we can see RECAL/UNCAL/instrument-error bits.
                    bool isTimeout = ex.GetType().Name.IndexOf("Timeout", StringComparison.OrdinalIgnoreCase) >= 0;
                    string detail = ex is Hp8902AException ? ex.Message : (isTimeout ? "read timeout" : ex.Message);
                    int sb = -1;
                    try { sb = _receiver.PollStatusByte(); } catch { /* ignore */ }
                    point.Error = sb >= 0 ? $"{detail} [SB=0x{sb:X2}]" : detail;
                    point.MeasuredRelativeDb = double.NaN;
                    point.MeasuredAttenuationDb = double.NaN;
                    point.ErrorDb = double.NaN;
                    try { _receiver.ClearError(); } catch { /* keep going */ }

                    // Only a run of read TIMEOUTS means we've hit the receiver floor; other
                    // errors (e.g. a malformed reading) are a different fault — log and continue
                    // so the full pattern is visible rather than stopping early.
                    consecutiveTimeouts = isTimeout ? consecutiveTimeouts + 1 : 0;
                }
                result.Points.Add(point);
                onPoint?.Invoke(++index, total, point);

                if (consecutiveTimeouts >= FloorStopCount)
                {
                    result.Warning = $"stopped at {atten} dB after {consecutiveTimeouts} consecutive read " +
                                     "timeouts — below the receiver floor (deeper attenuation is unmeasurable).";
                    break;
                }
            }
            return result;
        }

        /// <summary>
        /// Test 3: measures a list of individual attenuation settings (e.g. each section/step
        /// of each attenuator on its own) relative to a 0 dB reference. Takes SET REF with all
        /// sections bypassed, normalises to that reading, then engages exactly the digits for
        /// each setting and records the measured attenuation vs the expected dB.
        /// </summary>
        public FreqPointResult MeasureSettings(double freqMHz, IReadOnlyList<AttenSetting> settings,
            System.Action<int, int, AttenPointResult> onPoint = null)
        {
            var plan = Prepare(freqMHz);
            _receiver.BeginAttenuationMeasurement(freqMHz, plan.Regime, plan.LoMHz);

            var result = new FreqPointResult
            {
                FreqMHz = freqMHz, Regime = plan.Regime, LoMHz = plan.LoMHz,
                IfMHz = plan.IfMHz, Warning = plan.Warning
            };

            // 0 dB reference (all sections bypassed) + range calibration, then normalise to it.
            RunRangeCalibration(() => _attenuator.SetEngaged(System.Array.Empty<int>()));

            bool haveBaseline = false;
            double baselineRelDb = 0.0;
            try { baselineRelDb = ReadRelativeDbWithRetry(ReferenceReadAttempts); haveBaseline = true; }
            catch { /* baseline read failed; points reported un-normalised */ }

            int index = 0, total = settings.Count;
            foreach (var s in settings)
            {
                string command = _attenuator.SetEngaged(s.Digits);
                Settle();

                var point = new AttenPointResult
                {
                    CommandedDb = s.ExpectedDb,
                    Command = command,
                    Group = s.Group,
                    ExpectedAttenuationDb = s.ExpectedDb
                };

                try
                {
                    double rel = ReadRelativeDbWithRetry(StepReadAttempts);
                    double norm = haveBaseline ? rel - baselineRelDb : rel;
                    point.MeasuredRelativeDb = norm;
                    point.MeasuredAttenuationDb = -norm;
                    point.ErrorDb = point.MeasuredAttenuationDb - s.ExpectedDb;
                }
                catch (System.Exception ex)
                {
                    bool isTimeout = ex.GetType().Name.IndexOf("Timeout", StringComparison.OrdinalIgnoreCase) >= 0;
                    string detail = ex is Hp8902AException ? ex.Message : (isTimeout ? "read timeout" : ex.Message);
                    int sb = -1;
                    try { sb = _receiver.PollStatusByte(); } catch { /* ignore */ }
                    point.Error = sb >= 0 ? $"{detail} [SB=0x{sb:X2}]" : detail;
                    point.MeasuredRelativeDb = double.NaN;
                    point.MeasuredAttenuationDb = double.NaN;
                    point.ErrorDb = double.NaN;
                    try { _receiver.ClearError(); } catch { /* keep going */ }
                }
                result.Points.Add(point);
                onPoint?.Invoke(++index, total, point);
            }

            _attenuator.SetEngaged(System.Array.Empty<int>());   // leave at 0 dB
            return result;
        }

        /// <summary>Wait after a CALIBRATE before re-reading; the cal takes a few seconds.</summary>
        private const int PostCalibrateWaitMs = 2500;

        /// <summary>
        /// Reads the relative dB robustly, up to <paramref name="maxAttempts"/> times, recovering and
        /// re-reading on a transient: an empty/garbled response (a read that raced an RF-range
        /// auto-range or Data-Ready), an UNCAL reading, or a lost-lock instrument error (Error 96).
        /// Recovery depends on the fault: Error 96 fires a VCO retune (BC) to re-acquire the signal;
        /// everything else just clears the error (CL). This variant does NOT calibrate — it is used
        /// for the 0 dB reference and the per-attenuator settings reads, whose range is already
        /// calibrated. The stepping sweep instead uses <see cref="ReadStepWithBoundaryCal"/>, which
        /// CALIBRATEs a range-to-range boundary on RECAL per the manual. Between attempts it waits the
        /// longer <see cref="TransientReadSettleMs"/> so an auto-range boundary has time to settle
        /// before the re-read. Timeouts are not caught — they propagate as a floor/comms failure.
        /// </summary>
        private double ReadRelativeDbWithRetry(int maxAttempts)
        {
            System.Exception last = null;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try { return _receiver.ReadRelativeDb(); }
                catch (Exception ex) when (ex is Hp8902AException || ex is FormatException)
                {
                    last = ex;
                    // Error 96 = the tuned receiver lost lock at a range boundary. CL alone clears the
                    // error but does NOT re-acquire the signal, so every subsequent read re-throws 96 —
                    // a whole-sweep cascade. BC (blue+CLEAR) forces a VCO retune and recaptures the
                    // signal (manual O&C 3-116), which is what lets the sweep continue past the boundary.
                    bool lostLock = (ex as Hp8902AException)?.Code == 96;
                    try { if (lostLock) _receiver.RetuneToSignal(); else _receiver.ClearError(); }
                    catch { /* keep going */ }
                    if (attempt < maxAttempts) Thread.Sleep(TransientReadSettleMs);
                }
            }
            throw last;
        }

        /// <summary>
        /// Reads one stepping-sweep point, CALIBRATEing an RF input range-to-range boundary when the
        /// receiver flags RECAL, per the manual's Attenuator Measurements procedure (O&amp;C 3-115): "If
        /// RECAL is displayed, press the CALIBRATE key and hold the signal level steady until a valid
        /// measurement is displayed." The attenuator is already set and settled, so the level is
        /// steady for the CALIBRATE. Boundary calibration is capped at
        /// <see cref="MaxBoundaryCalibrations"/> per frequency (the manual stores exactly two
        /// range-to-range factors), which also prevents a deep/weak calibrate that would store a bad
        /// factor. Error 96 (lost lock) still fires a BC retune; other transients clear and re-read.
        /// </summary>
        private double ReadStepWithBoundaryCal(int maxAttempts, ref int boundaryCals)
        {
            System.Exception last = null;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                // Honour a pending RECAL before reading — calibrate the boundary at this steady level.
                MaybeCalibrateBoundary(ref boundaryCals);
                try { return _receiver.ReadRelativeDb(); }
                catch (Exception ex) when (ex is Hp8902AException || ex is FormatException)
                {
                    last = ex;
                    var he = ex as Hp8902AException;
                    if (he != null && he.Code == 96)
                    {
                        // Lost lock: VCO retune (BC) to recapture (manual O&C 3-116).
                        try { _receiver.RetuneToSignal(); } catch { /* keep going */ }
                    }
                    else if (he != null && he.IsUncal)
                    {
                        // UNCAL reading: the manual allows a CALIBRATE only if RECAL is ALSO set ("if
                        // both the UNCAL and RECAL annunciators are lighted, the instrument can still be
                        // calibrated"). MaybeCalibrateBoundary checks RECAL, so it's a no-op on pure UNCAL.
                        MaybeCalibrateBoundary(ref boundaryCals);
                    }
                    else
                    {
                        try { _receiver.ClearError(); } catch { /* keep going */ }
                    }
                    if (attempt < maxAttempts) Thread.Sleep(TransientReadSettleMs);
                }
            }
            throw last;
        }

        /// <summary>
        /// CALIBRATEs the current RF input range-to-range boundary when the receiver is asking for it
        /// (RECAL, status bit 0x20) and we haven't already used our <see cref="MaxBoundaryCalibrations"/>
        /// budget for this frequency. Per O&amp;C 3-115 the level must be held steady during CALIBRATE and
        /// allowed to settle to a valid measurement afterwards; the caller has already set + settled the
        /// attenuator, and <see cref="PostCalibrateWaitMs"/> covers the settle. Only fires on RECAL, so
        /// a pure UNCAL (too deep/weak to calibrate) is skipped — that is where a bad factor came from.
        /// </summary>
        private void MaybeCalibrateBoundary(ref int boundaryCals)
        {
            if (!_options.RangeCalibrate || boundaryCals >= MaxBoundaryCalibrations) return;

            bool recal;
            try { recal = _receiver.RecalRequested(); } catch { return; }
            if (!recal) return;

            try
            {
                _receiver.Calibrate();                 // C1 — hold steady (attenuator already settled)
                Thread.Sleep(PostCalibrateWaitMs);     // let it settle to a valid measurement
                boundaryCals++;
            }
            catch { try { _receiver.ClearError(); } catch { /* keep going */ } }
        }

        /// <summary>
        /// Establishes the 0 dB relative reference per the manual's "Attenuator Measurements"
        /// procedure (O&amp;C 3-115): enable the RECAL status (so the sweep can poll it), CALIBRATE the
        /// reference (top) RF range at 0 dB while the signal is strong and steady, then take SET REF.
        /// SET REF latches the current reading, so a settled measurement is triggered first (with no
        /// free-run there is otherwise no live level to latch, and it stays in absolute dBm). There is
        /// NO deep pre-pass: only the ~2 range-to-range boundaries need calibrating, and the sweep
        /// does that on RECAL (see <see cref="MeasureFrequency"/>). <paramref name="setZero"/> sets
        /// 0 dB (SetAttenuationDb(0) for the sweep, SetEngaged(none) per-attenuator).
        /// </summary>
        private void RunRangeCalibration(System.Action setZero)
        {
            if (_options.RangeCalibrate)
                _receiver.EnableRecalStatus();       // pollable RECAL, settled reads (no free-run)

            setZero();
            Settle();

            if (_options.RangeCalibrate)
            {
                // CALIBRATE the reference range at 0 dB — strong, steady signal, so it succeeds and
                // gives SET REF a calibrated range to anchor to.
                try { _receiver.Calibrate(); Thread.Sleep(PostCalibrateWaitMs); }
                catch { try { _receiver.ClearError(); } catch { /* keep going */ } }
            }

            // Prime a settled measurement so SET REF has a live level to latch (manual: "wait for the
            // measurement result to be displayed", then SET REF).
            try { _receiver.ReadRelativeDb(); } catch { /* just priming a measurement for SET REF */ }
            _receiver.SetReference();
        }

        private LoPlan Prepare(double freqMHz)
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
            }
            else
            {
                _lo.RfOff();
            }
            return plan;
        }

        private void Settle()
        {
            if (_options.SettleMs > 0) Thread.Sleep(_options.SettleMs);
        }
    }
}
