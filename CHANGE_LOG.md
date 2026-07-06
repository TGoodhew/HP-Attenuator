# Change Log

All notable changes to the HP-Attenuator project are recorded here.
Newest entries first. Format loosely follows [Keep a Changelog](https://keepachangelog.com).

Working process: one branch **per issue** (stacked on the branch we're already on when a new
issue starts), a commit for **every change**, and a matching entry here. Branches are **not
merged to `main` until explicitly approved**. We merge back up the stack as each branch finishes;
the branch sub-headings below record which branch each change came from.

## Unreleased — not yet merged

### branch `issue-16-adaptive-leveling` (stacked on `main`) — #16
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
  `--per-atten`. **Hardware verification pending** (multi-frequency run + confirm the leveled
  reference reads/sweeps in range at each frequency).

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
