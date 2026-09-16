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

            failures += Check(results, "no completion signal is UNCONFIRMED, not success",
                "Status never changes. Must fall out at the budget and say so — a sweep that works " +
                "today must keep working, so this does not throw.",
                link => link,                       // TrailingPoll defaults to 0x00
                expectThrow: false,
                expectLog: "UNCONFIRMED");

            failures += CheckFailedWriteIsTraced(results);
            failures += CheckFailedRelayWriteIsAdmitted(results);

            AnsiConsole.WriteLine();
            foreach (string r in results) AnsiConsole.MarkupLine(r);
            AnsiConsole.WriteLine();

            if (failures == 0)
                AnsiConsole.MarkupLine("[green]CALIBRATE self-test PASS[/] — all 8 cases behaved as specified.");
            else
                AnsiConsole.MarkupLine($"[red]CALIBRATE self-test FAIL[/] — {failures} of 8 cases failed.");

            return failures == 0 ? 0 : 1;
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
