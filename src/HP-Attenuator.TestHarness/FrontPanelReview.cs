using System;
using Spectre.Console;

namespace HpAttenuator.TestHarness
{
    /// <summary>
    /// Interactive front-panel review prompts. When a question can only be answered by a human
    /// reading the instrument's front panel — which annunciator lit (RECAL/UNCAL), whether an error
    /// is displayed, what the level reads — these print the question, pause, and wait for the
    /// operator's typed response. Enabled only for attended hardware runs (<see cref="Enabled"/>) and
    /// a no-op otherwise, so simulated / unattended / stdin-redirected runs never block.
    /// </summary>
    internal static class FrontPanelReview
    {
        /// <summary>When false every prompt is skipped (sim, no <c>--panel-review</c>, or unattended).</summary>
        public static bool Enabled { get; set; }

        private static bool CanPrompt => Enabled && !Console.IsInputRedirected;

        /// <summary>
        /// Asks the operator to read the front panel and answer <paramref name="question"/>, blocking
        /// until they respond. Returns the typed answer ("" when not attended).
        /// </summary>
        public static string Ask(string question)
        {
            if (!CanPrompt) return "";
            Banner();
            AnsiConsole.MarkupLine($"[yellow]{question.EscapeMarkup()}[/]");
            return Capture("Read the 8902A front panel, type your answer and press Enter");
        }

        /// <summary>
        /// Pause BEFORE a block of commands so the operator is watching the panel when it runs. Tells
        /// them what to watch for and blocks until they press Enter. No-op when not attended.
        /// </summary>
        public static void Watch(string whatToWatch)
        {
            if (!CanPrompt) return;
            Banner();
            AnsiConsole.MarkupLine($"[yellow]About to run: {whatToWatch.EscapeMarkup()}[/]");
            AnsiConsole.Markup("[yellow]Watch the 8902A front panel, then press Enter to proceed: [/]");
            Console.ReadLine();
        }

        /// <summary>
        /// The full pattern for an observation that can only be made AFTER commands run: pause and let
        /// the operator start watching (BEFORE), run <paramref name="commands"/>, then ask
        /// <paramref name="question"/> and capture the answer (AFTER). Returns the answer. When not
        /// attended, runs the commands without pausing and returns "".
        /// </summary>
        public static string Observe(string whatToWatch, Action commands, string question)
        {
            if (commands == null) throw new ArgumentNullException(nameof(commands));
            if (!CanPrompt) { commands(); return ""; }
            Watch(whatToWatch);
            commands();
            AnsiConsole.MarkupLine($"[yellow]{question.EscapeMarkup()}[/]");
            return Capture("Type what the panel showed and press Enter");
        }

        private static void Banner()
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[yellow]── FRONT PANEL REVIEW ──[/]");
        }

        private static string Capture(string prompt)
        {
            AnsiConsole.Markup($"[yellow]{prompt}: [/]");
            string r = Console.ReadLine() ?? "";
            AnsiConsole.MarkupLine($"[grey]  recorded: {r.EscapeMarkup()}[/]");
            return r;
        }
    }
}
