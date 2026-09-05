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

## STOPPING POINT — 2026-09-05, bench powered down

**Branch: `issue-24-sf-matrix`** (stacked: main → 21 → 22 → 23 → 24). All work committed and pushed.
Bench is idle; nothing was left mid-run. The 8902A's Tuned RF Level calibration is HEALTHY (verified:
RF Power and TRFL agree within 0.02 dB).

### The headline result of the session
**The deep-end error is the ATTENUATOR, not the receiver.** Parking the reference at −12.77 dBm instead
of 0 dBm decoupled commanded dB from absolute level; the +0.4 dB step **stayed at commanded 40 dB**, so
it tracks the attenuator setting — the 8496's first 40 dB section (digit 7). The per-step table had
already put digit 7 at +0.49 dB and digit 8 at +0.62 dB. This is real DUT behaviour to be measured and
reported, not calibrated away, and it **demotes #17**.

### Also established tonight
- **The synchronous detector is the right choice, not a dead end** (the old SharedMemory entry was
  wrong and is corrected below). Residual FM is **18 Hz at 3 GHz / 7 Hz at 500 MHz** measured in the
  specified 50 Hz–3 kHz bandwidth (`H1`/`L1` filters) — well inside the 50 Hz limit. Sync tracks
  linearly (~1 dB per 1 dB step) to the cliff; the average detector compresses and under-reads.
- **Depth reached:** 3 GHz converted **99 dB** (−100.5 dBm, exactly the 11793A spec); 500 MHz direct
  **114 dB** (−114.9 dBm). The converter, not the receiver, was the binding constraint.
- **Why not −127 dBm:** the 11792A contains a **10 dB pad** ahead of its RF switch (to let the sensor
  take 1 W). The instrument compensates the *reading*, not the *sensitivity*, so the DUT-referred floor
  is 10 dB worse: −127 + 10 = −117 dBm, and we measured −114.9. Spec says the **11722A** reaches
  **−120 dBm** (Option 050, 2.5–1300 MHz). Bypassing the module entirely (straight to the 8902A RF
  INPUT) should also work, since a SET REF relative measurement bypasses the sensor anyway.
- **The source is excellent and is not a contributor:** level sd 0.001–0.004 dB, drift 0.013 dB,
  0 Hz frequency error, residual AM 0.17–0.24 %.
- **#17 mechanism understood:** the descent fires nothing because RESIDENT range factors suppress
  RECAL. It fired 2 CALIBRATEs at 500 MHz where no resident factors existed. The operator confirmed on
  the front panel across three runs that RECAL/UNCAL never lights.
- **`--force-range-cal` is DANGEROUS as implemented** — its deep 20/55 dB calibrations corrupted the
  TRFL first calibration factor (+42.9 dB offset). Recovery is `--trfl-recal` (one CALIBRATE at 0 dB,
  full signal). Instrument preset, sensor re-cal and SF 39.9 all fail to clear it. **Do not use
  `--force-range-cal` until reworked.**
- **This 8902A lacks the SF 31/38/39 family** (firmware date code 94.199): 38.1–38.3 read unreadable,
  39.9 has no effect, 31.1 is accepted but useless. The SF-matrix experiment is not runnable here.

### NEXT STEP when the bench is back on
1. **Finish the discriminator sweep** — `--ref-target -10` was stopped at 40 dB. Re-run to see whether
   the 80 dB+ growth is also sectional (digits 7+8) or receiver-related:
   ```powershell
   dotnet run --project src/HP-Attenuator.TestHarness -- --hardware --x-atten 8494 --atten-sweep `
     --freq 500 --astop 110 --astep 10 --detector sync --ref-target -10 --debug --skip-sensor-cal `
     --out DebugResults/v22-ref-minus10.csv
   ```
2. **Then `--section-sum`** (ledger V5, built, never run) for per-section values on all eight sections,
   to cross-check digit 7 = +0.49 dB and digit 8 = +0.62 dB.
3. Deferred housekeeping: mark **V13** ✅ (0 dBm reference + adaptive plan ran clean all session), and
   decide **V1** (#4 — criterion met but the failure mode never occurred, so it is "no regression"
   rather than "fix demonstrated").
4. Re-scope **#15**: per-section summation may only be needed above 1300 MHz now that direct reaches
   114 dB.

New tooling this session: `--source-check`, `--trfl-recal`, `--trfl-cal`/`--clear-trfl-cal`,
`--noise-floor`, `--sf-matrix`, `--adaptive-steps`, `--fine-from/step/to`, `--repeats`,
`--step-tolerance`, `--hold-before-steps`/`--hold-at-db`. Sweep CSV now streams per point.

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
