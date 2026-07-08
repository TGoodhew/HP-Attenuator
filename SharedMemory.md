# Shared Memory — HP-Attenuator working state

A cross-machine handoff snapshot so work can continue from anywhere. Updated 2026-07-09.
(Personal per-machine notes live outside the repo; this file is the shared, committed record.)

## Working model (author is traveling — away from the GPIB rig until back in Renton)

- **`main` is the development trunk.** Issue work keeps merging here so combining solutions is never
  blocked — which means **`main` may carry HW-unvalidated code**. What's unvalidated is tracked in
  **[HardwareValidation.md](HardwareValidation.md)** — the step-by-step bench checklist to walk at home.
- **Every HW-affecting change keeps its issue branch alive** (`issue-NN-slug`, on `origin`) so it can
  be checked out and validated **in isolation**. **No branch is deleted/cleaned up** until it's
  bench-validated and the author says so.
- **Standing git default: commit + push** every change, branches included. No manual merge-to-`main`
  gate anymore — combine freely; validation is deferred to the ledger, not blocked before merge.

## Where we are right now

On **`main`** (b198f77). Goal of the current push: measure the 11713A + 8494/8496 step
attenuator's attenuation accurately across its full range.

**Stack MERGED to main (2026-07-09):** the #4 + #14 stack fast-forwarded onto `main` (build + sim
sweep green). `main` now carries the #4 debug-poll fix, the #14 direct-method work (detector
selection, 3-range cal pass, adaptive leveling), and the `--panel-review` / `--track-mode` tooling.
```
main (b198f77)  ← #4 (950093c) + #14 (…a32982f) + handoff (b198f77), all merged
```
Next issue work starts on a fresh branch off `main`. Not yet pushed to origin (main is local-only).

**Bench:** ATTEN X = HP 8494 (0–11 dB, 1 dB steps), ATTEN Y = HP 8496 (0–110 dB, 10 dB steps),
11713A @ GPIB 27. Source 8340B @ 20, LO 8673B @ 19, receiver 8902A @ 14, via 11793A converter
(>1300 MHz) + 11792A sensor. Default test freq 3 GHz (attenuators rated DC–4 GHz).

## The big finding (#14): direct measurement is floor-limited to ~95 dB

The full 110 dB **cannot be measured directly** through this chain. The **11793A converter path
floor is −100 dBm** (Microwave Product Note, verbatim: "any power level may be measured between
+0 dBm and −100 dBm"). With the reference pinned near 0 dBm (8902A relative-measurement ceiling),
that caps the usable range at ~95–98 dB. 110 dB (≈ −112 dBm) is below the floor.

Confirmed on the bench (Average detector, 3 GHz, 0–110/10 dB): accurate/holds lock to ~90 dB, then
readings saturate at ~−97.6 dB rel (≈ −98.7 dBm absolute), matching the −100 dBm floor.

**Dead ends ruled out (don't re-try):**
- **IF Synchronous detector (`4.0SP`, −127 dBm spec):** loses lock (Error 96) below ~−100 dBm on the
  drifting converted signal; the −127 dBm floor never applies through the converter. Reacquisition
  needs the signal ≥ −80 dBm.
- **Track Mode (`32.9SP`):** it's for a *drifting, free-running* source; our 8340B/8673B are
  synthesized (stable), so its continuous auto-ranging defeats the #16 leveler and breaks the fixed
  SET REF (produced garbage: 68 dB at a 10 dB step). Left as an off-by-default `--track-mode` flag.

**Correct direct method (per O&C Table 4-1 / Ch.5 + Product Note):** Average detector (`4.4SP`),
single SET REF at 0 dB, CALIBRATE the 3 RF ranges. **BUT see #17** — our pre-SET-REF 3-range
CALIBRATE currently fires nothing (no UNCAL detected); the ~90 dB accuracy is riding on resident
range factors, not a fresh calibration.

**→ The real path to a validated full 110 dB is #15: per-section characterize + SUM** (measure each
8496/8494 section where the signal is strong, sum — `--section-test` proved the sections add linearly
to 0.01 dB). This sidesteps the sub-floor measurement entirely.

## Open issues

- **#17 (NEW, blocks #14 accuracy claim):** the pre-SET-REF 3-range CALIBRATE descent
  (`CalibrateRfRanges`) is a no-op — no read throws UNCAL, so zero CALIBRATEs fire (proven: `--panel-review`
  never prompted). Fix: clear TRFL range cal factors to force a fresh calibration, and/or detect range
  crossings by reading-jump; add observability so a no-op descent is visible.
- **#15 (BUILT, on `main`, awaiting bench — ledger V5):** per-section characterize + sum → the path to
  the full 110 dB. `--section-sum` measures each section alone (≤40 dB, above the floor) and sums to
  synthesize the deep totals. Sim PASS (full scale 120.83 dB @ nominal 121). Bench check: HardwareValidation.md V5.
- **#13 (BUILT, on `main`, awaiting bench — ledger V6):** floor/plateau detection — deep points that
  saturate at the converter floor (100/110 dB read the floor, the −2.4/−12 dB errors) are now flagged
  **FLOOR** and excluded from the verdict instead of failing it; `--floor-dbm`/`--no-floor-detect`.
  Sim PASS (no false flags). Bench check: HardwareValidation.md V6.
- **#4 (fixed on branch `issue-4-debug-poll-falseflag`):** `--debug` no longer false-flags a failed
  serial poll as INSTRUMENT ERROR. Sim+stub validated; hardware `--debug` trace confirm pending (cosmetic).
- **#3 (BUILT, on `main`, awaiting bench — ledger V7):** selectable manual/auto Tuned RF Level tuning
  (`--manual-tune` default / `--auto-tune`). Auto-tune HP-IB code is bench-UNVERIFIED (OCR-ambiguous
  manual) — verify on the 8902A. Sim PASS (plumbing only).
- **#6 (BUILT, on `main`, awaiting bench — ledger V8):** empty/transient read at an auto-range boundary
  now recovers in place (own settle+re-trigger budget, `EmptyReadRetries`) instead of failing the point;
  reclassified as a distinct transient. Sim PASS. Bench check: HardwareValidation.md V8.
- **#2 (BUILT, on `main`, awaiting bench — ledger V9):** sweep timing profiler (`--profile`) attributes
  wall-clock by category (read / range-cal / settle / atten-set / other) so optimization targets the
  measured hotspot. Pure instrumentation, no measurement change. Sim renders it. Bench: HardwareValidation.md V9.
- Others (not yet built): #8 latched SRQ.

## What's DONE and validated

- **#16 adaptive reference leveling** (MERGED to main): levels the source per frequency so the 0 dB
  reference lands ~−2 dBm. Hardware PASS.
- **Test 1 / Test 2 / completion-handshake read** (issues #1/#5/#7/#9/#10/#11/#12, MERGED): the
  relative Tuned RF Level attenuation sweep, hardware-validated 0→~90 dB at 3 GHz.

## Tooling (on the branch)

- **`--panel-review`** — pauses to have the operator read the 8902A front panel; wraps a specific step
  tightly (pause immediately before + after), via `MeasurementEngine.PanelWatch`/`PanelReview` hooks
  wired to `FrontPanelReview`. Attended hardware only. NOTE: currently only wired around the (no-op)
  cal descent → doesn't prompt (that's #17).
- **`--detector avg|sync`, `--sync-detector`, `--track-mode`, `--lo-power dBm`** — TRFL detector / mode
  / LO drive selectors (Average is correct; sync + track are dead ends kept as flags).
- **`DebugResults/`** — all run artifacts / result CSVs go here (git-ignored as a whole). Harness
  writes there by default (`--out DebugResults/<name>.csv`).
- **`SpecFiles/8494G_8496G_series_attenuation_ranges.csv`** — per-dB pass/fail limits 0–121 dB; the
  spec the sweep should validate against (a future refinement so DUT pad tolerance isn't charged to
  the measurement).

## How to run (hardware)

```powershell
# Sensor cal is reused within 8 h (marker in %TEMP%); add --recal after a power-cycle.
# The correct direct method — Average detector, full range:
dotnet run --project src/HP-Attenuator.TestHarness -- --hardware --x-atten 8494 --atten-sweep `
  --freq 3000 --astop 110 --astep 10 --debug --out DebugResults/run.csv
```

Working process: one branch per issue, a commit per change, CHANGE_LOG.md updated on each, **no merge
until Tony approves**. Never claim a hardware result Tony hasn't confirmed.

## Suggested next step

Stack is merged (done 2026-07-09). Remaining decision: fix **#17** first (so the direct method's
ranges are genuinely calibrated vs relying on resident factors — cheap, sim-testable, makes the
≤90 dB claim honest) or go straight to **#15** (per-section characterize + sum — the actual path to
the full 110 dB, since #17 does NOT lift the ~95 dB converter floor). Either way, start on a new
branch off `main`.
