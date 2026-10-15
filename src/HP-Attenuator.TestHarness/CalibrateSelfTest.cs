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
                link => ErrorScript(link, 100),
                expectThrow: true,
                expectLog: "INSTRUMENT ERROR");

            failures += Check(results, "the real error code is captured, not deferred to the panel",
                "The status byte only says 0x04. The 8902A carries the code in the read sentinel, so " +
                "one extra read turns 'read the front panel' into 'Error 33: power sensor reference " +
                "error' — worth it where a panel observation costs an operator round-trip.",
                link => ErrorScript(link, 100),
                expectThrow: true,
                expectLog: "error code 33");

            failures += Check(results, "normal completion",
                "Data Ready after the minimum settle: completes, does not throw.",
                link => link.Poll(0x00, 100).Poll(0x41, 1),
                expectThrow: false,
                expectLog: "complete");

            failures += Check(results, "stale Data Ready is not completion",
                "Data Ready already set on entry (left by the previous read) must not end the wait " +
                "before the minimum settle, or a later error is missed.",
                link => ErrorScript(link.Poll(0x41, 10), 80),
                expectThrow: true,
                expectLog: "INSTRUMENT ERROR");

            failures += Check(results, "failed polls are not reported as 0x00",
                "Every poll throws. Must report a failed poll, must NOT claim status 0x00, and must " +
                "NOT raise a phantom instrument error from -1 & 0x04.",
                link => { link.TrailingPoll = ScriptedInstrumentLink.PollFault; return link; },
                expectThrow: false,
                expectLog: "no successful poll",
                rejectLog: "0x00");

            failures += Check(results, "no SRQ and a clean final poll is 'no error raised', not 'complete'",
                "Status never changes. Must fall out at the budget, poll once, and say only what it " +
                "knows: no error bit was latched. Does not throw — a sweep that works today keeps working.",
                link => link,                       // TrailingPoll defaults to 0x00
                expectThrow: false,
                expectLog: "no error raised");

            failures += CheckPollingCannotHideTheError(results);

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
            var rx = new Hp8902A(link) { CalibrateMinSettleMs = MinSettleMs, CalibrateBudgetMs = BudgetMs };

            Action<string> previous = Hp8902A.DebugLog;
            Hp8902A.DebugLog = m => log.AppendLine(m);
            bool threw = false;
            try { rx.Calibrate(); }
            catch { threw = true; }
            finally { Hp8902A.DebugLog = previous; }

            string text = log.ToString();
            var problems = new List<string>();
            if (!threw) problems.Add("the write failure must still propagate");
            if (text.IndexOf("WRITE FAILED", StringComparison.Ordinal) < 0)
                problems.Add("the failed write is missing from the trace");
            if (text.IndexOf("C1", StringComparison.Ordinal) < 0)
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

        /// <summary>
        /// Script for a CALIBRATE that runs quietly for <paramref name="quiet"/> polls and then raises
        /// an instrument error. The trailing 0x04 and the queued sentinel let the follow-up read inside
        /// the driver retrieve the actual code: +9000003300E+01 decodes to Error 33, the "power sensor
        /// reference error" that --force-range-cal reproducibly raises at 20 dB (#35).
        /// </summary>
        private static ScriptedInstrumentLink ErrorScript(ScriptedInstrumentLink link, int quiet)
        {
            link.TrailingPoll = 0x04;                       // the error stays latched for the read
            return link.Poll(0x00, quiet).Poll(0x04, 1).Reads("+9000003300E+01");
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
                PollTimeoutMs = PollBoundMs,
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

            // V17 (2026-10-15): the 8902A cannot set a status bit while it is being polled (O&C 3-32),
            // so every poll after C1 must answer a service request — apart from the stale-state clear
            // before C1, the --debug trace poll (two attempts when it fails), and the one decisive poll
            // at the end of the budget.
            if (link.PollCount > link.SrqWaits + 4)
                problems.Add($"{link.PollCount} polls for {link.SrqWaits} SRQ waits — polling without a " +
                             "service request holds off the bit being waited for");

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
        /// The bench failure of 2026-10-15 (V17), reproduced: Error 33 on the panel, status 0x00 in the
        /// log for 30 s. The scripted link refuses to reveal a pending bit to a poll that follows the
        /// previous one too closely — the 8902A's documented limit (O&amp;C 3-32). The first #34 fix
        /// polled every 250 ms and so never saw the error; waiting for SRQ and polling once does.
        /// </summary>
        private static int CheckPollingCannotHideTheError(List<string> results)
        {
            ScriptedInstrumentLink Script() =>
                ErrorScript(new ScriptedInstrumentLink { PollSuppressionMs = 50 }, 100);

            var link = Script();
            var rx = new Hp8902A(link)
            {
                DataReadyPollMs = PollMs, CalibrateMinSettleMs = MinSettleMs,
                CalibrateBudgetMs = BudgetMs, PollTimeoutMs = PollBoundMs, DataReadyBudgetMs = ReadBudgetMs,
            };
            string text = Capture(() => rx.Calibrate(), out long ms, out string thrown);

            var problems = new List<string>();
            if (thrown == null) problems.Add("the error was not reported");
            if (text.IndexOf("INSTRUMENT ERROR", StringComparison.Ordinal) < 0) problems.Add("log missing \"INSTRUMENT ERROR\"");

            bool legacyMissed = !LegacyPollingCalibrate(Script());
            bool ok = problems.Count == 0;
            results.Add((ok ? "[green]  PASS[/] " : "[red]  FAIL[/] ") +
                        $"an error raised while being polled is still caught [grey]({ms} ms, {link.PollCount} polls)[/]" +
                        (ok ? "" : "\n         [red]" + string.Join("; ", problems).EscapeMarkup() + "[/]") +
                        "\n         [grey]O&C 3-32: the 8902A cannot alter its status byte while being polled. " +
                        "V17 saw Error 33 on the panel and 0x00 in the log for 30 s.[/]" +
                        (legacyMissed
                            ? "\n         [red]old code (first #34 fix, 250 ms polling): MISSED the error - this is V17[/]"
                            : "\n         [yellow]old code (first #34 fix): also caught it on this script[/]"));
            return ok ? 0 : 1;
        }

        /// <summary>
        /// The first #34 fix, kept ONLY for <see cref="CheckPollingCannotHideTheError"/>: poll every
        /// interval until an error or the budget. Returns true if it would have reported a failure.
        /// </summary>
        private static bool LegacyPollingCalibrate(ScriptedInstrumentLink link)
        {
            link.Write("C1");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < BudgetMs)
            {
                System.Threading.Thread.Sleep(PollMs);
                int sb;
                try { sb = link.SerialPoll(); } catch { continue; }
                if ((sb & 0x04) != 0) return true;
            }
            return false;
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
