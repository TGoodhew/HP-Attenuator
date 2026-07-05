# Change Log

All notable changes to the HP-Attenuator project are recorded here.
Newest entries first. Format loosely follows [Keep a Changelog](https://keepachangelog.com).

Working process: one branch **per issue** (stacked on the branch we're already on when a new
issue starts), a commit for **every change**, and a matching entry here. Branches are **not
merged to `main` until explicitly approved**, so an entry may describe work still on a feature
branch (its branch is noted). We merge back up the stack as each branch finishes.

## [Unreleased]

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
