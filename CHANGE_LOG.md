# Change Log

All notable changes to the HP-Attenuator project are recorded here.
Newest entries first. Format loosely follows [Keep a Changelog](https://keepachangelog.com).

Working process: one branch **per issue** (stacked on the branch we're already on when a new
issue starts), a commit for **every change**, and a matching entry here. Branches are **not
merged to `main` until explicitly approved**. We merge back up the stack as each branch finishes;
the branch sub-headings below record which branch each change came from.

## Unreleased — not yet merged

### branch `issue-14-synchronous-deep-sweep` (stacked on `issue-4-debug-poll-falseflag`) — #14
- **#14 — selectable IF detector; Synchronous detector to reach the full 110 dB.** Consulting the
  8902A O&C manual (Chapter 5, *Attenuator Measurement*) settled the approach: the manual's
  wide-range method is a **single `SET REF` + CALIBRATE on each of the 3 RECALs (one per RF range)** —
  there is **no cascaded / re-referenced technique**, and re-referencing can't help because `SET REF`
  only re-zeroes the *relative* frame, not the absolute floor. The ~80–95 dB wall is the **IF Average
  detector's −100 dBm floor** (its noisy IF ranges 6/7 start ~−85 dBm), not the attenuator. Reaching
  110 dB (≈ −112 dBm at a −2 dBm reference) needs the **IF Synchronous detector** (`4.0SP`, 200 Hz BW,
  floor ≈ −127 dBm). So the sweep detector is now selectable: `IMeasuringReceiver.BeginAttenuationMeasurement`
  takes a `TrflDetector` (Average `4.4SP` / Synchronous `4.0SP`); `SweepOptions.Detector` (default
  Average — preserves the validated Test 2 path); harness flags `--detector avg|sync` and
  `--sync-detector`. The sweep summary line names the active detector. Everything else is unchanged
  (single SET REF, boundary CALIBRATE on RECAL cap 2, #16 leveling). Sim sweep 0–110 dB PASS on both
  detectors (the sim floor sits below both, so sim only exercises the command plumbing); the
  detector→command mapping (`4.0SP`/`4.4SP`) and full S4 setup sequence verified against the real
  `Hp8902A`. **HARDWARE RESULT (2026-07-06, 3 GHz, 0–110 dB / 10 dB steps, `--sync-detector`):** the
  Synchronous detector did **not** reach 110 dB — two distinct failures. (1) **Accuracy drift 80–90 dB**
  (+1.44 / +2.68 dB, *positive and growing* = an uncalibrated deep RF range, not the noise floor): the
  RECAL bit (0x20) **never set during the descent** (every read `SB=0x41`), so the mid-sweep boundary
  CALIBRATE never fired and Range 2 (below −40 dBm) / Range 3 (below −80 dBm) ran on stale factors (the
  resident ones are from the *Average* detector's different 0/−15/−50 ranges). Clean to ~70 dB (the
  +0.4 baseline is the 8496 40-dB pad). (2) **Lost lock (Error 96) at 100 / 110 dB** (≈ −102 / −112 dBm):
  the 200 Hz Synchronous loop can't hold the converter-degraded signal that deep (BC-retune couldn't
  recover) — a converter-path signal-quality limit, not the −127 dBm sensitivity floor. Verdict FAIL
  (2 error points, worst |err| 2.68 dB). Next: implement the manual's explicit sequential 3-range
  CALIBRATE (0 / −40 / −80 dBm up front, per O&C Table 4-1) to fix the 80–95 dB drift; the 100–110 dB
  region is likely not directly measurable through this converter path → #15 (per-section sum).
- **Manual review (11793A + Microwave Product Note) → direct-method redirect.** The **8902A Microwave
  Product Note** states the converter-path floor twice: *"any power level may be measured between
  +0 dBm and −100 dBm without further calibration."* So the −100 dBm floor is a property of the
  **11793A path, not the detector** — the Synchronous detector's −127 dBm spec never applied here, and
  110 dB (−112 dBm) is physically below the floor by any detector. The Note's prescribed low-level
  converter method is **Track Mode (SF 32.9 = Average detector + track + Log + offset)**, which holds
  lock on the drifting converted signal; it also explains the #14 lost-lock (reacquisition needs the
  signal ≥ −80 dBm, so BC-retune at −102/−112 dBm can't recover). The 11793A wants **+8..+13 dBm LO
  drive** (we run at the +8 dBm floor). **Increment (Track Mode + configurable LO drive):**
  `BeginAttenuationMeasurement` gains a `trackMode` flag (sends `32.9SP` in place of the detector +
  `LG`); `SweepOptions.TrackMode`; harness `--track-mode` and `--lo-power dBm`; the sweep summary names
  the mode. Sim plumbing PASS; the Track-Mode command sequence (`S4 27.3SP<LO>MZ <f>MZ 32.9SP 1.0SP
  32.1SP 22.37SP`) verified against the real `Hp8902A`. **Hardware next:** does Track Mode hold lock
  deeper (toward the ~−100 dBm floor) than the plain Average sweep? Then layer the explicit 3-range
  CALIBRATE. The full 110 dB stays a #15 (per-section sum) job.
- **Track Mode result → dropped; implement the manual's 3-range calibration instead.** The
  `--track-mode --lo-power 12` run produced non-physical data (68 dB "attenuation" at a 10 dB step,
  readings saturating at ~100 dB, leveler driven to −12 dBm, Error 1 at 100 dB). Root cause: Track
  Mode is the Product Note's tool for a *drifting, free-running* source; our 8340B/8673B are
  synthesized (stable), so Track Mode's continuous auto-ranging/auto-leveling just defeats the #16
  leveler (its reads stop tracking the source) and shifts the fixed SET REF the relative sweep is
  measured against. Track Mode left in as an (off-by-default) flag but not the path. **Correct method
  (O&C Table 4-1 / Chapter 5 + Product Note), now implemented:** `RunRangeCalibration` calibrates the
  **three RF ranges as a dedicated pass BEFORE SET REF** — new `CalibrateRfRanges` steps the signal
  down from 0 dB in `CalStepDb` (≤10 dB) increments and CALIBRATEs on each RECAL/UNCAL (surfaced by
  the completion-handshake read, capped at 3 ranges / `RangeCalReachDb`, stops on lost lock), then
  returns to 0 dB and takes SET REF. This replaces "CALIBRATE only Range 1 at 0 dB and hope RECAL
  re-fires mid-sweep" — which it didn't (every #14 sweep read `SB=0x41`, no RECAL), leaving Range 2/3
  on stale factors → the deep positive drift. Sim build/regression PASS (range-cal is hardware-only,
  off in sim). **HARDWARE RESULT (2026-07-06, 3 GHz, 0–110 dB / 10 dB, Average detector + 3-range
  cal): the method works.** Deep drift flattened — 90 dB error +2.68 (sync) → **+1.46**, and that
  residual is mostly the DUT (both 8496 40-dB pads, +0.35/+0.40 each) not the measurement (measurement
  error ~+0.6). The **Average detector held lock all the way** — 100/110 dB returned floor readings
  (~97.6), **no Error 96 lost lock** (opposite of the sync run). Floor confirmed on the bench: readings
  saturate at ~−97.6 dB rel to the −1.06 dBm reference ≈ **−98.7 dBm absolute**, matching the Product
  Note's −100 dBm. Leveling normal (ref −1.06 dBm, source unchanged). So the direct method is honest to
  **~90 dB**, saturating at the converter floor ~95–97 dB — the physical ceiling through this chain.
  100/110 read the floor (errors −2.6/−12.3 = saturated, not measurement failures → motivates #13 floor
  detection). **Full 110 dB still needs #15 (per-section sum).**
- **Front-panel review capability (`--panel-review`).** New `FrontPanelReview` helper (harness) prints
  a question, pauses, and captures the operator's typed answer — for questions only a human reading
  the 8902A front panel can settle (which annunciator lit, an error shown). Supports the pause-BEFORE
  / pause-AFTER pattern (`Watch` → run commands → `Ask`, or `Observe(...)`) for observations that can
  only be made after commands issue. The engine exposes `MeasurementEngine.PanelWatch` /
  `PanelReview` hooks (null by default); the harness wires them to the prompts on `--panel-review`,
  attended hardware only (guarded by `Console.IsInputRedirected` so sim / unattended / redirected runs
  never block). First use: wraps the 3-range calibration descent — pauses to have the operator watch
  the RECAL/UNCAL annunciators, then asks how many times they lit (expect ~3, one per RF range), the
  direct front-panel confirmation that the manual's range calibration fired. Sim: no-op, sweep PASS.

### branch `issue-4-debug-poll-falseflag` (off `main`) — #4
- **Fix #4 — the `--debug` trace no longer false-flags a failed serial poll as an INSTRUMENT
  ERROR.** `Hp8902A.Send`'s debug annotation ran the status-bit checks on the raw poll result, but a
  failed/thrown poll leaves `sb = -1`, and `-1 & 0x04 == 0x04` in two's-complement — so *every*
  failed poll printed `<-- INSTRUMENT ERROR (0x04)` (and would have false-flagged RECAL/UNCAL too).
  The checks are now guarded to `sb >= 0`; a `-1` prints `<-- serial poll failed (instrument busy?)`
  instead. The poll transiently fails on the first `27.3SP<LO>MZ` (frequency-offset entry) right
  after `S4` because the 8902A is briefly busy reconfiguring (benign — every later command polls
  cleanly and the measurement proceeds), so a new `PollStatusForTrace` retries the poll once after a
  200 ms settle, which usually catches the settled `SB=0x00`. Debug-path only — no change to the
  measurement hot path. Validated against the real `Hp8902A.Send` with a stub link: a failed poll
  reads "serial poll failed" (not INSTRUMENT ERROR), a fail-then-succeed poll recovers to `SB=0x00`,
  a genuine `0x04` still flags INSTRUMENT ERROR, and `0x20` still annotates RECAL/UNCAL. Sim sweep
  PASS (no regression). Full-hardware `--debug` trace confirmation pending.

## 2026-07-06 — merged to main: #16 adaptive reference leveling

### branch `issue-16-adaptive-leveling` (off `main`) — #16
- **Adaptive reference leveling — keep the 0 dB reference just under the 8902A's 0 dBm ceiling, per
  frequency.** Before taking SET REF at each frequency, the engine now measures the *absolute* 0 dB
  reference level (new `IMeasuringReceiver.ReadTunedLevelDbm` — the S4/LG Tuned RF Level read is
  absolute dBm until SET REF re-zeroes it) and nudges the 8340B source power so the reference lands
  in a target window (`--ref-target`, default −2 dBm). The level tracks source power 1:1, so each
  iteration moves the source by the remaining delta, clamped to the source's usable range; best-effort
  (aborts on Error 96 / no signal, leaving the source at the last commanded power). Runs inside
  `RunRangeCalibration` **before** the reference-range CALIBRATE + SET REF, so both anchor at the
  leveled level, and applies to both the sweep and `--per-atten`. Motivation: converter loss varies
  with frequency, so one fixed `--power` can't serve a multi-frequency / `--full` run — too hot
  over-ranges and hangs the reference (the ~12 dB hang at +10 dBm/3 GHz), too cold gives a shallow
  floor. Prerequisite for #14 (segmented sweep). New flags `--ref-target dBm` and `--no-leveling`
  (hold `--power` fixed, the pre-#16 behaviour). `FreqPointResult` now carries the achieved
  `ReferencePowerDbm` + `LeveledSourcePowerDbm`; the per-frequency table/line show `ref X dBm @ src
  Y dBm` and the CSV gains `leveled_ref_dbm` / `leveled_src_dbm` columns. **Sim PASS** across the
  multi-frequency sweep (reference pinned to −2.0 dBm at every frequency, source stepping −1.36→−1.02
  dBm across 1–13 GHz as path loss rises; max|err| 0.05 dB), the `--no-leveling` control, and
  `--per-atten`. **HARDWARE PASS (2026-07-06)** — 3/5/7 GHz, 0–60 dB: leveler held the reference in
  the [−3,−1] dBm window at every frequency and adapted the source per frequency to do it. At 7 GHz
  the reference came in cold (−4.5 dBm, higher converter loss), so it stepped the source +2.5 dBm and
  re-read −2.1 dBm; 3/5 GHz were already in-window (−1.3 / −2.7 dBm) so it left the source at 0 dBm —
  exactly the per-frequency divergence a fixed `--power` can't give. Worst |err| 0.45 dB (the 8496
  40-dB-pad term), all within ±1.5 dB, verdict PASS.

## 2026-07-06 — merged to main: Test 1 + Test 2 attenuation measurement

Merged the `test2-atten-sweep → issue-9 → issue-11 → issue-10 → issue-12` stack (issues #1, #5, #7,
#9, #10, #11, #12) — the hardware-validated relative attenuation sweep and the completion-handshake
read path.

### `issue-12-promote-polled-read` — #12
- **Fix #12 — the Data-Ready completion handshake is now the default read path.** Folded the
  trigger → poll status (Data Ready 0x01 / instr-error 0x04 / RECAL 0x20) → read logic into the core
  `Hp8902A.ReadMeasurement`, so *every* read (Tuned RF Level, the 0 dB reference, RF Power / Test 1,
  RF frequency / `--detect`, per-atten / Test 3) uses it — no flag. Removed
  `ReadRelativeDbAwaitingDataReady`, the `UseDataReadyRead` option and the `--handshake-probe` flag.
  A stalled read past the budget still propagates a timeout so the caller releases the bus (#11).
  Sim PASS across sweep / per-atten / detect / rf-power. **Hardware test matrix still required**
  (Test 1/2/3, `--detect`, direct path < 1300 MHz, multi-freq) before closing #12.
- **Hardware-matrix fix: unmask the Data Ready status bit in every measurement setup.** The first
  matrix run showed `--detect` and `--rf-power` burning the full budget per read (`SB=0x00`, correct
  value but slow): at IP the 8902A masks all status bits except HP-IB error (O&C 3-25), so the
  completion poll was blind. `BeginAttenuationMeasurement` / `BeginRfPowerMeasurement` now call
  `UnmaskMeasurementStatus()` (`22.37SP` = Data Ready + Instr Error + RECAL); `EnableRecalStatus` /
  `BeginRangeCalibration` use the same. Also cut the poll budget 120 s → 30 s (well above the ~12 s
  worst-case real read) so a genuine hang recovers promptly, and the debug line now prints seconds.
- **Default source power +10 dBm → 0 dBm.** The +10 dBm floor-test over-drove the 3 GHz reference to
  ~+9 dBm — above the 8902A's 0 dBm relative-measurement ceiling — which deterministically hung the
  sweep at the first range boundary (~12 dB). 0 dBm lands the reference ~−1 dBm (in-range) and the
  sweep runs clean (0–39 dB within ±0.11, the 8496 40 dB pad's +0.40 at 40 dB, ~6 s/read). The ideal
  level is frequency-dependent, so a multi-freq sweep will need per-frequency leveling. **#12
  hardware matrix now complete** — detect, rf-power, and the full sweep all pass through the promoted
  completion-handshake read.

### branch `issue-10-completion-handshake` (stacked on `issue-11-bus-timeout-crash-safety`)
- **Defaults: test frequency 5 GHz → 3 GHz, source power 0 → +10 dBm.** The 8494G/8496G step
  attenuators are rated DC–4 GHz, so 5 GHz was out of spec; 3 GHz is in-band. Raising the 8340B to
  +10 dBm lifts the 0 dB reference so the receiver's ~−100 dBm floor sits deeper in relative dB —
  tests whether the deep-end error (Part 2) is genuinely the measurement floor. (To propagate up
  the stack on merge.)
- **Probe for #10 — trigger → wait on Data Ready → read.** The `ProbeSignalAfterHang` result proved
  the 43 dB hang is #10, not #9: signal PRESENT (M5=5000.000 MHz, not lost lock) and SB=0x41
  (Data Ready set, no RECAL/UNCAL) — the settled level read just won't deliver via a blocking T3
  read even though Data Ready sets. New experimental read (`--handshake-probe`,
  `ReadRelativeDbAwaitingDataReady`): triggers, polls the status byte for Data Ready (0x01) up to a
  budget, then retrieves — tracing the Data-Ready timing under `--debug`. Gated behind the flag so
  default behaviour is unchanged; sim sweep PASS.
- **Reframe from the probe: 43 dB is a RECAL boundary (#9), not slow settling.** With `--handshake-probe`
  the status byte at 43 dB reads `SB=0x61` = Data Ready + **RECAL (0x20)** — the receiver *is* asking
  for a CALIBRATE, and Data Ready sets in ~6 s (not minutes). The reason #9 never fired: `0x20` only
  appears in the *post-trigger* status, so the pre-read `RecalRequested()` poll always missed it.
  Fix: on an UNCAL read (0x20 seen), `ReadStepWithBoundaryCal` now CALIBRATEs the boundary **directly**
  (no re-poll), capped at 2/frequency. Needs a hardware run (with `--handshake-probe`) to confirm it
  calibrates and reads deeper — and whether calibrating there stays accurate or corrupts.

### branch `issue-11-bus-timeout-crash-safety` (stacked on `issue-9-recal-boundary-calibrate`)
- **Fix #11 — survive a GPIB timeout and release the held bus.** A read timeout left the 8902A
  holding the bus (its handshake is inhibited until the measurement cycle completes, O&C 3-22);
  the next 11713A write then timed out and, being outside the try/catch, crashed the whole
  harness. `MeasureFrequency` now runs the attenuator-set and read inside the try; on a GPIB
  timeout it calls the new `IMeasuringReceiver.ReleaseBus()` (device clear / SDC) to free the bus
  and ends the frequency cleanly with a warning (a read timeout is the floor, and the device clear
  drops the relative reference). Removed the now-dead `FloorStopCount` accumulation. The real cure
  (wait for measurement completion instead of a blind fixed timeout) is #10. Sim sweep unchanged
  (PASS, max|err| 0.05 dB).
- **Diagnostic probe (#9 vs #10).** On a read timeout, after releasing the bus, re-establish the
  context and do an M5 RF-frequency read as a signal-presence check at the failing attenuation
  (`ProbeSignalAfterHang`). Logs "signal PRESENT — level wouldn't settle (re-range/#10)" vs "signal
  LOST (Error 96) — lost lock", so the 43 dB hang classifies itself in the run output.

### branch `issue-9-recal-boundary-calibrate` (stacked on `test2-atten-sweep`)
- **Fix #9 — CALIBRATE range boundaries on RECAL during the sweep, per the manual.** The 8902A
  Operation & Calibration manual's *Attenuator Measurements* (3-115) requires calibrating each
  RF input range-to-range boundary the first time RECAL appears. We had removed all mid-sweep
  calibration (bf6ba51) and were unknowingly relying on range factors left resident in the
  instrument from earlier runs — a power-cycle wipes them, so RECAL lit at ~7-9 dB and the
  receiver lost lock (Error 96 cascade). `MeasureFrequency` now calibrates on RECAL via
  `ReadStepWithBoundaryCal` / `MaybeCalibrateBoundary`: only when the receiver flags RECAL
  (status 0x20), capped at **two** range-to-range calibrations per frequency (the manual stores
  exactly two factors), holding the level steady. The cap and RECAL-only trigger keep it from the
  deep/weak calibrate that stored the bad factor bf6ba51 was chasing. Sim sweep unchanged
  (PASS, max|err| 0.05 dB); hardware verification pending.

### branch `test2-atten-sweep` (not yet merged)
- Test 1 — single-point absolute RF power readback (8902A RF Power via the 11793A + LO).
- Test 2 — relative Tuned RF Level attenuation sweep following the O&C manual's "Attenuator
  Measurements" procedure.
- Converter cal-factor loading (Normal + Frequency-Offset tables) and 50 MHz REF CF anchor.
- Error 96 (lost lock) recovery: `RetuneToSignal()` sends `BC` (VCO retune) before retrying.

## Baseline (main @ 8d76add)
- Test 1 RF power readback; 8902A Error 15 cal-factor-table fixes; 11713A attenuator control
  with X/Y auto-identification.
