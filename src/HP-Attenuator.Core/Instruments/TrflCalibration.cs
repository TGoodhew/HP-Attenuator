using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace HpAttenuator.Instruments
{
    /// <summary>
    /// Settings for a Tuned RF Level CALIBRATE (#41). All are configurable (harness flags
    /// <c>--cal-gate-samples</c>, <c>--cal-gate-tol</c>, <c>--cal-retries</c>, <c>--cal-retry-wait-ms</c>,
    /// <c>--cal-result-timeout-ms</c>, <c>--cal-suspect-hold-ms</c>); none is hard-coded in the routine.
    /// </summary>
    public sealed class TrflCalibrationOptions
    {
        /// <summary>Readings taken before C1 to check the level is steady. 0 disables the gate.
        /// Each is a full settled read (~6 s on this bench), so this is the main cost of the gate.</summary>
        public int StabilitySamples { get; set; } = 3;

        /// <summary>Maximum spread (max − min) of the gate readings, dB. The 8340B's own level scatter
        /// measured 0.001-0.004 dB (2026-09-05), so 0.10 dB is generous for the source and still tight
        /// against anything that would upset a CALIBRATE.</summary>
        public double StabilityToleranceDb { get; set; } = 0.10;

        /// <summary>Retries for a recoverable failure: Error 33/34/35, an unsteady gate, an empty
        /// result, or a CALIBRATE that left RECAL/UNCAL asserted.</summary>
        public int MaxRetries { get; set; } = 2;

        /// <summary>Wait before re-gating and retrying, ms.</summary>
        public int RetryWaitMs { get; set; } = 3000;

        /// <summary>
        /// How long to wait for the result read after C1, ms. The 8902A holds the bus for the whole
        /// cycle (measured 6.7-18 s, 2026-10-15) and may then sit in the '----' transitory state, which
        /// is never output to HP-IB (O&amp;C p. 3-285). The trigger write that follows C1 is bounded by
        /// the session timeout (<c>--read-timeout-ms</c>), not by this.
        /// </summary>
        public int ResultTimeoutMs { get; set; } = 60000;

        /// <summary>
        /// A CALIBRATE that releases the bus sooner than this is flagged SUSPECT, ms; 0 = off. Basis: on
        /// 2026-10-15 a clean CALIBRATE at 20 dB held the bus ~18 s and one that raised Error 33 at
        /// 60 dB only ~6.7 s. That is two data points, so by default this only WARNS.
        /// </summary>
        public int SuspectHoldMs { get; set; } = 10000;

        /// <summary>Treat a SUSPECT (short-hold) CALIBRATE as a failure instead of a warning.</summary>
        public bool SuspectHoldIsFailure { get; set; }
    }

    /// <summary>How a CALIBRATE ended.</summary>
    public enum TrflCalibrationOutcome
    {
        /// <summary>A valid reading after C1, with RECAL/UNCAL not asserted.</summary>
        Confirmed,
        /// <summary>Confirmed, but the cycle was shorter than <see cref="TrflCalibrationOptions.SuspectHoldMs"/>.</summary>
        ConfirmedSuspect,
        /// <summary>The level never steadied within tolerance — C1 was not sent.</summary>
        Unstable,
        /// <summary>The 8902A returned an error code for the calibration.</summary>
        Error,
        /// <summary>Error 31: RF Power must be zeroed and calibrated first.</summary>
        NeedsRfPowerCalibration,
        /// <summary>No valid reading, or RECAL/UNCAL still asserted, after the retries.</summary>
        NotConfirmed,
    }

    /// <summary>The record of one C1 attempt, kept for the log.</summary>
    public sealed class TrflCalibrationAttempt
    {
        public int Number { get; set; }
        /// <summary>Gate readings before this C1 (NaN for a non-numeric read).</summary>
        public List<double> GateReadings { get; } = new List<double>();
        /// <summary>Raw HP-IB responses of the gate reads.</summary>
        public List<string> GateRaw { get; } = new List<string>();
        public double GateSpreadDb { get; set; } = double.NaN;
        /// <summary>Every string sent to the 8902A for this attempt, in order.</summary>
        public List<string> Sent { get; } = new List<string>();
        /// <summary>Raw HP-IB response to the post-C1 read; null if none was taken.</summary>
        public string ResultRaw { get; set; }
        public Hp8902AReading Result { get; set; }
        /// <summary>Status byte from the poll after the result read; -1 if the poll failed.</summary>
        public int StatusAfter { get; set; } = -1;
        /// <summary>From sending C1 to the following trigger write being accepted — how long the
        /// CALIBRATE held the bus, ms.</summary>
        public long HoldMs { get; set; }
        /// <summary>From sending C1 to the result being read, ms.</summary>
        public long CycleMs { get; set; }
        public string Verdict { get; set; }

        public string GateSummary(double tolDb) =>
            GateReadings.Count == 0
                ? "gate off"
                : $"gate {GateReadings.Count} reads [{string.Join(", ", GateReadings.Select(F))}] " +
                  $"spread {F(GateSpreadDb)} dB (tol {F(tolDb)})";

        private static string F(double v) =>
            double.IsNaN(v) ? "n/a" : v.ToString("0.000", CultureInfo.InvariantCulture);
    }

    /// <summary>The whole CALIBRATE: every attempt, and how it ended.</summary>
    public sealed class TrflCalibrationResult
    {
        public List<TrflCalibrationAttempt> Attempts { get; } = new List<TrflCalibrationAttempt>();
        public TrflCalibrationOutcome Outcome { get; set; }
        /// <summary>The decoded error that ended it, when <see cref="Outcome"/> is an error outcome.</summary>
        public Hp8902AError Error { get; set; }
        /// <summary>Retries used (attempts beyond the first that were caused by a recoverable failure).</summary>
        public int Retries { get; set; }
        /// <summary>The level last read before C1 (the gate mean), for reporting — NaN if unknown.</summary>
        public double LevelBefore { get; set; } = double.NaN;
        /// <summary>One-line operator-facing explanation of the outcome and what to do next.</summary>
        public string Message { get; set; }

        public bool Succeeded =>
            Outcome == TrflCalibrationOutcome.Confirmed || Outcome == TrflCalibrationOutcome.ConfirmedSuspect;
    }

    /// <summary>
    /// A Tuned RF Level CALIBRATE that could not be positively confirmed (#41). Callers must STOP: the
    /// attenuator and the source must not be stepped after an unconfirmed calibration, because every
    /// reading after it rests on a range factor nobody can vouch for.
    /// </summary>
    public sealed class TrflCalibrationFailedException : Exception
    {
        public TrflCalibrationResult Result { get; }
        /// <summary>The 8902A error code, or -1 when the failure was not an error code.</summary>
        public int Code => Result.Error?.Code ?? -1;

        public TrflCalibrationFailedException(TrflCalibrationResult result)
            : base("8902A Tuned RF Level CALIBRATE failed: " + result.Message)
        {
            Result = result;
        }
    }
}
