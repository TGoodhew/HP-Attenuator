using System;
using System.Collections.Generic;
using System.Globalization;
using HpAttenuator.Instruments;
using HpAttenuator.Measurement;

namespace HpAttenuator.TestHarness
{
    /// <summary>Parsed command-line options for the test harness.</summary>
    internal sealed class HarnessOptions
    {
        public bool Hardware;        // --hardware : drive the real bench (default: simulation)
        public bool Full;           // --full     : full 1 MHz-18 GHz spec sweep
        public bool Detect;         // --detect   : signal-presence check only (no attenuation sweep)
        public double DetectThresholdDb = 10.0;
        public bool RfPower;        // --rf-power : Test 1 — single-point absolute RF power readback
        public double RfPowerFreqMHz = 3000.0;  // --freq  : frequency for --rf-power / --atten-sweep (3 GHz — the 8494G/8496G are rated DC-4 GHz)
        public int RfPowerAttenDb = 0;          // --atten : attenuation for --rf-power (default 0 dB)
        public bool AttenSweep;     // --atten-sweep : Test 2 — 1 dB relative attenuation sweep at --freq
        public bool PerAtten;       // --per-atten : Test 3 — exercise each attenuator's settings individually
        public bool SectionTest;    // --section-test : isolate the 8496's two 40 dB sections (digit 7 vs 8)
        public bool TrflCal;       // --trfl-cal : read back the stored TRFL range cal factors (SF 38)
        public bool ClearTrflCal;  // --clear-trfl-cal : clear them (SF 39.9) then read back
        public bool SfMatrix;      // --sf-matrix : #25 - SF 4 x SF 31 stage-1 matrix
        public string SfConfigs;   // --sf-configs A,B : restrict which matrix cells run
        public bool SectionSum;     // --section-sum : #15 — characterize each section alone, then SUM for the full range
        public bool CalDebug;       // --cal-debug : observe the 8902A status byte vs level (no CALIBRATE)
        public bool Debug;          // --debug : trace every 8902A command + status byte (find Error 35)
        public bool Profile;        // --profile : attribute sweep wall-clock by category (#2)
        public bool PanelReview;    // --panel-review : pause to have the operator read the 8902A front panel
        public bool CalProbe;       // --cal-probe : force one Tuned RF Level CALIBRATE and trace it (hunt Error 35)
        public bool ExplicitAstop;  // user gave --astop (don't auto-fill the attenuator max)
        public bool ExplicitAstep;  // user gave --astep (don't force 1 dB steps)
        public bool LoadCal;        // --load-cal : load converter cal factors into the 8902A first
        public bool NoCalPass;      // --no-cal-pass : skip the 3-point range calibration pass
        public bool SensorCal;      // --sensor-cal : interactive zero + (prompt to connect) + calibrate
        public bool SkipSensorCal;  // --skip-sensor-cal : bypass the mandatory pre-measurement sensor cal
        public bool Recal;          // --recal : force a fresh sensor cal even if the session one is still fresh
        public double CalMaxAgeHours = 8.0; // --cal-max-age : reuse a session sensor cal up to this age (hours)
        public bool NoBeep;         // --no-beep : silence the per-command beep
        public bool InvertAtten;    // --invert-atten : swap the 11713A A/B relay sense
        public bool SensorZero;     // --sensor-zero : upload cal factors + zero the power sensor
        public bool SensorCalibrate;// --sensor-calibrate : calibrate the sensor vs the 50 MHz/1 mW ref
        public bool SwappedSim;     // --swapped-sim : simulate the 8496-on-X wiring
        public bool AskAtten;       // --ask      : prompt for the X/Y attenuator assignment
        public int? XAttenSteps;    // --x-atten 8494|8496 : declare ATTEN X attenuator (skip auto-id)

        /// <summary>#24: pass/fail band for a SINGLE attenuator step's own error, dB. Tighter than the
        /// cumulative --tolerance: one 1 dB step missing by 0.5 dB is a real fault even though the
        /// cumulative total is still inside spec.</summary>
        public double StepToleranceDb = 0.25;

        public double ToleranceDb = 1.5;
        // Run artifacts live in DebugResults/ (git-ignored as a whole), not the repo root.
        public string CsvPath = "DebugResults/harness-results.csv";

        // Low-level Tuned RF Level reads (AUTO averaging near the floor) can take tens of
        // seconds; the VISA read blocks until Data-Ready, so this is mostly headroom.
        public int ReceiverTimeoutMs = 60000;   // --read-timeout-ms

        public string AddrSource = "GPIB0::20::INSTR";
        public string AddrLo = "GPIB0::19::INSTR";
        public string AddrReceiver = "GPIB0::14::INSTR";
        public string AddrAttenuator = "GPIB0::27::INSTR";   // this bench's 11713A (NOT the factory-default 28)

        public readonly SweepOptions Sweep = new SweepOptions();
        public double IdFreqMHz = 100.0;

        public bool ShowHelp;
        public bool ExplicitFreq;   // set when a frequency range override is given

        public static HarnessOptions Parse(string[] args)
        {
            var o = new HarnessOptions();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                switch (a)
                {
                    case "-h": case "--help": o.ShowHelp = true; break;
                    case "--hardware": o.Hardware = true; break;
                    case "--full": o.Full = true; break;
                    case "--detect": o.Detect = true; break;
                    case "--detect-threshold": o.DetectThresholdDb = D(Need(args, ++i)); break;
                    case "--rf-power": o.RfPower = true; break;
                    case "--atten-sweep": o.AttenSweep = true; break;
                    case "--per-atten": o.PerAtten = true; break;
                    case "--section-test": o.SectionTest = true; break;
                    case "--section-sum": o.SectionSum = true; break;
                    case "--trfl-cal": o.TrflCal = true; break;
                    case "--clear-trfl-cal": o.ClearTrflCal = true; break;
                    case "--sf-matrix": o.SfMatrix = true; break;
                    case "--sf-configs": o.SfConfigs = Need(args, ++i); break;
                    case "--repeats": o.Sweep.RepeatsPerPoint = I(Need(args, ++i)); break;
                    case "--noise-correction": o.Sweep.NoiseCorrection = true; break;
                    case "--cal-debug": o.CalDebug = true; break;
                    case "--debug": o.Debug = true; break;
                    case "--profile": o.Profile = true; break;
                    case "--panel-review": o.PanelReview = true; break;
                    case "--cal-probe": o.CalProbe = true; break;
                    case "--freq": o.RfPowerFreqMHz = D(Need(args, ++i)); break;
                    case "--atten": o.RfPowerAttenDb = I(Need(args, ++i)); break;
                    case "--load-cal": o.LoadCal = true; break;
                    case "--no-cal-pass": o.NoCalPass = true; break;
                    case "--sensor-cal": o.SensorCal = true; break;
                    case "--skip-sensor-cal": o.SkipSensorCal = true; break;
                    case "--recal": o.Recal = true; break;
                    case "--cal-max-age": o.CalMaxAgeHours = D(Need(args, ++i)); break;
                    case "--no-beep": o.NoBeep = true; break;
                    case "--invert-atten": o.InvertAtten = true; break;
                    case "--sensor-zero": o.SensorZero = true; break;
                    case "--sensor-calibrate": o.SensorCalibrate = true; break;
                    case "--swapped-sim": o.SwappedSim = true; break;
                    case "--ask": o.AskAtten = true; break;
                    case "--x-atten": o.XAttenSteps = Need(args, ++i) == "8496" ? 10 : 1; break;
                    case "--tolerance": o.ToleranceDb = D(Need(args, ++i)); break;
                    case "--step-tolerance": o.StepToleranceDb = D(Need(args, ++i)); break;
                    case "--read-timeout-ms": o.ReceiverTimeoutMs = I(Need(args, ++i)); break;
                    case "--out": o.CsvPath = Need(args, ++i); break;
                    case "--fstart": o.Sweep.FreqStartMHz = D(Need(args, ++i)); o.ExplicitFreq = true; break;
                    case "--fstop": o.Sweep.FreqStopMHz = D(Need(args, ++i)); o.ExplicitFreq = true; break;
                    case "--fstep": o.Sweep.FreqStepMHz = D(Need(args, ++i)); o.ExplicitFreq = true; break;
                    case "--power": o.Sweep.SourcePowerDbm = D(Need(args, ++i)); break;
                    case "--no-leveling": o.Sweep.AdaptiveLevel = false; break;
                    case "--ref-target": o.Sweep.TargetReferenceDbm = D(Need(args, ++i)); break;
                    case "--detector": o.Sweep.Detector = ParseDetector(Need(args, ++i)); break;
                    case "--sync-detector": o.Sweep.Detector = TrflDetector.Synchronous; break;
                    case "--track-mode": o.Sweep.TrackMode = true; break;
                    case "--auto-tune": o.Sweep.Tuning = TrflTuning.Auto; break;
                    case "--manual-tune": o.Sweep.Tuning = TrflTuning.Manual; break;
                    case "--lo-power": o.Sweep.LoPowerDbm = D(Need(args, ++i)); break;
                    case "--astart": o.Sweep.AttenStartDb = I(Need(args, ++i)); break;
                    case "--astop": o.Sweep.AttenStopDb = I(Need(args, ++i)); o.ExplicitAstop = true; break;
                    case "--astep": o.Sweep.AttenStepDb = I(Need(args, ++i)); o.ExplicitAstep = true; break;
                    case "--cal-step": o.Sweep.CalStepDb = I(Need(args, ++i)); break;
                    case "--force-range-cal": o.Sweep.ForceRangeCal = true; break;
                    case "--floor-dbm": o.Sweep.FloorDbmOverride = D(Need(args, ++i)); break;
                    case "--no-level-limits": o.Sweep.EnforceLevelLimits = false; break;
                    case "--fine-from": o.Sweep.FineFromDb = I(Need(args, ++i)); break;
                    case "--fine-step": o.Sweep.FineStepDb = I(Need(args, ++i)); break;
                    case "--fine-to": o.Sweep.FineToDb = I(Need(args, ++i)); break;
                    case "--adaptive-steps": o.Sweep.StepPlan = AttenStepPlan.Adaptive; break;
                    case "--floor-approach": o.Sweep.FloorApproachDb = I(Need(args, ++i)); break;
                    case "--no-floor-detect": o.Sweep.FloorDetect = false; break;
                    case "--settle": o.Sweep.SettleMs = I(Need(args, ++i)); break;
                    case "--addr-source": o.AddrSource = Need(args, ++i); break;
                    case "--addr-lo": o.AddrLo = Need(args, ++i); break;
                    case "--addr-receiver": o.AddrReceiver = Need(args, ++i); break;
                    case "--addr-attenuator": o.AddrAttenuator = Need(args, ++i); break;
                    default: throw new ArgumentException($"Unknown argument: {a}");
                }
            }
            return o;
        }

        /// <summary>The reduced, fast set of frequencies used when --full is not given.</summary>
        public static IReadOnlyList<double> QuickFrequenciesMHz { get; } = new[]
        {
            1.0, 50.0, 100.0, 500.0, 1000.0, 1300.0,   // direct regime + crossover
            1310.0, 2000.0, 5000.0, 10000.0, 18000.0   // converter regime + edges
        };

        /// <summary>Default frequencies for --detect: one direct, one through the converter.</summary>
        public static IReadOnlyList<double> DetectFrequenciesMHz { get; } = new[] { 100.0, 2000.0 };

        private static string Need(string[] args, int i)
        {
            if (i >= args.Length) throw new ArgumentException("Missing value for an argument.");
            return args[i];
        }

        private static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);
        private static int I(string s) => int.Parse(s, CultureInfo.InvariantCulture);

        private static TrflDetector ParseDetector(string s)
        {
            switch (s.Trim().ToLowerInvariant())
            {
                case "avg": case "average": return TrflDetector.Average;
                case "sync": case "synchronous": return TrflDetector.Synchronous;
                default: throw new ArgumentException($"--detector must be avg or sync (got '{s}').");
            }
        }

        public const string HelpText = @"HP-Attenuator test harness — steps the 8340B across frequency, measures
power via the 8902A/11793A/8673B chain, sweeps the 11713A attenuator and
reports the measured attenuation.

Usage: HP-Attenuator.TestHarness [options]

  (default)            Fast SIMULATION run over a representative frequency set.
  --hardware           Drive the real bench over GPIB (NI-VISA). A mandatory sensor
                       zero+calibrate runs first (prompts you to use the CAL output).
  --skip-sensor-cal    Bypass the sensor calibration entirely (path-check only; shallow).
  --recal              Force a fresh sensor cal even if the session one is still fresh.
  --cal-max-age H      Reuse a session sensor cal up to H hours old (default 8). The cal is
                       done once per session and skipped automatically while fresh.
  --no-beep            Silence the short beep emitted on every instrument command.
  --detect             Signal-presence check only (8902A RF-freq, RF on vs off);
                       no sweep. Default freqs 100 + 2000 MHz; no calibration needed.
  --rf-power           Test 1: single-point absolute RF power readback. Sets the
                       attenuator to 0 dB (or --atten), sources --freq at --power, and
                       reads absolute power (dBm) via the 8902A RF Power measurement.
  --atten-sweep        Test 2: at --freq, sets a 0 dB reference (Tuned RF Level SET REF,
                       normalising path loss) then steps the attenuator down in 1 dB
                       steps to the attenuator's maximum, reporting relative attenuation.
  --per-atten          Test 3: exercise each attenuator's settings individually at --freq —
                       the 8494 at 1..11 dB and the 8496 at 10..110 dB (the other at 0),
                       ~22 points. Isolates each attenuator's accuracy.
  --section-sum        #15: characterize each attenuator SECTION on its own (each <=40 dB, so
                       every read stays above the ~95 dB direct-measurement floor of the 11793A
                       path), then SUM the measured sections to synthesize totals — including the
                       full 110/121 dB that cannot be measured directly. Prints a per-section table
                       and a synthesized-total table (nominal vs summed). Sections add linearly
                       (proved by --section-test), so the sum is a valid full-range measurement.
  --freq MHz           Frequency for --rf-power / --atten-sweep (default 3000 = 3 GHz;
                       the 8494G/8496G attenuators are rated DC-4 GHz).
  --atten dB           Attenuation for --rf-power (default 0).
  --load-cal           Load the converter cal factors into the 8902A (both the Normal and
                       Frequency-Offset tables) and exit. Non-interactive.
  --no-cal-pass        Skip the 8902A 3-point range-calibration pass.
  --force-range-cal    Force one CALIBRATE per RF range during the pre-SET-REF descent even when the
                       8902A doesn't raise RECAL/UNCAL (resident factors suppress it — issue #17).
                       Off by default. Pair with --debug to see the per-step range-cal trace and the
                       no-op/CALIBRATE-count summary; with --panel-review to watch each CALIBRATE.
  --sensor-cal         Interactive: upload cal factors + zero, prompt you to attach the
                       sensor to the CAL output, then calibrate. (Run this one yourself.)
  --sensor-zero        Upload cal factors and ZERO the 8902A power sensor, then stop.
  --sensor-calibrate   Calibrate the sensor against the 50 MHz/1 mW reference output.
  --full               Full spec sweep: 1 MHz-18 GHz, 10 MHz steps, 0-110 dB.
  --swapped-sim        Simulate the 8496 (10 dB) wired to ATTEN X (to test auto-id).
  --x-atten 8494|8496  Declare which attenuator is on ATTEN X (skip auto-id).
  --ask                Prompt for the X/Y attenuator assignment.

  --fstart/--fstop/--fstep MHz   Frequency range/step overrides.
  --astart/--astop/--astep dB    Attenuation range/step overrides.
  --power dBm                    Source power (default 0 — lands the 0 dB reference just under the
                                 8902A's 0 dBm ceiling at 3 GHz; higher over-ranges it). With
                                 adaptive leveling on this is only the starting guess per frequency.
  --ref-target dBm               Target for the leveled 0 dB reference (default -2). Adaptive
                                 leveling nudges the source so the reference lands here, just under
                                 the 8902A's 0 dBm ceiling — kept in range at every frequency (#16).
  --no-leveling                  Disable adaptive reference leveling; hold --power fixed per
                                 frequency (the pre-#16 behaviour).
  --detector avg|sync            8902A IF detector for the Tuned RF Level sweep. avg (default,
                                 4.4SP, 30 kHz BW, floor ~-100 dBm) is robust through the
                                 converter/LO path; sync (4.0SP, 200 Hz BW, floor ~-127 dBm) reaches
                                 the full 110 dB but can lose lock on a drifty signal (#14).
  --sync-detector                Shorthand for --detector sync (the deep-sweep detector).
  --track-mode                   Use 8902A Track Mode (SF 32.9), the Microwave Product Note's
                                 low-level converter method: keeps the receiver locked onto the
                                 drifting converted signal so it holds toward the ~-100 dBm floor
                                 instead of losing lock partway (#14). Implies the Average detector.
  --manual-tune / --auto-tune    #3: Tuned RF Level signal acquisition. manual (default) tunes to the
                                 commanded frequency directly (<freq>MZ) — fast, deterministic, our usual
                                 case since we command the source. auto lets the 8902A search/acquire the
                                 signal first, then holds it — for an uncertain/drifting frequency. NOTE:
                                 the auto-tune HP-IB code is bench-unverified (OCR-ambiguous manual); pair
                                 with --debug to see the sequence. Verify per HardwareValidation.md V7.
  --lo-power dBm                 8673B LO drive into the 11793A (default 8; the converter wants
                                 +8..+13 dBm — try +10..+13 to cut conversion loss).
  --floor-dbm dBm                Override the measurable floor. Default (#21) is the SPEC limit for the
                                 path in use: 11793A converted -100 dBm (Microwave Product Note); 8902A
                                 direct -100 dBm on the IF average detector, -127 dBm on synchronous
                                 (O&C Table 1-1). Use this only to match where the reading actually
                                 saturates on the day.
  --no-level-limits              #21: disable the pre-flight level check and attempt every commanded
                                 point, even ones below the path floor (the pre-#21 behaviour — deep
                                 points then read the floor and are caught after the fact by #13).
  --fine-from dB                 #22: switch to a finer attenuation step at this depth, to sample the
  --fine-step dB                 approach to the measurement floor densely (default step 1 dB) while
  --fine-to dB                   the shallow region stays on the coarse grid. The coarse grid resumes
                                 past --fine-to (default: --astop). E.g. --astep 10 --fine-from 90
                                 --fine-to 100 gives 0,10..80, 90,91..100, then 110.
  --trfl-cal                     Read back the 8902A's stored Tuned RF Level range calibration
                                 factors (SF 38.1-38.3). A CALIBRATE done at a level the detector
                                 cannot reference stores a BAD factor that offsets every later
                                 reading and survives an instrument preset.
  --clear-trfl-cal               Clear all stored TRFL calibration factors (SF 39.9), then read
                                 back. The recovery for the above.
  --sf-matrix                    #25 stage 1: sweep the 2x2 matrix of IF detector (SF 4: 4.0
                                 synchronous / 4.4 average) against noise correction (SF 31:
                                 31.0 off / 31.1 on), to test whether the deep-end roll-off is
                                 residual system noise or a hard floor. Forces the range cal
                                 (SF 31.1 creates a Range 3 factor, so it needs one to fire).
                                 Writes one CSV row per reading, flushed as it goes.
  --sf-configs A,B               #25: run only these matrix cells (A=4.4/31.0, B=4.4/31.1,
                                 C=4.0/31.0, D=4.0/31.1). Default: all four. The synchronous
                                 cells (C,D) lose lock through the 11793A and can spend the
                                 full read budget failing every point, so A,B first is sane.
  --repeats n                    #25: readings per attenuation point (default 1). >1 gives the
                                 per-point standard deviation that separates noise from bias.
  --noise-correction             #25: enable SF 31.1 noise correction on an ordinary sweep.
                                 AVG detector + Sensor Module only (8902A O&C CAUTION).
  --adaptive-steps               #23: choose the points from what the hardware can actually measure
                                 here, instead of a fixed grid — 1 dB through the FINE attenuator's
                                 whole range (8494: 0-11 dB), then the --astep ladder to within
                                 --floor-approach of the deepest measurable point, then 1 dB in to
                                 that limit. The limit is (achieved reference - path floor), so
                                 levelling the reference to 0 dBm buys the most depth.
  --floor-approach dB            #23: how far above the limit the 1 dB approach begins (default 10).
  --ref-target dBm               Target for the leveled 0 dB reference (default 0). Levelling until
                                 the 8902A reads 0 dBm makes the reference an absolute anchor, so the
                                 generator's power error and the cable loss drop out of every point.
  --no-floor-detect              Disable #13 floor/plateau detection; count every point's error
                                 (the pre-#13 behaviour — deep floored points then fail the sweep).
  --settle ms                    Settle per attenuator step (default 100).
  --tolerance dB                 Pass/fail threshold on the CUMULATIVE error (default 1.5).
  --step-tolerance dB            #24: pass/fail band for a single step's own error in the per-step
                                 increment table (default 0.25).
  --read-timeout-ms ms           8902A read timeout (default 60000). Low-level Tuned RF
                                 Level reads near the floor take tens of seconds.
  --debug                        Trace every 8902A command + the status byte after it, to
                                 pinpoint which command sets an instrument error.
  --profile                      #2: attribute sweep wall-clock by category (settled reads, range-cal
                                 pre-pass, per-step settle, attenuator sets, setup/other) and print a
                                 breakdown so the real hotspot is measured, not guessed, before
                                 optimizing. Combine with --debug for per-command detail (note: --debug
                                 itself adds a serial poll after every command and inflates timings).
  --panel-review                 Pause at key points to have YOU read the 8902A front panel and type
                                 what it shows (which annunciator lit, errors) — for questions only a
                                 human at the panel can answer. Pauses before the relevant commands
                                 and after. Hardware + attended only (ignored in sim / redirected).
  --out file.csv                 CSV results path (default DebugResults/harness-results.csv;
                                 the DebugResults folder is git-ignored). Parent dirs are created.
  --addr-source/-lo/-receiver/-attenuator  VISA resource overrides.
  -h, --help                     This help.";
    }
}
