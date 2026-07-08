using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using HpAttenuator.Instruments;
using HpAttenuator.Measurement;
using HpAttenuator.Model;
using HpAttenuator.Visa;
using Spectre.Console;

namespace HpAttenuator.TestHarness
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            HarnessOptions opt;
            try { opt = HarnessOptions.Parse(args); }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]{ex.Message.EscapeMarkup()}[/]");
                AnsiConsole.WriteLine(HarnessOptions.HelpText);
                return 2;
            }

            if (opt.ShowHelp) { AnsiConsole.WriteLine(HarnessOptions.HelpText); return 0; }

            AnsiConsole.Write(new FigletText("11713A TEST").Color(Color.Aqua));
            AnsiConsole.MarkupLine("[grey]Attenuation-vs-frequency harness — 8340B + 8673B + 11793A + 8902A[/]");
            AnsiConsole.WriteLine();

            VisaInstrumentLink.BeepOnCommand = !opt.NoBeep;
            if (opt.Debug)
            {
                Hp8902A.DebugLog = s => AnsiConsole.MarkupLine($"[grey]  {s.EscapeMarkup()}[/]");
                // Engine-level range-cal trace + the #17 no-op warning (distinct colour from the raw
                // 8902A command trace so the descent summary stands out).
                MeasurementEngine.Trace = s => AnsiConsole.MarkupLine($"[yellow]  {s.EscapeMarkup()}[/]");
            }

            var disposables = new List<IDisposable>();
            try
            {
                // These steps only need the 8902A — don't open the whole bench.
                if (opt.SensorCal || opt.SensorZero || opt.SensorCalibrate || opt.LoadCal)
                {
                    var receiver = BuildReceiverOnly(opt, disposables);
                    if (opt.LoadCal) return RunLoadCalFactors(receiver);
                    if (opt.SensorCal) return RunSensorCalInteractive(receiver);
                    return opt.SensorZero ? RunSensorZero(receiver) : RunSensorCalibrate(receiver);
                }

                Bench bench = BuildBench(opt, disposables);

                // Settling and range calibration only matter on real hardware.
                if (bench.IsSimulated) { opt.Sweep.SettleMs = 0; opt.Sweep.RangeCalibrate = false; }
                if (opt.NoCalPass) opt.Sweep.RangeCalibrate = false;

                // Front-panel review (--panel-review): only on attended hardware. Wire the engine's
                // pause-before / ask-after hooks to the interactive prompts.
                if (opt.PanelReview && !bench.IsSimulated)
                {
                    FrontPanelReview.Enabled = true;
                    MeasurementEngine.PanelWatch = FrontPanelReview.Watch;
                    MeasurementEngine.PanelReview = FrontPanelReview.Ask;
                }

                if (opt.Detect)
                    return RunDetect(opt, bench);

                // Diagnostic: observe the 8902A status byte vs level (no CALIBRATE, no sensor
                // cal needed — Tuned RF Level uses the RF input). Used to fix RECAL gating.
                if (opt.CalDebug)
                    return RunCalDebug(opt, bench);

                // Diagnostic: force one Tuned RF Level CALIBRATE and trace it, to confirm
                // Error 35 is a reference-level (sensor-range) issue. No sensor cal prompt.
                if (opt.CalProbe)
                    return RunCalProbe(opt, bench);

                // The 8902A Tuned RF Level measurement requires a calibrated power sensor.
                // Calibrate ONCE PER SESSION: if a fresh session cal exists (and --recal was
                // not given), reuse the resident cal and skip the interactive step; otherwise
                // run the cal (pausing for the operator to use the CAL output) and mark it.
                // --skip-sensor-cal bypasses calibration entirely (shallow path-check only).
                if (!bench.IsSimulated && !opt.SkipSensorCal)
                {
                    bool reuse = !opt.Recal && SensorCalSession.IsFresh(TimeSpan.FromHours(opt.CalMaxAgeHours));
                    if (reuse)
                    {
                        AnsiConsole.MarkupLine(
                            $"[grey]Reusing this session's sensor cal from " +
                            $"{SensorCalSession.LastCal():HH:mm} (< {opt.CalMaxAgeHours:0.#} h old). " +
                            "Use [/][green]--recal[/][grey] to force a fresh one.[/]");
                    }
                    else
                    {
                        if (!InteractiveSensorCalibrate(bench.Receiver))
                        {
                            AnsiConsole.MarkupLine("[red]Sensor not calibrated — aborting (measurement needs it).[/]");
                            return 1;
                        }
                        SensorCalSession.Mark();
                        AnsiConsole.MarkupLine("Restore the measurement connections (sensor / converter IF as needed), " +
                                               "then press [green]Enter[/] to start the measurement...");
                        Console.ReadLine();
                    }
                }

                if (opt.RfPower)
                    return RunRfPower(opt, bench);

                AttenuatorConfig config = ResolveAttenuator(opt, bench);

                // Test 3: exercise each attenuator's settings individually (8494 1..11 dB,
                // 8496 10..110 dB), one attenuator engaged at a time.
                if (opt.PerAtten)
                {
                    var perAttn = bench.MakeAttenuator(config);
                    var perEngine = new MeasurementEngine(bench.Source, bench.Lo, perAttn, bench.Receiver, opt.Sweep);
                    return RunPerAtten(opt, perEngine, config);
                }

                // Diagnostic: isolate the 8496's two 40 dB sections (does the deep-boundary step
                // follow a specific physical section?). Measures each 40 dB section alone and paired
                // with the 10 dB section for 50 dB, so section 3 vs 4 can be compared directly.
                if (opt.SectionTest)
                {
                    var secAttn = bench.MakeAttenuator(config);
                    var secEngine = new MeasurementEngine(bench.Source, bench.Lo, secAttn, bench.Receiver, opt.Sweep);
                    return RunSectionTest(opt, secEngine, config);
                }

                // #15: per-section characterize + sum. Measure each section alone (each stays above the
                // ~95 dB converter floor), then sum to synthesize the full 110/121 dB that cannot be
                // measured directly. The real path to a validated full-range number.
                if (opt.SectionSum)
                {
                    var sumAttn = bench.MakeAttenuator(config);
                    var sumEngine = new MeasurementEngine(bench.Source, bench.Lo, sumAttn, bench.Receiver, opt.Sweep);
                    return RunSectionSum(opt, sumEngine, config);
                }

                // Test 2: a single-frequency relative attenuation sweep in 1 dB steps from
                // 0 dB to the attenuator's maximum. It reuses the Tuned RF Level relative
                // method (SET REF at 0 dB normalises the path loss); only the frequency and
                // attenuation grid differ from a normal sweep.
                if (opt.AttenSweep)
                {
                    opt.Sweep.FreqStartMHz = opt.Sweep.FreqStopMHz = opt.RfPowerFreqMHz;
                    opt.ExplicitFreq = true;                 // sweep exactly this one frequency
                    opt.Sweep.AttenStartDb = 0;
                    if (!opt.ExplicitAstep) opt.Sweep.AttenStepDb = 1;
                    if (!opt.ExplicitAstop) opt.Sweep.AttenStopDb = config.MaxDecibels;
                }

                // (Re)build the attenuator with the resolved wiring so dB maps to the
                // correct sections.
                IStepAttenuator attenuator = bench.MakeAttenuator(config);

                var engine = new MeasurementEngine(bench.Source, bench.Lo, attenuator, bench.Receiver, opt.Sweep);
                return RunSweep(opt, engine, config);
            }
            catch (Exception ex)
            {
                AnsiConsole.WriteException(ex, ExceptionFormats.ShortenEverything);
                return 2;
            }
            finally
            {
                foreach (var d in disposables) { try { d.Dispose(); } catch { } }
            }
        }

        // ---- Instrument wiring ---------------------------------------------

        /// <summary>Bundle of instruments plus an attenuator factory (for rebuild after id).</summary>
        private sealed class Bench
        {
            public ISignalSource Source;
            public ILocalOscillator Lo;
            public IMeasuringReceiver Receiver;
            public Func<AttenuatorConfig, IStepAttenuator> MakeAttenuator;
            public bool IsSimulated;
        }

        private static Bench BuildBench(HarnessOptions opt, List<IDisposable> disposables)
        {
            if (!opt.Hardware)
            {
                var sim = new SimulatedBench(xIs8494: !opt.SwappedSim);
                AnsiConsole.MarkupLine("[yellow]Mode:[/] SIMULATION" +
                    (opt.SwappedSim ? " [grey](bench wired 8496-on-X)[/]" : string.Empty));
                return new Bench
                {
                    Source = new SimulatedSource(sim),
                    Lo = new SimulatedLo(sim),
                    Receiver = new SimulatedReceiver(sim),
                    MakeAttenuator = cfg => new SimulatedAttenuator(sim, cfg),
                    IsSimulated = true
                };
            }

            AnsiConsole.MarkupLine("[green]Mode:[/] HARDWARE (NI-VISA)");
            var sourceLink = Open(opt.AddrSource, disposables);
            var loLink = Open(opt.AddrLo, disposables);
            // Low-level Tuned RF Level reads are SLOW: at AUTO averaging (4.0SP) the 8902A
            // ramps averaging far up near its floor (and the converter loss pushes deep
            // attenuation close to it), so a settled read can take tens of seconds. The
            // blocking read returns as soon as Data-Ready is set, so a generous timeout just
            // gives those reads room to complete; only truly-below-floor points hit it.
            var rxLink = Open(opt.AddrReceiver, disposables, opt.ReceiverTimeoutMs);
            var attLink = Open(opt.AddrAttenuator, disposables);

            var source = new Hp8340B(sourceLink);
            var lo = new Hp8673B(loLink);
            var receiver = new Hp8902A(rxLink) { SettleMilliseconds = 0 };

            // Clear + preset every device on first connect so no stale errors or latched
            // SRQ from a previous session carry into this run.
            AnsiConsole.MarkupLine("[grey]Clearing + presetting all instruments...[/]");
            source.Initialize();
            lo.Initialize();
            receiver.Initialize();
            new Hp11713A(attLink, AttenuatorConfig.Default()) { InvertSense = opt.InvertAtten }.Initialize();

            return new Bench
            {
                Source = source,
                Lo = lo,
                Receiver = receiver,
                MakeAttenuator = cfg => new Hp11713A(attLink, cfg) { InvertSense = opt.InvertAtten },
                IsSimulated = false
            };
        }

        private static IInstrumentLink Open(string resource, List<IDisposable> disposables, int timeoutMs = 5000)
        {
            var link = new VisaInstrumentLink(resource, timeoutMs);
            disposables.Add(link);
            return link;
        }

        private static IMeasuringReceiver BuildReceiverOnly(HarnessOptions opt, List<IDisposable> disposables)
        {
            if (!opt.Hardware)
            {
                AnsiConsole.MarkupLine("[yellow]Mode:[/] SIMULATION");
                return new SimulatedReceiver(new SimulatedBench());
            }
            AnsiConsole.MarkupLine($"[green]Mode:[/] HARDWARE (8902A @ {opt.AddrReceiver.EscapeMarkup()})");
            var rx = new Hp8902A(Open(opt.AddrReceiver, disposables, opt.ReceiverTimeoutMs));
            AnsiConsole.MarkupLine("[grey]Clearing + presetting 8902A...[/]");
            rx.Initialize();   // device clear + preset (no stale errors/SRQ)
            return rx;
        }

        // ---- Power-sensor zero / calibrate ---------------------------------

        /// <summary>
        /// Full interactive sensor setup. The calibrate step physically requires a human to
        /// move the sensor onto the 8902A CALIBRATION RF POWER OUTPUT, so this pauses and
        /// waits for the operator before calibrating.
        /// </summary>
        /// <summary>
        /// Loads the converter cal-factor tables into the 8902A (both the Normal and the
        /// Frequency-Offset RF-Power tables) and exits. Non-interactive — no sensor to move.
        /// </summary>
        private static int RunLoadCalFactors(IMeasuringReceiver receiver)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]Loading converter cal factors into the 8902A[/]");
            try
            {
                receiver.Reset();
                receiver.LoadCalFactors(ConverterCalFactors.ReferenceCf, ConverterCalFactors.Default);
            }
            catch (Hp8902AException ex)
            {
                AnsiConsole.MarkupLine($"  [red]✗ {ex.Message.EscapeMarkup()}[/]");
                return 1;
            }

            // Pairs written = a 50 MHz low-frequency anchor + the 2–18 GHz table entries.
            int pairs = ConverterCalFactors.Default.Count + 1;   // + the 50 MHz anchor pair
            double refCf = ConverterCalFactors.ReferenceCf;
            // 37.4SP "table size" counts the stored freq/CF pairs (capped to capacity) plus the REF CF.
            int normalExpected = Math.Min(pairs, Hp8902A.NormalTableMaxPairs) + 1;
            int offsetExpected = Math.Min(pairs, Hp8902A.OffsetTableMaxPairs) + 1;
            AnsiConsole.MarkupLine(
                $"  [grey]Set REF CF = {refCf:0.#}% + a 50 MHz anchor + {ConverterCalFactors.Default.Count} " +
                "freq/CF pairs (2–18 GHz) into the Normal and Frequency-Offset tables.[/]");
            if (pairs > Hp8902A.NormalTableMaxPairs)
                AnsiConsole.MarkupLine(
                    $"  [grey]The Normal table caps at {Hp8902A.NormalTableMaxPairs} pairs (instrument spec); " +
                    $"the top {pairs - Hp8902A.NormalTableMaxPairs} won't fit there — only reachable via the " +
                    "converter/Offset path anyway.[/]");

            // Verify the load committed: pair count (37.4SP) + Reference Cal Factor (37.5SP) per table.
            // A correct REF CF is the entry that clears Error 15, so it's the key confirmation.
            if (receiver is Hp8902A hp)
            {
                var (normal, offset, nRef, oRef) = hp.ReadCalFactorTables();
                string ShowN(int n, int exp) => n == exp ? $"[green]{n}[/]" : $"[red]{n}[/]";
                string ShowRef(double r) =>
                    double.IsNaN(r) ? "[red]unreadable[/]"
                    : Math.Abs(r - refCf) < 0.6 ? $"[green]{r:0.#}%[/]" : $"[red]{r:0.#}%[/]";
                AnsiConsole.MarkupLine(
                    $"  Table size (37.4SP, pairs + REF CF): Normal = {ShowN(normal, normalExpected)} (exp {normalExpected}), " +
                    $"Offset = {ShowN(offset, offsetExpected)} (exp {offsetExpected}).");
                AnsiConsole.MarkupLine(
                    $"  REF CF (37.5SP): Normal = {ShowRef(nRef)}, Offset = {ShowRef(oRef)} (exp {refCf:0.#}%).");

                // Leave the receiver in RF POWER so the front panel shows the true post-load state:
                // if cal factors are properly stored, Error 15 must NOT reappear.
                hp.SelectRfPower();
                AnsiConsole.MarkupLine("  [grey]Left the 8902A in RF POWER — check the panel: Error 15 should be gone.[/]");
            }
            return 0;
        }

        private static int RunSensorCalInteractive(IMeasuringReceiver receiver)
        {
            if (!InteractiveSensorCalibrate(receiver)) return 1;
            SensorCalSession.Mark();   // count this toward the session, so measurement runs reuse it
            return 0;
        }

        /// <summary>
        /// Mandatory before any 8902A measurement: zero the sensor, prompt the operator to
        /// connect it to the CALIBRATION RF POWER OUTPUT, calibrate, and verify ~0 dBm.
        /// Returns true on success. The connect step is physical, so it pauses for the human.
        /// </summary>
        private static bool InteractiveSensorCalibrate(IMeasuringReceiver receiver)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]8902A power-sensor setup[/] [grey](required before measuring)[/]");

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]Step 1 of 2 — zero[/]  (the sensor must see NO RF; do NOT put it on the");
            AnsiConsole.MarkupLine("CAL output yet — leave it on the source-under-test with RF off, or disconnected).");
            AnsiConsole.Markup("Press [green]Enter[/] to upload cal factors and zero the sensor...");
            Console.ReadLine();
            try
            {
                receiver.Reset();
                receiver.SelectRfPower();
                receiver.LoadCalFactors(ConverterCalFactors.ReferenceCf, ConverterCalFactors.Default);
                AnsiConsole.MarkupLine($"  [green]✓[/] Cal factors uploaded (REF CF {ConverterCalFactors.ReferenceCf:0}% + " +
                                       $"{ConverterCalFactors.Default.Count} entries)");
                double zeroW = receiver.ZeroSensor();
                AnsiConsole.MarkupLine($"  [green]✓[/] Sensor zeroed — residual {zeroW * 1e9:0.0} nW");
            }
            catch (Hp8902AException ex)
            {
                AnsiConsole.MarkupLine($"  [red]✗ {ex.Message.EscapeMarkup()}[/]");
                return false;
            }

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold yellow]Step 2 of 2 — calibrate[/]");
            AnsiConsole.MarkupLine("Now connect the sensor to the 8902A [bold]CALIBRATION RF POWER OUTPUT[/] " +
                                   "(50 MHz / 1 mW).");
            AnsiConsole.Markup("Press [green]Enter[/] once it is connected (or type 'q' to abort)... ");
            string reply = Console.ReadLine();
            if (reply != null && reply.Trim().Equals("q", StringComparison.OrdinalIgnoreCase))
            {
                AnsiConsole.MarkupLine("[grey]Aborted — sensor not calibrated.[/]");
                return false;
            }

            try
            {
                double refW = receiver.CalibrateSensor();
                double dbm = Rf.WattsToDbm(refW);
                AnsiConsole.MarkupLine($"  [green]✓[/] Reference reads {refW * 1e3:0.000} mW ({dbm:+0.00;-0.00;0.00} dBm) — cal saved");
            }
            catch (Hp8902AException ex)
            {
                AnsiConsole.MarkupLine($"  [red]✗ {ex.Message.EscapeMarkup()}[/]");
                AnsiConsole.MarkupLine("  [yellow](The sensor isn't seeing the 1 mW reference — check it is on the " +
                                       "CALIBRATION RF POWER OUTPUT, then re-run.)[/]");
                return false;
            }

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold green]Sensor zeroed and calibrated.[/]");
            return true;
        }

        private static int RunSensorZero(IMeasuringReceiver receiver)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]Sensor setup — upload cal factors + zero[/]");
            try
            {
                receiver.Reset();
                AnsiConsole.MarkupLine("  [grey]Instrument preset (IP)[/]");
                receiver.SelectRfPower();
                AnsiConsole.MarkupLine("  [grey]RF Power mode (M4)[/]");
                receiver.LoadCalFactors(ConverterCalFactors.ReferenceCf, ConverterCalFactors.Default);
                AnsiConsole.MarkupLine($"  [green]✓[/] Cal factors uploaded — REF CF {ConverterCalFactors.ReferenceCf:0}% + " +
                                       $"{ConverterCalFactors.Default.Count} entries (2–18 GHz)");
                AnsiConsole.MarkupLine("  [grey]Zeroing sensor (calibrator off, ZR)...[/]");
                double zeroW = receiver.ZeroSensor();
                AnsiConsole.MarkupLine($"  [green]✓[/] Sensor zeroed — residual {zeroW * 1e9:0.0} nW");
            }
            catch (Hp8902AException ex)
            {
                AnsiConsole.MarkupLine($"  [red]✗ {ex.Message.EscapeMarkup()}[/]");
                return 1;
            }
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold yellow]Next:[/] connect the sensor to the 8902A CALIBRATION RF POWER OUTPUT, " +
                                   "then run [bold]--sensor-calibrate[/].");
            return 0;
        }

        private static int RunSensorCalibrate(IMeasuringReceiver receiver)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]Sensor calibrate — against 50 MHz / 1 mW reference[/]");
            try
            {
                AnsiConsole.MarkupLine("  [grey]Calibrating (C1 → settle → SC → C0)...[/]");
                double refW = receiver.CalibrateSensor();
                double dbm = Rf.WattsToDbm(refW);
                AnsiConsole.MarkupLine($"  [green]✓[/] Reference reads {refW * 1e3:0.000} mW ({dbm:+0.00;-0.00;0.00} dBm) — cal saved");
            }
            catch (Hp8902AException ex)
            {
                AnsiConsole.MarkupLine($"  [red]✗ {ex.Message.EscapeMarkup()}[/]");
                AnsiConsole.MarkupLine("  [yellow](Is the sensor connected to the CALIBRATION RF POWER OUTPUT?)[/]");
                return 1;
            }
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold green]Sensor zero + calibration complete.[/]");
            return 0;
        }

        // ---- Attenuator identification -------------------------------------

        private static AttenuatorConfig ResolveAttenuator(HarnessOptions opt, Bench bench)
        {
            // Manual declaration wins.
            if (opt.XAttenSteps.HasValue)
            {
                var cfg = AttenuatorConfig.Build(xIs8494: opt.XAttenSteps.Value == 1);
                AnsiConsole.MarkupLine($"[grey]Attenuator (declared):[/] X = {cfg.XModel}, Y = {cfg.YModel}");
                return cfg;
            }

            // Interactive prompt when asked.
            if (opt.AskAtten)
            {
                var pick = AnsiConsole.Prompt(new SelectionPrompt<string>()
                    .Title("Which attenuator is on [green]ATTEN X[/]?")
                    .AddChoices("HP 8494 (1 dB, 0-11)", "HP 8496 (10 dB, 0-110)"));
                var cfg = AttenuatorConfig.Build(xIs8494: pick.StartsWith("HP 8494"));
                AnsiConsole.MarkupLine($"[grey]Attenuator (chosen):[/] X = {cfg.XModel}, Y = {cfg.YModel}");
                return cfg;
            }

            // Otherwise auto-identify by measurement (works on hardware and in sim).
            AnsiConsole.MarkupLine("[grey]Identifying attenuators by measurement...[/]");
            var idAtten = bench.MakeAttenuator(AttenuatorConfig.Default());
            var id = AttenuatorIdentifier.Identify(
                bench.Source, idAtten, bench.Receiver, opt.Sweep.SourcePowerDbm, opt.IdFreqMHz, opt.Sweep.SettleMs);

            var color = id.Confident ? "green" : "yellow";
            AnsiConsole.MarkupLine($"[{color}]{id.Summary.EscapeMarkup()}[/]");
            if (!id.Confident)
                AnsiConsole.MarkupLine("[yellow]  (low confidence — re-run with --x-atten to declare explicitly)[/]");
            return id.Config;
        }

        // ---- Signal-presence check -----------------------------------------

        private static int RunDetect(HarnessOptions opt, Bench bench)
        {
            // 0 dB engages no sections, so the attenuator config is irrelevant here.
            var attenuator = bench.MakeAttenuator(AttenuatorConfig.Default());
            var engine = new MeasurementEngine(bench.Source, bench.Lo, attenuator, bench.Receiver, opt.Sweep);

            IReadOnlyList<double> freqs = opt.ExplicitFreq
                ? opt.Sweep.Frequencies().ToList()
                : HarnessOptions.DetectFrequenciesMHz;

            AnsiConsole.MarkupLine(
                $"[grey]Signal detect:[/] source {opt.Sweep.SourcePowerDbm:0.#} dBm, attenuator 0 dB. " +
                "Measures the 8902A RF-frequency reading with the source RF on vs off.");
            AnsiConsole.WriteLine();

            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn(new TableColumn("Freq MHz").RightAligned());
            table.AddColumn("Regime");
            table.AddColumn(new TableColumn("LO MHz").RightAligned());
            table.AddColumn(new TableColumn("8902A reads MHz").RightAligned());
            table.AddColumn("RF on");
            table.AddColumn("RF off");
            table.AddColumn("Signal");

            bool all = true;
            foreach (double f in freqs)
            {
                DetectResult d = engine.DetectSignal(f, opt.DetectThresholdDb);
                all &= d.Detected;
                table.AddRow(
                    $"{d.FreqMHz:0.###}",
                    d.Regime.ToString(),
                    d.Regime == MeasurementRegime.Converted ? $"{d.LoMHz:0.##}" : "—",
                    double.IsNaN(d.MeasuredFreqMHz) ? "—" : $"{d.MeasuredFreqMHz:0.###}",
                    d.SignalWithRfOn ? "[green]signal[/]" : "[grey]none[/]",
                    d.SignalWithRfOff ? "[red]signal[/]" : "[grey]none[/]",
                    d.Detected ? "[green]DETECTED[/]" : "[red]no[/]");
            }
            AnsiConsole.Write(table);

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine(all
                ? "[green]Signal detected through the path at all test frequencies.[/]"
                : "[red]Signal NOT detected at one or more frequencies — check the path / connections.[/]");
            AnsiConsole.MarkupLine("[grey]Presence check only. Full absolute/attenuation accuracy needs the 8902A " +
                                   "cal factors loaded and the sensor calibrated to the 8902A reference output.[/]");
            return all ? 0 : 1;
        }

        // ---- Cal-debug: observe the 8902A status byte vs level -------------

        /// <summary>
        /// Steps the attenuation and prints the 8902A serial-poll status byte and a Tuned RF
        /// Level read at each level, WITHOUT issuing CALIBRATE (so it cannot trigger Error
        /// 35). Reveals how the RECAL/UNCAL status bit tracks the level and where reads floor
        /// out — the ground truth needed to gate the range calibration correctly.
        /// </summary>
        private static int RunCalDebug(HarnessOptions opt, Bench bench)
        {
            double freq = opt.RfPowerFreqMHz;
            var plan = MicrowaveConverter.Plan(freq, bench.Lo.MinFrequencyMHz, bench.Lo.MaxFrequencyMHz);

            bench.Source.SetFrequencyMHz(freq);
            bench.Source.SetPowerDbm(opt.Sweep.SourcePowerDbm);
            bench.Source.RfOn();
            if (plan.Regime == MeasurementRegime.Converted)
            {
                bench.Lo.SetFrequencyMHz(plan.LoMHz);
                bench.Lo.SetPowerDbm(opt.Sweep.LoPowerDbm);
                bench.Lo.RfOn();
            }
            else bench.Lo.RfOff();

            var attenuator = bench.MakeAttenuator(AttenuatorConfig.Default());
            var rx = bench.Receiver;

            rx.BeginAttenuationMeasurement(freq, plan.Regime, plan.LoMHz);
            rx.BeginRangeCalibration();   // enable RECAL/UNCAL status bit + free-run

            AnsiConsole.MarkupLine(
                $"[grey]Cal-debug:[/] {freq:0.###} MHz {plan.Regime}" +
                (plan.Regime == MeasurementRegime.Converted ? $" (LO {plan.LoMHz:0.##} MHz)" : "") +
                $", source {opt.Sweep.SourcePowerDbm:0.#} dBm. Steps attenuation and reads the 8902A " +
                "serial-poll status byte. [bold]No CALIBRATE is issued.[/]");
            AnsiConsole.MarkupLine("[grey]Status bits: 0x01 DataReady  0x02 HP-IB-err  0x04 InstrErr  " +
                                   "0x10 OffsetChg  0x20 RECAL/UNCAL  0x40 SRQ[/]");
            AnsiConsole.WriteLine();

            int settle = Math.Max(opt.Sweep.SettleMs, 300);
            attenuator.SetAttenuationDb(0);
            System.Threading.Thread.Sleep(settle);
            int sbInit = rx.PollStatusByte();
            rx.SetReference();
            int sbRef = rx.PollStatusByte();
            AnsiConsole.MarkupLine($"[grey]After setup @0 dB: SB=0x{sbInit:X2}; after SET REF: SB=0x{sbRef:X2}[/]");
            AnsiConsole.WriteLine();

            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn(new TableColumn("Atten dB").RightAligned());
            table.AddColumn("SB before");
            table.AddColumn("RECAL?");
            table.AddColumn(new TableColumn("Read rel dB").RightAligned());
            table.AddColumn("SB after");

            for (int atten = 0; atten <= 120; atten += 10)
            {
                attenuator.SetAttenuationDb(atten);
                System.Threading.Thread.Sleep(settle);
                int sb1 = rx.PollStatusByte();

                string read;
                try { read = rx.ReadRelativeDb().ToString("0.00", CultureInfo.InvariantCulture); }
                catch (Hp8902AException ex) { read = $"Err {ex.Code}"; try { rx.ClearError(); } catch { } }
                catch (Exception ex) { read = ex.GetType().Name; try { rx.ClearError(); } catch { } }

                int sb2 = rx.PollStatusByte();
                bool recal = (sb1 & 0x20) != 0;
                table.AddRow($"{atten}", $"0x{sb1:X2}", recal ? "[yellow]yes[/]" : "no",
                             read.EscapeMarkup(), $"0x{sb2:X2}");
            }
            AnsiConsole.Write(table);
            attenuator.SetAttenuationDb(0);

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[grey]Send me this table: it shows whether the RECAL bit (0x20) tracks the " +
                                   "level and where reads floor out, so the range-cal gating can be fixed.[/]");
            return 0;
        }

        // ---- Cal-probe: force one CALIBRATE and trace it ------------------

        /// <summary>
        /// Sets up Tuned RF Level at 0 dB, takes SET REF, then forces a single CALIBRATE and
        /// reports the result — distinguishing Error 35 (reference level outside the power
        /// sensor's range) from Error 31/33 (sensor not zeroed/calibrated). Traces every
        /// 8902A command with its status byte. No sensor-cal prompt, so it runs unattended.
        /// </summary>
        private static int RunCalProbe(HarnessOptions opt, Bench bench)
        {
            Hp8902A.DebugLog = s => AnsiConsole.MarkupLine($"[grey]  {s.EscapeMarkup()}[/]");

            double freq = opt.RfPowerFreqMHz;
            var plan = MicrowaveConverter.Plan(freq, bench.Lo.MinFrequencyMHz, bench.Lo.MaxFrequencyMHz);

            bench.Source.SetFrequencyMHz(freq);
            bench.Source.SetPowerDbm(opt.Sweep.SourcePowerDbm);
            bench.Source.RfOn();
            if (plan.Regime == MeasurementRegime.Converted)
            {
                bench.Lo.SetFrequencyMHz(plan.LoMHz);
                bench.Lo.SetPowerDbm(opt.Sweep.LoPowerDbm);
                bench.Lo.RfOn();
            }
            else bench.Lo.RfOff();

            var attenuator = bench.MakeAttenuator(AttenuatorConfig.Default());
            var rx = bench.Receiver;

            AnsiConsole.MarkupLine(
                $"[grey]Cal-probe:[/] {freq:0.###} MHz {plan.Regime}, source {opt.Sweep.SourcePowerDbm:0.#} dBm. " +
                "Forces one Tuned RF Level CALIBRATE at 0 dB and traces it.");
            AnsiConsole.MarkupLine("[grey]Error 35 = reference outside the power-sensor range (raise power); " +
                                   "Error 31/33 = sensor not zeroed/calibrated.[/]");
            AnsiConsole.WriteLine();

            rx.BeginAttenuationMeasurement(freq, plan.Regime, plan.LoMHz);
            rx.BeginRangeCalibration();
            attenuator.SetEngaged(System.Array.Empty<int>());     // 0 dB
            System.Threading.Thread.Sleep(Math.Max(opt.Sweep.SettleMs, 500));
            rx.SetReference();

            // Step down and force a CALIBRATE at each level to find where Error 35 first
            // appears (the manual: as the level drops past the power-sensor's range the
            // recalibration fails with a "level error").
            int settle = Math.Max(opt.Sweep.SettleMs, 500);
            int firstError = -1;
            foreach (int atten in new[] { 0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110 })
            {
                attenuator.SetAttenuationDb(atten);
                System.Threading.Thread.Sleep(settle);
                int sbBefore = SafePoll(rx);
                AnsiConsole.MarkupLine($"[grey]CALIBRATE @ {atten} dB (SB before=0x{sbBefore:X2})...[/]");
                rx.Calibrate();
                int sbAfter = SafePoll(rx);

                string read;
                int code = 0;
                try { read = rx.ReadRelativeDb().ToString("0.00", CultureInfo.InvariantCulture) + " dB"; }
                catch (Hp8902AException ex) { read = $"Error {ex.Code}"; code = ex.Code; try { rx.ClearError(); } catch { } }
                catch (Exception ex) { read = ex.GetType().Name; try { rx.ClearError(); } catch { } }

                bool instrErr = (sbAfter & 0x04) != 0;
                string flag = (code == 35 || instrErr) ? " [red]<-- ERROR 35 / instr-error[/]" : "";
                AnsiConsole.MarkupLine($"   SB after=0x{sbAfter:X2}, read {read.EscapeMarkup()}{flag}");

                if (firstError < 0 && (code == 35 || code == 34 || instrErr)) { firstError = atten; break; }
            }

            AnsiConsole.WriteLine();
            if (firstError < 0)
                AnsiConsole.MarkupLine("[green]No Error 35 across the probed range — CALIBRATE succeeded at every level.[/]");
            else
                AnsiConsole.MarkupLine($"[yellow]CALIBRATE first failed at {firstError} dB — recalibration past this level is " +
                                       "outside the sensor's range. The fix: range-cal only down to here, then stop (catch it).[/]");
            return firstError < 0 ? 0 : 1;
        }

        private static int SafePoll(IMeasuringReceiver rx)
        {
            try { return rx.PollStatusByte(); } catch { return 0; }
        }

        // ---- Test 3: per-attenuator individual settings -------------------

        private static int RunPerAtten(HarnessOptions opt, MeasurementEngine engine, AttenuatorConfig config)
        {
            double freq = opt.RfPowerFreqMHz;

            // Split the two attenuators: the 8494 (1 dB steps, 0-11) and the 8496 (10 dB,
            // 0-110). Exercise each across its own settings with the other bypassed.
            bool xIsFine = config.XModel.Contains("8494");
            var fine = xIsFine ? config.X : config.Y;
            var coarse = xIsFine ? config.Y : config.X;
            string fineModel = xIsFine ? config.XModel : config.YModel;
            string coarseModel = xIsFine ? config.YModel : config.XModel;

            var settings = new List<AttenSetting>();
            for (int v = 1; v <= 11; v++)
            {
                var d = CommandBuilder.Solve(fine, v);
                if (d != null) settings.Add(new AttenSetting(fineModel, v, d));
            }
            for (int v = 10; v <= 110; v += 10)
            {
                var d = CommandBuilder.Solve(coarse, v);
                if (d != null) settings.Add(new AttenSetting(coarseModel, v, d));
            }

            AnsiConsole.MarkupLine(
                $"[grey]Per-attenuator (Test 3):[/] {freq:0.###} MHz, {settings.Count} points — " +
                $"{fineModel.EscapeMarkup()} 1-11 dB and {coarseModel.EscapeMarkup()} 10-110 dB, " +
                "each exercised with the other at 0 dB.");
            AnsiConsole.WriteLine();

            Action<int, int, AttenPointResult> prog = (i, n, p) =>
            {
                string body = p.Error != null
                    ? $"[red]{p.Error.EscapeMarkup()}[/]"
                    : $"meas {p.MeasuredAttenuationDb,7:0.00} dB  (err {p.ErrorDb:+0.00;-0.00;0.00})";
                AnsiConsole.MarkupLine($"  {i,2}/{n}  {p.Group.EscapeMarkup()}  set {p.CommandedDb,3} dB -> {body}");
            };

            FreqPointResult r = engine.MeasureSettings(freq, settings, prog);

            // Results table grouped by attenuator.
            var table = new Table().Border(TableBorder.Rounded)
                .Title($"{freq:0.###} MHz  {r.Regime}".EscapeMarkup());
            table.AddColumn("Attenuator");
            table.AddColumn(new TableColumn("Set dB").RightAligned());
            table.AddColumn("Cmd");
            table.AddColumn(new TableColumn("Meas dB").RightAligned());
            table.AddColumn(new TableColumn("Error dB").RightAligned());

            double worst = 0; int errors = 0; string worstWhere = "";
            foreach (var p in r.Points)
            {
                if (p.Error != null)
                {
                    errors++;
                    table.AddRow(p.Group.EscapeMarkup(), p.CommandedDb.ToString(), p.Command.EscapeMarkup(),
                        "[red]—[/]", $"[red]{p.Error.EscapeMarkup()}[/]");
                    continue;
                }
                if (Math.Abs(p.ErrorDb) > worst) { worst = Math.Abs(p.ErrorDb); worstWhere = $"{p.Group} @ {p.CommandedDb} dB"; }
                string err = $"{p.ErrorDb:+0.00;-0.00;0.00}";
                string errCell = Math.Abs(p.ErrorDb) <= opt.ToleranceDb ? $"[green]{err}[/]" : $"[red]{err}[/]";
                table.AddRow(p.Group.EscapeMarkup(), p.CommandedDb.ToString(), p.Command.EscapeMarkup(),
                    $"{p.MeasuredAttenuationDb:0.00}", errCell);
            }
            AnsiConsole.Write(table);

            try
            {
                using var csv = OpenCsvWriter(opt.CsvPath);
                csv.WriteLine("attenuator,set_db,command,measured_atten_db,error_db,error");
                foreach (var p in r.Points)
                    csv.WriteLine(string.Join(",", new[]
                    {
                        (p.Group ?? "").Replace(",", ";"), p.CommandedDb.ToString(CultureInfo.InvariantCulture),
                        p.Command, F(p.MeasuredAttenuationDb), F(p.ErrorDb), (p.Error ?? "").Replace(",", ";")
                    }));
            }
            catch (Exception ex) { AnsiConsole.MarkupLine($"[yellow]CSV not written: {ex.Message.EscapeMarkup()}[/]"); }

            AnsiConsole.WriteLine();
            bool pass = errors == 0 && worst <= opt.ToleranceDb;
            var summary = new Table().Border(TableBorder.Heavy);
            summary.AddColumn("Result"); summary.AddColumn("");
            summary.AddRow("Attenuators", $"{fineModel}, {coarseModel}".EscapeMarkup());
            summary.AddRow("Points", r.Points.Count.ToString());
            summary.AddRow("Worst |error|", $"{worst:0.00} dB  ({worstWhere})".EscapeMarkup());
            if (errors > 0) summary.AddRow("8902A errors", $"[red]{errors} point(s)[/]");
            summary.AddRow("Tolerance", $"±{opt.ToleranceDb:0.#} dB");
            summary.AddRow("CSV", Path.GetFullPath(opt.CsvPath).EscapeMarkup());
            summary.AddRow("Verdict", pass ? "[green]PASS[/]" : "[red]FAIL[/]");
            AnsiConsole.Write(summary);

            return pass ? 0 : 1;
        }

        // ---- Diagnostic: isolate the 8496's two 40 dB sections --------------

        private static int RunSectionTest(HarnessOptions opt, MeasurementEngine engine, AttenuatorConfig config)
        {
            double freq = opt.RfPowerFreqMHz;
            bool xIsFine = config.XModel.Contains("8494");
            var coarse = (xIsFine ? config.Y : config.X);          // the 8496 (10 dB step) sections
            string coarseModel = xIsFine ? config.YModel : config.XModel;

            var s10 = coarse.FirstOrDefault(s => s.Decibels == 10);
            var s40 = coarse.Where(s => s.Decibels == 40).ToList();
            if (s10 == null || s40.Count < 2)
            {
                AnsiConsole.MarkupLine("[red]--section-test needs the 8496 (a 10 dB + two 40 dB sections) — " +
                                       $"found {coarseModel.EscapeMarkup()}.[/]");
                return 1;
            }
            int d10 = s10.Digit, d3 = s40[0].Digit, d4 = s40[1].Digit; // d3 = section 3, d4 = section 4

            var settings = new List<AttenSetting>
            {
                new AttenSetting($"40 via section 3 (digit {d3})", 40, new[] { d3 }),
                new AttenSetting($"40 via section 4 (digit {d4})", 40, new[] { d4 }),
                new AttenSetting($"10 (digit {d10})", 10, new[] { d10 }),
                new AttenSetting($"50 = 10 + section 3 ({d10}+{d3})", 50, new[] { d10, d3 }),
                new AttenSetting($"50 = 10 + section 4 ({d10}+{d4})", 50, new[] { d10, d4 }),
            };

            AnsiConsole.MarkupLine(
                $"[grey]8496 section isolation:[/] {freq:0.###} MHz — comparing the two 40 dB sections " +
                $"(digit {d3} = section 3 vs digit {d4} = section 4), alone and as 50 dB with the 10 dB (digit {d10}).");
            AnsiConsole.WriteLine();

            Action<int, int, AttenPointResult> prog = (i, n, p) =>
            {
                string body = p.Error != null
                    ? $"[red]{p.Error.EscapeMarkup()}[/]"
                    : $"meas {p.MeasuredAttenuationDb,7:0.00} dB  (err {p.ErrorDb:+0.00;-0.00;0.00})";
                AnsiConsole.MarkupLine($"  {i,2}/{n}  set {p.CommandedDb,3} dB  {p.Group.EscapeMarkup()}  -> {body}");
            };

            FreqPointResult r = engine.MeasureSettings(freq, settings, prog);

            var table = new Table().Border(TableBorder.Rounded).Title($"{freq:0.###} MHz  {r.Regime}".EscapeMarkup());
            table.AddColumn("What"); table.AddColumn(new TableColumn("Set dB").RightAligned());
            table.AddColumn("Cmd"); table.AddColumn(new TableColumn("Meas dB").RightAligned());
            table.AddColumn(new TableColumn("Error dB").RightAligned());
            foreach (var p in r.Points)
            {
                string measCell = p.Error != null ? $"[red]{p.Error.EscapeMarkup()}[/]" : $"{p.MeasuredAttenuationDb:0.00}";
                string errCell = p.Error != null ? "[red]—[/]" : $"{p.ErrorDb:+0.00;-0.00;0.00}";
                table.AddRow(p.Group.EscapeMarkup(), p.CommandedDb.ToString(), p.Command.EscapeMarkup(), measCell, errCell);
            }
            AnsiConsole.Write(table);

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[grey]Read section 3 vs section 4: if both ~40 (and both 50s match), the sections are " +
                "fine and the deep-boundary step is in the MEASUREMENT. If one 40 dB section reads off, that section is the culprit.[/]");
            return 0;
        }

        // ---- #15: per-section characterize + sum ---------------------------

        /// <summary>The ~dB above which a total can't be measured DIRECTLY through the 11793A path
        /// (its floor is −100 dBm; with the reference near −2 dBm that caps direct reads at ~95–98 dB —
        /// SharedMemory.md / Microwave Product Note). Above this, only the section SUM is valid.</summary>
        private const double DirectFloorDb = 95.0;

        /// <summary>
        /// #15 — per-section characterize + SUM. Measures each attenuator section on its own (every
        /// section is ≤40 dB, so each read stays well above the ~95 dB direct floor), then sums the
        /// measured sections to synthesize the totals — including the full 110/121 dB that cannot be
        /// measured directly through the converter. Sections add linearly (proved by --section-test to
        /// 0.01 dB), so the sum is a valid full-range measurement. Prints a per-section characterization
        /// table and a synthesized-total table (nominal vs summed), and writes both to CSV.
        /// </summary>
        private static int RunSectionSum(HarnessOptions opt, MeasurementEngine engine, AttenuatorConfig config)
        {
            double freq = opt.RfPowerFreqMHz;

            // One setting per physical section, engaged alone against the 0 dB reference.
            var sections = config.AllSections.OrderBy(s => s.Digit).ToList();
            var settings = sections
                .Select(s => new AttenSetting(SectionLabel(config, s), s.Decibels, new[] { s.Digit }))
                .ToList();

            AnsiConsole.MarkupLine(
                $"[grey]Per-section characterize + sum (#15):[/] {freq:0.###} MHz — measuring each of the " +
                $"{sections.Count} sections alone (all ≤40 dB, above the ~{DirectFloorDb:0} dB direct floor), then " +
                "summing to synthesize totals that can't be measured directly.");
            AnsiConsole.WriteLine();

            Action<int, int, AttenPointResult> prog = (i, n, p) =>
            {
                string body = p.Error != null
                    ? $"[red]{p.Error.EscapeMarkup()}[/]"
                    : $"meas {p.MeasuredAttenuationDb,7:0.00} dB  (err {p.ErrorDb:+0.00;-0.00;0.00})";
                AnsiConsole.MarkupLine($"  {i,2}/{n}  {p.Group.EscapeMarkup()}  nominal {p.CommandedDb,3} dB -> {body}");
            };

            FreqPointResult r = engine.MeasureSettings(freq, settings, prog);

            // digit -> measured section attenuation (NaN if that section's read errored).
            var measured = new Dictionary<int, double>();
            for (int i = 0; i < sections.Count; i++)
                measured[sections[i].Digit] = r.Points[i].Error != null ? double.NaN : r.Points[i].MeasuredAttenuationDb;

            // --- per-section characterization table ---
            var secTable = new Table().Border(TableBorder.Rounded)
                .Title($"{freq:0.###} MHz  {r.Regime}  — per-section".EscapeMarkup());
            secTable.AddColumn("Section"); secTable.AddColumn(new TableColumn("Nominal dB").RightAligned());
            secTable.AddColumn("Cmd"); secTable.AddColumn(new TableColumn("Meas dB").RightAligned());
            secTable.AddColumn(new TableColumn("Error dB").RightAligned());

            double worstSection = 0; int sectionErrors = 0;
            for (int i = 0; i < sections.Count; i++)
            {
                var p = r.Points[i];
                if (p.Error != null)
                {
                    sectionErrors++;
                    secTable.AddRow(p.Group.EscapeMarkup(), p.CommandedDb.ToString(), p.Command.EscapeMarkup(),
                        "[red]—[/]", $"[red]{p.Error.EscapeMarkup()}[/]");
                    continue;
                }
                worstSection = Math.Max(worstSection, Math.Abs(p.ErrorDb));
                string ec = $"{p.ErrorDb:+0.00;-0.00;0.00}";
                string errCell = Math.Abs(p.ErrorDb) <= opt.ToleranceDb ? $"[green]{ec}[/]" : $"[red]{ec}[/]";
                secTable.AddRow(p.Group.EscapeMarkup(), p.CommandedDb.ToString(), p.Command.EscapeMarkup(),
                    $"{p.MeasuredAttenuationDb:0.00}", errCell);
            }
            AnsiConsole.Write(secTable);

            // Characterized full scale = sum of every section, synthesized entirely from in-range reads.
            bool anyMissing = sections.Any(s => double.IsNaN(measured[s.Digit]));
            double fullScale = anyMissing ? double.NaN : sections.Sum(s => measured[s.Digit]);
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine(anyMissing
                ? "[yellow]Full-scale sum unavailable — at least one section read failed.[/]"
                : $"[grey]Characterized full scale (Σ all sections):[/] [green]{fullScale:0.00} dB[/] " +
                  $"(nominal {config.MaxDecibels}) — synthesized from reads that never went below the direct floor.");
            AnsiConsole.WriteLine();

            // --- validation by SUM: totals synthesized from the measured sections ---
            var targets = new List<int>();
            for (int v = 10; v <= config.MaxDecibels; v += 10) targets.Add(v);
            if (targets.Count == 0 || targets[targets.Count - 1] != config.MaxDecibels) targets.Add(config.MaxDecibels);

            var sumTable = new Table().Border(TableBorder.Rounded).Title("Synthesized totals (Σ measured sections)");
            sumTable.AddColumn(new TableColumn("Target dB").RightAligned());
            sumTable.AddColumn("Sections (dB)");
            sumTable.AddColumn(new TableColumn("Summed dB").RightAligned());
            sumTable.AddColumn(new TableColumn("Error dB").RightAligned());
            sumTable.AddColumn("Direct?");

            var allSections = config.AllSections.ToList();
            double worstSum = 0; int sumErrors = 0;
            var sumRows = new List<(int target, string parts, double summed, double error, bool beyond, bool failed)>();
            foreach (int target in targets)
            {
                var digits = CommandBuilder.Solve(allSections, target);
                if (digits == null) continue;                    // unreachable total — skip
                string parts = string.Join("+", digits.OrderByDescending(d => config.ForDigit(d).Decibels)
                                                       .Select(d => config.ForDigit(d).Decibels.ToString()));
                bool beyond = target >= DirectFloorDb;
                bool failed = digits.Any(d => double.IsNaN(measured[d]));
                double summed = failed ? double.NaN : digits.Sum(d => measured[d]);
                double error = summed - target;

                if (failed)
                {
                    sumErrors++;
                    sumTable.AddRow(target.ToString(), parts, "[red]—[/]", "[red]section read failed[/]",
                        beyond ? "[yellow]sum only[/]" : "yes");
                }
                else
                {
                    worstSum = Math.Max(worstSum, Math.Abs(error));
                    string ec = $"{error:+0.00;-0.00;0.00}";
                    // Error vs nominal folds in DUT pad tolerance, not just measurement — flag (yellow),
                    // don't fail, when it exceeds the per-point tolerance.
                    string errCell = Math.Abs(error) <= opt.ToleranceDb ? $"[green]{ec}[/]" : $"[yellow]{ec}[/]";
                    sumTable.AddRow(target.ToString(), parts, $"{summed:0.00}", errCell,
                        beyond ? "[yellow]no (sum only)[/]" : "yes");
                }
                sumRows.Add((target, parts, summed, error, beyond, failed));
            }
            AnsiConsole.Write(sumTable);

            // --- CSV (per-section rows then synthesized-total rows) ---
            try
            {
                using var csv = OpenCsvWriter(opt.CsvPath);
                csv.WriteLine("kind,label_or_target,command_or_parts,measured_or_summed_db,error_db,beyond_direct_floor,error");
                for (int i = 0; i < sections.Count; i++)
                {
                    var p = r.Points[i];
                    csv.WriteLine(string.Join(",", new[]
                    {
                        "section", (p.Group ?? "").Replace(",", ";"), p.Command,
                        F(p.MeasuredAttenuationDb), F(p.ErrorDb), "", (p.Error ?? "").Replace(",", ";")
                    }));
                }
                foreach (var s in sumRows)
                    csv.WriteLine(string.Join(",", new[]
                    {
                        "sum", s.target.ToString(CultureInfo.InvariantCulture), s.parts,
                        F(s.summed), F(s.error), s.beyond ? "1" : "0", s.failed ? "section read failed" : ""
                    }));
            }
            catch (Exception ex) { AnsiConsole.MarkupLine($"[yellow]CSV not written: {ex.Message.EscapeMarkup()}[/]"); }

            // Verdict: #15 succeeds when every section read cleanly (so a valid full-scale sum exists).
            // Absolute accuracy vs the nominal labels is a DUT+bench matter, surfaced for the operator.
            AnsiConsole.WriteLine();
            bool pass = sectionErrors == 0 && sumErrors == 0;
            var summary = new Table().Border(TableBorder.Heavy);
            summary.AddColumn("Result"); summary.AddColumn("");
            summary.AddRow("Method", "#15 per-section characterize + sum");
            summary.AddRow("Frequency", $"{freq:0.###} MHz  ({r.Regime})".EscapeMarkup());
            summary.AddRow("Sections read", $"{sections.Count - sectionErrors}/{sections.Count}");
            summary.AddRow("Full scale (Σ)", double.IsNaN(fullScale) ? "[red]n/a[/]" : $"{fullScale:0.00} dB (nominal {config.MaxDecibels})");
            summary.AddRow("Worst section |err vs nominal|", $"{worstSection:0.00} dB");
            summary.AddRow("Worst sum |err vs nominal|", $"{worstSum:0.00} dB");
            if (sectionErrors > 0) summary.AddRow("Section read failures", $"[red]{sectionErrors}[/]");
            summary.AddRow("CSV", Path.GetFullPath(opt.CsvPath).EscapeMarkup());
            summary.AddRow("Verdict", pass ? "[green]PASS[/]" : "[red]FAIL[/]");
            AnsiConsole.Write(summary);

            return pass ? 0 : 1;
        }

        /// <summary>Label for one section: its owning model (X = digits 1-4, Y = 5-8) and digit.</summary>
        private static string SectionLabel(AttenuatorConfig config, HpAttenuator.Model.Section s) =>
            $"{(s.Digit <= 4 ? config.XModel : config.YModel)} §{s.Digit}";

        // ---- Test 1: single-point RF power readback ------------------------

        private static int RunRfPower(HarnessOptions opt, Bench bench)
        {
            // Cal factors (both Normal + Frequency-Offset tables) are loaded exactly once,
            // by the mandatory sensor-cal step that runs before this. Loading them again
            // here would re-clear the offset table (37.9SP clears all) -> 8902A Error 15.
            // (Use --load-cal with --skip-sensor-cal if you bypass the sensor cal.)

            // 0 dB (default) engages no sections, so the attenuator wiring is irrelevant.
            var attenuator = bench.MakeAttenuator(AttenuatorConfig.Default());
            var engine = new MeasurementEngine(bench.Source, bench.Lo, attenuator, bench.Receiver, opt.Sweep);

            AnsiConsole.MarkupLine(
                $"[grey]RF power readback (Test 1):[/] source {opt.Sweep.SourcePowerDbm:0.#} dBm @ " +
                $"{opt.RfPowerFreqMHz:0.###} MHz, attenuator {opt.RfPowerAttenDb} dB.");
            AnsiConsole.WriteLine();

            RfPowerResult r = engine.MeasureRfPower(opt.RfPowerFreqMHz, opt.RfPowerAttenDb);

            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("Quantity");
            table.AddColumn(new TableColumn("Value").RightAligned());
            table.AddRow("Frequency", $"{r.FreqMHz:0.###} MHz");
            table.AddRow("Regime", r.Regime.ToString());
            if (r.Regime == MeasurementRegime.Converted)
            {
                table.AddRow("LO", $"{r.LoMHz:0.##} MHz");
                table.AddRow("IF", $"{r.IfMHz:0.##} MHz");
            }
            table.AddRow("Source level", $"{r.SourcePowerDbm:0.##} dBm");
            table.AddRow("Attenuator", $"{r.AttenuationDb} dB");
            if (r.Error != null)
                table.AddRow("Measured power", $"[red]{r.Error.EscapeMarkup()}[/]");
            else
            {
                table.AddRow("Measured power", $"[green]{r.MeasuredPowerDbm:0.00} dBm[/]");
                table.AddRow("Implied path loss", $"{r.ImpliedPathLossDb:0.00} dB");
            }
            AnsiConsole.Write(table);

            if (!string.IsNullOrEmpty(r.Warning))
                AnsiConsole.MarkupLine($"[yellow]! {r.Warning.EscapeMarkup()}[/]");

            AnsiConsole.WriteLine();
            if (r.Error != null)
            {
                AnsiConsole.MarkupLine("[red]No valid RF power reading — check the path, LO, and connections " +
                                       "(e.g. 8902A Error 96 = no signal sensed).[/]");
                return 1;
            }
            AnsiConsole.MarkupLine($"[green]RF power measured: {r.MeasuredPowerDbm:0.00} dBm.[/]");
            return 0;
        }

        // ---- Sweep + reporting ---------------------------------------------

        private static int RunSweep(HarnessOptions opt, MeasurementEngine engine, AttenuatorConfig config)
        {
            IReadOnlyList<double> frequencies =
                (opt.Full || opt.ExplicitFreq)
                    ? opt.Sweep.Frequencies().ToList()
                    : HarnessOptions.QuickFrequenciesMHz;

            string detectorTag = opt.Sweep.TrackMode
                ? "track mode (32.9SP, AVG)"
                : opt.Sweep.Detector == TrflDetector.Synchronous
                    ? "synchronous (4.0SP, ~-127 dBm)" : "average (4.4SP, ~-100 dBm)";
            string tuneTag = opt.Sweep.Tuning == TrflTuning.Auto ? "auto-tune (#3, unverified)" : "manual-tune";
            AnsiConsole.MarkupLine(
                $"[grey]Sweep:[/] {frequencies.Count} freqs, attenuation {opt.Sweep.AttenStartDb}-{opt.Sweep.AttenStopDb} dB " +
                $"step {opt.Sweep.AttenStepDb}, source {opt.Sweep.SourcePowerDbm:0.#} dBm, {detectorTag} detector, {tuneTag}, " +
                $"tolerance ±{opt.ToleranceDb:0.#} dB");
            AnsiConsole.WriteLine();

            bool detailed = frequencies.Count <= 12;
            double worstError = 0;
            int measured = 0;
            int errorPoints = 0;
            int floorPoints = 0;                       // #13: points that saturated at the converter floor
            double deepestMeasured = double.NaN;       // deepest attenuation actually tracked, across freqs
            string worstWhere = "";

            StreamWriter csvWriter;
            try { csvWriter = OpenCsvWriter(opt.CsvPath); }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Cannot open CSV '{opt.CsvPath.EscapeMarkup()}':[/] {ex.Message.EscapeMarkup()}");
                return 2;
            }

            using (var csv = csvWriter)
            {
                csv.WriteLine("freq_mhz,regime,lo_mhz,if_mhz,leveled_ref_dbm,leveled_src_dbm,commanded_db,command,measured_rel_db,measured_atten_db,expected_atten_db,error_db,floor_limited,error");

                foreach (double freq in frequencies)
                {
                    // Live per-step progress for short (single/few-frequency) detailed runs.
                    Action<int, int, AttenPointResult> prog = null;
                    if (detailed)
                    {
                        prog = (i, n, p) =>
                        {
                            string body = p.Error != null
                                ? $"[red]{p.Error.EscapeMarkup()}[/]"
                                : $"meas {p.MeasuredAttenuationDb,7:0.00} dB  (err {p.ErrorDb:+0.00;-0.00;0.00})";
                            // No square brackets in the plain text — Spectre parses them as markup.
                            AnsiConsole.MarkupLine($"  {i,3}/{n}  set {p.CommandedDb,3} dB -> {body}");
                        };
                    }

                    FreqPointResult r = engine.MeasureFrequency(freq, prog);
                    measured++;

                    foreach (var p in r.Points)
                    {
                        csv.WriteLine(string.Join(",", new[]
                        {
                            F(r.FreqMHz), r.Regime.ToString(), F(r.LoMHz), F(r.IfMHz),
                            F(r.ReferencePowerDbm), F(r.LeveledSourcePowerDbm),
                            p.CommandedDb.ToString(CultureInfo.InvariantCulture), p.Command,
                            F(p.MeasuredRelativeDb), F(p.MeasuredAttenuationDb),
                            F(p.ExpectedAttenuationDb), F(p.ErrorDb),
                            p.FloorLimited ? "1" : "0",
                            (p.Error ?? "").Replace(",", ";")
                        }));

                        if (p.Error != null) errorPoints++;
                        else if (p.FloorLimited) floorPoints++;    // #13: measurement floor, not an error
                        else if (Math.Abs(p.ErrorDb) > worstError)
                        {
                            worstError = Math.Abs(p.ErrorDb);
                            worstWhere = $"{r.FreqMHz:0.###} MHz @ {p.CommandedDb} dB";
                        }
                    }
                    if (!double.IsNaN(r.DeepestMeasuredDb))
                        deepestMeasured = Math.Max(deepestMeasured, r.DeepestMeasuredDb);

                    // Small detailed runs get a table; large ones already streamed progress.
                    if (detailed && r.Points.Count <= 15) RenderFrequencyTable(r, opt.ToleranceDb);
                    else RenderFrequencyLine(r, opt.ToleranceDb);
                }
            }

            AnsiConsole.WriteLine();
            bool pass = errorPoints == 0 && worstError <= opt.ToleranceDb;
            var summary = new Table().Border(TableBorder.Heavy);
            summary.AddColumn("Result"); summary.AddColumn("");
            summary.AddRow("Attenuator", $"X = {config.XModel}, Y = {config.YModel}".EscapeMarkup());
            summary.AddRow("Frequencies measured", measured.ToString());
            summary.AddRow("Worst |error|", $"{worstError:0.00} dB  ({worstWhere})");
            if (errorPoints > 0) summary.AddRow("8902A errors", $"[red]{errorPoints} point(s)[/]");
            if (floorPoints > 0)
            {
                summary.AddRow("Floor-limited (#13)", $"[yellow]{floorPoints} point(s)[/] — saturated at the " +
                    $"~{opt.Sweep.FloorDbm:0} dBm converter floor; excluded from the verdict");
                summary.AddRow("Deepest measured", double.IsNaN(deepestMeasured) ? "—" : $"{deepestMeasured:0.0} dB");
            }
            summary.AddRow("Tolerance", $"±{opt.ToleranceDb:0.#} dB");
            summary.AddRow("CSV", Path.GetFullPath(opt.CsvPath).EscapeMarkup());
            summary.AddRow("Verdict", pass ? "[green]PASS[/]" : "[red]FAIL[/]");
            AnsiConsole.Write(summary);

            return pass ? 0 : 1;
        }

        private static void RenderFrequencyTable(FreqPointResult r, double tol)
        {
            var header = $"{r.FreqMHz:0.###} MHz  {r.Regime}" +
                         (r.Regime == MeasurementRegime.Converted
                             ? $"  LO={r.LoMHz:0.##} MHz IF={r.IfMHz:0.##} MHz" : "") +
                         LevelTag(r);

            var table = new Table().Border(TableBorder.Rounded).Title(header.EscapeMarkup());
            table.AddColumn(new TableColumn("Set dB").RightAligned());
            table.AddColumn("Cmd");
            table.AddColumn(new TableColumn("Meas rel dB").RightAligned());
            table.AddColumn(new TableColumn("Meas att dB").RightAligned());
            table.AddColumn(new TableColumn("Error dB").RightAligned());

            foreach (var p in r.Points)
            {
                if (p.Error != null)
                {
                    table.AddRow(p.CommandedDb.ToString(), p.Command.EscapeMarkup(),
                        "[red]—[/]", "[red]—[/]", $"[red]{p.Error.EscapeMarkup()}[/]");
                    continue;
                }
                string err = $"{p.ErrorDb:+0.00;-0.00;0.00}";
                string errCell = p.FloorLimited
                    ? $"[yellow]FLOOR {err}[/]"                                   // #13: saturated, not an error
                    : Math.Abs(p.ErrorDb) <= tol ? $"[green]{err}[/]" : $"[red]{err}[/]";
                table.AddRow(
                    p.CommandedDb.ToString(),
                    p.Command.EscapeMarkup(),
                    $"{p.MeasuredRelativeDb:0.00}",
                    $"{p.MeasuredAttenuationDb:0.00}",
                    errCell);
            }
            if (!string.IsNullOrEmpty(r.Warning))
                table.Caption(("! " + r.Warning).EscapeMarkup());
            AnsiConsole.Write(table);
        }

        private static void RenderFrequencyLine(FreqPointResult r, double tol)
        {
            bool anyError = r.Points.Exists(p => p.Error != null);
            string flag = anyError ? "[red]ER[/]" : (r.MaxAbsErrorDb <= tol ? "[green]ok[/]" : "[red]!![/]");
            string warn = string.IsNullOrEmpty(r.Warning)
                ? ""
                : " [yellow](" + (r.Warning.Length > 60 ? r.Warning.Substring(0, 60) + "…" : r.Warning).EscapeMarkup() + ")[/]";
            // #13: note floor-limited points (excluded from max|err|) and the honest usable depth.
            string floor = r.FloorLimitedCount > 0
                ? $" [yellow]({r.FloorLimitedCount} floor, deepest {r.DeepestMeasuredDb:0.0} dB)[/]"
                : "";
            // No square brackets in the plain text — Spectre would parse them as markup.
            AnsiConsole.MarkupLine(
                $"{flag} {r.FreqMHz,9:0.###} MHz  {r.Regime,-9}  " +
                $"max|err|={r.MaxAbsErrorDb:0.00} dB{LevelTag(r)}{floor}{warn}");
        }

        /// <summary>Compact "ref X dBm @ src Y dBm" tag for the leveled 0 dB reference (#16); empty
        /// when leveling was off / the reference level wasn't captured.</summary>
        private static string LevelTag(FreqPointResult r)
        {
            if (double.IsNaN(r.ReferencePowerDbm)) return "";
            string src = double.IsNaN(r.LeveledSourcePowerDbm) ? "" : $" @ src {r.LeveledSourcePowerDbm:+0.0;-0.0;0.0} dBm";
            return $"  ref {r.ReferencePowerDbm:+0.0;-0.0;0.0} dBm{src}";
        }

        private static string F(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

        /// <summary>Opens a truncating StreamWriter for a CSV, creating its parent directory first so
        /// the default DebugResults/ path (or any --out subfolder) works on a fresh checkout.</summary>
        private static StreamWriter OpenCsvWriter(string path)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            return new StreamWriter(path, false);
        }

        /// <summary>Parses the 8902A 37.4SP table-size response (e.g. "+0000000017E+00") to an int; -1 on failure.</summary>
        private static int ParseTableSize(string raw)
        {
            if (double.TryParse(raw?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                return (int)Math.Round(v);
            return -1;
        }
    }
}
