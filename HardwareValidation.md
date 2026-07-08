# Hardware Validation Ledger

The bench-validation queue for changes that are **on `main` (or a live branch) but not yet
confirmed against the real GPIB rig** in Renton. Author develops away from the hardware; this file
is the step-by-step checklist to walk when back at the bench, plus where to fix anything that fails.

Updated 2026-07-09.

## The model (why this file exists)

- **`main` is the development trunk.** Issue work merges here so combining solutions is never
  blocked. **`main` may therefore contain HW-unvalidated code** — this ledger is the record of what
  that is.
- **Every HW-affecting change keeps its own issue branch alive** (`issue-NN-slug`, pushed to
  `origin`). The branch is **not deleted or cleaned up** until the change is bench-validated *and*
  the author says so. Surviving branches are how you validate one change **in isolation**: check the
  branch out, build, run just its recipe — no other in-flight work in the way.
- **Commit + push is the standing default** (branches included). Nothing waits locally; everything is
  backed up off the travel laptop.
- Never mark a row ✅ from a sim run. ✅ means the real 8902A / 11713A / 8340B chain, confirmed by the
  author on the bench.

### Adding an entry (do this whenever a change alters HW-observable behavior)

Add a row to the table **and** a detail block below with: the surviving branch, the exact isolation
command, the pass criterion, and where the fix goes if it fails. Keep the issue branch alive.

### Status vocabulary

| Mark | Meaning |
|------|---------|
| 🔨 | Needs code — not built yet; here so it's not forgotten |
| ⬜ | Built + on a branch/main, **awaiting bench** |
| 🔒 | Blocked — can't be validated until a prerequisite row passes |
| ✅ | Bench-validated PASS (author-confirmed on real hardware) |
| ❌ | Bench-validated FAIL — see notes; fix on the branch |
| ⏭️ | Closed without bench work (rejected dead-end / superseded) |

## Queue

| # | Change | Branch | Status | Blocked by |
|---|--------|--------|--------|-----------|
| V1 | #4 — `--debug` no longer false-flags a failed serial poll | `issue-4-debug-poll-falseflag` | ⬜ | — |
| V2 | #17 — real pre-`SET REF` 3-range CALIBRATE (`--force-range-cal`) + descent observability | `issue-17-range-cal-observability` | ⬜ | — |
| V3 | `--panel-review` actually pauses on each CALIBRATE | `issue-14-synchronous-deep-sweep` | 🔒 | V2 |
| V4 | #14 — 3-range cal genuinely improves 80–95 dB accuracy | `issue-14-synchronous-deep-sweep` | 🔒 | V2 |
| V5 | #15 — per-section characterize + sum reaches a validated full 110/121 dB | `issue-15-per-section-sum` | ⬜ | — |
| V6 | #13 — deep saturated points flagged FLOOR (not failed); verdict/depth honest | `issue-13-floor-detection` | ⬜ | — |
| V7 | #3 — verify the automatic-tuning HP-IB code + acquire-then-hold sequence | `issue-3-tune-mode` | ⬜ | — |
| V8 | #6 — empty/transient read recovers in place (auto-range boundary) instead of failing | `issue-6-empty-read-recovery` | ⬜ | — |
| V9 | #2 — `--profile` gives the real wall-clock breakdown to drive sweep optimization | `issue-2-sweep-profiling` | ⬜ | — |
| V10 | #8 — a CALIBRATE error (Error 35) is now polled + logged + surfaced, not silently latched | `issue-8-calibrate-error-surface` | ⬜ | — |
| — | #14 — `--detector sync` (IF Synchronous) | `issue-14-synchronous-deep-sweep` | ⏭️ | rejected: loses lock through the converter (CHANGE_LOG) |
| — | #14 — `--track-mode` (SF 32.9) | `issue-14-synchronous-deep-sweep` | ⏭️ | rejected: for a drifting source; defeats #16 leveler |

---

## V1 — #4 debug-poll false-flag (ready now, independent)

- **Branch:** `issue-4-debug-poll-falseflag` (also merged on `main`)
- **What changed:** with `--debug`, a failed/empty serial poll is no longer reported as an
  `INSTRUMENT ERROR`. Sim + stub validated; only the real 8902A serial-poll timing is unconfirmed.
- **Isolate & run** (this is a `--debug` overlay on an already-validated Average sweep, so it's the
  lowest-risk item — run it first to warm up the bench):
  ```powershell
  git checkout issue-4-debug-poll-falseflag
  dotnet run --project src/HP-Attenuator.TestHarness -- --hardware --x-atten 8494 --atten-sweep `
    --freq 3000 --astop 110 --astep 10 --debug --out DebugResults/v1-debug.csv
  ```
- **Expect (PASS):** the sweep completes with the same 0→~90 dB numbers as the non-`--debug` run, and
  the debug trace shows **no** spurious `INSTRUMENT ERROR` line from a serial poll.
- **If it fails:** fix on `issue-4-debug-poll-falseflag`, commit + push, re-run. Cosmetic — does not
  gate other rows.

## V2 — #17 real 3-range CALIBRATE + descent observability  ⬜ built, awaiting bench

- **Branch:** `issue-17-range-cal-observability` (built; sim PASS, worst |err| 0.04 dB).
- **Problem (from #17):** `CalibrateRfRanges` fired nothing — no read throws UNCAL during the
  pre-`SET REF` descent (resident range factors suppress RECAL), so zero CALIBRATEs happen; the ~90 dB
  accuracy rode **resident** factors, not a fresh calibration. `--panel-review` proved it never prompts.
- **What was built:** (1) full descent **observability** — `--debug` now traces every step and prints a
  loud `range-cal: NO-OP — 0 CALIBRATEs fired … RESIDENT factors (#17)` summary (or the CALIBRATE count);
  (2) **`--force-range-cal`** — issues one unconditional CALIBRATE per RF range at approximate boundary
  depths (0/20/55 dB — bench-tunable), since with resident factors nothing natural gates a CALIBRATE.
- **Step A — confirm the no-op (baseline, default behaviour):** run WITHOUT `--force-range-cal` and read
  the yellow trace. Expect the `NO-OP — 0 CALIBRATEs fired` summary — this reproduces/confirms #17 on the
  actual bench.
  ```powershell
  git checkout issue-17-range-cal-observability
  dotnet run --project src/HP-Attenuator.TestHarness -- --hardware --x-atten 8494 --atten-sweep `
    --freq 3000 --astop 110 --astep 10 --debug --out DebugResults/v2-noop.csv
  ```
- **Step B — force the calibration and watch it (the fix):**
  ```powershell
  dotnet run --project src/HP-Attenuator.TestHarness -- --hardware --x-atten 8494 --atten-sweep `
    --freq 3000 --astop 110 --astep 10 --force-range-cal --panel-review --debug --out DebugResults/v2-forced.csv
  ```
- **Expect (PASS):** with `--force-range-cal`, `--panel-review` prompts **3 times** (one per range) and
  the trace shows `3 CALIBRATE(s) fired`. Note on the panel whether RECAL was actually lit at each forced
  CALIBRATE — that tells us if the 0/20/55 dB boundary depths line up with the real range breaks (tune
  `ForceCalDepthsDb` if not).
- **If it fails:** fix on `issue-17-range-cal-observability`, commit + push, re-run. Feeds **V4** (does the
  forced cal actually improve 80–95 dB accuracy?).

## V3 — `--panel-review` pauses on each CALIBRATE  🔒 blocked by V2

- **Branch:** `issue-14-synchronous-deep-sweep`
- **What changed:** `--panel-review` wraps each CALIBRATE step tightly
  (`MeasurementEngine.PanelWatch`/`PanelReview` → `FrontPanelReview`). It currently **doesn't prompt**
  because the cal descent is a no-op (#17). Validating V2 validates this at the same time.
- **Validate:** covered by the V2 run above — confirm the prompts appear and correspond to real
  CALIBRATE steps. Mark ✅ only once V2 is ✅.

## V4 — #14 3-range cal improves 80–95 dB accuracy  🔒 blocked by V2

- **Branch:** `issue-14-synchronous-deep-sweep`
- **Claim to test:** once V2 makes the CALIBRATE real, does 80–95 dB accuracy actually improve vs the
  resident-factor baseline? (The 100/110 dB region stays below the −100 dBm converter floor — that's
  #15's job, not this row.)
- **Isolate & run:** the V2 command produces the CSV; compare 80–90 dB errors against a
  resident-factor run on `main`:
  ```powershell
  # baseline (resident factors, no real cal):
  git checkout main
  dotnet run --project src/HP-Attenuator.TestHarness -- --hardware --x-atten 8494 --atten-sweep `
    --freq 3000 --astop 110 --astep 10 --out DebugResults/v4-baseline.csv
  ```
- **Expect (PASS):** with the real 3-range cal, |error| at 80–90 dB is ≤ the baseline (ideally back
  toward the <0.5 dB seen ≤70 dB), not the +1.4/+2.7 dB drift the uncalibrated deep ranges showed.
- **If it fails:** the drift is a converter-path limit, not calibration → escalate to **#15**
  (per-section characterize + sum). Note the result here and in CHANGE_LOG.

## V5 — #15 per-section characterize + sum  ⬜ built, awaiting bench

- **Branch:** `issue-15-per-section-sum` (built; sim PASS — full scale 120.83 dB @ nominal 121, worst
  section |err| 0.04 dB, all 8 sections read). Also on `main`.
- **What it does:** measures each attenuator section alone (each ≤40 dB, so every read stays above the
  ~95 dB converter floor) and **sums** them to synthesize the full range — including the 100–121 dB
  totals that can't be measured directly. This is the real path to a validated full 110/121 dB.
- **Isolate & run:**
  ```powershell
  git checkout issue-15-per-section-sum   # (or run from main — it's merged)
  dotnet run --project src/HP-Attenuator.TestHarness -- --hardware --x-atten 8494 --section-sum `
    --freq 3000 --debug --out DebugResults/v5-sectionsum.csv
  ```
- **Expect (PASS):** all 8 sections read cleanly (none hit the floor — that's the whole point, each is
  ≤40 dB); the per-section table shows each near its nominal; the **characterized full scale (Σ)** lands
  near 121 dB (± the DUT pad tolerances, not measurement floor); the synthesized-totals table produces
  100/110/120/121 dB rows flagged "sum only".
- **Cross-check (the key validation):** for the totals that ARE directly measurable (≤ ~90 dB), the
  synthesized sum should agree with a direct `--atten-sweep` reading at the same target — e.g. compare
  the 70/80/90 dB sum rows here against `DebugResults/` from a direct sweep. Agreement there is what
  licenses trusting the sum where direct can't reach.
- **If a section reads the floor / errors:** it means that section alone is below the floor (shouldn't
  happen at ≤40 dB) or a path issue — investigate before trusting the deep sums. Fix on
  `issue-15-per-section-sum`, commit + push, re-run.

## V6 — #13 floor/plateau detection  ⬜ built, awaiting bench

- **Branch:** `issue-13-floor-detection` (built; sim PASS, no false flags). Also on `main`.
- **What it does:** a deep direct sweep past ~95 dB reads the converter floor and under-reads the
  target (the −2.4 / −12 dB errors at 100 / 110 dB). #13 flags those points **FLOOR** and excludes them
  from the accuracy verdict, so a good sweep isn't failed by the known measurement floor. It also reports
  the honest "deepest measured" depth.
- **Isolate & run** (the classic deep direct sweep that produced the −2.4 / −12 dB points):
  ```powershell
  git checkout issue-13-floor-detection   # (or run from main — it's merged)
  dotnet run --project src/HP-Attenuator.TestHarness -- --hardware --x-atten 8494 --atten-sweep `
    --freq 3000 --astop 110 --astep 10 --debug --out DebugResults/v6-floor.csv
  ```
- **Expect (PASS):** points to ~90 dB read accurately (green); 100 / 110 dB show **FLOOR** (yellow), are
  excluded from "Worst |error|", and the summary shows "Floor-limited (#13): 2 point(s)" with
  "Deepest measured ~90 dB". Verdict PASS instead of the old FAIL.
- **Tune if needed:** if the floor cut is in the wrong place (a good point flagged, or a saturated one
  missed), adjust `--floor-dbm` (default −98) to match where the reading actually saturates on the day,
  and confirm the `floor_limited` column in the CSV. `--no-floor-detect` restores the old behaviour for
  comparison.
- **Cross-check with #15 (V5):** the points #13 marks FLOOR here (100 / 110 dB) are exactly the ones
  `--section-sum` synthesizes by summation — so V6 tells you the direct method's honest ceiling and V5
  provides the validated number above it.

---

## V7 — #3 automatic-tuning HP-IB codes  ⬜ built, awaiting bench

- **Branch:** `issue-3-tune-mode` (built; sim PASS — flag/threading/header only, sim ignores tuning).
  Also on `main`.
- **What it does:** adds `--auto-tune` (vs the default `--manual-tune`). Manual is unchanged current
  behaviour (`<freq>MZ`). Auto issues an acquire → wait → hold → re-enter-TRFL sequence.
- **⚠ The reason this row exists:** the auto-tune special function (`AutoTuneSpecialFunction`, currently
  `7.1SP` in `Hp8902A`) and the acquire-then-hold sequence are **NOT verified** — the Operation manual's
  SF-7 tuning codes are OCR-ambiguous in the scan. This row is to confirm/correct them on the real 8902A.
- **Isolate & run** (watch the panel + the debug trace to see what the SF actually does):
  ```powershell
  git checkout issue-3-tune-mode   # (or run from main — it's merged)
  dotnet run --project src/HP-Attenuator.TestHarness -- --hardware --x-atten 8494 --atten-sweep `
    --freq 3000 --astop 30 --astep 10 --auto-tune --panel-review --debug --out DebugResults/v7-autotune.csv
  ```
- **Verify:** (1) does `7.1SP` actually select Auto Tuning on the panel? If not, read the correct SF from
  the 8902A Operation manual and fix `AutoTuneSpecialFunction`. (2) After auto-acquire, does `<freq>MZ`
  hold and the TRFL reading come up valid? (3) Tune `AutoTuneAcquireMs` (default 3000) to the real
  acquire time. (4) Below the auto-tune signal threshold it must fall back to manual — note the behaviour.
- **If wrong:** correct the SF/sequence on `issue-3-tune-mode`, commit + push, re-run. Manual tuning
  (the default, everything else) is unaffected either way.
- **Low priority:** we command the source, so the frequency is always known — manual is the right default
  for production. Auto is a convenience for uncertain/drifting sources; validate when convenient.

---

## V8 — #6 empty/transient read recovery  ⬜ built, awaiting bench

- **Branch:** `issue-6-empty-read-recovery` (built; sim PASS — the glitch is hardware-only, sim can't
  reproduce it). Also on `main`.
- **What it does:** an empty/short read (transient GPIB race across an RF-range auto-range) now gets its
  own settle+re-trigger retry budget and recovers in place, instead of burning the 3 quick retries on the
  same glitch and failing the point. Reclassified as a distinct transient, not "unrecognized" data.
- **Isolate & run** (the original repro — a converter sweep across the ~15–16 dB auto-range boundary):
  ```powershell
  git checkout issue-6-empty-read-recovery   # (or run from main — it's merged)
  dotnet run --project src/HP-Attenuator.TestHarness -- --hardware --x-atten 8494 --atten-sweep `
    --freq 5000 --astop 30 --astep 1 --debug --out DebugResults/v8-empty.csv
  ```
- **Expect (PASS):** where the old run showed `Unrecognized 8902A reading: '' [SB=0x41]` and failed the
  point, the trace now shows `empty/short read — transient (retry n/5)` and the point reads a valid value
  after the settle — the sweep no longer has holes at the auto-range boundary. Watch for the 15/16 dB
  region specifically.
- **If it still fails there:** the boundary may need more than 5 empty retries or a longer settle — bump
  `EmptyReadRetries` / `TransientReadSettleMs`, commit + push, re-run.

---

## V9 — #2 sweep timing profile  ⬜ built, awaiting bench

- **Branch:** `issue-2-sweep-profiling` (built; sim renders the breakdown, but sim has no real waits so
  the numbers are ~0 — the real attribution only appears on hardware). Also on `main`.
- **What it does:** `--profile` attributes the sweep's wall-clock to categories (settled read, range-cal
  pre-pass, per-step settle, attenuator set, setup/other) so we optimize the measured hotspot, not a
  guess. This is #2's required "profile first" step.
- **Run** (a representative single-frequency deep sweep, WITHOUT --debug so per-command polling doesn't
  distort the timing):
  ```powershell
  git checkout issue-2-sweep-profiling   # (or run from main — it's merged)
  dotnet run --project src/HP-Attenuator.TestHarness -- --hardware --x-atten 8494 --atten-sweep `
    --freq 5000 --astop 30 --astep 1 --profile --out DebugResults/v9-profile.csv
  ```
- **Read the breakdown → then optimize:**
  - If **settled read** dominates (expected): it's hardware-bound; the win is fewer reads / smarter
    retries, not shaving sleeps.
  - If **range-cal pre-pass** or **per-step settle** is large: those are fixed `Thread.Sleep`s — replace
    with poll-on-status waits (the post-calibrate wait is done in #8; extend the pattern to the settle).
  - **Batching** only matters if per-command I/O shows up big, which normally needs `--debug`; the
    profile tells you whether it's worth pursuing.
- **Follow-up:** whatever the profile shows is the target for the next optimization pass — record the
  breakdown here so the optimization is driven by data.

---

## V10 — #8 CALIBRATE-error surfacing  ⬜ built, awaiting bench

- **Branch:** `issue-8-calibrate-error-surface` (built; sim PASS — the cal error is hardware-only, sim's
  Calibrate is a no-op). Also on `main`.
- **What it does:** the ~2.5 s post-calibrate wait moved into `Hp8902A.Calibrate()`, which now polls the
  status **after** the CALIBRATE completes, logs it under `--debug`, and throws on the instrument-error
  bit — so an Error 35 (level error during calibration) that was previously latched-but-invisible now
  surfaces and the sweep doesn't silently trust a bad reference.
- **Isolate & run** (the marginal-level converter case where the SRQ latched — 5 GHz):
  ```powershell
  git checkout issue-8-calibrate-error-surface   # (or run from main — it's merged)
  dotnet run --project src/HP-Attenuator.TestHarness -- --hardware --x-atten 8494 --atten-sweep `
    --freq 5000 --astop 30 --astep 10 --debug --out DebugResults/v10-cal.csv
  ```
- **Expect:** the `--debug` trace now shows `8902A CALIBRATE complete, status = 0x..` after each
  CALIBRATE (no longer `0x00` everywhere). If the SRQ annunciator lights, the log now says whether it was
  a real `INSTRUMENT ERROR (0x04)` (→ the point/cal shows the failure and the sweep recovers via
  ClearError) or a clean status (→ cosmetic RQS latch). Cross-check against the 8902A front-panel error.
- **If a real Error 35 shows:** that's a genuine marginal-level cal failure (too little signal to
  calibrate that range) — the honest ceiling is shallower there; relates to #1/#7 and the #13 floor.

---

## Not on the bench queue (for reference)

- **#15 (per-section characterize + sum)** — the real path to a validated full 110 dB. New feature,
  will get its own `issue-15` branch + ledger rows when built.
- **#13 (floor/plateau detection)** — flag saturated deep points instead of failing them.
- **#16 adaptive leveling / Test 1 / Test 2** — already HW-validated (see SharedMemory.md); not here.
