using System;
using System.Collections.Generic;
using System.Text;
using HpAttenuator.Instruments;
using HpAttenuator.Visa;
using Spectre.Console;

namespace HpAttenuator.TestHarness
{
    /// <summary>
    /// Headless regression check for the CALIBRATE completion poll (#34), run with <c>--cal-selftest</c>.
    ///
    /// It drives the REAL <see cref="Hp8902A"/> driver over a <see cref="ScriptedInstrumentLink"/>, so
    /// the code that runs on the bench is the code under test. Simulation mode cannot cover this:
    /// sim substitutes <c>SimulatedReceiver</c>, whose <c>Calibrate()</c> is an empty method, so
    /// <see cref="Hp8902A.Calibrate"/> is never executed there.
    ///
    /// The case that matters is "late error". Settled CALIBRATE cycles take 6.5-7.4 s on the bench,
    /// and the old implementation slept a fixed 2500 ms and polled exactly once — so an Error 33
    /// raised at ~6 s was never seen, and the harness reported a calibration that had actually failed.
    /// </summary>
    internal static class CalibrateSelfTest
    {
        // Scaled-down timings so the scripted cases run in seconds, not minutes. The RATIOS match the
        // bench: the minimum settle is a fraction of the budget, and completion arrives after it.
        private const int PollMs = 5;
        private const int MinSettleMs = 300;
        private const int BudgetMs = 3000;

        public static int Run()
        {
            AnsiConsole.MarkupLine("[yellow]Mode:[/] SELF-TEST (scripted link — no instruments, no GPIB)");
            AnsiConsole.MarkupLine("[grey]Exercising Hp8902A.Calibrate() against scripted status-byte sequences (#34).[/]");
            AnsiConsole.WriteLine();

            var results = new List<string>();
            int failures = 0;

            failures += Check(results, "late error is caught",
                "Error raised at ~6 s, after the old 2500 ms sample point — the #34 regression.",
                link => link.Poll(0x00, 100).Poll(0x04, 1),
                expectThrow: true,
                expectLog: "INSTRUMENT ERROR");

            failures += Check(results, "normal completion",
                "Data Ready after the minimum settle: completes, does not throw.",
                link => link.Poll(0x00, 100).Poll(0x41, 1),
                expectThrow: false,
                expectLog: "complete");

            failures += Check(results, "stale Data Ready is not completion",
                "Data Ready already set on entry (left by the previous read) must not end the wait " +
                "before the minimum settle, or a later error is missed.",
                link => link.Poll(0x41, 10).Poll(0x00, 80).Poll(0x04, 1),
                expectThrow: true,
                expectLog: "INSTRUMENT ERROR");

            failures += Check(results, "failed polls are not reported as 0x00",
                "Every poll throws. Must report a failed poll, must NOT claim status 0x00, and must " +
                "NOT raise a phantom instrument error from -1 & 0x04.",
                link => { link.TrailingPoll = ScriptedInstrumentLink.PollFault; return link; },
                expectThrow: false,
                expectLog: "no successful poll",
                rejectLog: "0x00");

            failures += Check(results, "no completion signal is UNCONFIRMED, not success",
                "Status never changes. Must fall out at the budget and say so — a sweep that works " +
                "today must keep working, so this does not throw.",
                link => link,                       // TrailingPoll defaults to 0x00
                expectThrow: false,
                expectLog: "UNCONFIRMED");

            AnsiConsole.WriteLine();
            foreach (string r in results) AnsiConsole.MarkupLine(r);
            AnsiConsole.WriteLine();

            if (failures == 0)
                AnsiConsole.MarkupLine("[green]CALIBRATE self-test PASS[/] — all 5 cases behaved as specified.");
            else
                AnsiConsole.MarkupLine($"[red]CALIBRATE self-test FAIL[/] — {failures} of 5 cases failed.");

            return failures == 0 ? 0 : 1;
        }

        private static int Check(
            List<string> results,
            string name,
            string why,
            Func<ScriptedInstrumentLink, ScriptedInstrumentLink> script,
            bool expectThrow,
            string expectLog,
            string rejectLog = null)
        {
            var link = script(new ScriptedInstrumentLink());
            var log = new StringBuilder();

            var rx = new Hp8902A(link)
            {
                DataReadyPollMs = PollMs,
                CalibrateMinSettleMs = MinSettleMs,
                CalibrateBudgetMs = BudgetMs,
            };

            Action<string> previous = Hp8902A.DebugLog;
            Hp8902A.DebugLog = m => log.AppendLine(m);

            bool threw = false;
            string thrown = null;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try { rx.Calibrate(); }
            catch (Exception ex) { threw = true; thrown = ex.GetType().Name; }
            finally { Hp8902A.DebugLog = previous; }
            sw.Stop();

            string text = log.ToString();
            var problems = new List<string>();

            if (threw != expectThrow)
                problems.Add(expectThrow ? $"expected a throw, got none" : $"unexpected {thrown}");
            if (expectLog != null && text.IndexOf(expectLog, StringComparison.OrdinalIgnoreCase) < 0)
                problems.Add($"log missing \"{expectLog}\"");
            if (rejectLog != null && text.IndexOf(rejectLog, StringComparison.Ordinal) >= 0)
                problems.Add($"log must not contain \"{rejectLog}\"");

            // The whole point of #34: the wait must outlast the minimum settle before it concludes
            // anything. A verdict reached sooner is the old bug returning.
            if (sw.ElapsedMilliseconds < MinSettleMs)
                problems.Add($"concluded in {sw.ElapsedMilliseconds} ms, before the {MinSettleMs} ms minimum settle");

            // Run the OLD algorithm over an identical script. A regression test that the code it
            // replaces would also pass proves nothing; this makes the discrimination visible instead
            // of asserted. "MISSED" below is the #34 defect reproducing on demand.
            bool legacyThrew = LegacyCalibrate(script(new ScriptedInstrumentLink()));
            string legacy = legacyThrew == expectThrow
                ? "[grey]old code: same verdict[/]"
                : expectThrow
                    ? "[red]old code: MISSED the error - this is #34[/]"
                    : "[yellow]old code: differed[/]";

            bool ok = problems.Count == 0;
            results.Add((ok ? "[green]  PASS[/] " : "[red]  FAIL[/] ") + name.EscapeMarkup() +
                        $" [grey]({sw.ElapsedMilliseconds} ms, {link.PollCount} polls)[/]" +
                        (ok ? "" : "\n         [red]" + string.Join("; ", problems).EscapeMarkup() + "[/]") +
                        "\n         [grey]" + why.EscapeMarkup() + "[/]" +
                        "\n         " + legacy);
            return ok ? 0 : 1;
        }

        /// <summary>
        /// The implementation this fix replaces, kept ONLY so the cases above can be shown to
        /// discriminate: press CALIBRATE, sleep a fixed 2500 ms, serial-poll exactly once, and judge
        /// the calibration on that single sample. Returns true if it would have reported a failure.
        /// Settled cycles take 6.5-7.4 s on the bench, so this samples roughly 4 s early.
        /// </summary>
        private static bool LegacyCalibrate(ScriptedInstrumentLink link)
        {
            link.Write("C1");
            System.Threading.Thread.Sleep(MinSettleMs);   // stands in for the fixed 2500 ms
            int sb = -1;
            try { sb = link.SerialPoll(); } catch { /* poll failed; leave sb = -1 (unknown) */ }
            return sb >= 0 && (sb & 0x04) != 0;
        }
    }
}
