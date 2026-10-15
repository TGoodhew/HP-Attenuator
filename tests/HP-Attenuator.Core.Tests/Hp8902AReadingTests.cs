using System.Collections.Generic;
using System.Globalization;
using HpAttenuator.Instruments;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HpAttenuator.Tests
{
    /// <summary>
    /// The 8902A reading decoder (#41). Expected texts are copied from the Operation &amp; Calibration
    /// manual — Operating Errors p. 3-287, Entry and Tuned RF Level Calibration Errors p. 3-289, class
    /// boundaries p. 3-285/3-286, output format p. 3-286 — so these tests are the spec, not a mirror
    /// of the implementation.
    /// </summary>
    [TestClass]
    public class Hp8902AReadingTests
    {
        private static readonly Dictionary<int, string> ManualMessages = new Dictionary<int, string>
        {
            { 1, "IF frequency error." },
            { 2, "Input level too high." },
            { 3, "Input level too low." },
            { 4, "Audio circuits overdriven." },
            { 5, "FM squelched." },
            { 6, "RF input overload." },
            { 7, "Voltmeter and display overload." },
            { 8, "Calibrator error." },
            { 9, "Option not installed." },
            { 10, "Input frequency out of range." },
            { 11, "Calculated value out of range." },
            { 12, "Time Base oven cold (Option 002)." },
            { 13, "Power sensor not connected." },
            { 14, "Power sensor cannot zero." },
            { 15, "Calibration factor error." },
            { 16, "Audio circuits underdriven." },
            { 17, "Selective power measurement (option series 030)." },
            { 18, "RF Power will not calibrate." },
            { 19, "Tuned RF Level circuits underdriven." },
            { 20, "Entered value out of range." },
            { 21, "Invalid key sequence." },
            { 22, "Invalid Special Function prefix." },
            { 23, "Invalid Special Function suffix." },
            { 24, "Invalid HP-IB code." },
            { 25, "Special Functions selected not compatible with selected measurement." },
            { 26, "End of RF POWER calibration factor table reached." },
            { 30, "Manual input attenuation or gain selection." },
            { 31, "Requires new power reference." },
            { 32, "Calibration not possible." },
            { 33, "Power sensor reference error." },
            { 34, "Signal lost during calibration." },
            { 35, "Level error during calibration." },
        };

        /// <summary>The HP-IB form of an error: +900000NN00E+01 (p. 3-286).</summary>
        private static string Sentinel(int code) => $"+900000{code:00}00E+01";

        [TestMethod]
        public void EveryDocumentedCode_DecodesToItsManualMessage()
        {
            foreach (var kv in ManualMessages)
            {
                var r = Hp8902AReading.Decode(Sentinel(kv.Key) + "\r\n");
                Assert.AreEqual(Hp8902AReadingKind.Error, r.Kind, $"code {kv.Key}");
                Assert.AreEqual(kv.Key, r.Error.Code, $"code {kv.Key}");
                Assert.AreEqual(kv.Value, r.Error.Message, $"code {kv.Key}");
                Assert.IsTrue(r.Error.IsDocumented, $"code {kv.Key}");
                Assert.IsTrue(double.IsNaN(r.Value), $"code {kv.Key}: an error must not carry a value");
            }
        }

        [TestMethod]
        public void Error33_TheBenchCase()
        {
            var r = Hp8902AReading.Decode("+9000003300E+01");
            Assert.AreEqual(33, r.Error.Code);
            Assert.AreEqual(Hp8902AErrorClass.TrflCalibration, r.Error.Class);
            Assert.AreEqual("Maintain consistency in frequency and level at SENSOR during calibration.",
                            r.Error.CorrectiveAction);
        }

        [TestMethod]
        public void Error96_IsTheDashDashDisplay()
        {
            var r = Hp8902AReading.Decode(Sentinel(96));
            Assert.AreEqual(96, r.Error.Code);
            Assert.AreEqual(Hp8902AErrorClass.Operating, r.Error.Class);
            StringAssert.StartsWith(r.Error.Message, "No input signal sensed by instrument");
        }

        [DataTestMethod]
        [DataRow(1, Hp8902AErrorClass.Operating)]
        [DataRow(19, Hp8902AErrorClass.Operating)]
        [DataRow(90, Hp8902AErrorClass.Operating)]
        [DataRow(99, Hp8902AErrorClass.Operating)]
        [DataRow(20, Hp8902AErrorClass.Entry)]
        [DataRow(29, Hp8902AErrorClass.Entry)]
        [DataRow(30, Hp8902AErrorClass.TrflCalibration)]
        [DataRow(39, Hp8902AErrorClass.TrflCalibration)]
        [DataRow(40, Hp8902AErrorClass.Service)]
        [DataRow(89, Hp8902AErrorClass.Service)]
        [DataRow(0, Hp8902AErrorClass.Unknown)]
        public void Classes_FollowTheManualsRanges(int code, Hp8902AErrorClass expected)
        {
            Assert.AreEqual(expected, Hp8902AErrors.ClassOf(code));
        }

        [DataTestMethod]
        [DataRow("9E10")]                 // the threshold itself: code 0
        [DataRow("+9000000000E+01")]
        [DataRow("+9000000100E+01")]
        [DataRow("+9000004500E+01")]      // a service error
        [DataRow("9.99E10")]
        [DataRow("1E11")]
        [DataRow("1E30")]
        public void EveryValueAtOrAboveNineTimesTenToTheTen_IsAnError(string raw)
        {
            var r = Hp8902AReading.Decode(raw);
            Assert.AreEqual(Hp8902AReadingKind.Error, r.Kind, raw);
            Assert.IsNotNull(r.Error, raw);
        }

        [TestMethod]
        public void JustBelowTheThreshold_IsAValue()
        {
            var r = Hp8902AReading.Decode("8.99999E10");
            Assert.AreEqual(Hp8902AReadingKind.Value, r.Kind);
            Assert.AreEqual(8.99999E10, r.Value, 1);
        }

        [TestMethod]
        public void CodeIsValueMinusNineTimesTenToTheTenOverAThousand()
        {
            // p. 3-286: "subtracting 9x10^10 from the input number, then dividing the result by 1000".
            double v = 9e10 + 33 * 1000;
            var r = Hp8902AReading.Decode(v.ToString("E10", CultureInfo.InvariantCulture));
            Assert.AreEqual(33, r.Error.Code);
        }

        [DataTestMethod]
        [DataRow(27)]
        [DataRow(36)]
        [DataRow(50)]
        [DataRow(97)]
        public void UndocumentedCodes_AreTypedNotDropped(int code)
        {
            var r = Hp8902AReading.Decode(Sentinel(code));
            Assert.AreEqual(Hp8902AReadingKind.Error, r.Kind);
            Assert.AreEqual(code, r.Error.Code);
            Assert.IsFalse(r.Error.IsDocumented);
            Assert.AreEqual(Hp8902AErrors.ClassOf(code), r.Error.Class);
            Assert.IsFalse(string.IsNullOrEmpty(r.Error.Message));
        }

        [DataTestMethod]
        [DataRow("-0000060071E-03", -60.071)]
        [DataRow("+0000000994E-03", 0.994)]
        [DataRow("-0000011951E-03\r\n", -11.951)]
        public void Measurements_DecodeToTheirValue(string raw, double expected)
        {
            var r = Hp8902AReading.Decode(raw);
            Assert.AreEqual(Hp8902AReadingKind.Value, r.Kind);
            Assert.AreEqual(expected, r.Value, 1e-9);
            Assert.IsNull(r.Error);
        }

        [DataTestMethod]
        [DataRow("AAAAAAAAAAAAAAA")]
        [DataRow("CCCCCCCC")]
        [DataRow("aaaa\r\n")]
        public void LetterFill_IsUncal(string raw)
        {
            Assert.AreEqual(Hp8902AReadingKind.Uncal, Hp8902AReading.Decode(raw).Kind);
        }

        [DataTestMethod]
        [DataRow("")]
        [DataRow(null)]
        [DataRow("\r\n")]
        [DataRow("\u0000")]
        [DataRow("+-.")]
        public void EmptyOrDigitless_IsEmpty(string raw)
        {
            Assert.AreEqual(Hp8902AReadingKind.Empty, Hp8902AReading.Decode(raw).Kind);
        }

        [TestMethod]
        public void ParseReading_KeepsTheExceptionContract()
        {
            // The engine relies on these exceptions; the decoder must not change them.
            Assert.AreEqual(-60.071, Hp8902A.ParseReading("-0000060071E-03"), 1e-9);
            var ex = Assert.ThrowsException<Hp8902AException>(() => Hp8902A.ParseReading("+9000003300E+01"));
            Assert.AreEqual(33, ex.Code);
            Assert.IsTrue(Assert.ThrowsException<Hp8902AException>(() => Hp8902A.ParseReading("AAAA")).IsUncal);
            Assert.IsTrue(Assert.ThrowsException<Hp8902AException>(() => Hp8902A.ParseReading("")).IsEmpty);
        }

        [TestMethod]
        public void Describe_NoLongerShiftsTheOperatingErrors()
        {
            // The old table had 1 = "Input level too high", which is Error 02's message.
            StringAssert.StartsWith(Hp8902AException.Describe(1), "IF frequency error.");
            StringAssert.StartsWith(Hp8902AException.Describe(2), "Input level too high.");
        }
    }
}
