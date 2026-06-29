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

            var disposables = new List<IDisposable>();
            try
            {
                // The sensor cal steps only need the 8902A — don't open the whole bench.
                if (opt.SensorCal || opt.SensorZero || opt.SensorCalibrate)
                {
                    var receiver = BuildReceiverOnly(opt, disposables);
                    if (opt.SensorCal) return RunSensorCalInteractive(receiver);
                    return opt.SensorZero ? RunSensorZero(receiver) : RunSensorCalibrate(receiver);
                }

                Bench bench = BuildBench(opt, disposables);

                // Settling and range calibration only matter on real hardware.
                if (bench.IsSimulated) { opt.Sweep.SettleMs = 0; opt.Sweep.RangeCalibrate = false; }
                if (opt.NoCalPass) opt.Sweep.RangeCalibrate = false;

                if (opt.LoadCal)
                {
                    AnsiConsole.MarkupLine("[grey]Loading converter cal factors into the 8902A...[/]");
                    bench.Receiver.Reset();
                    bench.Receiver.LoadCalFactors(ConverterCalFactors.ReferenceCf, ConverterCalFactors.Default);
                }

                if (opt.Detect)
                    return RunDetect(opt, bench);

                // Mandatory: the 8902A Tuned RF Level measurement requires a calibrated
                // power sensor first. This pauses for the operator to use the CAL output.
                if (!bench.IsSimulated && !opt.SkipSensorCal)
                {
                    if (!InteractiveSensorCalibrate(bench.Receiver))
                    {
                        AnsiConsole.MarkupLine("[red]Sensor not calibrated — aborting (measurement needs it).[/]");
                        return 1;
                    }
                    AnsiConsole.MarkupLine("Restore the measurement connections (sensor / converter IF as needed), " +
                                           "then press [green]Enter[/] to start the measurement...");
                    Console.ReadLine();
                }

                if (opt.RfPower)
                    return RunRfPower(opt, bench);

                AttenuatorConfig config = ResolveAttenuator(opt, bench);

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
            // The 8902A's deepest settled read is <10 s; 15 s gives headroom while letting
            // genuinely unmeasurable points (below the receiver floor) fail fast instead of
            // stalling 30 s each at the bottom of a deep sweep.
            var rxLink = Open(opt.AddrReceiver, disposables, 15000);
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
            var rx = new Hp8902A(Open(opt.AddrReceiver, disposables, 30000));
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
        private static int RunSensorCalInteractive(IMeasuringReceiver receiver)
            => InteractiveSensorCalibrate(receiver) ? 0 : 1;

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

            AnsiConsole.MarkupLine(
                $"[grey]Sweep:[/] {frequencies.Count} freqs, attenuation {opt.Sweep.AttenStartDb}-{opt.Sweep.AttenStopDb} dB " +
                $"step {opt.Sweep.AttenStepDb}, source {opt.Sweep.SourcePowerDbm:0.#} dBm, tolerance ±{opt.ToleranceDb:0.#} dB");
            AnsiConsole.WriteLine();

            bool detailed = frequencies.Count <= 12;
            double worstError = 0;
            int measured = 0;
            int errorPoints = 0;
            string worstWhere = "";

            StreamWriter csvWriter;
            try { csvWriter = new StreamWriter(opt.CsvPath, false); }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Cannot open CSV '{opt.CsvPath.EscapeMarkup()}':[/] {ex.Message.EscapeMarkup()}");
                return 2;
            }

            using (var csv = csvWriter)
            {
                csv.WriteLine("freq_mhz,regime,lo_mhz,if_mhz,commanded_db,command,measured_rel_db,measured_atten_db,expected_atten_db,error_db,error");

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
                            p.CommandedDb.ToString(CultureInfo.InvariantCulture), p.Command,
                            F(p.MeasuredRelativeDb), F(p.MeasuredAttenuationDb),
                            F(p.ExpectedAttenuationDb), F(p.ErrorDb),
                            (p.Error ?? "").Replace(",", ";")
                        }));

                        if (p.Error != null) errorPoints++;
                        else if (Math.Abs(p.ErrorDb) > worstError)
                        {
                            worstError = Math.Abs(p.ErrorDb);
                            worstWhere = $"{r.FreqMHz:0.###} MHz @ {p.CommandedDb} dB";
                        }
                    }

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
                             ? $"  LO={r.LoMHz:0.##} MHz IF={r.IfMHz:0.##} MHz" : "");

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
                string errCell = Math.Abs(p.ErrorDb) <= tol ? $"[green]{err}[/]" : $"[red]{err}[/]";
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
            string warn = string.IsNullOrEmpty(r.Warning) ? "" : " [yellow](LO-below)[/]";
            // No square brackets in the plain text — Spectre would parse them as markup.
            AnsiConsole.MarkupLine(
                $"{flag} {r.FreqMHz,9:0.###} MHz  {r.Regime,-9}  " +
                $"max|err|={r.MaxAbsErrorDb:0.00} dB{warn}");
        }

        private static string F(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
    }
}
