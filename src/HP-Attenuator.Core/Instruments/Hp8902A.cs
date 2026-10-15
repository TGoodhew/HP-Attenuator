using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using HpAttenuator.Visa;

namespace HpAttenuator.Instruments
{
    /// <summary>
    /// Driver for the HP 8902A Measuring Receiver, measuring attenuation as a relative
    /// Tuned RF Level in dB (the manual-recommended method — see the attenuation
    /// procedure note). HP-IB codes (8902A Operation manual): S4 Tuned RF Level,
    /// 4.0SP IF synchronous detector, 1.0SP auto RF atten, LG dB display, 32.1SP fine
    /// resolution, RF = SET REF, C1 = CALIBRATE, M5 = RF frequency, 27.x SP = frequency
    /// offset, 37.x SP = cal factors. Readings are the 17-char implicit-point form; a
    /// value ≥ 9e10 is an error sentinel +900000NNNNE+01.
    /// </summary>
    public sealed class Hp8902A : IMeasuringReceiver
    {
        /// <summary>
        /// When set, every command sent to the 8902A is traced here together with the status
        /// byte read back immediately after, so the command that triggers an instrument error
        /// (status bit 0x04 — e.g. Error 35) can be identified. Gated by the harness --debug
        /// flag; null = no tracing (and no per-command serial poll).
        /// </summary>
        public static Action<string> DebugLog;

        private readonly IInstrumentLink _link;

        /// <summary>Settle delay after CALIBRATE / SET REF, ms. The settled read uses T3.</summary>
        public int SettleMilliseconds { get; set; } = 0;

        /// <summary>Delay after ZERO before reading back, ms.</summary>
        public int ZeroSettleMs { get; set; } = 5000;

        /// <summary>Delay after CALIBRATE-on before reading the reference, ms.</summary>
        public int CalSettleMs { get; set; } = 3000;

        public Hp8902A(IInstrumentLink link) => _link = link ?? throw new ArgumentNullException(nameof(link));

        public string ResourceName => _link.ResourceName;

        /// <summary>
        /// Sends a command and, when <see cref="DebugLog"/> is set, serial-polls the status
        /// byte immediately after and traces both — flagging the instrument-error bit (0x04)
        /// so the offending command (e.g. the one raising Error 35) is obvious.
        /// </summary>
        private void Send(string command)
        {
            // Trace the command even if the WRITE throws (#36). The trace used to be emitted only
            // after a successful write, so a wedged bus made the log fall silent at exactly the moment
            // things broke — an unbroken run of healthy traffic, then nothing, with no record of which
            // command was in flight. That is the same class of defect as rendering a failed poll as
            // 0x00: the diagnostic record is least trustworthy precisely where it matters most.
            try
            {
                _link.Write(command);
            }
            catch (Exception ex)
            {
                DebugLog?.Invoke($"8902A < {command,-14} WRITE FAILED ({ex.GetType().Name}) " +
                                 "<-- the bus did not accept this command; nothing after it ran");
                throw;
            }

            if (DebugLog == null) return;

            int sb = PollStatusForTrace();

            // A failed poll returns sb = -1. Do NOT run the flag checks on it: -1 & 0x04 == 0x04 in
            // two's-complement, so the old code printed "INSTRUMENT ERROR" for *every* failed poll
            // (and would have false-flagged RECAL/UNCAL too) — issue #4. A -1 means the poll didn't
            // answer, not that the instrument errored, so label it as such.
            string flags, note;
            if (sb < 0)
            {
                flags = "?";
                note = "  <-- serial poll failed (instrument busy?)";
            }
            else
            {
                flags = $"0x{sb:X2}";
                note = (sb & 0x04) != 0 ? "  <-- INSTRUMENT ERROR (0x04)"
                     : (sb & 0x20) != 0 ? "  (RECAL/UNCAL 0x20)" : "";
            }
            DebugLog($"8902A < {command,-14} SB={flags}{note}");
        }

        /// <summary>Delay before the single debug-trace poll retry, ms (#4).</summary>
        private const int TracePollRetryMs = 200;

        /// <summary>
        /// Serial-polls the status byte for the debug trace, retrying once after a short settle. The
        /// poll transiently fails right after entering frequency-offset mode (<c>27.3SP&lt;LO&gt;MZ</c>
        /// following <c>S4</c>): the 8902A is briefly busy reconfiguring and doesn't answer within the
        /// VISA timeout (issue #4). It is benign — every later command polls cleanly and the
        /// measurement proceeds — so a single retry usually catches the settled status. Returns -1 if
        /// the poll still fails. Debug path only (never on the measurement hot path). Each attempt is
        /// bounded by <see cref="PollTimeoutMs"/> (#36) — unbounded, a poll that never answered cost a
        /// full session timeout per attempt, after every command.
        /// </summary>
        private int PollStatusForTrace()
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try { return _link.SerialPoll(PollTimeoutMs); }
                catch { if (attempt == 0) Thread.Sleep(TracePollRetryMs); }
            }
            return -1;
        }

        public void Initialize()
        {
            // Device clear drops pending I/O and deasserts a latched SRQ; IP presets to a
            // known state (Free-Run T0, SRQ mask = HP-IB code error only).
            _link.Clear();
            Send("IP");
        }

        public void Reset() => Send("IP");

        public void SelectRfPower() => Send("M4");

        /// <summary>Table #1 (primary / Normal) holds at most this many freq/CF pairs, plus the
        /// separate Reference Cal Factor (8902A Operation manual, RF Power specifications).</summary>
        public const int NormalTableMaxPairs = 16;

        /// <summary>Table #2 (frequency offset) holds at most this many freq/CF pairs, plus REF CF.</summary>
        public const int OffsetTableMaxPairs = 22;

        /// <summary>LO (MHz) used only to genuinely ENTER Frequency Offset mode while loading /
        /// reading the second cal-factor table. The Operation manual: "Every time you use the
        /// second table, you must enter the external LO value" — <c>27.1SP</c> only re-enters with
        /// a previously-set LO, so with no prior LO the offset table is never truly active and the
        /// measurement (which enters offset via <c>27.3SP&lt;LO&gt;MZ</c>) sees an empty table →
        /// Error 15. The exact value is arbitrary; any valid LO enables the table.</summary>
        private const double OffsetTableLoadLoMHz = 5120.53;

        /// <summary>
        /// Loads BOTH cal-factor tables the 8902A needs for RF Power measurements — the Normal
        /// table (direct, <c>27.0SP</c>) and the Frequency-Offset table (converter path,
        /// <c>27.1SP</c>). Per table: <c>M4T0</c> (RF Power + free-run trigger), select the table,
        /// clear it (<c>37.9SP</c>), set the <b>Reference Cal Factor</b> — a separate store entered
        /// value-only as <c>37.3SP{cf}CF</c> (no <c>MZ</c>); this is what actually clears Error 15
        /// ("no Cal Factors stored") — then write each freq/CF pair as <c>37.3SP{freqMHz}MZ{cf}CF</c>
        /// (<c>CF</c> = the "% CAL FACTOR" terminator; values fixed-2-decimal). Pairs are capped to
        /// the table's documented capacity (<see cref="NormalTableMaxPairs"/> / <see cref="OffsetTableMaxPairs"/>);
        /// entries beyond the cap won't fit (they're unreachable in the low-frequency direct regime
        /// anyway). The <c>T0</c> free-run state is required for the entries to commit.
        /// </summary>
        public void LoadCalFactors(double referenceCf, IReadOnlyList<CalFactor> table)
        {
            WriteCalFactorTable(useOffsetTable: false, referenceCf, table);   // Normal table
            WriteCalFactorTable(useOffsetTable: true, referenceCf, table);    // Frequency-Offset table
            Send("27.0SP");   // leave in Normal mode (not offset-with-no-LO, which re-flags Error 15)
        }

        private void WriteCalFactorTable(bool useOffsetTable, double referenceCf, IReadOnlyList<CalFactor> table)
        {
            Send("M4T0");                                    // RF Power + free-run trigger
            // Select the table. The offset table is only genuinely active when offset mode is
            // ENTERED with an LO (27.3SP<LO>MZ); 27.1SP alone (no prior LO) does not activate it,
            // so the entries never reach the table the measurement consults → Error 15.
            Send(useOffsetTable
                ? "27.3SP" + Fmt(OffsetTableLoadLoMHz) + "MZ"
                : "27.0SP");                                 // Normal (direct) table
            Send("37.9SP");                                  // clear the selected table

            // The Reference Cal Factor is a SEPARATE store, entered value-only with NO frequency:
            // "37.3 SPCL, REF CF value, BLUE, MHz" (Microwave Product Note p.3). Entering it is what
            // clears the idle "no cal factors" Error 15 — a plain 50 MHz *pair* does NOT set the REF CF.
            Send("37.3SP" + string.Format(CultureInfo.InvariantCulture, "{0:F2}CF", referenceCf));

            // The pairs, capped to the table's capacity (Table #1 = 16, Table #2 = 22). Lead with a
            // 50 MHz anchor PAIR: the Operation manual (11792A) — "enter the reference cal factor as
            // an entry in the table at 50 MHz. If this pair is not entered, the instrument will not
            // measure power at frequencies less than the lowest frequency entered" (2 GHz here). This
            // lets the direct regime (<1.3 GHz) measure by interpolating up to the 2 GHz entry.
            int max = useOffsetTable ? OffsetTableMaxPairs : NormalTableMaxPairs;
            int written = 0;
            WriteEntry(ReferenceCfFreqMHz, referenceCf);   // 50 MHz low-frequency anchor pair
            written++;
            foreach (var c in table)
            {
                if (written >= max) break;
                WriteEntry(c.FreqMHz, c.Cf);
                written++;
            }
        }

        /// <summary>The 50 MHz calibrator frequency — entered as a table pair to anchor the low end.</summary>
        private const double ReferenceCfFreqMHz = 50.0;

        private void WriteEntry(double freqMHz, double calFactorPercent) =>
            Send("37.3SP" + string.Format(CultureInfo.InvariantCulture, "{0:F2}MZ{1:F2}CF", freqMHz, calFactorPercent));

        /// <summary>
        /// Reads back BOTH cal-factor tables — Normal (27.0SP) and Frequency-Offset (27.1SP) — to
        /// verify a load committed: the freq/CF pair count (37.4SP) and the Reference Cal Factor
        /// (37.5SP) of each. Leaves Normal mode. A count is -1 / a REF CF is NaN when unreadable.
        /// </summary>
        public (int normalPairs, int offsetPairs, double normalRefCf, double offsetRefCf) ReadCalFactorTables()
        {
            _link.Write("27.0SP"); int normal = TryReadTableSize(); double normalRef = TryReadRefCf();
            // Enter offset mode with an LO so the ACTIVE second table is read (not a 27.1SP no-LO state).
            _link.Write("27.3SP" + Fmt(OffsetTableLoadLoMHz) + "MZ");
            int offset = TryReadTableSize(); double offsetRef = TryReadRefCf();
            _link.Write("27.0SP");                       // leave in Normal mode
            return (normal, offset, normalRef, offsetRef);
        }

        private int TryReadTableSize()
        {
            try
            {
                string raw = _link.Query("37.4SP").Trim();
                if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                    return (int)Math.Round(v);
            }
            catch { /* unreadable */ }
            return -1;
        }

        /// <summary>Recalls the Reference Cal Factor of the currently-selected table: 37.5SP makes
        /// it the "current" cal factor, then the CF query reads that value back (a bare read after
        /// 37.5SP returns the live measurement, not the recalled factor).</summary>
        private double TryReadRefCf()
        {
            try
            {
                _link.Write("37.5SP");                       // recall reference cal factor to "current"
                string raw = _link.Query("CF").Trim();       // read current calibration factor
                if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                    return v;
            }
            catch { /* unreadable */ }
            return double.NaN;
        }

        public double ZeroSensor()
        {
            Send("M4");        // RF Power
            Send("C0");        // calibrator off — no reference power while zeroing
            Send("ZR");        // zero the sensor
            if (ZeroSettleMs > 0) Thread.Sleep(ZeroSettleMs);
            return ReadMeasurement(); // watts, ~0
        }

        public double CalibrateSensor()
        {
            Send("M4");        // ensure RF Power mode (continues from the zero step)
            Send("C1");        // calibrator on: 50 MHz / 1 mW reference
            if (CalSettleMs > 0) Thread.Sleep(CalSettleMs);

            // Settled read with the calibrator on (manual: C1 T3 SC). If the reference
            // isn't ~1 mW (0 dBm), the sensor is not on the CALIBRATION RF POWER OUTPUT;
            // do NOT save — saving here would corrupt the sensor cal.
            double pre = ReadMeasurement();
            double preDbm = Rf.WattsToDbm(pre);
            if (preDbm < -10.0)
            {
                Send("C0");
                throw new Hp8902AException(18,
                    $"reference reads {preDbm:0.0} dBm, not ~0 dBm — {Hp8902AException.Describe(18)}");
            }

            Send("SC");        // save cal — scales the reference to read 1.000 mW
            double reference = ReadMeasurement();
            Send("C0");        // calibrator off
            return reference;         // watts, ≈ 1e-3
        }

        /// <summary>HP-IB special function that selects AUTOMATIC tuning for Tuned RF Level (#3). The
        /// Operation-manual SF-7 tuning codes are OCR-ambiguous in the scan, so this is a BEST-EFFORT
        /// value to confirm/correct on the bench — which is why auto tuning is opt-in (manual is the
        /// default). See HardwareValidation.md V7.</summary>
        private const string AutoTuneSpecialFunction = "7.1SP";

        /// <summary>Time to let AUTOMATIC tuning search for and acquire the signal before holding it, ms.</summary>
        private const int AutoTuneAcquireMs = 3000;

        public void BeginAttenuationMeasurement(double rfMHz, MeasurementRegime regime, double loMHz,
            TrflDetector detector = TrflDetector.Average, bool trackMode = false,
            TrflTuning tuning = TrflTuning.Manual, bool noiseCorrection = false)
        {
            // Manual "Attenuator Measurements" (relative Tuned RF Level, O&C 3-115): S4, tune, pick
            // the detector/mode, then SET REF (done by the engine).
            Send("S4");      // Tuned RF Level
            if (regime == MeasurementRegime.Converted)
                Send("27.3SP" + Fmt(loMHz) + "MZ");  // frequency-offset: external LO
            else
                Send("27.0SP");                      // direct / normal mode

            if (tuning == TrflTuning.Auto)
            {
                // Automatic tuning (#3): let the receiver SEARCH for and acquire the signal, wait for it
                // to lock, then drop to manual tune to HOLD it and re-enter TRFL — the O&C manual's
                // "select Auto Tuning … then press MHz to select Manual Tuning and re-enter TRFL"
                // sequence. AutoTuneSpecialFunction is an unverified SF (OCR-ambiguous — verify on bench).
                DebugLog?.Invoke($"8902A AUTO-TUNE via {AutoTuneSpecialFunction} (UNVERIFIED SF — #3; confirm on bench), " +
                                 $"acquire {AutoTuneAcquireMs} ms, then hold at {Fmt(rfMHz)} MHz");
                Send(AutoTuneSpecialFunction);     // search + acquire
                Thread.Sleep(AutoTuneAcquireMs);   // give it time to lock onto the signal
            }
            Send(Fmt(rfMHz) + "MZ");   // tune to the fixed frequency (manual, or HOLD after auto-acquire)

            if (trackMode)
            {
                // Microwave Product Note (low-level microwave TRFL through the converter): Track Mode
                // (32.9SP) keeps the receiver LOCKED onto the drifting converted signal, which is what
                // lets it hold down toward the ~-100 dBm converter floor instead of losing lock
                // (Error 96) partway down. 32.9SP IS 4.4 (IF Average detector) + 8.1 + Log + Track +
                // offset, so it supersedes the detector arg. Caveat (our earlier Error 35): to
                // CALIBRATE in Track Mode the level must be held steady (attenuator fixed) during the
                // CALIBRATE, and reacquisition after a lost lock needs the signal ≥ -80 dBm.
                Send("32.9SP");        // Track Mode (AVG detector + track + Log + offset)
            }
            else
            {
                // Detector selects noise BW vs depth (O&C, Tuned RF Level ranges). AVERAGE (4.4SP,
                // 30 kHz BW) tolerates the converter/LO path's residual FM; SYNCHRONOUS (4.0SP, 200 Hz
                // BW) has a lower raw floor but its narrow band loses lock on a drifty converted signal
                // (confirmed #14 — Error 96 below ~-100 dBm). Through the 11793A the practical floor is
                // ~-100 dBm either way (Microwave Product Note). Range-to-range CALIBRATE references the
                // sensor module (the 11792A, in the chain).
                Send(detector == TrflDetector.Synchronous ? "4.0SP" : "4.4SP");

                // Noise correction (SF 31). The manual: "For added noise correction when using the IF
                // Average detector to measure low-level signals, key in 31.1 SPCL when you select the IF
                // Average detector (that is prior to calibrating Range 1) ... causes the instrument to
                // create an additional calibration factor for Range 3 (-60 to -100 dBm) which it uses to
                // compensate for any residual noise inherent within the measurement system."
                //
                // Two conditions bind here. (1) It must be sent NOW - immediately after selecting the
                // detector and BEFORE any range CALIBRATE - which is why it lives here and not in the
                // engine. (2) The CAUTION forbids 31.1 for "any uncalibrated, or relative signal level
                // measurement in Tuned RF Level mode WITHOUT a power sensor". Ours IS a relative
                // measurement, so this is only legitimate because the 11792A Sensor Module is in the
                // chain - which the same passage requires for AVG-detector range-to-range calibration.
                // It also only does anything if a Range 3 CALIBRATE actually fires (see #17 /
                // ForceRangeCal): with no calibration there is no factor for it to create.
                Send(noiseCorrection ? "31.1SP" : "31.0SP");

                Send("LG");            // dB display -> bus returns dB
            }
            Send("1.0SP");             // auto RF attenuation (keep fixed after cal)
            Send("32.1SP");            // 0.001 dB resolution
            UnmaskMeasurementStatus(); // so ReadMeasurement's completion poll can see Data Ready
        }

        public void BeginRfPowerMeasurement(double rfMHz, MeasurementRegime regime, double loMHz)
        {
            Send("M4");      // RF Power (power sensor)
            if (regime == MeasurementRegime.Converted)
                Send("27.3SP" + Fmt(loMHz) + "MZ");  // frequency-offset: external LO
            else
                Send("27.0SP");                      // direct / normal mode
            Send(Fmt(rfMHz) + "MZ");   // tune to the RF frequency: the automatic cal factor is
                                       // selected only AFTER the receiver has tuned (Operation
                                       // manual, RF Power) — without it, RF POWER raises Error 15.
            Send("37.0SP");  // automatic cal-factor selection (table loaded separately)
            UnmaskMeasurementStatus(); // so ReadMeasurement's completion poll can see Data Ready
        }

        /// <summary>
        /// Unmasks the Status Byte bits the completion handshake polls for — Data Ready (bit 0),
        /// Instrument Error (bit 2) and RECAL/UNCAL (bit 5), i.e. SF 22.37 (1+4+32; the HP-IB error
        /// bit 1/weight 2 is permanently set). REQUIRED before any measurement: at power-up / IP the
        /// 8902A masks every Status Byte bit except HP-IB error (O&amp;C 3-25), so without this a serial
        /// poll reads 0x00 and <see cref="ReadMeasurement"/> never sees Data Ready — it would burn the
        /// whole budget on every read.
        /// </summary>
        private void UnmaskMeasurementStatus() => Send("22.37SP");

        public double ReadRfPowerDbm()
        {
            // RF Power fundamental unit = watts; convert to dBm for reporting.
            return Rf.WattsToDbm(ReadMeasurement());
        }

        /// <summary>RECAL/UNCAL condition weight in the 8902A status byte (Special Function 22).</summary>
        private const byte RecalStatusBit = 0x20;   // 32 = "Recal or Uncal"

        /// <summary>Data Ready bit in the 8902A status byte — set when a measurement result is ready.</summary>
        private const byte DataReadyBit = 0x01;

        /// <summary>Instrument-error bit in the 8902A status byte (e.g. Error 96 no-signal).</summary>
        private const byte InstrErrorBit = 0x04;

        /// <summary>Serial-poll interval while waiting for Data Ready, ms.</summary>
        private const int DataReadyPollMsDefault = 250;

        /// <summary>Status-byte poll interval, ms. Overridable for the headless self-test.</summary>
        public int DataReadyPollMs { get; set; } = DataReadyPollMsDefault;

        /// <summary>How long to watch the status byte for a completed measurement before giving up, ms.
        /// Well above the longest legitimate settled read (~6 s typical, ~12 s near the floor) so a
        /// real measurement always finishes first; a stalled read past this propagates as a timeout so
        /// the caller can release the bus (#11) — kept modest so a genuine hang recovers promptly.</summary>
        private const int DataReadyBudgetMsDefault = 30000;

        /// <summary>
        /// The read budget, ms — and since #36 it is actually enforced. It used to be tested only
        /// BETWEEN polls while each poll inherited the 60 s session timeout, so one poll that did not
        /// answer overran it without bound: the 5 GHz hang logged "NOT set after 134.5 s" against this
        /// 30 s. Now each poll is given only what is left of the budget (never less than
        /// <see cref="PollTimeoutMs"/>), so the wait ends within budget + one poll timeout.
        ///
        /// The budgets, reconciled — worst case for one settled read that never completes:
        /// this budget (30 s) + at most one <see cref="PollTimeoutMs"/> of overrun (15 s) + the
        /// retrieve, which still uses the session timeout (60 s, <c>--read-timeout-ms</c>) because a
        /// slow-but-real result must not be cut off. ~105 s, every part of it now logged — against
        /// an unbounded total before. Overridable for the self-test.
        /// </summary>
        public int DataReadyBudgetMs { get; set; } = DataReadyBudgetMsDefault;

        /// <summary>
        /// Timeout for a single serial poll that has no larger budget to draw on, ms: diagnostic polls,
        /// the release check, and the floor for the last poll of a read (so the poll that decides a
        /// read is a fair one, not a 10 ms sliver that fails and muddies the diagnosis). A healthy
        /// 8902A answers a poll in milliseconds — but it inhibits the bus handshake until a triggered
        /// cycle completes (O&amp;C 3-22), so a poll can legitimately wait out a cycle: ~6 s settled,
        /// ~12 s near the floor. 15 s clears that and is still a quarter of the 60 s session timeout
        /// every poll used to inherit. Overridable for the self-test.
        /// </summary>
        public int PollTimeoutMs { get; set; } = 15000;

        /// <summary>A poll slower than this is logged on its own as it happens, ms — so a hang is
        /// visible in the log while it is in progress, not only in the summary after it.</summary>
        private const int SlowPollMs = 1000;

        /// <summary>
        /// Where settled-read time goes, accumulated across every read this driver makes (#36, V9):
        /// trigger write, waiting on the status byte (and how much of that was spent INSIDE polls
        /// rather than sleeping between them), retrieving the result, and the post-read settle.
        /// Collected whether or not <see cref="DebugLog"/> is set, so <c>--profile</c> (which must run
        /// without <c>--debug</c>) can report it. V9's unexplained 18.4 s/read needs exactly this split.
        /// </summary>
        public sealed class ReadPhaseTimes
        {
            public int Reads;
            public long TriggerMs, WaitMs, InsidePollsMs, RetrieveMs, SettleMs, LongestPollMs;
            public int Polls, FailedPolls;
        }

        public ReadPhaseTimes ReadTimes { get; } = new ReadPhaseTimes();

        public void BeginRangeCalibration()
        {
            UnmaskMeasurementStatus();   // Data Ready + Instr Error + Recal/Uncal in the status byte
            // Free-run trigger so the receiver keeps measuring (and updating RECAL) as the
            // attenuator steps down, rather than waiting for a settled trigger.
            Send("T0");
        }

        /// <summary>Unmask RECAL/UNCAL (and Data Ready + Instr Error) in the status byte WITHOUT the
        /// free-run trigger, so a serial poll reflects RECAL but the receiver keeps its settled (T3)
        /// ranging instead of auto-ranging.</summary>
        public void EnableRecalStatus() => UnmaskMeasurementStatus();

        public bool RecalRequested() => (_link.SerialPoll(PollTimeoutMs) & RecalStatusBit) != 0;

        /// <summary>Diagnostic poll, bounded by <see cref="PollTimeoutMs"/>. The sweep calls this right
        /// after a read has timed out — i.e. on the bus that just hung — where an unbounded poll could
        /// add another full session timeout before anything is released (#36).</summary>
        public int PollStatusByte() => _link.SerialPoll(PollTimeoutMs);

        /// <summary>Settings for <see cref="Calibrate"/> — gate size and tolerance, retries, timeouts
        /// (#41). Configurable from the harness; nothing in the routine is hard-coded.</summary>
        public TrflCalibrationOptions CalibrationOptions { get; set; } = new TrflCalibrationOptions();

        /// <summary>
        /// Every CALIBRATE attempt is logged here — raw HP-IB strings, decoded result, gate spread,
        /// retry count, bus-hold time and outcome (#41). Separate from <see cref="DebugLog"/> so it can
        /// be on for every run, not only under <c>--debug</c>.
        /// </summary>
        public static Action<string> CalibrationLog;

        /// <summary>
        /// Called on Error 31 ("requires new power reference — calibrate RF Power before attempting
        /// calibration of Tuned RF Level", O&amp;C p. 3-289) so the host can walk the operator through
        /// the RF Power zero and calibration, which needs the sensor moved to the CALIBRATION RF POWER
        /// OUTPUT. The CALIBRATE still fails: the sensor procedure presets the 8902A, so the run has to
        /// be re-established afterwards anyway.
        /// </summary>
        public Action<TrflCalibrationResult> RfPowerCalibrationRequired { get; set; }

        /// <summary>The last <see cref="Calibrate"/>, for callers that want the record of a success.</summary>
        public TrflCalibrationResult LastCalibration { get; private set; }

        /// <summary>
        /// Tuned RF Level CALIBRATE (C1), confirmed positively rather than by the status byte (#41).
        ///
        /// WHY NOT SRQ / THE STATUS BYTE. The obvious design — unmask Instrument Error (22.<n>SP, bit
        /// weight 4, O&amp;C p. 3-30/3-31) and wait for it — cannot detect a failed calibration on this
        /// receiver:
        /// <list type="bullet">
        /// <item>Bench, 2026-10-15 (V17): Error 33 on the display at 60 dB with mask 22.37 in force; the
        /// SRQ lamp never lit and the status byte read 0x00. A read taken straight after C1 also
        /// returned a normal level, not the +9000003300E+01 sentinel.</item>
        /// <item>The status byte cannot change while the instrument is being polled (p. 3-32), so
        /// "poll until the bit appears" holds off the bit it waits for.</item>
        /// <item>The RECAL/UNCAL bit (32) is asserted when the annunciator TURNS ON (p. 3-31) — it is
        /// edge-triggered, and a failed calibration that leaves RECAL lit makes no new edge.</item>
        /// </list>
        /// Status-byte use for Data Ready in the settled read is unchanged: that part works.
        ///
        /// THE ROUTINE, per attempt:
        /// <list type="number">
        /// <item>Stability gate: <see cref="TrflCalibrationOptions.StabilitySamples"/> settled readings;
        /// proceed only if their spread is within <see cref="TrflCalibrationOptions.StabilityToleranceDb"/>.
        /// The manual's procedure is "hold the signal level steady" (p. 3-115) and Error 33's corrective
        /// action is "maintain consistency in frequency and level at SENSOR during calibration"
        /// (p. 3-289).</item>
        /// <item>C1, then a triggered read: the manual's completion signal is "hold the signal level
        /// steady until a valid measurement is displayed" (p. 3-115). The 8902A holds the bus for the
        /// whole cycle, so the trigger write itself waits it out; how long it held is logged.</item>
        /// <item>Success = a valid numeric reading AND RECAL/UNCAL not asserted — no UNCAL fill, no
        /// RECAL-without-result during the read, and no bit 32 in the poll after it. There is no HP-IB
        /// query for RECAL state in the manual, so those are the only ways to see it.</item>
        /// <item>Any decoded error fails the attempt, handled per code below.</item>
        /// </list>
        /// Per error (codes and texts O&amp;C p. 3-289; class p. 3-286):
        /// 33/34/35 — wait, re-gate, retry up to <see cref="TrflCalibrationOptions.MaxRetries"/>, then
        /// stop and suggest a source stability check or the E4438C. 31 — stop, and call
        /// <see cref="RfPowerCalibrationRequired"/>. 32 — stop and report the level. 30 — set Special
        /// Functions 1 and 9 to automatic selection (1.0SP, 9.0SP: p. 3-286, "setting Special Functions
        /// 1 and 9 to automatic selection") and retry once. NOT 'AU': AUTOMATIC OPERATION resets every
        /// special function 1-10 and most others, which would wipe the Tuned RF Level setup.
        ///
        /// On failure this throws <see cref="TrflCalibrationFailedException"/>, and callers must stop:
        /// never step the attenuator or change the source after an unconfirmed calibration.
        ///
        /// KNOWN LIMIT: on this unit Error 33 reaches the bus neither way, so a 33 that the panel shows
        /// can still end here as Confirmed. The bus-hold time is the one signal seen so far that
        /// differs (~18 s clean vs ~6.7 s failing, two points); below
        /// <see cref="TrflCalibrationOptions.SuspectCycleMs"/> the result is ConfirmedSuspect.
        /// </summary>
        public void Calibrate()
        {
            var o = CalibrationOptions ?? new TrflCalibrationOptions();
            var result = new TrflCalibrationResult();
            LastCalibration = result;
            bool autoRestored = false;

            for (int number = 1; ; number++)
            {
                var a = new TrflCalibrationAttempt { Number = number };
                result.Attempts.Add(a);
                string tag = $"TRFL CAL attempt {number}";

                // 1. Stability gate --------------------------------------------------------------
                string gateProblem = RunStabilityGate(a, o, result);
                CalibrationLog?.Invoke($"{tag}: {a.GateSummary(o.StabilityToleranceDb)}" +
                                       (a.GateRaw.Count > 0 ? $" raw [{string.Join(", ", a.GateRaw)}]" : "") +
                                       (gateProblem == null ? (o.StabilitySamples > 0 ? " — steady" : "") : $" — {gateProblem}"));
                if (gateProblem != null)
                {
                    if (a.Result?.Error != null)
                    {
                        // An error before C1 was even sent (e.g. 96, no signal): not a calibration
                        // problem, and nothing to retry until the signal is back.
                        Fail(result, a, TrflCalibrationOutcome.Error, a.Result.Error,
                             $"the receiver reported {a.Result.Error} before CALIBRATE was sent — fix that first.");
                    }
                    if (result.Retries < o.MaxRetries)
                    {
                        result.Retries++;
                        a.Verdict = $"level not steady — retry {result.Retries}/{o.MaxRetries} after {o.RetryWaitMs} ms";
                        CalibrationLog?.Invoke($"{tag}: {a.Verdict}");
                        Thread.Sleep(Math.Max(0, o.RetryWaitMs));
                        continue;
                    }
                    Fail(result, a, TrflCalibrationOutcome.Unstable, null,
                         $"the level did not steady within {o.StabilityToleranceDb:0.###} dB over {o.StabilitySamples} " +
                         $"readings after {result.Retries} retries ({gateProblem}); CALIBRATE was not sent. " +
                         "Check the source's level stability (8340B), or substitute the E4438C.");
                }

                // 2. C1, then the result read ------------------------------------------------------
                var sw = System.Diagnostics.Stopwatch.StartNew();
                Send("C1");
                a.Sent.Add("C1");
                long sendMs = sw.ElapsedMilliseconds;   // includes the --debug trace poll, if on
                RawRead r;
                try
                {
                    a.Sent.Add("T3");
                    r = ReadRawMeasurement(Math.Max(1, o.ResultTimeoutMs));
                }
                catch (Exception ex)
                {
                    a.CycleMs = sw.ElapsedMilliseconds;
                    a.Verdict = $"no result: {ex.GetType().Name}";
                    CalibrationLog?.Invoke($"{tag}: > C1 > T3 — no result after {a.CycleMs / 1000.0:0.0} s " +
                                           $"({ex.GetType().Name}: {ex.Message})");
                    throw;   // a bus fault, not a calibration verdict: let the caller's bus handling see it
                }
                a.CycleMs = sw.ElapsedMilliseconds;
                // The trigger write waits for the CALIBRATE to release the bus, so C1 + that write is
                // how long the cycle held it.
                a.HoldMs = sendMs + r.TriggerMs;
                a.ResultRaw = r.Raw;
                a.Result = r.RecalNoResult ? null : Hp8902AReading.Decode(r.Raw);

                // RECAL is visible only as bit 32 or as the UNCAL fill (no HP-IB query for it is given
                // in the manual). Read it once, after the result, without polling continuously (p. 3-32).
                try { a.StatusAfter = _link.SerialPoll(PollTimeoutMs); } catch { a.StatusAfter = -1; }
                bool recal = r.RecalNoResult
                             || a.Result?.Kind == Hp8902AReadingKind.Uncal
                             || (a.StatusAfter >= 0 && (a.StatusAfter & RecalStatusBit) != 0);

                string sentLine = $"> {string.Join(" > ", a.Sent)} < '{(r.RecalNoResult ? "(none: RECAL/UNCAL)" : r.Raw)}' " +
                                  $"(held bus {a.HoldMs / 1000.0:0.0} s, cycle {a.CycleMs / 1000.0:0.0} s, " +
                                  $"SB after {(a.StatusAfter < 0 ? "poll failed" : $"0x{a.StatusAfter:X2}")})";

                // 3. Decide ------------------------------------------------------------------------
                if (a.Result != null && a.Result.IsValue && !recal)
                {
                    bool suspect = o.SuspectCycleMs > 0 && a.CycleMs < o.SuspectCycleMs;
                    a.Verdict = suspect
                        ? $"CONFIRMED but SUSPECT — the cycle took {a.CycleMs / 1000.0:0.0} s, under " +
                          $"{o.SuspectCycleMs / 1000.0:0.0} s (Error 33 cycles ran 14.2-16.4 s, clean ones 19.4-24.0 s " +
                          "on the bench). Error 33 does not reach HP-IB on this unit; check the panel."
                        : "CONFIRMED";
                    CalibrationLog?.Invoke($"{tag}: {sentLine} -> {a.Result} -> {a.Verdict}");
                    if (suspect && o.SuspectCycleIsFailure)
                        Fail(result, a, TrflCalibrationOutcome.NotConfirmed, null,
                             $"the CALIBRATE cycle took only {a.CycleMs / 1000.0:0.0} s (SUSPECT, treated as failure).");
                    result.Outcome = suspect ? TrflCalibrationOutcome.ConfirmedSuspect : TrflCalibrationOutcome.Confirmed;
                    result.Message = $"{a.Verdict} after {number} attempt(s), {result.Retries} retr{(result.Retries == 1 ? "y" : "ies")}: " +
                                     $"reads {a.Result}";
                    CalibrationLog?.Invoke($"TRFL CAL: {result.Message}");
                    return;
                }

                Hp8902AError err = a.Result?.Error;
                CalibrationLog?.Invoke($"{tag}: {sentLine} -> " +
                                       (err != null ? err.ToString() : recal ? "RECAL/UNCAL still asserted" : $"{a.Result?.Kind}"));
                if (err != null) { try { ClearError(); a.Sent.Add("CL"); } catch { /* best effort */ } }

                if (err == null || err.Code == 33 || err.Code == 34 || err.Code == 35)
                {
                    // Recoverable in principle: the level or frequency moved during the cycle, or the
                    // CALIBRATE did not take. Wait, re-gate, retry.
                    if (result.Retries < o.MaxRetries)
                    {
                        result.Retries++;
                        a.Verdict = $"retry {result.Retries}/{o.MaxRetries} after {o.RetryWaitMs} ms";
                        CalibrationLog?.Invoke($"{tag}: {a.Verdict}");
                        Thread.Sleep(Math.Max(0, o.RetryWaitMs));
                        continue;
                    }
                    if (err != null)
                        Fail(result, a, TrflCalibrationOutcome.Error, err,
                             $"{err} — still failing after {result.Retries} retries. The level at the sensor is " +
                             "not holding steady through the cycle: check the source's level stability (8340B), " +
                             "or substitute the E4438C.");
                    Fail(result, a, TrflCalibrationOutcome.NotConfirmed, null,
                         (recal ? "RECAL/UNCAL was still asserted" : $"no valid reading ({a.Result?.Kind})") +
                         $" after {result.Retries} retries — the calibration could not be confirmed.");
                }

                switch (err.Code)
                {
                    case 30 when !autoRestored:
                        // Manual input attenuation/gain: put SF 1 and SF 9 back to automatic (p. 3-286)
                        // and try once more. This does not count against the retry budget.
                        autoRestored = true;
                        Send("1.0SP"); a.Sent.Add("1.0SP");
                        Send("9.0SP"); a.Sent.Add("9.0SP");
                        a.Verdict = "Error 30: set SF 1 and SF 9 to automatic (1.0SP, 9.0SP), retrying once";
                        CalibrationLog?.Invoke($"{tag}: {a.Verdict}");
                        continue;

                    case 31:
                        Fail(result, a, TrflCalibrationOutcome.NeedsRfPowerCalibration, err,
                             $"{err} Zero and calibrate RF Power with the sensor on the CALIBRATION RF POWER " +
                             "OUTPUT, then re-run.", notifyRfPower: true);
                        break;

                    case 32:
                        Fail(result, a, TrflCalibrationOutcome.Error, err,
                             $"{err} The level before C1 was " +
                             (double.IsNaN(result.LevelBefore) ? "unknown" : $"{result.LevelBefore:0.00} dB(m)") +
                             " — not in a valid calibration range for this RF input range.");
                        break;

                    default:
                        Fail(result, a, TrflCalibrationOutcome.Error, err, $"{err}");
                        break;
                }
            }
        }

        /// <summary>
        /// Takes the gate readings for one attempt. Returns null if the level is steady (or the gate is
        /// off), otherwise why not. A reading that is not a number ends the gate: UNCAL is the normal
        /// reason to be calibrating at all, so it is NOT a gate failure — there is just no level to
        /// compare; an error code is reported through <c>a.Result</c>.
        /// </summary>
        private string RunStabilityGate(TrflCalibrationAttempt a, TrflCalibrationOptions o, TrflCalibrationResult result)
        {
            if (o.StabilitySamples <= 0) return null;

            for (int i = 0; i < o.StabilitySamples; i++)
            {
                RawRead g = ReadRawMeasurement(DataReadyBudgetMs);
                a.Sent.Add("T3");
                string raw = g.RecalNoResult ? "(none: RECAL/UNCAL)" : g.Raw;
                a.GateRaw.Add(raw);
                var d = g.RecalNoResult ? null : Hp8902AReading.Decode(g.Raw);

                if (d == null || d.Kind == Hp8902AReadingKind.Uncal)
                {
                    a.GateReadings.Add(double.NaN);
                    a.GateSpreadDb = double.NaN;
                    a.Sent.Clear();   // the gate's T3s are not part of the calibration exchange
                    return null;      // UNCAL/RECAL: nothing to compare, and the reason to calibrate
                }
                if (d.Kind == Hp8902AReadingKind.Error)
                {
                    a.GateReadings.Add(double.NaN);
                    a.Result = d;
                    a.Sent.Clear();
                    return $"gate read {i + 1} returned {d.Error}";
                }
                if (!d.IsValue)
                {
                    a.GateReadings.Add(double.NaN);
                    a.Sent.Clear();
                    return $"gate read {i + 1} was {d.Kind} ('{raw}')";
                }
                a.GateReadings.Add(d.Value);
            }

            a.Sent.Clear();
            double min = a.GateReadings.Min(), max = a.GateReadings.Max();
            a.GateSpreadDb = max - min;
            result.LevelBefore = a.GateReadings.Average();
            return a.GateSpreadDb <= o.StabilityToleranceDb
                ? null
                : $"spread {a.GateSpreadDb:0.000} dB exceeds {o.StabilityToleranceDb:0.###} dB";
        }

        private void Fail(TrflCalibrationResult result, TrflCalibrationAttempt a, TrflCalibrationOutcome outcome,
                          Hp8902AError err, string message, bool notifyRfPower = false)
        {
            result.Outcome = outcome;
            result.Error = err;
            result.Message = message;
            a.Verdict = $"FAILED: {outcome}";
            CalibrationLog?.Invoke($"TRFL CAL: FAILED ({outcome}) after {result.Attempts.Count} attempt(s), " +
                                   $"{result.Retries} retr{(result.Retries == 1 ? "y" : "ies")}: {message}");
            if (notifyRfPower)
            {
                try { RfPowerCalibrationRequired?.Invoke(result); }
                catch (Exception ex) { CalibrationLog?.Invoke($"TRFL CAL: the RF Power calibration prompt failed ({ex.GetType().Name})"); }
            }
            throw new TrflCalibrationFailedException(result);
        }

        /// <summary>
        /// Presses CALIBRATE (C1) and returns at once — no completion wait, no verdict. Diagnostic only
        /// (<c>--cal-then-read</c>): V17 showed the status byte never reports Error 33, so the probe
        /// follows C1 immediately with a triggered read to see whether the error comes back as the
        /// read sentinel instead.
        /// </summary>
        public void PressCalibrate() => Send("C1");

        /// <summary>
        /// Sends one raw HP-IB code. Diagnostic only (<c>--srq-diag</c>, #42), for codes the driver has
        /// no method for — an SRQ mask value, or a deliberately invalid code to provoke an Entry Error.
        /// </summary>
        public void SendDiagnostic(string command) => Send(command);

        public void SetReference()
        {
            Send("RF");   // SET REF (special function 26) at the current level
            Settle();
        }

        public void ClearError() => Send("CL");   // CLEAR key — clears a displayed error

        public void RetuneToSignal()
        {
            // Blue Key + CLEAR (O&C 3-116): forces a VCO retune during Tuned RF Level and recaptures
            // the signal if it hasn't drifted more than 5 MHz. The remedy for a lost-lock Error 96 at
            // a range boundary — CL alone clears the error but does NOT re-acquire the signal.
            Send("BC");
            Settle();
        }

        /// <summary>Settle after the device clear before checking the 8902A answers again, ms.</summary>
        private const int ReleaseSettleMs = 500;

        /// <summary>
        /// GPIB device clear (SDC). Aborts a measurement cycle that is holding the bus handshake
        /// (O&amp;C 3-22) and frees the bus so the next write can't collide. Resets the 8902A to preset —
        /// caller must re-setup.
        ///
        /// Then CHECKS that it worked (#36). <c>_link.Clear()</c> swallows its own failure (some
        /// listen-only devices ignore clear), so this used to return normally whether or not the bus
        /// was freed — and the sweep went on to report "released it" and run the post-hang probe, which
        /// failed with IOTimeoutException in both 5 GHz hangs. That is consistent with a clear that did
        /// not take, and nothing recorded it. Now a serial poll that still does not answer after the
        /// clear (twice, <see cref="PollTimeoutMs"/> each) throws, so the caller knows the bus is still
        /// held instead of discovering it one timeout later.
        /// </summary>
        public void ReleaseBus()
        {
            _link.Clear();

            Exception last = null;
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                Thread.Sleep(ReleaseSettleMs);   // SDC presets the 8902A; give it a moment
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    int sb = _link.SerialPoll(PollTimeoutMs);
                    DebugLog?.Invoke($"8902A bus released: answers a serial poll after device clear (SB=0x{sb:X2})");
                    return;
                }
                catch (Exception ex)
                {
                    last = ex;
                    DebugLog?.Invoke($"8902A release check {attempt}/2: no answer to a serial poll after " +
                                     $"device clear ({ex.GetType().Name}, {sw.ElapsedMilliseconds / 1000.0:0.0} s)");
                }
            }
            throw new System.IO.IOException(
                "device clear sent, but the 8902A still does not answer a serial poll — the bus is NOT " +
                $"released (last: {last?.GetType().Name})", last);
        }

        public double ReadRelativeDb()
        {
            // LOG relative mode returns dB. ReadMeasurement's completion handshake surfaces a UNCAL
            // range (RECAL 0x20, or a 'CCCC'/'AAAA' fill) as an Hp8902AException so the caller can
            // CALIBRATE; a valid level parses to dB.
            return ReadMeasurement();
        }

        public double ReadTunedLevelDbm()
        {
            // Before SET REF the S4/LG Tuned RF Level reading IS the absolute level in dBm (SET REF
            // later re-zeroes it to relative dB). Same settled-read path as ReadRelativeDb; the only
            // difference is the caller reads it BEFORE taking the reference, for #16 leveling.
            return ReadMeasurement();
        }

        /// <summary>
        /// Reads the firmware date code (SF 42.0). Some special functions are firmware-gated - notably
        /// noise correction (SF 31.1), which the manual marks "not available with firmware date codes
        /// 234.1985 and below" and "available on instruments serial prefixed 2535A and above". Checking
        /// this first stops us reading "unsupported" as "made no difference".
        /// </summary>
        /// <summary>
        /// Reads a stored Tuned RF Level calibration factor (SF 38.1/38.2/38.3 = RF Range 1/2/3).
        /// These are what a CALIBRATE writes, and a bad one shifts every subsequent absolute reading —
        /// so reading them back is how you tell a corrupt range factor from a real level change.
        /// 38.4 (the SET REF reference value) is firmware-gated above date code 234.1985; 38.1-38.3
        /// are not. Returns NaN if unreadable.
        /// </summary>
        public double ReadTrflCalFactor(int range)
        {
            try
            {
                Send($"38.{range}SP");
                return ReadMeasurement();
            }
            catch { return double.NaN; }
        }

        /// <summary>
        /// Clears ALL stored Tuned RF Level calibration factors (SF 39.9, "Clear all calibration
        /// factors"). This is the recovery when a CALIBRATE has stored a bad range factor: the
        /// instrument preset (IP) does NOT clear them, so a corrupt factor survives a reset and keeps
        /// offsetting every reading. Not firmware-gated (unlike 39.4).
        ///
        /// Side effect worth knowing: with no resident factors the receiver raises RECAL/UNCAL as it
        /// descends, which is exactly the condition #17 found missing — so a cleared instrument
        /// calibrates naturally instead of riding stale factors.
        /// </summary>
        public void ClearTrflCalFactors() => Send("39.9SP");

        public double ReadFirmwareDateCode()
        {
            try
            {
                Send("42.0SP");
                return ReadMeasurement();
            }
            catch { return double.NaN; }
        }

        /// <summary>
        /// Residual AM depth of the tuned signal, % (8902A M1 = AM). Amplitude instability on the
        /// source shows up here and lands directly on a level measurement.
        /// </summary>
        public double ReadAmDepthPercent()
        {
            Send("M1");
            return ReadMeasurement();
        }

        /// <summary>
        /// Residual FM deviation of the tuned signal, Hz (8902A M2 = FM). This is the number that
        /// decides whether the IF SYNCHRONOUS detector is usable: O&amp;C Table 1-1 footnote 12 — "If the
        /// residual FM(peak) is >50 Hz measured over a 30 second period in a 3 kHz BW, Tuned RF Level
        /// measurements should be made using the IF average detector (30 kHz BW)". Measured through the
        /// 11793A this includes the external LO's contribution, which is correct for our purposes: it is
        /// the stability of the CONVERTED signal that the synchronous detector has to lock to.
        /// </summary>
        public double ReadFmDeviationHz()
        {
            Send("M2");
            // Measure in the bandwidth the specification names, or the number means nothing: the
            // 50 Hz threshold is defined "measured over a 30 second period in a 3 kHz BW". Without
            // these filters the reading integrates far more noise and overstates the residual FM.
            Send("H1");    // 50 Hz high-pass filter on
            Send("L1");    // 3 kHz low-pass filter on
            return ReadMeasurement();
        }

        public double ReadSignalFrequencyMHz()
        {
            Send("M5");                       // RF Frequency measurement
            double hz = ReadMeasurement();           // fundamental units = Hz
            return hz / 1e6;
        }

        /// <summary>
        /// Triggers a settled measurement and retrieves the result using the completion handshake:
        /// write the trigger, then poll the status byte until the measurement produces something to
        /// read — Data Ready (0x01), an instrument error (0x04), or RECAL/UNCAL (0x20) — and only then
        /// read. This replaces the old single blocking <c>Query("T3")</c>, which at deep levels could
        /// time out WITHOUT delivering the result even though the receiver had set Data Ready, and
        /// which never surfaced the RECAL that drives the range-boundary CALIBRATE. On RECAL with no
        /// result it throws UNCAL; otherwise <see cref="ParseReading"/> turns the response into a value
        /// or the appropriate error. If nothing completes within <see cref="DataReadyBudgetMs"/> the
        /// read is attempted anyway and any GPIB timeout propagates so the caller can release the bus
        /// (#11). Verified on hardware via the former <c>--handshake-probe</c>; ~6 s per settled read.
        ///
        /// Since #36 the budget is enforced across a blocking poll (see <see cref="DataReadyBudgetMs"/>),
        /// and every phase is timed into <see cref="ReadTimes"/>: under <c>--debug</c> the summary line
        /// says how many polls ran, how many failed and with what, and how much of the wait was spent
        /// INSIDE polls — which is what separates "the 8902A kept answering 0x00" (fast polls, time in
        /// the sleeps) from "the bus stopped answering" (time inside one or two polls).
        /// </summary>
        private double ReadMeasurement()
        {
            RawRead r = ReadRawMeasurement(DataReadyBudgetMs);

            // RECAL set but no result produced → the range needs calibrating at this level.
            if (r.RecalNoResult) throw Hp8902AException.Uncal();

            try { return ParseReading(r.Raw); }
            catch (FormatException)
            {
                if ((_link.SerialPoll(PollTimeoutMs) & RecalStatusBit) != 0) throw Hp8902AException.Uncal();
                throw;
            }
        }

        /// <summary>One settled read, undecoded: what <see cref="ReadMeasurement"/> sees before it
        /// interprets anything. The CALIBRATE routine (#41) needs the raw string and the timings.</summary>
        private struct RawRead
        {
            public string Raw;            // null when RecalNoResult
            public int StatusByte;        // last successful poll; -1 if none succeeded
            public bool Ready;
            public bool RecalNoResult;    // RECAL/UNCAL asserted and no result was produced
            public long TriggerMs;        // how long the T3 write took to be accepted
        }

        private RawRead ReadRawMeasurement(int budgetMs)
        {
            var t = ReadTimes;
            var phase = System.Diagnostics.Stopwatch.StartNew();
            _link.Write("T3");                       // trigger; do NOT block-read yet
            t.Reads++;
            long triggerMs = phase.ElapsedMilliseconds;
            t.TriggerMs += triggerMs;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            int sb = -1;
            bool ready = false, budgetSpent = false;
            int polls = 0, failed = 0;
            long insidePolls = 0, longest = 0;
            string firstFailure = null;
            while (true)
            {
                long remaining = budgetMs - sw.ElapsedMilliseconds;
                if (remaining <= 0) { budgetSpent = true; break; }

                // Give this poll only what is left of the budget, so it cannot carry the wait past it.
                polls++;
                string failure = null;
                var pw = System.Diagnostics.Stopwatch.StartNew();
                try { sb = _link.SerialPoll((int)Math.Max(remaining, PollTimeoutMs)); }
                catch (Exception ex) { sb = -1; failed++; failure = ex.GetType().Name; firstFailure ??= failure; }
                pw.Stop();
                insidePolls += pw.ElapsedMilliseconds;
                longest = Math.Max(longest, pw.ElapsedMilliseconds);
                if (pw.ElapsedMilliseconds >= SlowPollMs)
                    DebugLog?.Invoke($"8902A poll #{polls} took {pw.ElapsedMilliseconds / 1000.0:0.0} s " +
                                     (failure != null ? $"and FAILED ({failure})" : $"(SB=0x{sb:X2})"));

                if (sb >= 0 && (sb & RecalStatusBit) != 0) break;                     // RECAL/UNCAL
                if (sb >= 0 && (sb & (DataReadyBit | InstrErrorBit)) != 0) { ready = true; break; }
                Thread.Sleep(DataReadyPollMs);
            }
            sw.Stop();
            t.WaitMs += sw.ElapsedMilliseconds;
            t.InsidePollsMs += insidePolls;
            t.LongestPollMs = Math.Max(t.LongestPollMs, longest);
            t.Polls += polls;
            t.FailedPolls += failed;

            // A failed poll is reported as a failed poll, not as 0x00 (#36). SerialPoll returns a byte
            // and is never negative, so sb = -1 can only come from the catch above — but the old
            // rendering printed it as "SB=0x00", identical to a genuine zero status. Those imply
            // opposite root causes (wedged bus vs lost status mask), and the 5 GHz hang turns on which.
            DebugLog?.Invoke($"8902A DataReady {(ready ? "SET" : "NOT set")} after " +
                             $"{sw.ElapsedMilliseconds / 1000.0:0.0} s " +
                             (sb < 0 ? "(serial poll failed)" : $"(SB=0x{sb:X2})") +
                             $" — {polls} polls, {insidePolls / 1000.0:0.0} s inside them, longest " +
                             $"{longest / 1000.0:0.00} s" +
                             (failed > 0 ? $", {failed} FAILED (first: {firstFailure})" : "") +
                             (budgetSpent ? $" — {budgetMs / 1000.0:0.#} s read budget spent" : ""));

            // RECAL set but no result produced → the range needs calibrating at this level.
            if (sb >= 0 && (sb & RecalStatusBit) != 0 && !ready)
                return new RawRead { StatusByte = sb, RecalNoResult = true, TriggerMs = triggerMs };

            phase.Restart();
            string raw;
            try { raw = _link.Read(); }              // retrieve the now-ready result
            catch (Exception ex)
            {
                t.RetrieveMs += phase.ElapsedMilliseconds;
                DebugLog?.Invoke($"8902A retrieve FAILED after {phase.ElapsedMilliseconds / 1000.0:0.0} s " +
                                 $"({ex.GetType().Name})" +
                                 (ready ? "" : " — Data Ready was never seen, so there may have been nothing to read"));
                throw;
            }
            t.RetrieveMs += phase.ElapsedMilliseconds;
            DebugLog?.Invoke($"8902A read after DataReady: '{raw}' (retrieve {phase.ElapsedMilliseconds / 1000.0:0.00} s)");

            phase.Restart();
            Settle();
            t.SettleMs += phase.ElapsedMilliseconds;
            return new RawRead { Raw = raw, StatusByte = sb, Ready = ready, TriggerMs = triggerMs };
        }

        private void Settle()
        {
            if (SettleMilliseconds > 0) Thread.Sleep(SettleMilliseconds);
        }

        private static string Fmt(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

        /// <summary>
        /// Parses an 8902A reading. Values ≥ 9e10 are error sentinels (+900000NNNNE+01);
        /// these throw <see cref="Hp8902AException"/>. Otherwise returns the value in the
        /// measurement's fundamental units.
        /// </summary>
        internal static double ParseReading(string raw)
        {
            // One decoder for every raw 8902A response (#41); this keeps the exception contract the
            // engine has always relied on (UNCAL, EMPTY, FormatException, Hp8902AException(code)).
            var r = Hp8902AReading.Decode(raw);
            switch (r.Kind)
            {
                case Hp8902AReadingKind.Value: return r.Value;
                case Hp8902AReadingKind.Uncal: throw Hp8902AException.Uncal();
                case Hp8902AReadingKind.Empty: throw Hp8902AException.EmptyRead();
                case Hp8902AReadingKind.Error: throw new Hp8902AException(r.Error.Code, r.Error.Describe());
                default: throw new FormatException($"Unrecognized 8902A reading: '{raw}'.");
            }
        }
    }
}
