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
                    bench.Receiver.LoadOffsetCalFactors(ConverterCalFactors.ReferenceCf, ConverterCalFactors.Default);
                }

                if (opt.Detect)
                    return RunDetect(opt, bench);

                AttenuatorConfig config = ResolveAttenuator(opt, bench);

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
            // The 8902A's settled read can take ~10 s (averaging), so give it headroom.
            var rxLink = Open(opt.AddrReceiver, disposables, 30000);
            var attLink = Open(opt.AddrAttenuator, disposables);

            var receiver = new Hp8902A(rxLink) { SettleMilliseconds = 0 };
            return new Bench
            {
                Source = new Hp8340B(sourceLink),
                Lo = new Hp8673B(loLink),
                Receiver = receiver,
                MakeAttenuator = cfg => new Hp11713A(attLink, cfg),
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
            return new Hp8902A(Open(opt.AddrReceiver, disposables, 30000));
        }

        // ---- Power-sensor zero / calibrate ---------------------------------

        /// <summary>
        /// Full interactive sensor setup. The calibrate step physically requires a human to
        /// move the sensor onto the 8902A CALIBRATION RF POWER OUTPUT, so this pauses and
        /// waits for the operator before calibrating.
        /// </summary>
        private static int RunSensorCalInteractive(IMeasuringReceiver receiver)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]8902A power-sensor setup[/]");

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
                return 1;
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
                return 1;
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
                return 1;
            }

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold green]Sensor zeroed and calibrated. Ready for power measurements.[/]");
            return 0;
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
                    FreqPointResult r = engine.MeasureFrequency(freq);
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

                    if (detailed) RenderFrequencyTable(r, opt.ToleranceDb);
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
