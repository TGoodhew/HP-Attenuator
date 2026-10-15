using System;
using System.Collections.Generic;
using System.Linq;
using HpAttenuator.Instruments;
using HpAttenuator.Visa;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HpAttenuator.Tests
{
    /// <summary>
    /// <see cref="Hp8902A.Calibrate"/> (#41), driven through a scripted link so the real driver is the
    /// code under test. Each settled read is scripted as one Data Ready poll (0x41) plus the response —
    /// which is what the bench shows: one poll that the 8902A holds until its cycle ends.
    /// </summary>
    [TestClass]
    public class TrflCalibrationTests
    {
        private const string Steady = "-0000060071E-03";      // -60.071
        private const string Steady2 = "-0000060075E-03";     // -60.075: 0.004 dB away
        private const string Wander = "-0000060571E-03";      // -60.571: 0.5 dB away
        private const string Err33 = "+9000003300E+01";

        private ScriptedInstrumentLink _link;
        private Hp8902A _rx;
        private List<string> _log;

        [TestInitialize]
        public void Setup()
        {
            _link = new ScriptedInstrumentLink();
            _rx = new Hp8902A(_link)
            {
                DataReadyPollMs = 1,
                CalibrationOptions = new TrflCalibrationOptions
                {
                    StabilitySamples = 3,
                    StabilityToleranceDb = 0.10,
                    MaxRetries = 2,
                    RetryWaitMs = 0,
                    SuspectCycleMs = 0,         // scripted cycles are instantaneous; tested separately
                },
            };
            _log = new List<string>();
            Hp8902A.CalibrationLog = _log.Add;
        }

        [TestCleanup]
        public void Teardown() => Hp8902A.CalibrationLog = null;

        // ---- script helpers ---------------------------------------------------------------------

        private void Gate(params string[] raws)
        {
            foreach (string r in raws) _link.Poll(0x41).Reads(r);
        }

        /// <summary>The post-C1 read and the single status poll after it.</summary>
        private void Result(string raw, int statusAfter = 0x00) =>
            _link.Poll(0x41).Reads(raw).Poll(statusAfter);

        private int Count(string command) => _link.History.Count(h => h == command);

        private TrflCalibrationFailedException CalibrateExpectingFailure() =>
            Assert.ThrowsException<TrflCalibrationFailedException>(() => _rx.Calibrate());

        // ---- success -------------------------------------------------------------------------------

        [TestMethod]
        public void SteadyLevel_ValidResult_IsConfirmed()
        {
            Gate(Steady, Steady2, Steady);
            Result(Steady);

            _rx.Calibrate();

            var r = _rx.LastCalibration;
            Assert.AreEqual(TrflCalibrationOutcome.Confirmed, r.Outcome);
            Assert.AreEqual(0, r.Retries);
            Assert.AreEqual(1, Count("C1"));
            CollectionAssert.AreEqual(new[] { "T3", "T3", "T3", "C1", "T3" }, _link.History.ToArray(),
                "gate reads, then C1, then the result read — nothing else");
            Assert.AreEqual(0.004, r.Attempts[0].GateSpreadDb, 1e-9);
        }

        [TestMethod]
        public void EveryAttemptIsLogged_WithRawStringsSpreadAndOutcome()
        {
            Gate(Steady, Steady2, Steady);
            Result(Steady);
            _rx.Calibrate();

            string all = string.Join("\n", _log);
            StringAssert.Contains(all, "spread 0.004 dB");
            StringAssert.Contains(all, "raw [" + Steady);          // gate raw strings
            StringAssert.Contains(all, "> C1 > T3 < '" + Steady);  // the calibration exchange
            StringAssert.Contains(all, "held bus");
            StringAssert.Contains(all, "CONFIRMED after 1 attempt(s), 0 retries");
        }

        [TestMethod]
        public void GateOff_GoesStraightToC1()
        {
            _rx.CalibrationOptions.StabilitySamples = 0;
            Result(Steady);
            _rx.Calibrate();
            CollectionAssert.AreEqual(new[] { "C1", "T3" }, _link.History.ToArray());
        }

        [TestMethod]
        public void UncalBeforeC1_IsTheReasonToCalibrate_NotAGateFailure()
        {
            Gate("AAAAAAAA");                 // the first gate read is the UNCAL fill
            Result(Steady);
            _rx.Calibrate();
            Assert.AreEqual(TrflCalibrationOutcome.Confirmed, _rx.LastCalibration.Outcome);
            Assert.AreEqual(1, Count("C1"));
        }

        [TestMethod]
        public void ShortCycle_IsSuspect_ButOnlyWarnsByDefault()
        {
            _rx.CalibrationOptions.SuspectCycleMs = 18000;  // a scripted cycle takes ~0 ms
            Gate(Steady, Steady, Steady);
            Result(Steady);

            _rx.Calibrate();

            Assert.AreEqual(TrflCalibrationOutcome.ConfirmedSuspect, _rx.LastCalibration.Outcome);
            StringAssert.Contains(string.Join("\n", _log), "SUSPECT");
        }

        [TestMethod]
        public void ShortCycle_CanBeMadeAFailure()
        {
            _rx.CalibrationOptions.SuspectCycleMs = 18000;
            _rx.CalibrationOptions.SuspectCycleIsFailure = true;
            Gate(Steady, Steady, Steady);
            Result(Steady);

            var ex = CalibrateExpectingFailure();
            Assert.AreEqual(TrflCalibrationOutcome.NotConfirmed, ex.Result.Outcome);
        }

        // ---- the stability gate ----------------------------------------------------------------

        [TestMethod]
        public void UnsteadyLevel_NeverSendsC1()
        {
            for (int i = 0; i <= 2; i++) Gate(Steady, Wander, Steady);   // first try + 2 retries

            var ex = CalibrateExpectingFailure();

            Assert.AreEqual(TrflCalibrationOutcome.Unstable, ex.Result.Outcome);
            Assert.AreEqual(0, Count("C1"), "C1 must not be sent on an unsteady level");
            Assert.AreEqual(2, ex.Result.Retries);
            StringAssert.Contains(ex.Result.Message, "E4438C");
            StringAssert.Contains(string.Join("\n", _log), "spread 0.500 dB");
        }

        [TestMethod]
        public void LevelThatSteadiesOnRetry_ThenCalibrates()
        {
            Gate(Steady, Wander, Steady);      // unsteady
            Gate(Steady, Steady2, Steady);     // steady on the retry
            Result(Steady);

            _rx.Calibrate();

            Assert.AreEqual(TrflCalibrationOutcome.Confirmed, _rx.LastCalibration.Outcome);
            Assert.AreEqual(1, _rx.LastCalibration.Retries);
            Assert.AreEqual(1, Count("C1"));
        }

        [TestMethod]
        public void ErrorDuringTheGate_StopsBeforeC1()
        {
            Gate("+9000009600E+01");           // Error 96: no signal

            var ex = CalibrateExpectingFailure();

            Assert.AreEqual(96, ex.Code);
            Assert.AreEqual(0, Count("C1"));
        }

        // ---- per-error handling ----------------------------------------------------------------

        [TestMethod]
        public void Error33_IsRetriedAfterReGating_AndCanRecover()
        {
            Gate(Steady, Steady, Steady); Result(Err33);
            Gate(Steady, Steady, Steady); Result(Steady);

            _rx.Calibrate();

            var r = _rx.LastCalibration;
            Assert.AreEqual(TrflCalibrationOutcome.Confirmed, r.Outcome);
            Assert.AreEqual(1, r.Retries);
            Assert.AreEqual(2, Count("C1"));
            Assert.AreEqual(1, Count("CL"), "the displayed error is cleared before retrying");
            Assert.AreEqual(33, r.Attempts[0].Result.Error.Code);
        }

        [DataTestMethod]
        [DataRow(33)]
        [DataRow(34)]
        [DataRow(35)]
        public void Errors33to35_StopAfterTheRetryBudget(int code)
        {
            string raw = $"+900000{code}00E+01";
            for (int i = 0; i <= 2; i++) { Gate(Steady, Steady, Steady); Result(raw); }

            var ex = CalibrateExpectingFailure();

            Assert.AreEqual(code, ex.Code);
            Assert.AreEqual(TrflCalibrationOutcome.Error, ex.Result.Outcome);
            Assert.AreEqual(3, Count("C1"), "one try plus MaxRetries");
            StringAssert.Contains(ex.Result.Message, "E4438C");
            StringAssert.Contains(ex.Result.Message, "level stability");
        }

        [TestMethod]
        public void Error31_StopsAndAsksForTheRfPowerCalibration()
        {
            int prompts = 0;
            _rx.RfPowerCalibrationRequired = _ => prompts++;
            Gate(Steady, Steady, Steady); Result("+9000003100E+01");

            var ex = CalibrateExpectingFailure();

            Assert.AreEqual(TrflCalibrationOutcome.NeedsRfPowerCalibration, ex.Result.Outcome);
            Assert.AreEqual(1, prompts);
            Assert.AreEqual(1, Count("C1"), "no retry: the sensor procedure has to happen first");
            StringAssert.Contains(ex.Result.Message, "CALIBRATION RF POWER OUTPUT");
        }

        [TestMethod]
        public void Error32_StopsAndReportsTheLevel()
        {
            Gate(Steady, Steady, Steady); Result("+9000003200E+01");

            var ex = CalibrateExpectingFailure();

            Assert.AreEqual(32, ex.Code);
            Assert.AreEqual(1, Count("C1"));
            StringAssert.Contains(ex.Result.Message, "-60.07");
        }

        [TestMethod]
        public void Error30_RestoresSf1AndSf9ToAutomatic_AndRetriesOnce()
        {
            Gate(Steady, Steady, Steady); Result("+9000003000E+01");
            Gate(Steady, Steady, Steady); Result(Steady);

            _rx.Calibrate();

            Assert.AreEqual(TrflCalibrationOutcome.Confirmed, _rx.LastCalibration.Outcome);
            Assert.AreEqual(1, Count("1.0SP"));
            Assert.AreEqual(1, Count("9.0SP"));
            Assert.AreEqual(0, Count("AU"), "AUTOMATIC OPERATION would wipe the Tuned RF Level setup");
            Assert.AreEqual(0, _rx.LastCalibration.Retries, "the Error 30 retry is not a stability retry");
        }

        [TestMethod]
        public void Error30Twice_Stops()
        {
            for (int i = 0; i < 2; i++) { Gate(Steady, Steady, Steady); Result("+9000003000E+01"); }
            var ex = CalibrateExpectingFailure();
            Assert.AreEqual(30, ex.Code);
            Assert.AreEqual(2, Count("C1"));
        }

        // ---- positive confirmation -------------------------------------------------------------

        [TestMethod]
        public void RecalStillAssertedAfterTheResult_IsNotConfirmed()
        {
            for (int i = 0; i <= 2; i++) { Gate(Steady, Steady, Steady); Result(Steady, statusAfter: 0x20); }

            var ex = CalibrateExpectingFailure();

            Assert.AreEqual(TrflCalibrationOutcome.NotConfirmed, ex.Result.Outcome);
            StringAssert.Contains(ex.Result.Message, "RECAL/UNCAL");
        }

        [TestMethod]
        public void UncalFillAsTheResult_IsNotConfirmed()
        {
            for (int i = 0; i <= 2; i++) { Gate(Steady, Steady, Steady); Result("AAAAAAAA"); }
            var ex = CalibrateExpectingFailure();
            Assert.AreEqual(TrflCalibrationOutcome.NotConfirmed, ex.Result.Outcome);
        }

        [TestMethod]
        public void RecalWithNoResult_IsNotConfirmed()
        {
            for (int i = 0; i <= 2; i++)
            {
                Gate(Steady, Steady, Steady);
                _link.Poll(0x20).Poll(0x00);   // RECAL during the read, no Data Ready, then the after-poll
            }
            var ex = CalibrateExpectingFailure();
            Assert.AreEqual(TrflCalibrationOutcome.NotConfirmed, ex.Result.Outcome);
        }

        [TestMethod]
        public void ABusFault_PropagatesAsItself_NotAsACalibrationVerdict()
        {
            // The caller's bus handling (#11/#36) has to see a refused write for what it is.
            _rx.CalibrationOptions.StabilitySamples = 0;
            _link.FailWrites = true;
            Assert.ThrowsException<TimeoutException>(() => _rx.Calibrate());
        }
    }
}
