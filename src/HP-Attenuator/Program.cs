using System;
using System.Collections.Generic;
using System.Linq;
using HpAttenuator.Model;
using HpAttenuator.Visa;
using Spectre.Console;

namespace HpAttenuator
{
    internal static class Program
    {
        private const string DefaultResource = "GPIB0::28::INSTR"; // 11713A factory address = 28

        private static readonly AttenuatorConfig Config = AttenuatorConfig.Default();
        private static readonly DeviceState State = new DeviceState();
        private static IInstrumentLink _link;

        private static int Main()
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            AnsiConsole.Write(
                new FigletText("HP 11713A")
                    .Color(Color.SpringGreen3));
            AnsiConsole.MarkupLine("[grey]Attenuator / Switch Driver controller — NI-VISA + Spectre.Console[/]");
            AnsiConsole.WriteLine();

            try
            {
                Connect();
                RunMenu();
            }
            catch (Exception ex)
            {
                AnsiConsole.WriteException(ex, ExceptionFormats.ShortenEverything);
                return 1;
            }
            finally
            {
                _link?.Dispose();
            }

            AnsiConsole.MarkupLine("[grey]Goodbye.[/]");
            return 0;
        }

        // ---- Connection -----------------------------------------------------

        private static void Connect()
        {
            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Select a [green]connection[/]:")
                    .AddChoices("Connect to a VISA resource", "List VISA resources", "Simulated driver (no hardware)"));

            switch (choice)
            {
                case "Simulated driver (no hardware)":
                    _link = new SimulatedInstrumentLink();
                    break;

                case "List VISA resources":
                    ListResources();
                    Connect();
                    return;

                default:
                    var resource = AnsiConsole.Prompt(
                        new TextPrompt<string>("VISA resource string:")
                            .DefaultValue(DefaultResource));
                    OpenVisa(resource);
                    break;
            }

            AnsiConsole.MarkupLine($"Connected to [green]{_link.ResourceName.EscapeMarkup()}[/]" +
                                   (_link.IsSimulated ? " [yellow](simulated)[/]" : string.Empty));
            AnsiConsole.WriteLine();
        }

        private static void OpenVisa(string resource)
        {
            try
            {
                AnsiConsole.Status().Start($"Opening {resource}...", _ => { _link = new VisaInstrumentLink(resource); });
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Could not open {resource.EscapeMarkup()}:[/] {ex.Message.EscapeMarkup()}");
                if (AnsiConsole.Confirm("Fall back to the simulated driver?"))
                    _link = new SimulatedInstrumentLink();
                else
                    throw;
            }
        }

        private static void ListResources()
        {
            var resources = AnsiConsole.Status()
                .Start("Scanning VISA bus...", _ => VisaInstrumentLink.FindResources().ToList());

            if (resources.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No VISA INSTR resources found.[/]");
                return;
            }

            var table = new Table().Border(TableBorder.Rounded).AddColumn("VISA resource");
            foreach (var r in resources) table.AddRow(r.EscapeMarkup());
            AnsiConsole.Write(table);
        }

        // ---- Main loop ------------------------------------------------------

        private static void RunMenu()
        {
            while (true)
            {
                ShowState();

                var action = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("Choose an [green]action[/]:")
                        .PageSize(12)
                        .AddChoices(
                            "Set total attenuation",
                            "Set ATTEN X",
                            "Set ATTEN Y",
                            "Toggle switch S9",
                            "Toggle switch S0",
                            "Send raw data string",
                            "Show send history",
                            "Reconnect",
                            "Quit"));

                switch (action)
                {
                    case "Set total attenuation": SetTotal(); break;
                    case "Set ATTEN X": SetBank("X", Config.X); break;
                    case "Set ATTEN Y": SetBank("Y", Config.Y); break;
                    case "Toggle switch S9": ToggleSwitch9(); break;
                    case "Toggle switch S0": ToggleSwitch0(); break;
                    case "Send raw data string": SendRaw(); break;
                    case "Show send history": ShowHistory(); break;
                    case "Reconnect": _link?.Dispose(); Connect(); break;
                    case "Quit": return;
                }
            }
        }

        // ---- State display --------------------------------------------------

        private static void ShowState()
        {
            var table = new Table().Border(TableBorder.Rounded).Expand();
            table.AddColumn("Bank");
            table.AddColumn("Model");
            table.AddColumn(new TableColumn("Sections (digit:dB)").NoWrap());
            table.AddColumn(new TableColumn("dB").RightAligned());

            table.AddRow("ATTEN X", Config.XModel.EscapeMarkup(), DescribeBank(Config.X), State.BankDecibels(Config.X).ToString());
            table.AddRow("ATTEN Y", Config.YModel.EscapeMarkup(), DescribeBank(Config.Y), State.BankDecibels(Config.Y).ToString());

            var s9 = State.Switch9 == null ? "[grey]unset[/]" : (State.Switch9.Value ? "[green]A9[/]" : "[blue]B9[/]");
            var s0 = State.Switch0 == null ? "[grey]unset[/]" : (State.Switch0.Value ? "[green]A0[/]" : "[blue]B0[/]");

            var panel = new Panel(table)
                .Header($" {_link.ResourceName.EscapeMarkup()}  •  TOTAL = [bold yellow]{State.TotalDecibels(Config)} dB[/]  •  S9={s9}  S0={s0} ")
                .Border(BoxBorder.Heavy);
            AnsiConsole.Write(panel);
        }

        private static string DescribeBank(IEnumerable<Section> bank)
        {
            return string.Join("  ", bank.Select(s =>
            {
                var engaged = State.Engaged.Contains(s.Digit);
                return engaged
                    ? $"[green]{s.Digit}:{s.Decibels}[/]"
                    : $"[grey]{s.Digit}:{s.Decibels}[/]";
            }));
        }

        // ---- Actions --------------------------------------------------------

        private static void SetTotal()
        {
            int target = AnsiConsole.Prompt(
                new TextPrompt<int>($"Target attenuation in dB [grey](0-{Config.MaxDecibels})[/]:")
                    .Validate(v => v >= 0 && v <= Config.MaxDecibels
                        ? ValidationResult.Success()
                        : ValidationResult.Error($"Out of range 0-{Config.MaxDecibels}")));

            var engaged = CommandBuilder.Solve(Config.AllSections.ToList(), target);
            if (engaged == null)
            {
                AnsiConsole.MarkupLine($"[red]{target} dB is not achievable with this section set.[/]");
                Pause();
                return;
            }

            var engagedSet = new HashSet<int>(engaged);
            string command = CommandBuilder.BuildString(Config.AllSections, engagedSet);
            if (Send(command))
            {
                State.Engaged.Clear();
                foreach (var d in engaged) State.Engaged.Add(d);
            }
        }

        private static void SetBank(string name, IReadOnlyList<Section> bank)
        {
            int max = bank.Sum(s => s.Decibels);
            int target = AnsiConsole.Prompt(
                new TextPrompt<int>($"ATTEN {name} attenuation in dB [grey](0-{max})[/]:")
                    .Validate(v => v >= 0 && v <= max
                        ? ValidationResult.Success()
                        : ValidationResult.Error($"Out of range 0-{max}")));

            var engaged = CommandBuilder.Solve(bank, target);
            if (engaged == null)
            {
                AnsiConsole.MarkupLine($"[red]{target} dB is not achievable on ATTEN {name}.[/]");
                Pause();
                return;
            }

            var engagedSet = new HashSet<int>(engaged);
            // Only address this bank's digits; the other bank retains its state.
            string command = CommandBuilder.BuildString(bank, engagedSet);
            if (Send(command))
            {
                foreach (var s in bank) State.Engaged.Remove(s.Digit);
                foreach (var d in engaged) State.Engaged.Add(d);
            }
        }

        private static void ToggleSwitch9()
        {
            bool next = !(State.Switch9 ?? false);
            if (Send(CommandBuilder.Switch9(next))) State.Switch9 = next;
        }

        private static void ToggleSwitch0()
        {
            bool next = !(State.Switch0 ?? false);
            if (Send(CommandBuilder.Switch0(next))) State.Switch0 = next;
        }

        private static void SendRaw()
        {
            string command = AnsiConsole.Prompt(
                new TextPrompt<string>("Raw data string [grey](e.g. A12B34)[/]:"));

            if (!CommandBuilder.IsValidDataString(command))
            {
                if (!AnsiConsole.Confirm($"[yellow]'{command.EscapeMarkup()}' contains characters outside A/B/0-9. Send anyway?[/]", false))
                    return;
            }
            Send(command.Trim());
            AnsiConsole.MarkupLine("[grey]Note: raw sends are not reflected in the tracked state above.[/]");
            Pause();
        }

        private static void ShowHistory()
        {
            if (_link.History.Count == 0)
            {
                AnsiConsole.MarkupLine("[grey]Nothing sent yet.[/]");
            }
            else
            {
                var table = new Table().Border(TableBorder.Rounded)
                    .AddColumn("#").AddColumn("Data string");
                int i = 1;
                foreach (var cmd in _link.History)
                    table.AddRow((i++).ToString(), cmd.EscapeMarkup());
                AnsiConsole.Write(table);
            }
            Pause();
        }

        // ---- Transport helper ----------------------------------------------

        private static bool Send(string command)
        {
            try
            {
                _link.Write(command);
                AnsiConsole.MarkupLine($"[green]→ sent[/] [bold]{command.EscapeMarkup()}[/]");
                return true;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Write failed:[/] {ex.Message.EscapeMarkup()}");
                Pause();
                return false;
            }
        }

        private static void Pause()
        {
            AnsiConsole.MarkupLine("[grey]Press any key to continue...[/]");
            Console.ReadKey(true);
        }
    }
}
