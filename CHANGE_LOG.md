# Change Log

All notable changes to the HP-Attenuator project are recorded here.
Newest entries first. Format loosely follows [Keep a Changelog](https://keepachangelog.com).

Working process: one branch **per issue**, a commit for **every change**, and a matching
entry in this file. Branches are **not merged to `main` until explicitly approved**, so an
entry may describe work that is still on a feature branch (its branch is noted).

## [Unreleased]

### On `main`
- `CHANGE_LOG.md` added to establish per-change history going forward.

### On branch `test2-atten-sweep` (not yet merged)
- Test 1 — single-point absolute RF power readback (8902A RF Power via the 11793A + LO).
- Test 2 — relative Tuned RF Level attenuation sweep following the 8902A O&C manual's
  "Attenuator Measurements" procedure; no mid-sweep recalibration.
- Converter cal-factor loading (Normal + Frequency-Offset tables) and 50 MHz REF CF anchor.
- Error 96 (lost lock) recovery: `RetuneToSignal()` sends `BC` (VCO retune) before retrying,
  instead of only clearing the error. *Hardware behaviour under investigation.*

## Baseline (main @ 8d76add)
- Test 1 RF power readback; 8902A Error 15 cal-factor-table fixes; 11713A attenuator control
  with X/Y auto-identification.
