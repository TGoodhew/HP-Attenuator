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
                Bench bench = BuildBench(opt, disposables);

                // Settling only matters for real relays/measurement hardware.
                if (bench.IsSimulated) opt.Sweep.SettleMs = 0;

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
            var rxLink = Open(opt.AddrReceiver, disposables);
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

        private static IInstrumentLink Open(string resource, List<IDisposable> disposables)
        {
            var link = new VisaInstrumentLink(resource);
            disposables.Add(link);
            return link;
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
                csv.WriteLine("freq_mhz,regime,lo_mhz,if_mhz,commanded_db,command,measured_dbm,measured_atten_db,expected_atten_db,error_db");

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
                            F(p.MeasuredPowerDbm), F(p.MeasuredAttenuationDb),
                            F(p.ExpectedAttenuationDb), F(p.ErrorDb)
                        }));

                        if (Math.Abs(p.ErrorDb) > worstError)
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
            bool pass = worstError <= opt.ToleranceDb;
            var summary = new Table().Border(TableBorder.Heavy);
            summary.AddColumn("Result"); summary.AddColumn("");
            summary.AddRow("Attenuator", $"X = {config.XModel}, Y = {config.YModel}".EscapeMarkup());
            summary.AddRow("Frequencies measured", measured.ToString());
            summary.AddRow("Worst |error|", $"{worstError:0.00} dB  ({worstWhere})");
            summary.AddRow("Tolerance", $"±{opt.ToleranceDb:0.#} dB");
            summary.AddRow("CSV", Path.GetFullPath(opt.CsvPath).EscapeMarkup());
            summary.AddRow("Verdict", pass ? "[green]PASS[/]" : "[red]FAIL[/]");
            AnsiConsole.Write(summary);

            return pass ? 0 : 1;
        }

        private static void RenderFrequencyTable(FreqPointResult r, double tol)
        {
            var header = $"{r.FreqMHz:0.###} MHz  [{r.Regime}]" +
                         (r.Regime == MeasurementRegime.Converted
                             ? $"  LO={r.LoMHz:0.##} MHz IF={r.IfMHz:0.##} MHz" : "") +
                         $"  ref={r.ReferencePowerDbm:0.00} dBm";

            var table = new Table().Border(TableBorder.Rounded).Title(header.EscapeMarkup());
            table.AddColumn(new TableColumn("Set dB").RightAligned());
            table.AddColumn("Cmd");
            table.AddColumn(new TableColumn("Meas dBm").RightAligned());
            table.AddColumn(new TableColumn("Meas att dB").RightAligned());
            table.AddColumn(new TableColumn("Error dB").RightAligned());

            foreach (var p in r.Points)
            {
                string err = $"{p.ErrorDb:+0.00;-0.00;0.00}";
                string errCell = Math.Abs(p.ErrorDb) <= tol ? $"[green]{err}[/]" : $"[red]{err}[/]";
                table.AddRow(
                    p.CommandedDb.ToString(),
                    p.Command.EscapeMarkup(),
                    $"{p.MeasuredPowerDbm:0.00}",
                    $"{p.MeasuredAttenuationDb:0.00}",
                    errCell);
            }
            if (!string.IsNullOrEmpty(r.Warning))
                table.Caption(("⚠ " + r.Warning).EscapeMarkup());
            AnsiConsole.Write(table);
        }

        private static void RenderFrequencyLine(FreqPointResult r, double tol)
        {
            string flag = r.MaxAbsErrorDb <= tol ? "[green]ok[/]" : "[red]!![/]";
            string warn = string.IsNullOrEmpty(r.Warning) ? "" : " [yellow](LO-below)[/]";
            // No square brackets in the plain text — Spectre would parse them as markup.
            AnsiConsole.MarkupLine(
                $"{flag} {r.FreqMHz,9:0.###} MHz  {r.Regime,-9}  ref={r.ReferencePowerDbm,7:0.0} dBm  " +
                $"max|err|={r.MaxAbsErrorDb:0.00} dB{warn}");
        }

        private static string F(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
    }
}
