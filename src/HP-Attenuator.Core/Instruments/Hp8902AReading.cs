using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace HpAttenuator.Instruments
{
    /// <summary>
    /// The 8902A's error classes, as the Operation manual groups them (O&amp;C p. 3-285/3-286).
    /// </summary>
    public enum Hp8902AErrorClass
    {
        /// <summary>01-19 and 90-99: a condition has not been met to assure a calibrated measurement.</summary>
        Operating,
        /// <summary>20-29: an invalid key sequence or keyboard (or HP-IB) entry.</summary>
        Entry,
        /// <summary>30-39: "Tuned RF Level Calibration Errors" — a condition has not been met to assure
        /// calibration. Usually cleared by readjusting the input signal, or by setting Special Functions
        /// 1 and 9 to automatic selection (p. 3-286).</summary>
        TrflCalibration,
        /// <summary>40-89: service errors, normally disabled (Service Manual, Section 8).</summary>
        Service,
        /// <summary>Anything outside the documented ranges.</summary>
        Unknown,
    }

    /// <summary>One decoded 8902A error: its code, class, and the manual's message and corrective action.</summary>
    public sealed class Hp8902AError
    {
        public int Code { get; }
        public Hp8902AErrorClass Class { get; }
        /// <summary>The manual's message, or a description of the gap if the code is undocumented.</summary>
        public string Message { get; }
        /// <summary>The manual's corrective action; empty when the manual gives none.</summary>
        public string CorrectiveAction { get; }
        /// <summary>True when the code appears in the manual's error tables.</summary>
        public bool IsDocumented { get; }

        internal Hp8902AError(int code, Hp8902AErrorClass cls, string message, string action, bool documented)
        {
            Code = code;
            Class = cls;
            Message = message;
            CorrectiveAction = action ?? "";
            IsDocumented = documented;
        }

        /// <summary>"Message — corrective action", the line a log should carry.</summary>
        public string Describe() =>
            CorrectiveAction.Length > 0 ? $"{Message} — {CorrectiveAction}" : Message;

        public override string ToString() => $"Error {Code:00} ({Class}): {Describe()}";
    }

    /// <summary>
    /// The 8902A error table. Text is the Operation &amp; Calibration manual's, read from the text layer
    /// of the scan on 2026-10-15:
    /// <list type="bullet">
    /// <item>Operating Errors 01-19 and 96: O&amp;C p. 3-287.</item>
    /// <item>Entry Errors 20-26 and the Tuned RF Level Calibration Errors 30-35: O&amp;C p. 3-289.</item>
    /// <item>Class boundaries: O&amp;C p. 3-285/3-286.</item>
    /// </list>
    /// On p. 3-289 the 30-35 table sits under a repeated "Entry Errors" heading; p. 3-286 classifies
    /// 30-39 separately as Tuned RF Level Calibration Errors, which is what is used here.
    /// <para>
    /// This replaces the old <c>Hp8902AException.Describe</c> table, which had codes 01-18 shifted (it
    /// gave 1 = "Input level too high", which is 02; 01 is "IF frequency error").
    /// </para>
    /// </summary>
    public static class Hp8902AErrors
    {
        private static readonly Dictionary<int, (string Message, string Action)> Table =
            new Dictionary<int, (string, string)>
        {
            // Operating Errors (p. 3-287)
            { 1,  ("IF frequency error.", "Retune to signal at input.") },
            { 2,  ("Input level too high.", "Increase RF input attenuation.") },
            { 3,  ("Input level too low.", "Decrease RF input attenuation.") },
            { 4,  ("Audio circuits overdriven.", "Increase audio range.") },
            { 5,  ("FM squelched.", "Reduce signal level variations (AM) at RF INPUT.") },
            { 6,  ("RF input overload.", "Reduce RF signal level at RF INPUT, then press any key.") },
            { 7,  ("Voltmeter and display overload.", "Increase audio range.") },
            { 8,  ("Calibrator error.", "Connect CALIBRATION AM/FM OUTPUT to RF INPUT or turn off calibrator.") },
            { 9,  ("Option not installed.", "Select another instrument function.") },
            { 10, ("Input frequency out of range.", "Adjust input frequency to within specified limits.") },
            { 11, ("Calculated value out of range.", "Conversion from % to dB is not possible. Press LOG/LIN for linear display.") },
            { 12, ("Time Base oven cold (Option 002).", "For highest accuracy, wait until oven is warm. (Refer to Special Function 15.)") },
            { 13, ("Power sensor not connected.", "Press RF POWER. RF level measurement is initiated if the power sensor is not connected.") },
            { 14, ("Power sensor cannot zero.", "Assure zero power at SENSOR input.") },
            { 15, ("Calibration factor error.", "Input calibration factors.") },
            { 16, ("Audio circuits underdriven.", "Increase external audio input level.") },
            { 17, ("Selective power measurement (option series 030).", "Release RANGE HOLD, adjust input level or re-reference measurement.") },
            { 18, ("RF Power will not calibrate.", "Assure 0 dBm at SENSOR input.") },
            { 19, ("Tuned RF Level circuits underdriven.", "Press Automatic Operation.") },
            { 96, ("No input signal sensed by instrument (HP-IB only; the '--' display).", "Increase signal level at RF INPUT or retune.") },

            // Entry Errors (p. 3-289)
            { 20, ("Entered value out of range.", "Re-enter new value.") },
            { 21, ("Invalid key sequence.", "Check for compatibility of functions selected.") },
            { 22, ("Invalid Special Function prefix.", "Re-enter correct Special Function Code.") },
            { 23, ("Invalid Special Function suffix.", "Re-enter correct Special Function Code.") },
            { 24, ("Invalid HP-IB code.", "Re-enter correct HP-IB code.") },
            { 25, ("Special Functions selected not compatible with selected measurement.", "Press AUTOMATIC OPERATION.") },
            { 26, ("End of RF POWER calibration factor table reached.", "Check number of entries.") },

            // Tuned RF Level Calibration Errors (p. 3-289; class per p. 3-286)
            { 30, ("Manual input attenuation or gain selection.", "Change RF input attenuation and gain.") },
            { 31, ("Requires new power reference.", "Calibrate RF Power before attempting calibration of Tuned RF Level.") },
            { 32, ("Calibration not possible.", "Move input signal level into a valid calibration range.") },
            { 33, ("Power sensor reference error.", "Maintain consistency in frequency and level at SENSOR during calibration.") },
            { 34, ("Signal lost during calibration.", "Maintain frequency stability at RF INPUT during calibration.") },
            { 35, ("Level error during calibration.", "Maintain signal stability at RF INPUT during calibration.") },
        };

        /// <summary>The class a code belongs to, from the manual's ranges (p. 3-285/3-286).</summary>
        public static Hp8902AErrorClass ClassOf(int code)
        {
            if ((code >= 1 && code <= 19) || (code >= 90 && code <= 99)) return Hp8902AErrorClass.Operating;
            if (code >= 20 && code <= 29) return Hp8902AErrorClass.Entry;
            if (code >= 30 && code <= 39) return Hp8902AErrorClass.TrflCalibration;
            if (code >= 40 && code <= 89) return Hp8902AErrorClass.Service;
            return Hp8902AErrorClass.Unknown;
        }

        /// <summary>Looks a code up. Never returns null: an undocumented code is typed, with its class,
        /// rather than dropped.</summary>
        public static Hp8902AError Lookup(int code)
        {
            var cls = ClassOf(code);
            if (Table.TryGetValue(code, out var entry))
                return new Hp8902AError(code, cls, entry.Message, entry.Action, documented: true);

            string message = cls == Hp8902AErrorClass.Service
                ? "Service error (normally disabled) — refer to Section 8 of the 8902A Service Manual."
                : $"Undocumented {cls} error code — not in the Operation manual's tables.";
            return new Hp8902AError(code, cls, message, "", documented: false);
        }
    }

    /// <summary>What a raw 8902A reading turned out to be.</summary>
    public enum Hp8902AReadingKind
    {
        /// <summary>A measurement result, in the measurement's fundamental units.</summary>
        Value,
        /// <summary>An error sentinel (any value ≥ 9×10¹⁰).</summary>
        Error,
        /// <summary>The UNCAL fill — a run of letters ('CCCC', 'AAAA') instead of a number.</summary>
        Uncal,
        /// <summary>Empty, or no numeric content after stripping control characters (#6, retriable).</summary>
        Empty,
        /// <summary>Printable, with digits, but not a number the format allows.</summary>
        Unrecognized,
    }

    /// <summary>
    /// A decoded 8902A reading — the ONE place a raw HP-IB response is interpreted (#41).
    ///
    /// HP-IB output format (O&amp;C p. 3-286): <c>+900000NN00E+01</c> CR LF for an error, where the code
    /// is "derived by subtracting 9×10¹⁰ from the input number, then dividing the result by 1000"; so
    /// Error 33 arrives as <c>+9000003300E+01</c>. The '--' display is output as Error 96; the '----'
    /// and '------' displays are never output to HP-IB (p. 3-285).
    /// </summary>
    public sealed class Hp8902AReading
    {
        /// <summary>Smallest value that is an error sentinel rather than a measurement.</summary>
        public const double ErrorThreshold = 9e10;

        public string Raw { get; }
        public Hp8902AReadingKind Kind { get; }
        /// <summary>The measured value; NaN unless <see cref="Kind"/> is <see cref="Hp8902AReadingKind.Value"/>.</summary>
        public double Value { get; }
        /// <summary>The decoded error; null unless <see cref="Kind"/> is <see cref="Hp8902AReadingKind.Error"/>.</summary>
        public Hp8902AError Error { get; }

        public bool IsValue => Kind == Hp8902AReadingKind.Value;

        private Hp8902AReading(string raw, Hp8902AReadingKind kind, double value, Hp8902AError error)
        {
            Raw = raw;
            Kind = kind;
            Value = value;
            Error = error;
        }

        public static Hp8902AReading Decode(string raw)
        {
            // A read that raced Data Ready / an RF-range auto-range can come back empty or as a stray
            // control byte — a transient timing glitch, not bad data (#6).
            if (string.IsNullOrWhiteSpace(raw)) return Make(raw, Hp8902AReadingKind.Empty);

            string s = Regex.Replace(raw, @"[\x00-\x1F\x7F]", "").Trim();
            if (s.Length == 0) return Make(raw, Hp8902AReadingKind.Empty);

            // UNCAL fill: a run of repeated letters when the current RF range is uncalibrated — 'CCCC…'
            // (RF Power) or 'AAAA…'/'aaaa…' (Tuned RF Level). It does NOT reliably set the RECAL status
            // bit, so it has to be recognised from the response itself.
            if (Regex.IsMatch(s, "^[A-Za-z]+$")) return Make(raw, Hp8902AReadingKind.Uncal);

            // No digits at all is a short read, not "unrecognized" — retriable (#6).
            if (!Regex.IsMatch(s, "[0-9]")) return Make(raw, Hp8902AReadingKind.Empty);

            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
            {
                Match m = Regex.Match(s, @"[-+]?[0-9]*\.?[0-9]+([eE][-+]?[0-9]+)?");
                if (!m.Success || !double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                    return Make(raw, Hp8902AReadingKind.Unrecognized);
            }

            if (v >= ErrorThreshold)
            {
                int code = (int)Math.Round((v - ErrorThreshold) / 1000.0);
                return new Hp8902AReading(raw, Hp8902AReadingKind.Error, double.NaN, Hp8902AErrors.Lookup(code));
            }
            return new Hp8902AReading(raw, Hp8902AReadingKind.Value, v, null);
        }

        private static Hp8902AReading Make(string raw, Hp8902AReadingKind kind) =>
            new Hp8902AReading(raw, kind, double.NaN, null);

        public override string ToString() =>
            Kind == Hp8902AReadingKind.Value ? Value.ToString("G9", CultureInfo.InvariantCulture)
          : Kind == Hp8902AReadingKind.Error ? Error.ToString()
          : Kind.ToString().ToUpperInvariant();
    }
}
