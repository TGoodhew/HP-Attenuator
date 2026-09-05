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

On **`main`** (`635755a`, pushed to origin). Goal: measure the 11713A + 8494/8496 step attenuator's
attenuation accurately across its full range.

**Milestone (2026-07-09): every substantive issue is built + merged to `main`; nothing left to build
away from the hardware.** The remaining work is **bench validation in Renton** — walk
[HardwareValidation.md](HardwareValidation.md) rows **V1–V10** (V1 first). Each built-but-unverified
issue also carries the GitHub **`needs-verification`** label (#2, #3, #6, #8, #13, #15, #17). Issue **#18**
(P0/Required) is the bench task to run V1 first. Issue **#14 is CLOSED** (not planned / superseded — see
its finding below). `gh` CLI is installed + authenticated (TGoodhew) — use it for issues/PRs.

**Bench:** ATTEN X = HP 8494 (0–11 dB, 1 dB steps), ATTEN Y = HP 8496 (0–110 dB, 10 dB steps),
11713A @ GPIB 27. Source 8340B @ 20, LO 8673B @ 19, receiver 8902A @ 14, via 11793A converter
(>1300 MHz) + 11792A sensor. Default test freq 3 GHz (attenuators rated DC–4 GHz).

## The big finding (#14, now CLOSED-superseded): direct measurement is floor-limited to ~95 dB

The full 110 dB **cannot be measured directly** through this chain. The **11793A converter path
floor is −100 dBm** (Microwave Product Note, verbatim: "any power level may be measured between
+0 dBm and −100 dBm"). With the reference pinned near 0 dBm (8902A relative-measurement ceiling),
that caps the usable range at ~95–98 dB. 110 dB (≈ −112 dBm) is below the floor.

Confirmed on the bench (Average detector, 3 GHz, 0–110/10 dB): accurate/holds lock to ~90 dB, then
readings saturate at ~−97.6 dB rel (≈ −98.7 dBm absolute), matching the −100 dBm floor.

**CORRECTED 2026-09-04 — the IF Synchronous detector is NOT a dead end. It is the better choice here.**
- The earlier "loses lock (Error 96), never re-try" entry was **wrong**, and was recorded without ever
  measuring the one quantity that decides it: residual FM. Measured properly (`--source-check`, in the
  specified 50 Hz–3 kHz bandwidth via the `H1`/`L1` audio filters) the source is **18 Hz** — well inside
  the 50 Hz limit above which the average detector is required (O&C Table 1-1 fn.12). Measured without
  those filters it reads 447 Hz and looks disqualifying; that was the mistake.
- On the bench, sync (`4.0SP`) **tracks linearly to −100.5 dBm** — every 1 dB attenuation step produced
  ~1 dB of change (0.88–1.12, mean 1.03) from 90 through 99 dB — then fails at 100 dB with a **clean
  cliff** (Error 01), not compression. The average detector by contrast **compresses** from ~94 dB and
  plateaus at −96 dBm, silently under-reading.
- So sync reaches the 11793A's published −100 dBm limit essentially exactly, ~4 dB deeper than average,
  **and fails honestly** (flagged Error 01) instead of returning a plausible wrong number.
- **Sync needs its own calibration.** The manual: "the first calibration factor will be different
  depending on the detector used when CALIBRATE is selected the first time … you will need to make a
  first calibration for both 4.0 SPCL and 4.4 SPCL". Switching detectors silently inherits the other
  one's factors. Use `--trfl-recal --detector sync` (one CALIBRATE at 0 dB, full signal).
- **Residual error with sync is a constant +1.3 to +1.6 dB offset while tracking stays linear** — that
  is a calibration error, categorically not a noise limit. It is #17: the range factors are never
  written because RECAL never fires (confirmed on the panel three times by the author).

**Neither the 11793A manual nor the Microwave Product Note recommends the synchronous detector.** The
11793A manual never mentions the detector; the Product Note prescribes the **average** detector inside
Track Mode ("32.9 SPCL … the same as entering 4.4 SPCL, 8.1 SPCL, Log units, Track Mode, and 27.3
SPCL"). It does so because it is written for a **drifting** source — Track Mode exists to chase a
wandering carrier and the average detector's wide bandwidth tolerates the drift. Our 8340B/8673B are
synthesized and quiet (18 Hz residual FM, 0.004 dB level stability), so that premise does not hold and
following the documented procedure costs ~4 dB of depth for nothing.

**Dead ends ruled out (don't re-try):**
- **Track Mode (`32.9SP`):** it's for a *drifting, free-running* source; our 8340B/8673B are
  synthesized (stable), so its continuous auto-ranging defeats the #16 leveler and breaks the fixed
  SET REF (produced garbage: 68 dB at a 10 dB step). Left as an off-by-default `--track-mode` flag.

**Correct direct method (per O&C Table 4-1 / Ch.5 + Product Note):** Average detector (`4.4SP`),
single SET REF at 0 dB, CALIBRATE the 3 RF ranges. **#17 addressed the no-op cal descent** — the pre-SET-REF
3-range CALIBRATE was firing nothing (no UNCAL; ~90 dB accuracy rode resident factors). Now observable, with
opt-in `--force-range-cal` to force a real per-range CALIBRATE (bench-verify: V2/V4).

**#14 disposition:** the issue's original ask (segmented *re-referencing* sweep) is physically non-viable —
`SET REF` re-zeroes only the *relative* frame, not the absolute converter floor — so it was closed as
superseded. Its sync/track experiments were HW-tested dead-ends; the full-110 goal is #15.

**→ The real path to a validated full 110 dB is #15: per-section characterize + SUM** (measure each
8496/8494 section where the signal is strong, sum — `--section-test` proved the sections add linearly
to 0.01 dB). This sidesteps the sub-floor measurement entirely.

## Open issues — ALL BUILT + MERGED, awaiting bench (labeled `needs-verification`)

- **#17 (BUILT, ledger V2/V3/V4):** the pre-SET-REF 3-range CALIBRATE descent was a silent no-op (no UNCAL
  → zero CALIBRATEs; ~90 dB rode resident factors). Now every descent step is traced with a loud
  `NO-OP — 0 CALIBRATEs fired` summary, and `--force-range-cal` issues one unconditional CALIBRATE per RF
  range. Sim PASS. Bench: does the forced cal fire 3× (panel-review) and improve 80–95 dB accuracy.
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
- **#8 (BUILT, on `main`, awaiting bench — ledger V10):** the post-CALIBRATE settle moved into
  `Hp8902A.Calibrate()`, which now polls after completion, logs the status under `--debug`, and throws on
  a raised cal error (Error 35) instead of leaving it latched-but-invisible. Sim PASS. Bench: HardwareValidation.md V10.

Remaining work is **bench validation (HardwareValidation.md V1–V10)** in Renton — no more building needed.

## What's DONE and validated

- **#16 adaptive reference leveling** (MERGED to main): levels the source per frequency so the 0 dB
  reference lands ~−2 dBm. Hardware PASS.
- **Test 1 / Test 2 / completion-handshake read** (issues #1/#5/#7/#9/#10/#11/#12, MERGED): the
  relative Tuned RF Level attenuation sweep, hardware-validated 0→~90 dB at 3 GHz.

## Tooling (all on `main`)

- **`--section-sum`** (#15) — characterize each attenuator section alone (≤40 dB, above the floor), then
  SUM to synthesize the full 110/121 dB that can't be measured directly. The real full-range path.
- **`--force-range-cal`** (#17) — force one CALIBRATE per RF range in the pre-SET-REF descent (default off);
  pair with `--debug` for the descent trace / no-op summary and `--panel-review` to watch each CALIBRATE.
- **`--profile`** (#2) — attribute sweep wall-clock by category (read / range-cal / settle / atten-set /
  other) to find the real hotspot before optimizing. Run WITHOUT `--debug` (its per-command poll distorts).
- **`--floor-dbm dBm` / `--no-floor-detect`** (#13) — deep points saturated at the converter floor are
  flagged FLOOR and excluded from the verdict (default on, threshold −98 dBm).
- **`--manual-tune` (default) / `--auto-tune`** (#3) — TRFL signal acquisition. Auto-tune HP-IB code is
  bench-UNVERIFIED (`Hp8902A.AutoTuneSpecialFunction = 7.1SP`).
- **`--panel-review`** — pauses to have the operator read the 8902A front panel; wraps a step tightly via
  `MeasurementEngine.PanelWatch`/`PanelReview` → `FrontPanelReview`. Attended hardware only. Now prompts
  around each forced CALIBRATE when `--force-range-cal` is on (#17).
- **`--detector avg|sync`, `--sync-detector`, `--track-mode`, `--lo-power dBm`** — TRFL detector / mode
  / LO drive selectors (Average is correct; sync + track are HW-tested dead ends kept as flags).
- **`--debug`** — traces every 8902A command + status byte; also drives `MeasurementEngine.Trace` (yellow
  range-cal descent lines, #17) and now the post-CALIBRATE status line (#8). Slows a run (per-command poll).
- **`DebugResults/`** — all run artifacts / CSVs (git-ignored). Harness writes there by default.
- **`SpecFiles/8494G_8496G_series_attenuation_ranges.csv`** — per-dB pass/fail limits 0–121 dB (a future
  refinement so DUT pad tolerance isn't charged to the measurement).

## How to run (hardware)

```powershell
# Sensor cal is reused within 8 h (marker in %TEMP%); add --recal after a power-cycle.
# Direct method — Average detector, sweep (honest to ~90 dB; deep points flagged FLOOR by #13):
dotnet run --project src/HP-Attenuator.TestHarness -- --hardware --x-atten 8494 --atten-sweep `
  --freq 3000 --astop 110 --astep 10 --debug --out DebugResults/run.csv

# Full-range path (#15) — per-section characterize + sum, reaches a validated 110/121 dB:
dotnet run --project src/HP-Attenuator.TestHarness -- --hardware --x-atten 8494 --section-sum `
  --freq 3000 --debug --out DebugResults/sectionsum.csv
```

Working process (current): one branch per issue, a commit per change, CHANGE_LOG.md updated on each.
**`main` is the dev trunk — issue work merges freely (ff)**; branches are kept alive (not deleted) until
bench-validated. Standing git default is commit + push. Never claim a hardware result Tony hasn't confirmed
(only mark HardwareValidation.md rows ✅ from real-hardware runs).

## Suggested next step

**Nothing left to build away from the hardware.** Next step is bench validation in Renton: open
[HardwareValidation.md](HardwareValidation.md) and walk **V1 first** (#4 `--debug`, the low-risk warm-up →
issue #18), then V2 (#17 `--force-range-cal`), … through V10. Each row has the exact isolation command,
pass criterion, and where the fix goes. Mark rows ✅ as they pass and drop the `needs-verification` label
(and/or close the issue) once confirmed. If a row fails, fix on its surviving `issue-NN` branch, commit +
push, re-run.
