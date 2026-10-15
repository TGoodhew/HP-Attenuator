using System;
using System.Collections.Generic;
using System.Text;
using HpAttenuator.Instruments;
using HpAttenuator.Model;
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

        public static int Run()
        {
            AnsiConsole.MarkupLine("[yellow]Mode:[/] SELF-TEST (scripted link — no instruments, no GPIB)");
            AnsiConsole.MarkupLine("[grey]Exercising the real Hp8902A / Hp11713A drivers against scripted links (#34, #36, #37).[/]");
            AnsiConsole.WriteLine();

            var results = new List<string>();
            int failures = 0;

            // The CALIBRATE-algorithm cases that lived here tested the SRQ-era Calibrate(), which #41
            // replaced (the 8902A does not report a CALIBRATE error over HP-IB at all — V17). The new
            // routine is covered by tests/HP-Attenuator.Core.Tests (dotnet test).

            failures += CheckFailedWriteIsTraced(results);
            failures += CheckFailedRelayWriteIsAdmitted(results);
            failures += CheckHungPollCannotOverrunReadBudget(results);
            failures += CheckReadPhasesAreAttributed(results);
            failures += CheckUnreleasedBusIsReported(results);

            AnsiConsole.WriteLine();
            foreach (string r in results) AnsiConsole.MarkupLine(r);
            AnsiConsole.WriteLine();

            if (failures == 0)
                AnsiConsole.MarkupLine($"[green]CALIBRATE self-test PASS[/] — all {results.Count} cases behaved as specified.");
            else
                AnsiConsole.MarkupLine($"[red]CALIBRATE self-test FAIL[/] — {failures} of {results.Count} cases failed.");

            return failures == 0 ? 0 : 1;
        }

        // Scaled-down read timings for the #36 cases. Ratios as on the bench: a poll that is never
        // answered blocks for the whole session timeout (60 s there, HangMs here), which is several
        // times the read budget (30 s there, ReadBudgetMs here).
        private const int ReadBudgetMs = 600;
        private const int PollBoundMs = 200;
        private const int HangMs = 3000;

        /// <summary>
        /// A serial poll that is never answered must not carry the read past its budget (#36). This is
        /// the 5 GHz hang: the budget was checked only BETWEEN polls and each poll inherited the 60 s
        /// session timeout, so a 30 s budget was logged as "NOT set after 134.5 s". The legacy loop is
        /// run over the same script to show the overrun reproducing.
        /// </summary>
        private static int CheckHungPollCannotOverrunReadBudget(List<string> results)
        {
            ScriptedInstrumentLink Script()
            {
                var l = new ScriptedInstrumentLink { HangMs = HangMs, TrailingPoll = ScriptedInstrumentLink.PollHang };
                return l.Poll(0x00, 3);                     // a few quiet polls, then the bus stops answering
            }

            var link = Script();
            var rx = new Hp8902A(link) { DataReadyPollMs = PollMs, DataReadyBudgetMs = ReadBudgetMs, PollTimeoutMs = PollBoundMs };
            string text = Capture(() => rx.ReadRelativeDb(), out long ms, out _);

            long limit = ReadBudgetMs + PollBoundMs + 300;  // budget + one bounded poll + scheduling slack
            var problems = new List<string>();
            if (ms > limit) problems.Add($"took {ms} ms, past the {limit} ms bound");
            if (text.IndexOf("FAILED (first: TimeoutException)", StringComparison.Ordinal) < 0)
                problems.Add("the summary does not name the failed polls and their exception");
            if (text.IndexOf("read budget spent", StringComparison.Ordinal) < 0)
                problems.Add("the summary does not say the budget ran out");
            if (text.IndexOf("(serial poll failed)", StringComparison.Ordinal) < 0)
                problems.Add("the final failed poll is not reported as a failed poll");

            long legacyMs = LegacyReadWait(Script());
            bool ok = problems.Count == 0;
            results.Add((ok ? "[green]  PASS[/] " : "[red]  FAIL[/] ") +
                        $"a poll that never answers cannot overrun the read budget [grey]({ms} ms, budget {ReadBudgetMs} ms)[/]" +
                        (ok ? "" : "\n         [red]" + string.Join("; ", problems).EscapeMarkup() + "[/]") +
                        "\n         [grey]The 5 GHz hang: a 30 s budget logged as 134.5 s, because one poll " +
                        "inherited the 60 s session timeout and the budget was only checked between polls.[/]" +
                        (legacyMs > ReadBudgetMs + PollBoundMs
                            ? $"\n         [red]old code: overran the {ReadBudgetMs} ms budget to {legacyMs} ms - this is #36[/]"
                            : "\n         [yellow]old code: did not overrun on this script[/]"));
            return ok ? 0 : 1;
        }

        /// <summary>
        /// A normal settled read is attributed phase by phase (#36, V9): the debug summary says how
        /// many polls ran and how long was spent inside them, and <see cref="Hp8902A.ReadTimes"/>
        /// accumulates the same split for <c>--profile</c>, which runs without <c>--debug</c>.
        /// </summary>
        private static int CheckReadPhasesAreAttributed(List<string> results)
        {
            var link = new ScriptedInstrumentLink().Poll(0x00, 10).Poll(0x41).Reads("-0000011951E-03");
            var rx = new Hp8902A(link) { DataReadyPollMs = PollMs, DataReadyBudgetMs = ReadBudgetMs, PollTimeoutMs = PollBoundMs };
            double value = double.NaN;
            string text = Capture(() => value = rx.ReadRelativeDb(), out _, out string thrown);

            var t = rx.ReadTimes;
            var problems = new List<string>();
            if (thrown != null) problems.Add($"unexpected {thrown}");
            if (Math.Abs(value - (-11.951)) > 1e-6) problems.Add($"read {value}, expected -11.951");
            if (text.IndexOf("11 polls", StringComparison.Ordinal) < 0) problems.Add("the summary does not count the polls");
            if (text.IndexOf("inside them", StringComparison.Ordinal) < 0) problems.Add("the summary does not say how long was spent inside polls");
            if (text.IndexOf("retrieve", StringComparison.Ordinal) < 0) problems.Add("the retrieve is not timed");
            if (t.Reads != 1 || t.Polls != 11 || t.FailedPolls != 0)
                problems.Add($"ReadTimes counted {t.Reads} reads / {t.Polls} polls / {t.FailedPolls} failed, expected 1 / 11 / 0");
            if (t.WaitMs < t.InsidePollsMs) problems.Add("time inside polls exceeds the whole wait");

            bool ok = problems.Count == 0;
            results.Add((ok ? "[green]  PASS[/] " : "[red]  FAIL[/] ") +
                        "a settled read is attributed: trigger / wait (and inside polls) / retrieve / settle" +
                        (ok ? "" : "\n         [red]" + string.Join("; ", problems).EscapeMarkup() + "[/]") +
                        "\n         [grey]What V9's unexplained 18.4 s/read needs, available to --profile without --debug.[/]" +
                        "\n         [grey]old code: one line, total elapsed only[/]");
            return ok ? 0 : 1;
        }

        /// <summary>
        /// A device clear that does not free the bus must be reported, not assumed (#36). ReleaseBus
        /// used to return normally whatever happened, so the sweep said "released it" and ran the
        /// post-hang probe on a bus that could only time out again — which is what both 5 GHz hangs
        /// logged. Also checks the healthy case still succeeds quietly.
        /// </summary>
        private static int CheckUnreleasedBusIsReported(List<string> results)
        {
            var held = new ScriptedInstrumentLink { TrailingPoll = ScriptedInstrumentLink.PollHang };
            var rxHeld = new Hp8902A(held) { PollTimeoutMs = PollBoundMs };
            string text = Capture(() => rxHeld.ReleaseBus(), out _, out string thrownHeld);

            var freed = new ScriptedInstrumentLink();            // answers 0x00 after the clear
            var rxFreed = new Hp8902A(freed) { PollTimeoutMs = PollBoundMs };
            Capture(() => rxFreed.ReleaseBus(), out _, out string thrownFreed);

            var problems = new List<string>();
            if (thrownHeld == null) problems.Add("a bus that still does not answer was reported as released");
            if (text.IndexOf("no answer to a serial poll after device clear", StringComparison.Ordinal) < 0)
                problems.Add("the failed release check is not traced");
            if (thrownFreed != null) problems.Add($"a bus that answers after the clear threw {thrownFreed}");
            if (!System.Linq.Enumerable.Contains(held.History, "<CLEAR>")) problems.Add("no device clear was sent");

            bool ok = problems.Count == 0;
            results.Add((ok ? "[green]  PASS[/] " : "[red]  FAIL[/] ") +
                        "a device clear that does not free the bus is reported, not assumed" +
                        (ok ? "" : "\n         [red]" + string.Join("; ", problems).EscapeMarkup() + "[/]") +
                        "\n         [grey]Both 5 GHz hangs said \"released it\", then the probe failed with " +
                        "IOTimeoutException on the same bus.[/]" +
                        "\n         [grey]old code: returned normally either way[/]");
            return ok ? 0 : 1;
        }

        /// <summary>Runs <paramref name="action"/> with <see cref="Hp8902A.DebugLog"/> captured; returns
        /// the log, the elapsed time and the type of anything thrown (null if nothing was).</summary>
        private static string Capture(Action action, out long ms, out string thrown)
        {
            var log = new StringBuilder();
            Action<string> previous = Hp8902A.DebugLog;
            Hp8902A.DebugLog = m => log.AppendLine(m);
            thrown = null;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try { action(); }
            catch (Exception ex) { thrown = ex.GetType().Name; }
            finally { Hp8902A.DebugLog = previous; }
            ms = sw.ElapsedMilliseconds;
            return log.ToString();
        }

        /// <summary>
        /// The wait loop this fix replaces, kept ONLY to show the hung-poll case discriminates: the
        /// budget is tested between polls, and each poll is unbounded (it inherits the session
        /// timeout). Returns how long the wait took.
        /// </summary>
        private static long LegacyReadWait(ScriptedInstrumentLink link)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ReadBudgetMs)
            {
                int sb;
                try { sb = link.SerialPoll(); } catch { sb = -1; }
                if (sb >= 0 && (sb & 0x21) != 0) break;
                System.Threading.Thread.Sleep(PollMs);
            }
            return sw.ElapsedMilliseconds;
        }

        /// <summary>
        /// A relay command the bus refuses must leave the attenuator state marked UNKNOWN (#37).
        ///
        /// The 11713A is listen-only, so there is no readback to fall back on, and a failed write may
        /// still have reached the instrument. Keeping the previous setting asserts something nobody
        /// can know — and it is the value the app shows the operator as the current attenuation.
        /// </summary>
        private static int CheckFailedRelayWriteIsAdmitted(List<string> results)
        {
            var link = new ScriptedInstrumentLink();
            var config = AttenuatorConfig.Default();
            var atten = new Hp11713A(link, config);

            atten.SetAttenuationDb(30);                 // succeeds: state is known and correct
            bool knownAfterGood = atten.State.IsKnown;
            int dbAfterGood = atten.State.TotalDecibels(config);

            link.FailWrites = true;
            bool threw = false;
            try { atten.SetAttenuationDb(60); } catch { threw = true; }
            bool knownAfterBad = atten.State.IsKnown;

            link.FailWrites = false;
            atten.SetAttenuationDb(20);                 // a later success re-establishes the state
            bool knownAfterRecovery = atten.State.IsKnown;

            var problems = new List<string>();
            if (!knownAfterGood)      problems.Add("a successful write must leave the state KNOWN");
            if (dbAfterGood != 30)    problems.Add($"expected 30 dB after a good write, got {dbAfterGood}");
            if (!threw)               problems.Add("the failed relay write must still propagate");
            if (knownAfterBad)        problems.Add("state still claims to be KNOWN after a FAILED write");
            if (!knownAfterRecovery)  problems.Add("a later successful write must restore KNOWN");

            bool ok = problems.Count == 0;
            results.Add((ok ? "[green]  PASS[/] " : "[red]  FAIL[/] ") +
                        "a refused relay write marks the attenuator state unknown" +
                        (ok ? "" : "\n         [red]" + string.Join("; ", problems).EscapeMarkup() + "[/]") +
                        "\n         [grey]The 11713A is listen-only, so a failed write leaves a setting " +
                        "nobody can verify. The old code kept the previous value.[/]" +
                        "\n         [grey]old code: kept the stale setting and called it current[/]");
            return ok ? 0 : 1;
        }

        /// <summary>
        /// A write the bus refuses must still appear in the trace (#36). The trace used to be emitted
        /// only after a SUCCESSFUL write, so a wedged bus made the log fall silent at exactly the point
        /// of failure — healthy traffic, then nothing, with no record of the command in flight. The
        /// exception must still propagate: this is about the diagnostic record, not about recovery.
        /// </summary>
        private static int CheckFailedWriteIsTraced(List<string> results)
        {
            var link = new ScriptedInstrumentLink { FailWrites = true };
            var log = new StringBuilder();
            var rx = new Hp8902A(link);

            Action<string> previous = Hp8902A.DebugLog;
            Hp8902A.DebugLog = m => log.AppendLine(m);
            bool threw = false;
            try { rx.SetReference(); }               // SET REF goes out as "RF" through Send
            catch { threw = true; }
            finally { Hp8902A.DebugLog = previous; }

            string text = log.ToString();
            var problems = new List<string>();
            if (!threw) problems.Add("the write failure must still propagate");
            if (text.IndexOf("WRITE FAILED", StringComparison.Ordinal) < 0)
                problems.Add("the failed write is missing from the trace");
            if (text.IndexOf("< RF", StringComparison.Ordinal) < 0)
                problems.Add("the trace does not say WHICH command failed");

            bool ok = problems.Count == 0;
            results.Add((ok ? "[green]  PASS[/] " : "[red]  FAIL[/] ") +
                        "a refused write is traced, not silent" +
                        (ok ? "" : "\n         [red]" + string.Join("; ", problems).EscapeMarkup() + "[/]") +
                        "\n         [grey]A wedged bus used to make the log fall silent at the moment " +
                        "of failure, with no record of the command in flight.[/]" +
                        "\n         [grey]old code: logged nothing at all[/]");
            return ok ? 0 : 1;
        }

    }
}
