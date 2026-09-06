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
        /// <summary>Read attempts for the 0 dB reference point (it anchors the whole sweep).</summary>
        private const int ReferenceReadAttempts = 5;

        /// <summary>Read attempts for each ordinary sweep point (transient empty/garbled reads at an
        /// RF-range auto-range boundary can take a few settled re-reads to clear).</summary>
        private const int StepReadAttempts = 6;

        /// <summary>Wait between read retries, ms. Longer than a normal settle so an RF-range
        /// auto-range (which briefly returns empty/garbled reads) has time to settle before re-read.</summary>
        private const int TransientReadSettleMs = 1200;

        /// <summary>Extra settle+re-trigger retries granted specifically to an empty/short read (#6) —
        /// a transient GPIB race across an auto-range boundary. These do NOT consume the main read
        /// attempts, so a cluster of glitches recovers in place instead of failing the point.</summary>
        private const int EmptyReadRetries = 5;

        /// <summary>
        /// How far below the source level the range CALIBRATE may go, in dB. CALIBRATE needs
        /// the signal still measurable; past ~this depth it raises Error 35 ("level error
        /// during calibration"). --cal-probe read cleanly to ~-90 dB; 80 keeps a margin. The
        /// receiver then measures DEEPER than this on the calibration it established.
        /// </summary>
        private const int RangeCalReachDb = 80;

        /// <summary>
        /// During the pre-SET-REF descent the read is the ABSOLUTE level (dBm), so between two steps
        /// it should fall by the attenuation added (<see cref="SweepOptions.CalStepDb"/>). A deviation
        /// larger than this (dB) means the receiver crossed into a different RF range and the reading
        /// jumped by the range factor — a diagnostic marker in the trace. (A jump only appears when the
        /// entered range is UNcalibrated; with resident factors there is no jump AND no RECAL, which is
        /// exactly the #17 no-op that <see cref="SweepOptions.ForceRangeCal"/> works around.)
        /// </summary>
        private const double RangeStepThresholdDb = 2.0;

        /// <summary>
        /// Approximate depths (dB below the 0 dB reference) at which each successive RF measurement
        /// range is entered. The Average detector's input ranges break near 0 / −15 / −50 dBm (O&amp;C
        /// 3-115), so from a ~0 dBm reference the 2nd and 3rd ranges begin ~15 and ~50 dB down; the
        /// values here sit comfortably inside each range so a forced CALIBRATE has signal to reference.
        /// Used ONLY by <see cref="SweepOptions.ForceRangeCal"/> to place one unconditional CALIBRATE
        /// per range when resident factors suppress the natural RECAL/UNCAL (#17). Approximate and
        /// bench-tunable — confirm against where RECAL actually lights on the panel.
        /// </summary>
        private static readonly int[] ForceCalDepthsDb = { 0, 20, 55 };

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

        /// <summary>
        /// Optional hook (the harness wires this on <c>--panel-review</c>): pause the run so the
        /// operator can start watching the 8902A front panel BEFORE a block of commands the engine is
        /// about to issue. Null = no pause. Used to answer questions only a human reading the panel
        /// can (which annunciator lit, etc.).
        /// </summary>
        public static Action<string> PanelWatch;

        /// <summary>
        /// Optional hook (harness-wired on <c>--panel-review</c>): AFTER a block of commands, ask the
        /// operator what the front panel showed and return their typed answer. Null = skipped.
        /// </summary>
        public static Func<string, string> PanelReview;

        /// <summary>
        /// Optional engine diagnostic sink (the harness wires this on <c>--debug</c>): the step-by-step
        /// range-calibration trace and the loud NO-OP warning that makes issue #17 visible — i.e. that a
        /// descent fired zero CALIBRATEs and the RF ranges are riding resident factors. Null = silent.
        /// </summary>
        public static Action<string> Trace;

        /// <summary>
        /// Optional hook invoked ONCE per frequency, after the reference/levelling/calibration setup is
        /// complete and immediately before the first attenuator step. Lets an attended bench run hold
        /// until the operator is actually watching the front panel, so they only have to watch the part
        /// that matters — the stepping — rather than the whole setup. Null = no hold.
        /// </summary>
        public static Action BeforeStepping;

        /// <summary>
        /// Optional hook invoked before EACH attenuation point, with the attenuation about to be set.
        /// Lets an attended run hold at a chosen depth, so the operator watches only the region of
        /// interest instead of the whole sweep. Null = no hold.
        /// </summary>
        public static Action<int> BeforeStep;

        /// <summary>Wall-clock attribution for the most recent <see cref="MeasureFrequency"/> (issue #2).
        /// The harness aggregates it across frequencies and prints the breakdown under <c>--profile</c>.
        /// Null until the first frequency is measured.</summary>
        public SweepTiming Timing { get; private set; }

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
            _receiver.BeginAttenuationMeasurement(freqMHz, plan.Regime, plan.LoMHz, _options.Detector, _options.TrackMode, _options.Tuning, _options.NoiseCorrection);
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
            System.Action<int, int, AttenPointResult> onPoint = null,
            System.Action<int, int, double, double> onReading = null)
        {
            int total = 0;   // set once the attenuation plan is known (it can depend on the reference, #23)
            int index = 0;
            Timing = new SweepTiming();                         // #2: attribute this frequency's wall-clock
            var wall = System.Diagnostics.Stopwatch.StartNew();
            var plan = Prepare(freqMHz);
            _receiver.BeginAttenuationMeasurement(freqMHz, plan.Regime, plan.LoMHz, _options.Detector, _options.TrackMode, _options.Tuning, _options.NoiseCorrection);

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
            Timing.Time(SweepTiming.RangeCal,
                () => RunRangeCalibration(() => _attenuator.SetAttenuationDb(_options.AttenStartDb), result));

            // #21: the measurable level window of the path actually in use, from the instrument specs
            // (see LevelLimits) — 11793A converted floors at −100 dBm; the 8902A direct path floors at
            // −100 dBm on the IF average detector and −127 dBm on the synchronous one. With the achieved
            // reference known, any target deeper than (reference − floor) lands below the floor, where
            // the receiver cannot track the signal at all: it under-reads and raises a real Error 01
            // (signal out of IF range). Those points are skipped rather than measured and failed.
            result.LevelWindow = _options.LevelWindowFor(plan.Regime);
            double pathFloorDbm = _options.EffectiveFloorDbm(plan.Regime);
            double referenceDbm = result.ReferencePowerDbm;   // NaN when leveling is off / unreadable
            bool enforceLimits = _options.EnforceLevelLimits && !double.IsNaN(referenceDbm);
            if (_options.EnforceLevelLimits && double.IsNaN(referenceDbm))
                Trace?.Invoke("level-limits: reference level unknown — cannot predict per-point levels, " +
                              "limits NOT enforced (#21); falling back to post-hoc floor detection (#13).");

            // The 8902A SET REF can leave a small residual offset, so we also normalise in
            // software: the reading at the start attenuation defines 0 dB and every reading
            // is reported relative to it (substitution method, attenuation = reading0 −
            // reading_i). This guarantees the first point is exactly 0 dB — only the
            // attenuation shows, with no path-loss / reference offset.
            bool haveBaseline = false;
            double baselineRelDb = 0.0;
            int boundaryCals = 0;   // range-to-range CALIBRATEs done this frequency (cap: MaxBoundaryCalibrations)

            List<int> attenPlan = BuildAttenuationPlan(referenceDbm, pathFloorDbm);
            total = attenPlan.Count;

            // Setup is finished; everything after this point is the measurement itself.
            BeforeStepping?.Invoke();

            foreach (int atten in attenPlan)
            {
                double expected = atten - _options.AttenStartDb;
                var point = new AttenPointResult
                {
                    CommandedDb = atten,
                    ExpectedAttenuationDb = expected
                };

                BeforeStep?.Invoke(atten);

                // #21: skip a point whose level would fall outside the path's measurable window. Not a
                // failure and not a measurement — the hardware simply cannot report it, so record what
                // it would have been and move on without commanding the attenuator or reading.
                if (enforceLimits)
                {
                    double predicted = referenceDbm - expected;
                    if (predicted < pathFloorDbm)
                    {
                        point.OutOfRange = true;
                        point.PredictedLevelDbm = predicted;
                        point.MeasuredRelativeDb = double.NaN;
                        point.MeasuredAttenuationDb = double.NaN;
                        point.ErrorDb = double.NaN;
                        Trace?.Invoke($"level-limits: skip {atten} dB — predicted {predicted:0.0} dBm is " +
                                      $"below the {result.LevelWindow.PathName} floor {pathFloorDbm:0.0} dBm (#21).");
                        result.Points.Add(point);
                        onPoint?.Invoke(++index, total, point);
                        continue;
                    }
                }

                try
                {
                    // Setting the attenuator is a GPIB write, so it must be INSIDE the try: if the
                    // previous 8902A measurement cycle is still holding the bus (a read timed out
                    // mid-cycle — its handshake is inhibited until the cycle completes, O&C 3-22), this
                    // write itself times out. Left outside the try, that IOTimeoutException is unhandled
                    // and crashes the whole harness (issue #11).
                    point.Command = Timing.Time(SweepTiming.AttenSet, () => _attenuator.SetAttenuationDb(atten));
                    Timing.Time(SweepTiming.Settle, () => Settle());

                    // Per the manual's Attenuator Measurements procedure (O&C 3-115), the stepping
                    // points CALIBRATE each RF input range-to-range boundary the first time RECAL
                    // appears (ReadStepWithBoundaryCal); the 0 dB reference reads without boundary cal
                    // (its top range is calibrated by RunRangeCalibration + SET REF). The reference also
                    // gets extra read attempts since it anchors every other point.
                    bool isReference = atten == _options.AttenStartDb;
                    // (ref boundaryCals can't be captured by a lambda, so time this read explicitly.)
                    var swRead = System.Diagnostics.Stopwatch.StartNew();
                    double relDb = 0;
                    int repeats = _options.RepeatsPerPoint < 1 ? 1 : _options.RepeatsPerPoint;
                    for (int rep = 0; rep < repeats; rep++)
                    {
                        // Only the FIRST reading of the reference point may re-baseline / boundary-cal;
                        // the extra repeats are plain re-reads at the same setting, so the spread they
                        // measure is the measurement's own repeatability and nothing else.
                        relDb = isReference
                            ? ReadRelativeDbWithRetry(ReferenceReadAttempts)
                            : ReadStepWithBoundaryCal(StepReadAttempts, ref boundaryCals);
                        point.Repeats.Add(relDb);
                        onReading?.Invoke(atten, rep, relDb, referenceDbm);
                    }
                    if (point.Repeats.Count > 1) point.StdDevDb = StdDev(point.Repeats);
                    relDb = Mean(point.Repeats);      // the point's value is the mean of its repeats
                    swRead.Stop();
                    Timing.Add(SweepTiming.Read, swRead.ElapsedMilliseconds);

                    // Capture the start-attenuation reading as the software zero reference.
                    if (atten == _options.AttenStartDb) { baselineRelDb = relDb; haveBaseline = true; }
                    double normRelDb = haveBaseline ? relDb - baselineRelDb : relDb;

                    point.MeasuredRelativeDb = normRelDb;
                    point.MeasuredAttenuationDb = -normRelDb;
                    point.ErrorDb = point.MeasuredAttenuationDb - expected;
                }
                catch (System.Exception ex)
                {
                    // Capture the full detail — for a FormatException ex.Message holds the raw 8902A
                    // response; also append the status byte (RECAL/UNCAL/instrument-error bits).
                    bool isTimeout = ex.GetType().Name.IndexOf("Timeout", StringComparison.OrdinalIgnoreCase) >= 0;
                    string detail = ex is Hp8902AException ? ex.Message : (isTimeout ? "read timeout" : ex.Message);
                    int sb = -1;
                    try { sb = _receiver.PollStatusByte(); } catch { /* ignore */ }
                    point.Error = sb >= 0 ? $"{detail} [SB=0x{sb:X2}]" : detail;
                    point.MeasuredRelativeDb = double.NaN;
                    point.MeasuredAttenuationDb = double.NaN;
                    point.ErrorDb = double.NaN;

                    if (isTimeout)
                    {
                        // A GPIB timeout means the 8902A hung the bus mid measurement-cycle (its
                        // handshake is inhibited until the cycle completes — O&C 3-22). Release the bus
                        // with a device clear so the next instrument write can't collide (which was
                        // crashing the harness, #11), then end this frequency: a read timeout is the
                        // floor / unrecoverable at the fixed timeout, and the device clear drops the
                        // relative reference, so continuing would only log garbage. Waiting for
                        // measurement completion instead of a blind timeout is issue #10.
                        try { _receiver.ReleaseBus(); } catch { /* best effort */ }

                        // DIAGNOSTIC (#9 vs #10): the bus is now free but we don't know WHY the read
                        // hung. Re-establish the context and do an M5 RF-frequency read — the counter
                        // sees a signal at lower levels than a settled Tuned RF Level, so it tells us
                        // whether the receiver still has the signal (level just wouldn't settle → a
                        // re-range / #10 completion problem) or lost it entirely (Error 96 → lost lock).
                        string probe = ProbeSignalAfterHang(freqMHz, plan);
                        point.Error = $"{point.Error} | {probe}";
                        result.Points.Add(point);
                        onPoint?.Invoke(++index, total, point);
                        result.Warning = $"stopped at {atten} dB — the 8902A read timed out and held the " +
                                         $"GPIB bus; released it and ended the sweep. {probe}";
                        break;
                    }

                    // A non-timeout error (e.g. a malformed reading) is a different fault — clear it and
                    // carry on so the full pattern is visible rather than stopping early.
                    try { _receiver.ClearError(); } catch { /* keep going */ }
                }
                result.Points.Add(point);
                onPoint?.Invoke(++index, total, point);
            }

            ClassifyFloorLimited(result);
            ComputeStepDeltas(result);

            // Attribute whatever wall-clock wasn't caught by a category (Prepare, Begin…, post-hang
            // probe, overhead) so the --profile breakdown sums to the real elapsed time (#2).
            wall.Stop();
            long other = wall.ElapsedMilliseconds - Timing.TotalMs;
            if (other > 0) Timing.Add("setup/other", other, 0);
            return result;
        }

        /// <summary>
        /// #25 — the system noise floor at this frequency: the level the receiver reports with the
        /// source RF switched OFF, in the exact detector / noise-correction configuration about to be
        /// used. This is the number that says whether a deep reading is a real measurement or the
        /// measurement system listening to itself. Returns NaN when the receiver cannot read a level at
        /// all with no signal present (Error 96) — which is itself the good answer: the floor is below
        /// anything it can report. Always restores RF on.
        /// </summary>
        public double MeasureNoiseFloorDbm(double freqMHz)
        {
            var plan = Prepare(freqMHz);
            _receiver.BeginAttenuationMeasurement(freqMHz, plan.Regime, plan.LoMHz,
                _options.Detector, _options.TrackMode, _options.Tuning, _options.NoiseCorrection);
            _attenuator.SetAttenuationDb(_options.AttenStartDb);
            try
            {
                _source.RfOff();
                Settle();
                Thread.Sleep(1000);          // let the receiver settle on an absent signal
                return _receiver.ReadTunedLevelDbm();
            }
            catch (Exception ex) when (ex is Hp8902AException || ex is FormatException)
            {
                try { _receiver.ClearError(); } catch { /* best effort */ }
                return double.NaN;
            }
            finally
            {
                _source.RfOn();
                Settle();
            }
        }

        /// <summary>
        /// Recovery for a corrupt Tuned RF Level FIRST calibration factor — the factor that maps the
        /// detector reading to absolute dBm. A CALIBRATE performed at a level the detector cannot
        /// reference stores a bad one, and it survives an instrument preset, a sensor re-calibration and
        /// (on early firmware) SF 39.9, because none of those rewrite it. The manual's mechanism is the
        /// way back: "the first calibration factor will be different depending on the detector used when
        /// CALIBRATE is selected the first time" — so one CALIBRATE at full signal rewrites it.
        ///
        /// Deliberately does ONE calibration at 0 dB attenuation with the source at full commanded power
        /// (no levelling, no descent): a strong, steady signal is the condition under which a CALIBRATE
        /// is safe. It is the deep, weak forced calibrations that store bad factors in the first place.
        /// Returns the absolute level read after calibrating; <paramref name="before"/> gets the level
        /// beforehand, so the caller can show the correction.
        /// </summary>
        public double RecalibrateTrflFirstFactor(double freqMHz, out double before)
        {
            var plan = Prepare(freqMHz);          // sets source power + LO, tunes the chain
            _receiver.BeginAttenuationMeasurement(freqMHz, plan.Regime, plan.LoMHz,
                _options.Detector, false, _options.Tuning, false);   // noise correction OFF
            _attenuator.SetAttenuationDb(0);
            Settle();

            before = SafeReadLevel();
            _receiver.Calibrate();                // rewrites the first calibration factor
            return SafeReadLevel();
        }

        private double SafeReadLevel()
        {
            try { return _receiver.ReadTunedLevelDbm(); }
            catch (Exception ex) when (ex is Hp8902AException || ex is FormatException)
            {
                try { _receiver.ClearError(); } catch { /* best effort */ }
                return double.NaN;
            }
        }

        /// <summary>
        /// Characterizes the source through the measurement chain at 0 dB attenuation: counted
        /// frequency, absolute level by both the sensor and Tuned RF Level paths, residual AM and FM,
        /// and level stability over repeated reads. The 8340B is never touched between runs, so any
        /// instability in it is a common-mode error that shows up as attenuator error.
        ///
        /// The residual FM number is the one that matters most: above ~50 Hz peak the synchronous
        /// detector cannot hold lock, which is what has confined us to the average detector and its
        /// higher noise floor.
        /// </summary>
        public SourceCheckResult CheckSource(double freqMHz, int stabilityReads)
        {
            var plan = Prepare(freqMHz);
            _attenuator.SetAttenuationDb(0);
            Settle();

            var r = new SourceCheckResult
            {
                FreqMHz = freqMHz, Regime = plan.Regime, LoMHz = plan.LoMHz, IfMHz = plan.IfMHz,
                CommandedPowerDbm = _options.SourcePowerDbm, Warning = plan.Warning
            };

            // Sensor path first — independent of the Tuned RF Level calibration chain, so the two
            // together also cross-check that the TRFL scale is sane.
            try
            {
                _receiver.BeginRfPowerMeasurement(freqMHz, plan.Regime, plan.LoMHz);
                Settle();
                r.RfPowerDbm = _receiver.ReadRfPowerDbm();
            }
            catch (Exception ex) when (ex is Hp8902AException || ex is FormatException)
            { try { _receiver.ClearError(); } catch { } }

            // Each of M5 / M1 / M2 leaves Tuned RF Level, so re-enter it before the next reading.
            r.MeasuredFreqMHz = TrflThen(freqMHz, plan, () => _receiver.ReadSignalFrequencyMHz());
            r.AmDepthPercent  = TrflThen(freqMHz, plan, () => _receiver.ReadAmDepthPercent());
            r.FmDeviationHz   = TrflThen(freqMHz, plan, () => _receiver.ReadFmDeviationHz());

            EnterTrfl(freqMHz, plan);
            for (int i = 0; i < System.Math.Max(1, stabilityReads); i++)
            {
                double v = SafeReadLevel();
                if (!double.IsNaN(v)) r.LevelSamples.Add(v);
            }
            if (r.LevelSamples.Count > 0)
            {
                r.LevelMeanDbm = Mean(r.LevelSamples);
                r.TunedLevelDbm = r.LevelMeanDbm;
                double min = double.MaxValue, max = double.MinValue;
                foreach (double v in r.LevelSamples) { if (v < min) min = v; if (v > max) max = v; }
                r.LevelSpanDb = max - min;
                r.LevelDriftDb = r.LevelSamples[r.LevelSamples.Count - 1] - r.LevelSamples[0];
            }
            if (r.LevelSamples.Count > 1) r.LevelSdDb = StdDev(r.LevelSamples);
            return r;
        }

        private void EnterTrfl(double freqMHz, LoPlan plan)
        {
            _receiver.BeginAttenuationMeasurement(freqMHz, plan.Regime, plan.LoMHz,
                _options.Detector, false, _options.Tuning, false);
            Settle();
        }

        /// <summary>Re-enters Tuned RF Level, then takes one reading that itself switches measurement
        /// mode (M5/M1/M2). NaN if the reading fails.</summary>
        private double TrflThen(double freqMHz, LoPlan plan, Func<double> read)
        {
            try
            {
                EnterTrfl(freqMHz, plan);
                return read();
            }
            catch (Exception ex) when (ex is Hp8902AException || ex is FormatException)
            {
                try { _receiver.ClearError(); } catch { }
                return double.NaN;
            }
        }

        private static double Mean(System.Collections.Generic.List<double> v)
        {
            double t = 0;
            foreach (double x in v) t += x;
            return v.Count == 0 ? double.NaN : t / v.Count;
        }

        /// <summary>Sample standard deviation (n-1) — the point's measurement repeatability (#25).</summary>
        private static double StdDev(System.Collections.Generic.List<double> v)
        {
            if (v.Count < 2) return double.NaN;
            double m = Mean(v), sum = 0;
            foreach (double x in v) sum += (x - m) * (x - m);
            return System.Math.Sqrt(sum / (v.Count - 1));
        }

        /// <summary>
        /// #24 — works out what each individual attenuator step actually did to the signal, by
        /// differencing consecutive measured points. The cumulative error column cannot answer that:
        /// it carries every earlier step's contribution, so one bad section makes every later point
        /// look wrong. The per-step delta isolates each transition, which is what tells you whether a
        /// given step applies its nominal value.
        ///
        /// Differences are taken against the previous point that actually produced a reading, so a
        /// skipped (#21) or floored (#13) point doesn't corrupt the chain — the nominal is taken from
        /// the same pair, so the comparison stays honest across a gap in the plan (e.g. 80 → 90 dB).
        /// </summary>
        private static void ComputeStepDeltas(FreqPointResult result)
        {
            bool havePrev = false;
            double prevMeasured = 0, prevCommanded = 0;

            foreach (var p in result.Points)
            {
                // Excluded covers skipped (#21), floor-saturated (#13), errored and unreadable points.
                // A floor-limited reading is a real number but not a real measurement — including it
                // would report the saturation as though it were a step's own error.
                if (p.Excluded) continue;

                if (havePrev)
                {
                    p.StepDeltaDb = p.MeasuredAttenuationDb - prevMeasured;
                    p.NominalStepDb = p.CommandedDb - prevCommanded;
                    p.StepErrorDb = p.StepDeltaDb - p.NominalStepDb;
                }
                prevMeasured = p.MeasuredAttenuationDb;
                prevCommanded = p.CommandedDb;
                havePrev = true;
            }
        }

        /// <summary>
        /// #23 — chooses the attenuation points for one frequency. <see cref="AttenStepPlan.Uniform"/>
        /// returns the fixed grid. <see cref="AttenStepPlan.Adaptive"/> builds a plan around what the
        /// hardware can actually do here, given the reference the leveller achieved and the path's
        /// measurable floor:
        ///
        ///   1. every step of the FINE attenuator, 1 dB at a time (the 8494's full 0–11 dB), so each of
        ///      its sections is characterized individually;
        ///   2. the coarse 10 dB ladder onward, to within <see cref="SweepOptions.FloorApproachDb"/> of
        ///      the deepest measurable point;
        ///   3. 1 dB steps from there in to that limit, so the approach to the floor — where accuracy
        ///      rolls off — is sampled densely rather than jumped over.
        ///
        /// The limit is (reference − floor), so a better reference automatically buys more depth. Points
        /// are ascending and deduplicated; step 2 stays on the original coarse grid (0, 10, 20 …) and so
        /// naturally skips the 10 dB point already covered by step 1. Falls back to the fixed grid when
        /// the reference is unknown, since without it there is no limit to plan against.
        /// </summary>
        private List<int> BuildAttenuationPlan(double referenceDbm, double floorDbm)
        {
            if (_options.StepPlan != AttenStepPlan.Adaptive || double.IsNaN(referenceDbm))
                return new List<int>(_options.AttenuationSteps());

            // Deepest point whose level still sits at/above the floor. Floor of the real value so we
            // never plan a point the #21 gate would then refuse.
            int limit = (int)System.Math.Floor(referenceDbm - floorDbm);
            if (limit > _options.AttenStopDb) limit = _options.AttenStopDb;

            var points = new List<int>();
            int last = int.MinValue;
            System.Action<int> add = a =>
            {
                if (a > last && a >= _options.AttenStartDb && a <= limit) { points.Add(a); last = a; }
            };

            // 1. Every step of the fine attenuator.
            int head = System.Math.Min(_options.FineAttenuatorMaxDb, limit);
            for (int a = _options.AttenStartDb; a <= head; a++) add(a);

            // 2. Coarse ladder to within FloorApproachDb of the limit (original grid alignment).
            int coarseEnd = limit - _options.FloorApproachDb;
            if (_options.AttenStepDb > 0)
                for (int a = _options.AttenStartDb; a <= coarseEnd; a += _options.AttenStepDb) add(a);

            // 3. 1 dB approach to the limit.
            for (int a = System.Math.Max(coarseEnd + 1, _options.AttenStartDb); a <= limit; a++) add(a);

            Trace?.Invoke($"step-plan (#23): {points.Count} points — 1 dB to {head} dB (fine attenuator), " +
                          $"{_options.AttenStepDb} dB ladder to {coarseEnd} dB, then 1 dB in to {limit} dB " +
                          $"(reference {referenceDbm:0.00} dBm − floor {floorDbm:0.0} dBm).");
            return points;
        }

        /// <summary>
        /// #13 — flags deep sweep points that saturated at the converter floor. Past the 11793A path
        /// floor (~−100 dBm; with the reference near −2 dBm the usable depth is ~95–98 dB) the reading
        /// stops tracking: a 100/110 dB point reads the floor (~−98 dBm) and so UNDER-reads its target by
        /// a growing amount (the −2.4 / −12 dB "errors"). Those are the measurement floor, not a DUT or
        /// sweep fault, so they're marked <see cref="AttenPointResult.FloorLimited"/> and excluded from
        /// the accuracy verdict instead of failing it. A point is flagged only when it UNDER-reads its
        /// target by more than <see cref="SweepOptions.FloorMarginDb"/> AND either (a) its absolute level
        /// (reference + relative) sits at/below the effective floor (<see cref="SweepOptions.EffectiveFloorDbm"/>),
        /// or (b) it plateaued —
        /// its reading didn't rise past the deepest attenuation genuinely tracked so far. The AND with
        /// under-reading keeps an accurate deep point near the floor from being mistakenly flagged.
        /// </summary>
        private void ClassifyFloorLimited(FreqPointResult result)
        {
            if (!_options.FloorDetect) return;

            double floorDbm = _options.EffectiveFloorDbm(result.Regime);   // #21: spec limit, or --floor-dbm
            double margin = _options.FloorMarginDb;
            double refDbm = result.ReferencePowerDbm;    // NaN when leveling was off / unread
            double bestAtten = double.NegativeInfinity;   // deepest attenuation genuinely tracked so far

            foreach (var p in result.Points)              // Points are in ascending target order
            {
                if (p.OutOfRange || p.Error != null || double.IsNaN(p.MeasuredAttenuationDb)) continue;

                bool underReads = p.MeasuredAttenuationDb < p.ExpectedAttenuationDb - margin;
                bool atFloorAbs = !double.IsNaN(refDbm)
                                  && (refDbm + p.MeasuredRelativeDb) <= floorDbm + margin;
                bool plateau = p.MeasuredAttenuationDb <= bestAtten + margin;   // didn't rise meaningfully

                p.FloorLimited = underReads && (atFloorAbs || plateau);

                if (!p.FloorLimited)
                    bestAtten = System.Math.Max(bestAtten, p.MeasuredAttenuationDb);
            }
        }

        /// <summary>
        /// Diagnostic probe (issues #9/#10): after a read timed out and <see cref="IMeasuringReceiver.ReleaseBus"/>
        /// reset the receiver, re-establish the measurement context and do an M5 RF-frequency read as a
        /// signal-presence check at the failing attenuation. The frequency counter detects a signal at
        /// lower levels than a settled Tuned RF Level, so this separates two very different faults:
        /// "signal PRESENT — the level measurement just wouldn't settle / re-ranged" (points at #10, the
        /// measurement-completion handshake) versus "signal LOST (Error 96) — the receiver lost lock"
        /// (points at needing the boundary CALIBRATE / lock-holding). Returns a short human string for
        /// the point's error text; never throws (best-effort diagnostic).
        /// </summary>
        private string ProbeSignalAfterHang(double freqMHz, LoPlan plan)
        {
            try
            {
                _receiver.BeginAttenuationMeasurement(freqMHz, plan.Regime, plan.LoMHz, _options.Detector, _options.TrackMode, _options.Tuning, _options.NoiseCorrection);
                Settle();
                double f = _receiver.ReadSignalFrequencyMHz();
                double tolMHz = System.Math.Max(1.0, freqMHz * 0.001);
                return System.Math.Abs(f - freqMHz) <= tolMHz
                    ? $"post-hang probe: signal PRESENT (M5={f:F3} MHz) — receiver still sees the signal, " +
                      "the level measurement wouldn't settle (re-range / #10)"
                    : $"post-hang probe: signal at {f:F3} MHz vs expected {freqMHz:F0} MHz — off-frequency/mistuned";
            }
            catch (Hp8902AException ex)
            {
                return ex.Code == 96
                    ? "post-hang probe: signal LOST (Error 96) — receiver lost lock at this level"
                    : $"post-hang probe: {ex.Message}";
            }
            catch (System.Exception ex)
            {
                return "post-hang probe failed: " + ex.GetType().Name;
            }
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
            _receiver.BeginAttenuationMeasurement(freqMHz, plan.Regime, plan.LoMHz, _options.Detector, _options.TrackMode, _options.Tuning, _options.NoiseCorrection);

            var result = new FreqPointResult
            {
                FreqMHz = freqMHz, Regime = plan.Regime, LoMHz = plan.LoMHz,
                IfMHz = plan.IfMHz, Warning = plan.Warning
            };

            // 0 dB reference (all sections bypassed) + range calibration, then normalise to it.
            RunRangeCalibration(() => _attenuator.SetEngaged(System.Array.Empty<int>()), result);

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
            int empties = 0;
            for (int attempt = 1; attempt <= maxAttempts; )
            {
                try { return _receiver.ReadRelativeDb(); }
                catch (Exception ex) when (ex is Hp8902AException || ex is FormatException)
                {
                    last = ex;
                    var he = ex as Hp8902AException;

                    // Empty/short read (#6): a transient GPIB race (read raced Data Ready / an auto-range),
                    // not bad data. Settle + re-trigger on its OWN budget so a cluster of glitches across a
                    // range boundary doesn't burn the real attempts. No CL — it isn't an error condition.
                    if (he != null && he.IsEmpty && empties < EmptyReadRetries)
                    {
                        empties++;
                        Trace?.Invoke($"empty/short read — transient (retry {empties}/{EmptyReadRetries}); settle {TransientReadSettleMs} ms + re-trigger");
                        Thread.Sleep(TransientReadSettleMs);
                        continue;                           // does NOT consume a main attempt
                    }

                    // Error 96 = the tuned receiver lost lock at a range boundary. CL alone clears the
                    // error but does NOT re-acquire the signal, so every subsequent read re-throws 96 —
                    // a whole-sweep cascade. BC (blue+CLEAR) forces a VCO retune and recaptures the
                    // signal (manual O&C 3-116), which is what lets the sweep continue past the boundary.
                    bool lostLock = he?.Code == 96;
                    try { if (lostLock) _receiver.RetuneToSignal(); else _receiver.ClearError(); }
                    catch { /* keep going */ }
                    if (attempt < maxAttempts) Thread.Sleep(TransientReadSettleMs);
                    attempt++;
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
            int empties = 0;
            for (int attempt = 1; attempt <= maxAttempts; )
            {
                // Honour a pending RECAL before reading — calibrate the boundary at this steady level.
                MaybeCalibrateBoundary(ref boundaryCals);
                // ReadRelativeDb uses the completion handshake (trigger → wait for Data Ready → read),
                // which is what surfaces the RECAL below and reads deep points the old blocking read
                // couldn't (issue #10/#12).
                try { return _receiver.ReadRelativeDb(); }
                catch (Exception ex) when (ex is Hp8902AException || ex is FormatException)
                {
                    last = ex;
                    var he = ex as Hp8902AException;

                    // Empty/short read (#6): transient GPIB race, not bad data. Settle + re-trigger on its
                    // own budget (no CL, no calibrate) so a cluster across an auto-range boundary doesn't
                    // burn the real attempts — the exact 15/16 dB double-empty this issue reported.
                    if (he != null && he.IsEmpty && empties < EmptyReadRetries)
                    {
                        empties++;
                        Trace?.Invoke($"empty/short read at step — transient (retry {empties}/{EmptyReadRetries}); settle {TransientReadSettleMs} ms + re-trigger");
                        Thread.Sleep(TransientReadSettleMs);
                        continue;                           // does NOT consume a main attempt
                    }

                    if (he != null && he.Code == 96)
                    {
                        // Lost lock: VCO retune (BC) to recapture (manual O&C 3-116).
                        try { _receiver.RetuneToSignal(); } catch { /* keep going */ }
                    }
                    else if (he != null && he.IsUncal)
                    {
                        // The read reported UNCAL — RECAL (0x20) was set at THIS level (the polled read
                        // saw it in the post-trigger status byte, SB=0x61). Calibrate this boundary
                        // DIRECTLY per the manual ("If RECAL is displayed, press CALIBRATE and hold the
                        // level steady"), respecting the 2-per-frequency cap. Do NOT re-poll
                        // RecalRequested() here: 0x20 only shows transiently during the measurement, so a
                        // fresh pre-read poll misses it — that is exactly why #9's pre-read trigger never
                        // fired. The attenuator is already set + settled, so the level is steady for C1.
                        if (_options.RangeCalibrate && boundaryCals < MaxBoundaryCalibrations)
                        {
                            try { _receiver.Calibrate(); boundaryCals++; }   // Calibrate() waits + surfaces a cal error (#8)
                            catch { try { _receiver.ClearError(); } catch { /* keep going */ } }
                        }
                    }
                    else
                    {
                        try { _receiver.ClearError(); } catch { /* keep going */ }
                    }
                    if (attempt < maxAttempts) Thread.Sleep(TransientReadSettleMs);
                    attempt++;
                }
            }
            throw last;
        }

        /// <summary>
        /// CALIBRATEs the current RF input range-to-range boundary when the receiver is asking for it
        /// (RECAL, status bit 0x20) and we haven't already used our <see cref="MaxBoundaryCalibrations"/>
        /// budget for this frequency. Per O&amp;C 3-115 the level must be held steady during CALIBRATE and
        /// allowed to settle to a valid measurement afterwards; the caller has already set + settled the
        /// attenuator, and <see cref="IMeasuringReceiver.Calibrate"/> waits for completion (and now
        /// surfaces a raised cal error, #8). Only fires on RECAL, so a pure UNCAL (too deep/weak to
        /// calibrate) is skipped — that is where a bad factor came from.
        /// </summary>
        private void MaybeCalibrateBoundary(ref int boundaryCals)
        {
            if (!_options.RangeCalibrate || boundaryCals >= MaxBoundaryCalibrations) return;

            bool recal;
            try { recal = _receiver.RecalRequested(); } catch { return; }
            if (!recal) return;

            try
            {
                _receiver.Calibrate();                 // C1 — waits for completion + surfaces a cal error (#8)
                boundaryCals++;
            }
            catch { try { _receiver.ClearError(); } catch { /* keep going */ } }
        }

        /// <summary>
        /// Establishes the 0 dB relative reference per the manual's TRFL Calibration + Attenuator
        /// Measurement procedure (O&amp;C Table 4-1 / Chapter 5 + Microwave Product Note): enable the
        /// RECAL status, adaptively level the reference (#16), then — BEFORE taking SET REF —
        /// **calibrate the three RF measurement ranges** by stepping the signal down and pressing
        /// CALIBRATE each time RECAL lights (<see cref="CalibrateRfRanges"/>), return to the 0 dB
        /// reference, and take SET REF. The three-range calibration is a *prerequisite* done as a
        /// dedicated pass here, NOT during the measurement sweep: the 8902A only flags RECAL for an
        /// uncalibrated range while descending in the fresh (pre-SET REF) state, so calibrating only
        /// Range 1 at 0 dB and hoping RECAL re-fires mid-sweep leaves Range 2/3 on stale factors — the
        /// deep positive drift seen in the #14 sync run. <paramref name="setZero"/> sets 0 dB
        /// (SetAttenuationDb(0) for the sweep, SetEngaged(none) per-attenuator).
        /// </summary>
        private void RunRangeCalibration(System.Action setZero, FreqPointResult result = null)
        {
            if (_options.RangeCalibrate)
                _receiver.EnableRecalStatus();       // pollable RECAL, settled reads (no free-run)

            setZero();
            Settle();

            // Adaptive reference leveling (#16): with the attenuator at 0 dB and the receiver in Tuned
            // RF Level (pre-SET REF, so the read is the ABSOLUTE level), nudge the source so the
            // reference lands just under the 8902A's 0 dBm ceiling. Done BEFORE the range calibration
            // and SET REF below so both anchor at the leveled reference.
            if (_options.AdaptiveLevel)
                LevelReference(result);

            if (_options.RangeCalibrate)
            {
                // Calibrate all three RF ranges by stepping DOWN and CALIBRATEing on each RECAL, then
                // come back to the 0 dB reference for SET REF (manual TRFL Calibration, Table 4-1). The
                // --panel-review prompts are wrapped tightly around each CALIBRATE inside the descent
                // (see CalibrateRfRanges), not around the whole multi-step descent.
                CalibrateRfRanges();
                setZero();
                Settle();
            }

            // Prime a settled measurement so SET REF has a live level to latch (manual: "wait for the
            // measurement result to be displayed", then SET REF).
            try { _receiver.ReadRelativeDb(); } catch { /* just priming a measurement for SET REF */ }
            _receiver.SetReference();
        }

        /// <summary>Range-to-range CALIBRATEs done in the pre-SET-REF pass. The 8902A has three RF
        /// measurement ranges, so RECAL lights at most three times (once per range) on the way down —
        /// O&amp;C Table 4-1, "RECAL will only appear three times for each frequency (once for each
        /// measurement range)".</summary>
        private const int MaxRfRangeCalibrations = 3;

        /// <summary>
        /// Calibrates the 8902A's three RF measurement ranges the way the manual prescribes (O&amp;C
        /// Table 4-1 / Microwave Product Note "Low Level ... Measurements"): step the signal DOWN from
        /// the 0 dB reference in <see cref="SweepOptions.CalStepDb"/> (≤10 dB) increments and, whenever
        /// the receiver flags RECAL/UNCAL, press CALIBRATE — holding the level steady (the attenuator
        /// is set + settled before each CALIBRATE, else Error 35). RECAL is surfaced by the
        /// completion-handshake read (a bare serial poll misses it — it only shows in the post-trigger
        /// status, #9). Capped at <see cref="MaxRfRangeCalibrations"/> and <see cref="RangeCalReachDb"/>
        /// so it never calibrates deeper than the signal can reference (a deep/weak CALIBRATE stores a
        /// bad factor). Stops early on lost lock (Error 96) — that is past the ~-100 dBm converter
        /// floor. Leaves the attenuator deep; the caller returns it to 0 dB for SET REF.
        ///
        /// <para>Observability (#17): every step is traced via <see cref="Trace"/> (commanded depth,
        /// absolute read, off-trend jump, and whether a CALIBRATE fired), and the pass ends with an
        /// explicit summary — a loud NO-OP warning when zero CALIBRATEs fired, which is the #17 symptom
        /// (resident range factors suppress the natural RECAL/UNCAL, so the descent silently rides stale
        /// factors). <see cref="SweepOptions.ForceRangeCal"/> works around that by issuing one
        /// UNCONDITIONAL CALIBRATE per range at the <see cref="ForceCalDepthsDb"/> boundaries, since with
        /// resident factors neither UNCAL nor an off-trend jump ever appears to gate on.</para>
        /// </summary>
        private void CalibrateRfRanges()
        {
            int cals = 0;
            int start = _options.AttenStartDb;
            int forceIdx = 0;              // next ForceCalDepthsDb boundary awaiting a forced CALIBRATE
            double prev = double.NaN;      // previous absolute read, for the off-trend jump marker
            bool force = _options.ForceRangeCal;

            Trace?.Invoke($"range-cal descent: start={start} dB, reach={RangeCalReachDb} dB, step={_options.CalStepDb} dB — " +
                          (force ? "FORCE (one unconditional CALIBRATE per range)" : "UNCAL-gated (natural RECAL only)"));

            for (int db = start; db <= start + RangeCalReachDb && cals < MaxRfRangeCalibrations; db += _options.CalStepDb)
            {
                try { _attenuator.SetAttenuationDb(db); }
                catch { break; }                       // beyond the attenuator's range — done
                Settle();

                double read = double.NaN;
                bool uncal = false;
                try { read = _receiver.ReadRelativeDb(); }  // triggers; throws UNCAL if this range needs calibrating
                catch (Hp8902AException ex) when (ex.IsUncal) { uncal = true; }
                catch (Hp8902AException ex) when (ex.Code == 96)
                {
                    // Lost lock — below the ~-100 dBm converter floor; no point calibrating deeper.
                    Trace?.Invoke($"  {db,3} dB: lost lock (Error 96) — below the converter floor; stop descent");
                    try { _receiver.ClearError(); } catch { /* keep going */ }
                    break;
                }
                catch { Trace?.Invoke($"  {db,3} dB: transient read — continue"); continue; }

                // Off-trend jump = crossed into a different (uncalibrated) RF range. Diagnostic only.
                bool jumped = !double.IsNaN(read) && !double.IsNaN(prev)
                              && System.Math.Abs((read - prev) - (-_options.CalStepDb)) > RangeStepThresholdDb;
                if (!double.IsNaN(read)) prev = read;

                // Force mode calibrates the moment the descent reaches each range boundary, whether or
                // not the receiver volunteered RECAL (with resident factors it never does — that's #17).
                bool forceHere = force && forceIdx < ForceCalDepthsDb.Length && db >= start + ForceCalDepthsDb[forceIdx];
                bool doCal = uncal || forceHere;

                string tag = uncal ? "UNCAL" : jumped ? "jump" : "ok";
                string level = double.IsNaN(read) ? "UNCAL" : read.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " dBm";
                Trace?.Invoke($"  {db,3} dB: read={level}  [{tag}]{(doCal ? (forceHere && !uncal ? "  -> CALIBRATE (forced)" : "  -> CALIBRATE") : "")}");

                if (!doCal) continue;
                if (forceHere) forceIdx++;

                // --panel-review wraps THIS step tightly: pause immediately before the CALIBRATE and
                // immediately after, so the operator confirms this exact step against the front panel.
                PanelWatch?.Invoke(uncal
                    ? $"the CALIBRATE at {db} dB — RECAL/UNCAL should be lit on the panel now"
                    : $"the forced CALIBRATE at {db} dB — note whether RECAL/UNCAL is lit (it may not be)");
                try { _receiver.Calibrate(); cals++; }   // Calibrate() waits + surfaces a cal error (#8)
                catch { try { _receiver.ClearError(); } catch { /* keep going */ } }
                PanelReview?.Invoke($"After the CALIBRATE at {db} dB — did the reading stay valid (and any RECAL/UNCAL clear)?");
            }

            if (cals == 0)
                Trace?.Invoke("range-cal: NO-OP — 0 CALIBRATEs fired. The RF ranges are running on RESIDENT " +
                              "factors, not a fresh calibration (issue #17). Add --force-range-cal to force a " +
                              "per-range CALIBRATE, or check that RECAL lights on the panel during the descent.");
            else
                Trace?.Invoke($"range-cal: {cals} CALIBRATE(s) fired.");
        }

        /// <summary>
        /// Adaptive reference leveling (issue #16). With the attenuator already at its 0 dB reference
        /// setting and the receiver in Tuned RF Level mode (pre-SET REF, so <see cref="IMeasuringReceiver.ReadTunedLevelDbm"/>
        /// returns the ABSOLUTE level in dBm), nudges the 8340B source power until the reference lands
        /// in the target window just under the 8902A's 0 dBm relative-measurement ceiling. The level
        /// tracks source power 1:1 (both dB), so each step moves the source by the remaining delta,
        /// clamped to the source's usable range. Converter loss varies with frequency, so one fixed
        /// <c>--power</c> can't keep the reference in range across a multi-frequency / <c>--full</c>
        /// sweep — too hot over-ranges and hangs the reference (the ~12 dB hang at +10 dBm/3 GHz), too
        /// cold gives a shallow floor. Best-effort: if the level can't be read (e.g. Error 96, no
        /// signal) it leaves the source at the last commanded power and returns NaN. Records the
        /// achieved reference and settled source power on <paramref name="result"/>.
        /// </summary>
        private double LevelReference(FreqPointResult result)
        {
            double target = _options.TargetReferenceDbm;
            double achieved = double.NaN;
            double grid = _options.LevelFineStepDb;   // the source's own amplitude resolution

            // Command only values the source can actually produce. The 8340B quantizes to 0.05 dB, so
            // anything finer is silently rounded and the leveller chases a level it cannot command.
            System.Func<double, double> quantize = v => System.Math.Round(v / grid) * grid;

            double power = quantize(_options.SourcePowerDbm);   // Prepare() commanded this baseline

            // The source grid rarely lands exactly on the target, so remember the best reading at or
            // below it. Once a step crosses above the target we have the target bracketed within one
            // grid step and no further move can improve on that best — the leveller returns to it.
            double bestBelowLevel = double.NaN, bestBelowPower = double.NaN;
            bool sawAbove = false;

            for (int iter = 0; iter <= _options.MaxLevelIterations; iter++)
            {
                double level;
                try { level = _receiver.ReadTunedLevelDbm(); }
                catch (Exception ex) when (ex is Hp8902AException || ex is FormatException)
                {
                    // No settled absolute level (e.g. lost lock / no signal). Abort leveling and let
                    // the sweep surface the fault; leave the source at the last commanded power.
                    try { _receiver.ClearError(); } catch { /* keep going */ }
                    break;
                }
                achieved = level;

                double delta = target - level;                               // dB the reference must move
                Trace?.Invoke($"level: read {level:+0.000;-0.000;0.000} dBm, target {target:+0.0;-0.0;0.0} " +
                              $"(delta {delta:+0.000;-0.000;0.000}), source {power:0.00} dBm");

                // Accept ONLY from below the target. With the target at the 0 dBm Tuned RF Level ceiling
                // there is no headroom above it, so any positive excess is an over-range and is always
                // corrected down, however small — a symmetric window would happily settle above the
                // ceiling. Converging from below also means the last move is always a reduction.
                if (delta >= 0)
                {
                    // At or below the target: remember it if it's the closest such reading so far.
                    if (double.IsNaN(bestBelowLevel) || level > bestBelowLevel)
                    {
                        bestBelowLevel = level;
                        bestBelowPower = power;
                    }
                    // Within one grid step of the target is as close as the source can place it.
                    if (delta <= grid) break;
                }
                else
                {
                    sawAbove = true;
                }

                // Both sides seen: the source's 0.05 dB grid straddles the target, so no further move
                // can do better than the best reading at or below it. Go back to that power and stop —
                // this is what previously oscillated forever between 1.12 and 1.13 dBm.
                //
                // The width test below matters. The level tracks source power 1:1 only to within
                // the source's own flatness, so a coarse jump can overshoot by more than the grid and
                // leave the two samples far apart. At 18 GHz a 13.20 dB jump landed 0.219 dB high;
                // taking this shortcut then reverted to the 13 dB-away baseline and reported it as
                // "settled", measuring that whole frequency against a reference 13 dB too low.
                if (sawAbove && !double.IsNaN(bestBelowPower) &&
                    System.Math.Abs(power - bestBelowPower) <= grid * 1.5)
                {
                    if (System.Math.Abs(power - bestBelowPower) > 1e-9)
                    {
                        power = bestBelowPower;
                        _source.SetPowerDbm(power);
                        Settle();
                    }
                    achieved = bestBelowLevel;
                    Trace?.Invoke($"level: source grid ({grid:0.###} dB) straddles the target — settling on the " +
                                  $"closest reachable point below it, {achieved:+0.000;-0.000;0.000} dBm.");
                    break;
                }

                // Two-phase approach. While the error is large, jump to one grid step SHORT of the
                // target so the final approach is always upward from below; then creep one grid step at
                // a time. A reading ABOVE the target is not a near-miss at the 0 dBm ceiling — it is an
                // over-range — so that case steps back down rather than creeping.
                double step;
                if (delta < 0)
                    // Over target: jump the whole way when far off, creep only within the fine window.
                    // Creeping unconditionally meant a reference 41 dB high moved 0.05 dB per iteration
                    // and burned the entire budget without arriving.
                    step = delta < -_options.LevelFineWindowDb ? delta : -grid;
                else if (delta > _options.LevelFineWindowDb)
                    step = delta - grid;                                      // coarse, landing just under
                else
                    step = grid;                                              // fine creep, from below

                double next = quantize(System.Math.Max(_options.SourcePowerMinDbm,
                              System.Math.Min(_options.SourcePowerMaxDbm, power + step)));
                if (System.Math.Abs(next - power) < 1e-9) break;              // clamped — no further move
                power = next;
                _source.SetPowerDbm(power);
                Settle();
            }

            // Never settle ABOVE the target: at the 0 dBm Tuned RF Level ceiling that is an
            // over-range. If the iteration budget ran out, or the source clamped, on a reading
            // above the target, fall back to the best reading at or below it.
            if (!double.IsNaN(achieved) && achieved > target + 1e-9 && !double.IsNaN(bestBelowPower))
            {
                if (System.Math.Abs(power - bestBelowPower) > 1e-9)
                {
                    power = bestBelowPower;
                    _source.SetPowerDbm(power);
                    Settle();
                }
                achieved = bestBelowLevel;
                Trace?.Invoke("level: iteration budget ended above the target - falling back to " +
                              "the best reading at or below it.");
            }

            if (result != null)
            {
                result.ReferencePowerDbm = achieved;
                result.LeveledSourcePowerDbm = power;
            }
            if (!double.IsNaN(achieved))
                Trace?.Invoke($"level: settled at {achieved:+0.000;-0.000;0.000} dBm " +
                              $"(source {power:0.00} dBm) — the reference is the measurement anchor, so the " +
                              "source's own power error and the cable loss drop out of every point.");
            return achieved;
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
