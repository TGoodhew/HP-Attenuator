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
  --hardware           Drive the real bench over GPIB (NI-VISA).
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
