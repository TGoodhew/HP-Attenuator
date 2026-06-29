using System;
using System.Collections.Generic;
using System.Globalization;
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
        public double RfPowerFreqMHz = 5000.0;  // --freq  : frequency for --rf-power (default 5 GHz)
        public int RfPowerAttenDb = 0;          // --atten : attenuation for --rf-power (default 0 dB)
        public bool LoadCal;        // --load-cal : load converter cal factors into the 8902A first
        public bool NoCalPass;      // --no-cal-pass : skip the 3-point range calibration pass
        public bool SensorCal;      // --sensor-cal : interactive zero + (prompt to connect) + calibrate
        public bool SkipSensorCal;  // --skip-sensor-cal : bypass the mandatory pre-measurement sensor cal
        public bool NoBeep;         // --no-beep : silence the per-command beep
        public bool InvertAtten;    // --invert-atten : swap the 11713A A/B relay sense
        public bool SensorZero;     // --sensor-zero : upload cal factors + zero the power sensor
        public bool SensorCalibrate;// --sensor-calibrate : calibrate the sensor vs the 50 MHz/1 mW ref
        public bool SwappedSim;     // --swapped-sim : simulate the 8496-on-X wiring
        public bool AskAtten;       // --ask      : prompt for the X/Y attenuator assignment
        public int? XAttenSteps;    // --x-atten 8494|8496 : declare ATTEN X attenuator (skip auto-id)

        public double ToleranceDb = 1.5;
        public string CsvPath = "harness-results.csv";

        public string AddrSource = "GPIB0::20::INSTR";
        public string AddrLo = "GPIB0::19::INSTR";
        public string AddrReceiver = "GPIB0::14::INSTR";
        public string AddrAttenuator = "GPIB0::28::INSTR";

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
                    case "--freq": o.RfPowerFreqMHz = D(Need(args, ++i)); break;
                    case "--atten": o.RfPowerAttenDb = I(Need(args, ++i)); break;
                    case "--load-cal": o.LoadCal = true; break;
                    case "--no-cal-pass": o.NoCalPass = true; break;
                    case "--sensor-cal": o.SensorCal = true; break;
                    case "--skip-sensor-cal": o.SkipSensorCal = true; break;
                    case "--no-beep": o.NoBeep = true; break;
                    case "--invert-atten": o.InvertAtten = true; break;
                    case "--sensor-zero": o.SensorZero = true; break;
                    case "--sensor-calibrate": o.SensorCalibrate = true; break;
                    case "--swapped-sim": o.SwappedSim = true; break;
                    case "--ask": o.AskAtten = true; break;
                    case "--x-atten": o.XAttenSteps = Need(args, ++i) == "8496" ? 10 : 1; break;
                    case "--tolerance": o.ToleranceDb = D(Need(args, ++i)); break;
                    case "--out": o.CsvPath = Need(args, ++i); break;
                    case "--fstart": o.Sweep.FreqStartMHz = D(Need(args, ++i)); o.ExplicitFreq = true; break;
                    case "--fstop": o.Sweep.FreqStopMHz = D(Need(args, ++i)); o.ExplicitFreq = true; break;
                    case "--fstep": o.Sweep.FreqStepMHz = D(Need(args, ++i)); o.ExplicitFreq = true; break;
                    case "--power": o.Sweep.SourcePowerDbm = D(Need(args, ++i)); break;
                    case "--astart": o.Sweep.AttenStartDb = I(Need(args, ++i)); break;
                    case "--astop": o.Sweep.AttenStopDb = I(Need(args, ++i)); break;
                    case "--astep": o.Sweep.AttenStepDb = I(Need(args, ++i)); break;
                    case "--cal-step": o.Sweep.CalStepDb = I(Need(args, ++i)); break;
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

        public const string HelpText = @"HP-Attenuator test harness — steps the 8340B across frequency, measures
power via the 8902A/11793A/8673B chain, sweeps the 11713A attenuator and
reports the measured attenuation.

Usage: HP-Attenuator.TestHarness [options]

  (default)            Fast SIMULATION run over a representative frequency set.
  --hardware           Drive the real bench over GPIB (NI-VISA). A mandatory sensor
                       zero+calibrate runs first (prompts you to use the CAL output).
  --skip-sensor-cal    Bypass the mandatory pre-measurement sensor calibration.
  --no-beep            Silence the short beep emitted on every instrument command.
  --detect             Signal-presence check only (8902A RF-freq, RF on vs off);
                       no sweep. Default freqs 100 + 2000 MHz; no calibration needed.
  --rf-power           Test 1: single-point absolute RF power readback. Sets the
                       attenuator to 0 dB (or --atten), sources --freq at --power, and
                       reads absolute power (dBm) via the 8902A RF Power measurement.
  --freq MHz           Frequency for --rf-power (default 5000 = 5 GHz).
  --atten dB           Attenuation for --rf-power (default 0).
  --load-cal           Load the converter cal factors into the 8902A first (hardware).
  --no-cal-pass        Skip the 8902A 3-point range-calibration pass.
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
  --power dBm                    Source power (default 0).
  --settle ms                    Settle per attenuator step (default 100).
  --tolerance dB                 Pass/fail threshold (default 1.5).
  --out file.csv                 CSV results path (default harness-results.csv).
  --addr-source/-lo/-receiver/-attenuator  VISA resource overrides.
  -h, --help                     This help.";
    }
}
