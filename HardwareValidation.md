# Hardware Validation Ledger

The bench-validation queue for changes that are **on `main` (or a live branch) but not yet
confirmed against the real GPIB rig** in Renton. Author develops away from the hardware; this file
is the step-by-step checklist to walk when back at the bench, plus where to fix anything that fails.

Updated 2026-09-15.

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

### ⚠ Validating the merge candidate, not the isolation branches (added 2026-09-15)

Each row below carries a `git checkout issue-NN-...` recipe. That was correct while the job was
validating **one change at a time**, in isolation, as it was written.

It is **wrong for a close-out session.** Those branches all predate the leveller straddle fix (V14),
which sits in the setup path of every measurement — so running them validates code that is *not* what
lands on `main`. When the goal is to clear the queue and merge, **run every row from the merge
candidate** (currently `issue-24-sf-matrix`), and keep the per-row commands only for their flags,
frequencies and pass criteria.

Use the isolation checkout only when a row has **failed** from the merge candidate and you need to
find out whether that change or its neighbours caused it.

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
| V1 | #4 — `--debug` no longer false-flags a failed serial poll | `issue-4-debug-poll-falseflag` | ✅ | — |
| V2 | #17 — real pre-`SET REF` 3-range CALIBRATE (`--force-range-cal`) + descent observability | `issue-17-range-cal-observability` | ❌ | **#35** — Error 33 below the top range |
| V3 | `--panel-review` actually pauses on each CALIBRATE | `issue-14-synchronous-deep-sweep` | 🔒 | Tony must run it himself — Claude stdin is redirected, so the prompts no-op |
| V4 | #14 — 3-range cal genuinely improves 80–95 dB accuracy | `issue-14-synchronous-deep-sweep` | 🔒 | V2 — untestable until a 3-range cal is achievable (#35) |
| V5 | #15 — per-section characterize + sum reaches a validated full 110/121 dB | `issue-15-per-section-sum` | ✅ | — |
| V6 | #13 — deep saturated points flagged FLOOR (not failed); verdict/depth honest | `issue-13-floor-detection` | ⬜ | — needs a step-increment test; 100 dB was missed (2026-09-15) |
| V7 | #3 — verify the automatic-tuning HP-IB code + acquire-then-hold sequence | `issue-3-tune-mode` | ⬜ | — |
| V8 | #6 — empty/transient read recovers in place (auto-range boundary) instead of failing | `issue-6-empty-read-recovery` | ⬜ | — 3 attempts 2026-09-15, glitch never occurred; unproven |
| V9 | #2 — `--profile` gives the real wall-clock breakdown to drive sweep optimization | `issue-2-sweep-profiling` | ✅ | — |
| V10 | #8 — a CALIBRATE error (Error 35) is now polled + logged + surfaced, not silently latched | `issue-8-calibrate-error-surface` | ❌ | **#34** — reported 0x00 through a real Error 33, twice |
| V11 | #21 — spec-derived per-path level limits; points below the path floor are skipped, not failed | `issue-21-device-level-limits` | ✅ | — |
| V12 | #22 — fine (1 dB) steps from 90 dB to the floor characterize the last few dB | `issue-22-fine-step-near-floor` | ✅ | — |
| V13 | #23 — reference leveled to 0 dBm; step plan derived from the attenuator + path floor | `issue-23-zero-dbm-ref-adaptive-steps` | ✅ | — |
| V14 | Leveller straddle fix — a coarse jump that overshoots must not settle on the far-below sample (18 GHz: ref was 13 dB low) | `issue-24-sf-matrix` | ✅ | — |
| V15 | **#30** — leveller silently no-ops when the first level read is UNCAL, taking the adaptive step plan and #21 limits down with it | `issue-30-leveller-uncal-recovery` | ⬜ | — 2026-10-15: no regression, but the first read was not UNCAL, so the recovery path was not exercised |
| V16 | `--hold-before-cal` — gate each CALIBRATE on a file so an observation can be timed to it | `issue-17-cal-hold-gate` | ✅ | — used 5x on 2026-09-15; found #34 and #35 |
| V17 | **#34** — CALIBRATE waits for completion instead of sampling 2500 ms in | `issue-34-calibrate-completion-poll` → `issue-34-srq-completion` | ❌ | **2026-10-15: the 8902A never reports a CALIBRATE's Error 33 over HP-IB** — not in the status byte, not on the read. See V17 below |
| V19 | **#41** — TRFL CALIBRATE confirmed by gate + result read, per-error handling, stops on failure | `issue-41-trfl-cal-errors` | ✅ | — 2026-10-16: SUSPECT caught the 60 dB Error 33; heuristic moved to cycle time after a false alarm |
| V18 | **#36** — read budget enforced across a blocking poll; per-poll timing; bus release verified | `issue-36-bounded-poll` | ⬜ | — 2026-10-16: 31/31 PASS at 5 GHz, the hang did NOT recur, so the bound was not exercised; instrumentation clean |
| — | #14 — `--detector sync` (IF Synchronous) | `issue-24-sf-matrix` | ✅ | **REINSTATED 2026-09-04**: reaches 99 dB / −100.5 dBm vs average's 96 dB, tracks linearly, fails honestly. The earlier rejection was made without measuring residual FM (18 Hz, well in spec). |
| — | #14 — `--track-mode` (SF 32.9) | `issue-14-synchronous-deep-sweep` | ⏭️ | rejected: for a drifting source; defeats #16 leveler |

### Close-out run order (2026-09-15 session)

All from `issue-24-sf-matrix` — see the merge-candidate note above. Sensor-cal once at the start,
then `--skip-sensor-cal` for the rest. Ordered so that if the session is cut short, the runs that
carry real weight are already done.

| # | Run | Outcome | Output |
|---|---|---|---|
| 1 | 18 GHz `--section-sum` | **V14 ✅** — leveller recovered to −0.012 dBm. §3/§4 reproduced, so the old data stands | `v27-sec-18000.csv` |
| 2 | 2 GHz `--section-sum` | §5 **confirmed, not cleared** — −0.21 vs ±0.20, repeating 09-06's −0.23 | `v26-sec-2000.csv` |
| 3 | 3 GHz adaptive, average detector | **Invalid for V13** (command omitted `--ref-target 0`) but exposed **V15** and disproved the V6 shortcut | `v28-adaptive.csv` |
| 4 | 5 GHz `--astep 1 --debug` | PASS 0.15 dB. **V8 not exercised** (no glitch); **V10** evidenced from run 3 | `v29-empty.csv` |
| 5 | 3 GHz adaptive, **sync**, `--ref-target 0` | **V13 ✅** — 29/29 points to 99.67 dB, errors flat | `v31-adaptive-sync.csv` |
| — | 5 GHz `--profile` (V9) | **NOT RUN** — displaced by the V13 re-run | — |

**Deferred, not gating a merge:** V2/V3/V4 (needs an operator at the 8902A panel for every
`--panel-review` prompt — a session of its own), V7 (ledger marks it low priority; manual tuning is
the production default), and the 10 MHz §7/§8 UNCAL (needs the sensor physically moved to the 8902A
CALIBRATION RF POWER OUTPUT and back — author only).

**V6 may be closable without a run:** V12's recorded result already shows #13 flagging the three
saturated points and reporting an honest depth. Author's call whether that counts as bench-validated
or whether it needs its own 3 GHz deep sweep.

---

## V16 — `--hold-before-cal`, the observation gate  ✅ BENCH PASS (2026-09-15)

- **Branch:** `issue-17-cal-hold-gate` (serves #17's observability goal).
- **Why it exists:** a front-panel observation has to be timed to the instant of the event. Asking the
  operator to "watch the panel for the next few minutes" loses the observation — proven on 2026-09-15,
  where a whole forced-cal run was wasted that way. `--panel-review` is the designed mechanism but is a
  **no-op whenever Claude drives the harness**: `FrontPanelReview.CanPrompt` requires
  `!Console.IsInputRedirected`, and a tool-driven run's stdin is always redirected.
- **What it does:** wires the engine's existing `PanelWatch` / `PanelReview` hooks — which already fire
  immediately before and after each `Calibrate()` — to a **file gate** instead of a keypress. The run
  blocks printing `CAL-HOLD:`, and proceeds the moment the named file appears. No measurement-engine
  change; only which delegates the harness binds. Skipped in sim, 30-minute timeout so an unattended
  run cannot wedge, takes precedence over `--panel-review`.
- **Usage:** `--hold-before-cal <file>`; the driver sounds an alarm, creates the file, and the CALIBRATE
  fires within ~250 ms, so the operator is looking at the panel at the moment it happens.
- **Result — PASS.** Used **5 times across two hardware runs**. Every hold engaged, printed, waited, and
  released cleanly; the CALIBRATE followed immediately each time. It is what made **#34** and **#35**
  findable at all — both were invisible in the logs and only observable on the panel.

---

## V1 — #4 debug-poll false-flag  ✅ BENCH PASS (2026-09-15)

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

### Result — PASS, 2026-09-15 (observed across four `--debug` runs, not a dedicated run)

Validated as an overlay, which is what it is: **zero `INSTRUMENT ERROR` lines across all four
`--debug` runs of the session** (18 GHz and 2 GHz `--section-sum`, 3 GHz adaptive, 5 GHz 1 dB sweep),
and no serial-poll failures reported as instrument faults. Genuine status conditions still surfaced
correctly in the same traces — `SB=0x61` was read as UNCAL and acted on, `SB=0x41` as Data Ready — so
the fix suppresses the false positive without blinding the poll.

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

### Result — FAIL, 2026-09-15 → issue #35

**Step A confirmed, and now by eye as well as by log.** The descent fires nothing on its own, and the
operator confirmed directly that **no RECAL and no UNCAL was lit** at either forced depth. That is the
first independent, non-software confirmation of #17 — it had previously rested entirely on the
harness's own status reads, which #34 shows can be wrong about calibration state.

**Step B failed.** Of the three forced CALIBRATEs at `ForceCalDepthsDb = { 0, 20, 55 }`, only 0 dB
succeeds. 20 dB raises **Error 33 — "Power sensor reference error"** (service manual, *Tuned RF Level
Calibration Errors 30-39*), reproducibly across two runs, and 55/60 dB is deeper still.

| CALIBRATE | Level | run `v34` | run `v37` (after a fresh sensor cal) |
|---|---|---|---|
| 0 dB | ~0 dBm | clean | clean |
| 20 dB | ~-20 dBm | **Error 33** | **Error 33** |

**It is not a sensor-cal problem.** `v37` ran after a verified sensor calibration — zeroed to 0.0 nW
residual, then calibrated against the 50 MHz reference reading exactly **1.000 mW (0.00 dBm)**. Error 33
returned unchanged.

Reading: the CALIBRATE needs a level the power sensor can reference against, and does not have one once
the attenuator is in the path. Inference from behaviour — the manual gives only the one-line
description. The cutoff between 0 and 20 dB is unmeasured.

**Consequence: V4 is untestable** until a 3-range calibration is achievable at all. Options are in #35:
find the real cutoff, derive depths from level rather than dB, or accept that only the top range is
calibratable and the deep ranges must run on resident factors.

**Both failures were logged as `status = 0x00`.** See #34 / V10 — without the operator at the panel this
would have been recorded as a clean 3-CALIBRATE success.

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

## V5 — #15 per-section characterize + sum  ✅ BENCH PASS (2026-09-06)

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

### Result — PASS, 2026-09-06 (run from `issue-24-sf-matrix`, at **both** 500 MHz and 3 GHz)

Run at two frequencies rather than the single 3 GHz above, to separate DUT behaviour from path
behaviour: `DebugResults/v24-sections-500.csv` and `v24-sections-3000.csv`. **8/8 sections read
cleanly at both** — none hit the floor, which is the premise of the method.

| Digit | Unit | Nominal | err 500 MHz | err 3 GHz | Limit (Table 1-6) | Worst % of limit |
|---|---|---|---|---|---|---|
| 1 | 8494G | 1 dB | +0.01 | +0.00 | ±0.2 | 5% |
| 2 | 8494G | 2 dB | +0.03 | +0.03 | ±0.3 | 10% |
| 3 | 8494G | 4 dB | +0.03 | +0.03 | ±0.3 | 10% |
| 4 | 8494G | 4 dB | +0.04 | +0.01 | ±0.3 | 13% |
| 5 | 8496G | 10 dB | −0.07 | −0.06 | ±0.2 | 35% |
| 6 | 8496G | 20 dB | −0.04 | −0.06 | ±0.4 | 15% |
| 7 | 8496G | 40 dB | +0.41 | +0.40 | ±0.7 | 59% |
| 8 | 8496G | 40 dB | +0.45 | +0.45 | ±0.7 | **64%** |

**Every section passes at both frequencies; worst case is digit 8 at 64% of its limit.** Synthesized
totals pass too, including ones no direct measurement can reach: 80 dB +0.86 (66% of ±1.3), 110 dB
+0.75 (42% of ±1.8), full scale **121.84 dB vs nominal 121** (+0.84 against a cascaded ±2.3).

**The cross-check that licenses the method** (the "key validation" above) came from the 0–110 dB
discriminator sweep rather than a direct `--atten-sweep`: digits 5, 6 and 7, solved indirectly from
points above −53 dBm, agree with the direct per-section measurement to within **0.03 dB**. Digit 8,
solved from the single 80 dB point at −93.4 dBm, came out 0.19 dB *low* — exactly the direction
predicted if compression had already started there. So the sum is trustworthy, and it also dates the
onset of compression: **usable linear range ends nearer −90 dBm than −100 dBm.**

The sections are **frequency-flat** 500 MHz → 3 GHz — no section moves by more than 0.03 dB — despite
one path being Direct and the other Converted. That is an independent check on the measurement chain
itself, not just on the DUT.

Consequence: the *direct* 110 dB read of −3.62 dB error is **receiver compression, not attenuator
error**, and must not be reported as DUT accuracy. Ledger method validated — characterize each section
where the receiver is linear, then sum.

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

### 2026-09-15: NOT closable from V12's result — the floor cut is in the wrong place

The close-out plan suggested V6 might be closed from V12's existing data. **It can't.** The 3 GHz
average-detector run (`v28-adaptive.csv`) flagged **110 dB as FLOOR but not 100 dB**, although 100 dB
was equally saturated: it read −2.37 dB error, and the per-step table shows the 90→100 step applying
only **6.48 dB of a nominal 10**. A point that loses 3.5 dB of a 10 dB step is at the floor by any
reasonable definition.

Cause is the default `--floor-dbm -98` against readings that plateaued near −97.6 dBm: 110 dB read
−98.54 (caught), 100 dB read −97.63 (missed by 0.37 dB). A fixed absolute cut cannot separate these —
the plateau is where consecutive steps stop delivering their nominal increment, which is exactly what
the per-step table already computes. **Suggest #13 gain a step-increment test** (flag a point whose
step applied < ~50% of nominal) rather than relying solely on an absolute threshold, then re-run.

Note this only bites the **average** detector. The V13 sync run reached −99.7 dBm with errors flat to
±0.07 dB and had nothing to flag.

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

### 2026-09-15: THREE attempts, glitch never occurred — still ⬜ (and a different fault found)

Attempt 1 (`v29-empty`) passed all 31 points cleanly. Attempts 2 and 3 (`v38-empty`, `v39-empty`) both
**hung at exactly 13 dB** — `DataReady NOT set after 134.5 s (SB=0x00)`, read timeout, and the
post-hang probe failing with `IOTimeoutException`. Same command all three times.

So the #6 recovery path has still never been entered and nothing about it is proven. What the retries
found instead is a **reproducible 5 GHz fault, not yet filed** — see the handoff in `SharedMemory.md`.
Scoped before stopping: **3 GHz is unaffected** (`v40-scope3g`: 29/29 points, full depth, deep tail
within 0.11 dB of the same morning's `v31`), so it is confined to 5 GHz and is out-of-band
characterization territory rather than a blocker.

Two defects to fold into that issue when it is written: `ProbeSignalAfterHang` throwing at exactly the
moment it exists to diagnose the hang, and three disagreeing read budgets (`ReceiverTimeoutMs` = 60 s,
#12's documented 30 s, observed 134.5 s).

### 2026-09-15 (attempt 1, superseded): run completed cleanly, but the glitch did NOT occur

The exact repro command was run (`DebugResults/v29-empty.csv`, 5 GHz, 0–30 dB in 1 dB steps):
**PASS, worst |error| 0.15 dB, all 31 points read, no holes.** But the trace contains no
`empty/short read` line and no `Unrecognized 8902A reading` — the transient simply did not happen this
time, so **the recovery path was never entered and nothing was proven.** The glitch is intermittent by
nature. Leave ⬜ and re-run opportunistically; a clean sweep is not evidence.

---

## V9 — #2 sweep timing profile  ✅ BENCH PASS (2026-09-15)

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

### Result — PASS, 2026-09-15 (`DebugResults/v30-profile.csv` / `.log`)

5 GHz, 0–30 dB in 1 dB steps, `--profile`, no `--debug`. Sweep itself **PASS, worst |error| 0.17 dB**.
Levelling ran normally (source ended at **+2.8 dBm**), so this is a healthy run, not a degraded one.

| Category | Time | % | Count | Per call |
|---|---|---|---|---|
| **settled read** | **569.79 s** | **84.0%** | 31 | **18.4 s** |
| range-cal pre-pass | 102.37 s | 15.1% | 1 | 102 s |
| per-step settle | 3.36 s | 0.5% | 31 | 108 ms |
| setup/other | 1.67 s | 0.2% | — | — |
| attenuator set | 1.52 s | 0.2% | 31 | 49 ms |
| **total** | **678.71 s** | 100% | | |

**The answer #2 asked for: settled reads dominate at 84%, and per-command GPIB I/O is negligible.**
So batching writes is not worth pursuing, and the fixed `Thread.Sleep` waits are not the problem
either — `per-step settle` is 0.5% of wall clock and the 8902A's own `SettleMilliseconds` is already
`0` (`Program.cs:291`). Shaving sleeps would buy nothing.

**Unresolved, and it is the lead for the optimization pass: the 18.4 s/read does not reconcile with
the measured Data Ready waits.** The comparable `--debug` run of the *same* sweep (`v29-empty.log`)
logged 45 Data Ready waits averaging **6.2 s** (min 6.0, max 10.0, 280 s total). At that rate the 31
sweep reads should account for ~190 s, not 570 s. A normal read is a single
trigger → poll → retrieve cycle (`ReadStepWithBoundaryCal` returns on the first `ReadRelativeDb()`;
no double-read on the happy path), so roughly **two-thirds of the dominant category is not the Data
Ready wait** and is currently unattributed.

Candidates, none confirmed: run-to-run settling variance (a 3× shift seems too large); time inside
`ReadRelativeDb` outside the polled window; `MaybeCalibrateBoundary`, which is called at the top of
every read attempt and sits inside the read stopwatch. **Next step is finer instrumentation inside
`SweepTiming.Read`** — split trigger / poll / retrieve — before optimizing anything. Profiling the
category told us where to look; it is not yet granular enough to say what to change.

---

## V10 — #8 CALIBRATE-error surfacing  ❌ BENCH FAIL (2026-09-15) → issue #34
### → fix built on `issue-34-calibrate-completion-poll`; re-validate as **V17** below.

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

### 2026-09-15 (later): FAIL — a real Error 33 was reported as `status = 0x00`, twice

Supersedes the partial finding below. With the operator watching the panel at the instant of each
CALIBRATE (via `--hold-before-cal`, V16), two separate runs produced **Error 33 on the display** while
the harness logged:

```
  8902A < C1             SB=0x00
  8902A CALIBRATE complete, status = 0x00
```

`Calibrate()` sends `C1`, sleeps a **fixed 2500 ms**, then polls the status **once**. But settled
measurement cycles on this bench take **6.5-7.4 s** (one outlier at 34.8 s). So the poll samples before
the CALIBRATE has finished, reads `0x00`, and the error raises with nobody looking — precisely the
failure the method's own comment says it exists to avoid, just at 2500 ms instead of 0 ms.

Fix direction in #34: replace the blind sleep with the poll-on-status completion wait `ReadMeasurement`
already uses, poll repeatedly rather than once, confirm `22.37SP` is in force before a bare
`Calibrate()`, and investigate reading the actual error number over HP-IB so a failure is diagnosable
from the log alone.

**This invalidates every "CALIBRATE succeeded" the harness has ever reported**, including the earlier
`range-cal: 3 CALIBRATE(s) fired` with all three at `0x00`.

### 2026-09-15 (earlier, superseded): partial evidence — the polled line now exists

Not run as its own 5 GHz recipe, but the 3 GHz run (`v28-adaptive.log`) fired a real CALIBRATE and the
trace carries exactly what #8 added:

```
  0 dB: read=UNCAL  [UNCAL]  -> CALIBRATE
  8902A < C1             SB=0x00
  8902A CALIBRATE complete, status = 0x00
```

The post-CALIBRATE status is now polled and reported rather than silently latched, and `0x00` here is
a genuine clean result (the CALIBRATE succeeded and every subsequent read was valid). What is still
missing for ✅ is the row's own cross-check — an observation of the 8902A front panel during a
CALIBRATE that *does* raise an error, which no run this session produced. Left ⬜.

---

---

## V11 — #21 per-path measurable level limits  ✅ BENCH PASS (2026-09-04)

- **Branch:** `issue-21-device-level-limits` (built; sim PASS on all three paths).
- **Why:** the V1 run attempted 100 / 110 dB at 3 GHz from a −1.14 dBm reference — levels of −101 and
  −111 dBm, below the 11793A path's −100 dBm floor. Result: −3.22 / −12.16 dB "errors", a FAIL verdict,
  and a real **Error 01 (signal out of IF range)** on the 8902A panel. #13 caught it after the fact
  against a **guessed** flat −98 dBm; #21 replaces that with the numbers the manuals state, per path.
- **The limits now encoded** (`Instruments/LevelLimits.cs`, citations in source):
  | Path | Floor | Source |
  |---|---|---|
  | 11793A converted (RF > 1300 MHz) | **−100 dBm** | Microwave Product Note: "+0 dBm and -100 dBm" |
  | 8902A direct, IF average (4.4SP) | **−100 dBm** | O&C Table 1-1 fn.12 |
  | 8902A direct, IF synchronous (4.0SP) | **−127 dBm** | O&C Table 1-1 / General Information |
- **Isolate & run** (the same sweep that failed as V1 — it should now stop at the honest depth):
  ```powershell
  git checkout issue-21-device-level-limits
  dotnet run --project src/HP-Attenuator.TestHarness -- --hardware --x-atten 8494 --atten-sweep `
    --freq 3000 --astop 110 --astep 10 --debug --skip-sensor-cal --out DebugResults/v11-limits.csv
  ```
- **Expect (PASS):** the header states `11793A converted floor -100 dBm -> usable ~98 dB`; 0→90 dB
  measure as before (worst |err| ≈ 1 dB); **100 and 110 dB are SKIPped, never commanded** — so the
  8902A shows **no Error 01 at any point in the run** (that's the observable bench proof), the summary
  reports 2 out-of-range points and "Deepest measured ~90 dB", and the verdict is **PASS** rather than
  being failed by unmeasurable points.
- **Watch on the panel:** confirm the sweep now ends cleanly at 90 dB with no error display, and that
  no CL/retry cycling happens at the end of the run.
- **Cross-check:** `--no-level-limits` restores the old behaviour and should reproduce the Error 01 and
  the −3.2 / −12.2 dB points — a direct A/B that the limit is doing the work.
- **If the real floor differs on the day:** `--floor-dbm` overrides the spec value; note the value that
  matches where the reading actually saturates.

### Result — PASS, 2026-09-04 (3 GHz, converted path, author-confirmed on the bench)

Reference leveled to −1.1 dBm → usable depth **98.9 dB** against the 11793A's −100 dBm spec floor.
0→90 dB measured normally (worst |err| **0.94 dB @ 80 dB**, inside ±1.5); **100 and 110 dB were
skipped, never commanded** (predicted −101.1 / −111.1 dBm). Deepest measured 90.6 dB. Verdict **PASS**.

The bench proof: the author confirmed the 8902A front panel showed **no errors at any point** in the
run. The trace agrees — zero error sentinels, zero `CL` error-clears, zero `SB=0x44`, versus three of
each in the pre-#21 run of the same sweep, which ended in a real **Error 01 (signal out of IF range)**
at 110 dB. Driving the receiver below its floor is what produced that error, and the limit gate
removes it at the source.

---

---

## V12 — #22 fine steps on the approach to the floor  ✅ BENCH PASS (2026-09-04)

- **Branch:** `issue-22-fine-step-near-floor` (off `issue-21-device-level-limits`; sim PASS).
- **Why:** #21 caps the 3 GHz sweep at ~98.9 dB usable, and the coarse grid samples that final stretch
  with one point (90 dB). The bench data shows error growing +0.27 dB @ 70 → +0.94 @ 80 → +0.63 @ 90;
  three widely spaced points can't separate drift, range-boundary error and floor onset.
- **Isolate & run:**
  ```powershell
  git checkout issue-22-fine-step-near-floor
  dotnet run --project src/HP-Attenuator.TestHarness -- --hardware --x-atten 8494 --atten-sweep `
    --freq 3000 --astop 110 --astep 10 --fine-from 90 --fine-step 1 --fine-to 100 `
    --debug --skip-sensor-cal --out DebugResults/v12-fine.csv
  ```
- **Expect (PASS):** header reads `step 10 (step 1 from 90 to 100 dB)`; 21 points; coarse 0-80 as
  before; **90,91,…,98 each measured** (98 dB predicts ≈ −99.1 dBm, just inside the −100 dBm floor);
  99/100/110 SKIPped; no error on the 8902A panel at any point; verdict PASS.
- **What we're actually looking for:** does |error| stay flat across 90→98 dB, or climb as the level
  approaches −100 dBm? A clean flat run says the path is honest right up to its spec floor; a rising
  tail tells us where the practical limit really is, and is the number to feed back into #13/#21
  (`--floor-dbm`) and into the #15 per-section cross-check.

### Result — PASS, 2026-09-04 (author-confirmed: no errors on the 8902A through all 21 points)

The tail is **not** flat — a clean monotonic roll-off, and it found the chain's practical floor:

| Set dB | 90 | 91 | 92 | 93 | 94 | 95 | 96 | 97 | 98 |
|---|---|---|---|---|---|---|---|---|---|
| Error dB | +0.36 | +0.28 | +0.13 | −0.12 | −0.33 | −0.71 | −1.06 | −2.31 | −2.76 |
| Level dBm | −91.5 | −92.4 | −93.2 | −94.0 | −94.8 | −95.4 | −96.0 | −95.8 | −96.3 |

Readings **compress and plateau at about −96 dBm** — 97 and 98 dB read *less* attenuation than 95 dB
does. So this chain's practical floor is **≈ −96 dBm, ~4 dB short of the 11793A's −100 dBm spec**
(real conversion loss and noise vs the ideal figure). Honest depth at 3 GHz: **~94 dB within ±0.5 dB**,
~95 dB within ±1 dB. #21 refused the 3 impossible points, #13 flagged the 3 saturated ones, and the
verdict was PASS on the 15 real points — the two mechanisms working together as designed.

Also visible: error takes step changes at 30→40 dB (−0.15 → +0.34) and 70→80 dB (+0.24 → +0.83),
which look like the two uncalibrated RF range-to-range boundaries — with `NO-OP — 0 CALIBRATEs fired`
and no RECAL all run. That is **#17 showing up directly in the accuracy numbers**, and makes row V2
(`--force-range-cal`) worth running to see if ~0.5 dB of mid-range error is recoverable.

---

---

## V13 — #23 0 dBm reference + adaptive step plan  ✅ BENCH PASS (2026-09-15)

- **Branch:** `issue-23-zero-dbm-ref-adaptive-steps` (off `issue-22-fine-step-near-floor`; sim PASS).
- **What changed:** (a) the leveller drives the source until the **8902A reads 0 dBm**, so the
  generator's power error and cable loss drop out of every point and the usable depth is maximised;
  (b) `--adaptive-steps` derives the points from the hardware — 1 dB through the 8494's full 0–11 dB,
  the 10 dB ladder to within 10 dB of the limit, then 1 dB in to the limit.
- **Isolate & run:**
  ```powershell
  git checkout issue-23-zero-dbm-ref-adaptive-steps
  dotnet run --project src/HP-Attenuator.TestHarness -- --hardware --x-atten 8494 --atten-sweep `
    --freq 3000 --astop 110 --astep 10 --adaptive-steps --debug --skip-sensor-cal `
    --out DebugResults/v13-adaptive.csv
  ```
- **Expect (PASS):** the `level:` trace converges to a reference within 0.1 dB **below** 0 dBm and never
  settles above it; the plan reports ~30 points (0,1..11, 20,30..90, 91..100); 0–11 dB each measure
  near nominal; no error on the 8902A panel at any point.
- **What we're looking for:** (1) do the 8494's individual 1 dB steps come out accurate — this is the
  first time each has been measured on its own; (2) with the reference at 0 dBm instead of −1.1, the
  usable depth grows ~1 dB — does the roll-off still start around −95 dBm absolute (which would confirm
  the −96 dBm practical floor found in V12 is a property of the path, not of the reference level)?

### Result — PASS, 2026-09-15 (`DebugResults/v31-adaptive-sync.csv` / `.log`)

Run with `--detector sync --ref-target 0`. **29/29 points measured, nothing skipped, nothing flagged
FLOOR, deepest 99.67 dB.** Worst |error| **0.93 dB @ 80 dB**, verdict **PASS**.

- **Leveller:** `settled at -0.028 dBm (source 0.10 dBm)` in three iterations — within 0.1 dB of the
  0 dBm target and, as required, *below* it. This is the other half of V14's guard, from the opposite
  direction (converging down onto the ceiling rather than recovering from a 13 dB overshoot).
- **Plan derived from the hardware, as #23 specifies:** *"1 dB to 11, ladder … 89 dB, then 1 dB in to
  99 dB (reference −0.03 dBm − floor −100.0 dBm)"* — the endpoint is computed from the achieved
  reference and the path floor, not hardcoded.
- **The 8494's 1 dB steps, measured individually for the first time:** every one of 0→11 dB within
  **+0.01 to +0.07 dB**. That pad is excellent.

**The answer to "does the roll-off still start around −95 dBm" is NO — and it supersedes V12.**

| Set dB | 90 | 91 | 92 | 93 | 94 | 95 | 96 | 97 | 98 | 99 |
|---|---|---|---|---|---|---|---|---|---|---|
| Error dB | +0.63 | +0.63 | +0.69 | +0.71 | +0.75 | +0.77 | +0.72 | +0.69 | +0.77 | +0.67 |

Flat to ±0.07 dB across the last 10 dB, down to **−99.7 dBm**. Compare V12 at the same frequency
(average detector, reference −1.1 dBm), which compressed and plateaued at −96 dBm: +0.36 @ 90 dB
decaying to **−2.76 @ 98 dB**.

So **V12's "practical floor ≈ −96 dBm, ~4 dB short of spec" was a property of the AVERAGE detector,
not of the path.** With the synchronous detector and the reference at 0 dBm, the 11793A converted path
reads honestly to its stated −100 dBm floor. V12's conclusion should be read as scoped to `4.4SP`.

The residual ~+0.7 dB through 90–99 dB is **not** compression — it is the §7+§8 sectional error
(+0.40 / +0.45 measured independently in V5, summing to +0.85) showing up in every total that engages
both 40 dB sections. The per-step table confirms it: the 30→40 and 70→80 steps apply **+0.50** and
**+0.54**, and every other step is within ±0.11.

---

## V14 — leveller straddle fix  ✅ BENCH PASS (2026-09-15)

- **Branch:** `issue-24-sf-matrix` (sim-tested only; **not** hardware-validated).
- **Why this row is the highest-risk one on the queue:** the leveller runs in the setup of *every*
  measurement at *every* frequency. A bad reference doesn't fail loudly — it silently shifts an entire
  frequency's results by the reference error, and every number downstream looks plausible.
- **The defect (observed at 18 GHz, 2026-09-06):** the leveller found source **+13.20 dBm** giving
  **+0.187 dBm** — on target — then discarded it and "settled" back on source **0.00 dBm** with a
  reference of **−13.232 dBm**, measuring that whole frequency 13 dB low. The straddle shortcut assumed
  its two bracketing samples were one 0.05 dB grid step apart, which holds only if level tracks source
  power exactly 1:1; the 8340B's flatness across a 13 dB jump broke that assumption. Same defect as the
  earlier `--ref-target -10` run that settled 2.7 dB below a −10 dBm target.
- **What changed** (`MeasurementEngine.cs`, the leveller loop):
  1. **Straddle shortcut now width-tested** — the bracket is only taken if the two samples are within
     **1.5 grid steps** of each other. A coarse jump that overshoots no longer reverts to a far-away
     baseline and calls it settled.
  2. **New post-loop guard** — if the iteration budget runs out (or the source clamps) on a reading
     *above* the target, fall back to the best reading at or below it. At the 0 dBm Tuned RF Level
     ceiling, settling above the target is an over-range.
- **Isolate & run** — 18 GHz is the known repro, so this is the run that proves it:
  ```powershell
  git checkout issue-24-sf-matrix
  dotnet run --project src/HP-Attenuator.TestHarness -- --hardware --x-atten 8494 --section-sum `
    --freq 18000 --detector sync --ref-target 0 --debug --skip-sensor-cal `
    --out DebugResults/v27-sec-18000.csv
  ```
- **Expect (PASS) — read the `level:` trace, not just the table:**
  - The leveller **keeps** the high-source solution: source lands near **+13 dBm** with the reference
    near the 0 dBm target, instead of reverting to source 0.00 dBm / ref −13.232 dBm.
  - The reference **never settles above** the target. If the fallback fires, the trace says
    `level: iteration budget ended above the target - falling back to the best reading at or below it.`
  - ~~§3 and §4 come into line.~~ **This prediction was wrong — see the Result below.** They
    reproduced almost exactly against a correct reference, which is itself the more useful finding.
- **Cross-check at 3 GHz:** the V13 run exercises the same guard from the other side — its pass
  criterion ("converges within 0.1 dB **below** 0 dBm and never settles above it") *is* this fix.
  V13 and V14 validate together; neither alone covers both branches of the change.
- **⚠ Scope of the doubt — RESOLVED by the 2026-09-15 run, and it was narrower than feared.** The
  worry was that every frequency in the 2026-09-06 10 MHz–26 GHz sweep had been levelled by the buggy
  code, so any row might be silently shifted. The re-run answers it: **a wrong reference costs
  absolute headroom, not relative accuracy.** `--section-sum` measures each section against the 0 dB
  reference taken at the *same* source power, so a common offset divides out. The bad 18 GHz reference
  was 13 dB low, and the section values still reproduced to within 0.09 dB. No row of the
  10 MHz–26 GHz sweep needs re-measuring on account of this bug.
- **If it fails:** fix on `issue-24-sf-matrix`, commit + push, re-run. Nothing else on the queue should
  be trusted until this one passes, because every other run's reference comes from this code.

### Result — PASS, 2026-09-15 (`DebugResults/v27-sec-18000.csv` / `.log`)

Both halves of the fix are visible in four consecutive trace lines, and they are exactly the failure
and the recovery:

```
level: read -13.255 dBm, target 0.0 (delta +13.255), source 0.00 dBm
level: read  +0.183 dBm, target 0.0 (delta  -0.183), source 13.20 dBm
level: read  -0.012 dBm, target 0.0 (delta  +0.012), source 13.00 dBm
level: settled at -0.012 dBm (source 13.00 dBm)
```

Line 2 is the sample the old code found **and then threw away** — it reverted to the line-1 baseline
and reported source 0.00 dBm / reference −13.232 dBm as "settled". Now the width test keeps it, the
leveller refines 13.20 → 13.00 dBm, and the post-loop guard lands it at **−0.012 dBm — just *below*
the 0 dBm target**, which is the guard's whole requirement. Reference error against target: **12 mdB**,
versus **13.2 dB** before.

**The prediction about §3/§4 was wrong, and the way it was wrong is worth more than the prediction.**

| | §1 | §2 | §3 | §4 | §5 | §6 | §7 | §8 |
|---|---|---|---|---|---|---|---|---|
| 2026-09-06, ref −13.232 dBm | +0.02 | −0.02 | **−1.03** | **+1.36** | −0.13 | +0.14 | −0.42 | +0.13 |
| 2026-09-15, ref −0.012 dBm | +0.05 | +0.03 | **−1.01** | **+1.33** | −0.18 | +0.05 | −0.45 | +0.14 |
| Δ | 0.03 | 0.05 | **0.02** | **0.03** | 0.05 | 0.09 | 0.03 | 0.01 |

Every section reproduced to within **0.09 dB** across a 13 dB change of reference level. So:

1. **V14 is confirmed fixed** — on its own criterion, the leveller trace.
2. **The old 18 GHz data was not corrupted after all.** A relative measurement anchored to a reference
   taken at the same source power is immune to a common offset, as long as the deepest read stays above
   the floor — and at 18 GHz it did (deepest ≈ −54 dBm against a −13 dBm reference). The bad reference
   cost headroom, not accuracy. The ledger's earlier "not trustworthy, should not be carried forward"
   was over-cautious and is retracted above.
3. **§3/§4 at 18 GHz are a real, repeatable property**, not an artifact: §3 reads ~1.0 dB low and §4
   ~1.3 dB high, and they **partially cancel in the sum** (§3+§4 = 8.32 dB vs 8 nominal, +0.32). Both
   are nominal 4 dB sections in the 8494G at 4.5× its rated ceiling, so out-of-band mismatch ripple
   between adjacent sections is the natural explanation. Worth noting that this is exactly the
   "two per-section errors that cancel in aggregate and look correct" failure mode #28 was written to
   catch — a total-path sweep would never have shown it.

**Incidental observations from the same run:**
- **V1 evidence (#4):** zero `INSTRUMENT ERROR` lines in the whole trace, and no serial-poll failures.
- **V2/#17 reproduced again on the bench:** `range-cal: NO-OP — 0 CALIBRATEs fired … RESIDENT factors`.
  The RF ranges are still riding resident factors, unchanged.
- One read at 80 dB took **34.8 s** to set Data Ready (typical is 6.5–7.4 s). It completed correctly
  and did not trip the 30 s poll budget into a failure, but it is the longest settle seen so far and
  is worth watching if it recurs.

---

## V15 — #30 leveller silently no-ops on an UNCAL first read  ⬜ FIXED, awaiting bench

### Fix built on `issue-30-leveller-uncal-recovery` (2026-09-15) — all three suggested changes below

1. **The abort is no longer silent.** The `break` now traces the iteration, the reason (Error code +
   description, "still UNCAL after a CALIBRATE retry", or the exception type), the source power it is
   leaving behind, and that the reference is UNKNOWN.
2. **UNCAL is recovered, once.** `ReadTunedLevelWithUncalRecovery` does what the range-cal descent 40
   lines below already did: on `IsUncal`, CALIBRATE at this level and re-read. Budgeted to one attempt,
   so a level that is genuinely outside the calibratable range cannot turn the leveller into a
   CALIBRATE loop. Lost lock / Error 96 / a second UNCAL still propagate.
3. **The downstream degradation is loud, and it distinguishes the two causes of a NaN reference.** New
   `FreqPointResult.ReferenceLevelingFailed` separates "levelling is off (`--no-leveling`, expected)"
   from "levelling RAN AND FAILED". The #21 bypass and the #23 step-plan fallback now both say which,
   and the failed case says plainly that a FAIL from that run is a harness fault, not a DUT fault.
   It **warns rather than refusing to sweep** — `--no-leveling` is a legitimate mode that also yields
   a NaN reference, so refusing would break a working path.

### Verified in simulation (all four paths), via new `--sim-uncal-first-read [n]`

Sim previously could not produce an UNCAL at all, so this defect was unreachable without the bench.
The flag makes `SimulatedReceiver.ReadTunedLevelDbm` return UNCAL for the first *n* reads. The count
is independent of CALIBRATE so both outcomes are reachable.

| Run | Result |
|---|---|
| baseline (no flag) | unchanged — levels to -0.024 dBm, 29-point adaptive plan, PASS |
| `--sim-uncal-first-read 1` | **recovers**: `read 0 came back UNCAL -> CALIBRATE and retry`, then levels, 29-point plan |
| `--sim-uncal-first-read 9` | **fails loudly**: `level: ABORTED on iteration 0 - still UNCAL after a CALIBRATE retry`, then `*** reference level UNKNOWN because LEVELLING FAILED ***` and `adaptive plan UNAVAILABLE` |
| `--no-leveling` | correctly reports `levelling is off`, NOT "failed" |

```powershell
dotnet run --project src/HP-Attenuator.TestHarness -- --x-atten 8494 --atten-sweep --freq 3000 `
  --astop 110 --astep 10 --adaptive-steps --sim-uncal-first-read 1 --debug
```

**Not yet run on hardware.** The bench regression is the command at the end of this row.

### Original finding (2026-09-15)


- **Issue:** [#30](https://github.com/TGoodhew/HP-Attenuator/issues/30). Found on `issue-24-sf-matrix`
  (now merged to `main`); no fix written yet, and no branch cut for it.
- **Severity: high, because it fails quietly and the symptom looks like a DUT failure.** A run that
  never levelled is almost indistinguishable from one that did, except by the *absence* of trace lines
  you would have to know to look for.
- **How it was found:** the 3 GHz average-detector run (`DebugResults/v28-adaptive.log`) returned
  **FAIL, worst |error| 2.37 dB @ 100 dB**. The attenuator was fine; the harness had disabled its own
  safeguards.
- **Mechanism** — `MeasurementEngine.LevelReference()`, the top of the iteration loop:
  ```csharp
  try { level = _receiver.ReadTunedLevelDbm(); }
  catch (Exception ex) when (ex is Hp8902AException || ex is FormatException)
  {
      try { _receiver.ClearError(); } catch { }
      break;                      // <- no Trace, achieved stays NaN
  }
  ```
  The first read came back **UNCAL** (`SB=0x61`), so the loop breaks on iteration 0 **before the first
  `Trace?.Invoke`**. `achieved` stays `NaN` and the reference is unknown.
- **The cascade from that one silent `break`** — three failures, only one of which prints anything:
  1. **No levelling.** Source left at its commanded baseline; the reference is whatever it happens to be.
  2. **The adaptive step plan silently degrades.** #23 derives the plan from the reference and the path
     floor; with a NaN reference it falls back to the plain ladder — **12 points instead of 29**, and
     the announced plan line still claims "1 dB to 11, ladder, 1 dB for the last 10".
  3. **#21's level limits switch off** (`limits NOT enforced (#21)` — the one visible symptom), so the
     sweep commands 100 and 110 dB, precisely the points **V11 proved it must refuse**, reads them
     saturated, and reports FAIL.
- **Why it is clearly fixable:** the UNCAL condition is recoverable, and the code **40 lines further
  down already recovers from it**. The range-cal descent hits the same UNCAL and handles it:
  `0 dB: read=UNCAL [UNCAL] -> CALIBRATE`, `C1`, after which every read in the run was valid. The
  leveller gives up on the one condition its immediate neighbour knows how to fix.
- **Suggested fix, in priority order:**
  1. **Never fail silently** — trace the abort and the reason, at minimum. A levelling step that did
     not happen must say so.
  2. **CALIBRATE and retry once** on UNCAL specifically, mirroring `MaybeCalibrateBoundary`, before
     giving up. Distinguish UNCAL (recoverable) from lost lock / Error 96 (genuinely fatal).
  3. **Make a NaN reference loud downstream.** Both the step-plan fallback and the #21 bypass should
     be prominent warnings, not a quiet degradation — and consider whether a sweep with limits
     disabled should refuse to command points it cannot bound at all.
- **Regression test once fixed:** re-run the exact failing command and expect a levelled reference, a
  29-point plan, 100/110 dB skipped by #21, and PASS:
  ```powershell
  dotnet run --project src/HP-Attenuator.TestHarness -- --hardware --x-atten 8494 --atten-sweep `
    --freq 3000 --astop 110 --astep 10 --adaptive-steps --debug --skip-sensor-cal `
    --out DebugResults/v32-v15-regression.csv
  ```
  Note it must be run **without** `--detector sync` — the synchronous detector did not hit UNCAL at
  3 GHz and therefore does not reproduce the defect.


### Result — no regression, recovery path NOT exercised (2026-10-15, `v46-v15-regression`)

The ledger's regression command, from `bench-2026-10-15`, right after `--trfl-recal`. The first level
read was a valid -1.061 dBm, not UNCAL, so the #30 recovery never ran. **Leave ⬜.** SF 39.9 does not
clear factors on this firmware, so UNCAL cannot be forced. Retry opportunistically, as with V8.
Everything downstream of a successful levelling behaved: settled at -0.036 dBm, a **29-point** plan,
and #21 skipped 100/110 dB.

The verdict was **FAIL on depth, not on #30**: 0-70 dB within 0.5 dB (+0.4 from 40 dB is digit 7's known
in-spec section error), then 80 dB +1.60 and 90 dB +2.70 before compressing into the floor. That is
V4's "+1.4/+2.7 dB drift" on uncalibrated deep ranges with the average detector. Today's two Error-33
CALIBRATEs at 60 dB may also have left a bad factor below that depth.
---

## V17 — #34 CALIBRATE completion poll  ⬜ built, awaiting bench

- **Issue:** [#34](https://github.com/TGoodhew/HP-Attenuator/issues/34). Branch
  `issue-34-calibrate-completion-poll`. This is the re-validation of V10, which failed.
- **Severity: highest open item.** Until this is confirmed on the bench, **no CALIBRATE result from
  this rig can be trusted**, and V2/V4 and #35 all sit behind it.
- **What was wrong:** `Hp8902A.Calibrate()` sent `C1`, slept a fixed 2500 ms, and serial-polled
  exactly once. Settled CALIBRATE cycles measure 6.5-7.4 s on this bench, so that single sample
  landed roughly 4 s before the calibration finished and could not see an error the instrument had
  not raised yet. Two runs logged `status = 0x00` while the front panel showed **Error 33**.
- **What changed:**
  1. `Calibrate()` still holds the same 2500 ms minimum (so it can never conclude *sooner* than the
     code it replaces), then polls the status byte until the receiver reports completion — Data Ready
     (0x01) or instrument error (0x04) — up to a 30 s budget. An error at **any** poll fails the
     calibration, not just one latched at t=2500 ms.
  2. Exhausting the budget logs **UNCONFIRMED** and does **not** throw, so a sweep that works today
     keeps working. An unconfirmed calibration must be read as untrusted, not as a pass.
  3. A failed serial poll is now reported as a failed poll rather than `0x00` — in `Calibrate()`, in
     `ReadMeasurement()`, and in the harness `SafePoll` (see #36). `SafePoll` returned 0 on failure,
     which is **exactly the "0x00" the V10 runs recorded**, so part of that observation may have been
     a failing poll rather than an early sample. The two have different fixes.
  4. `SafePoll`'s caller tested `(sbAfter & 0x04)` on a value that is now -1 on failure; guarded,
     since `-1 & 0x04` is non-zero and would report a phantom instrument error.
- **Verified off-bench:** `--cal-selftest` drives the real `Hp8902A` driver over a scripted link
  (sim mode cannot reach it — sim's `Calibrate()` is an empty method). Five cases pass, and each is
  also run against the old algorithm: the two error cases are reported `old code: MISSED the error`,
  so the check demonstrably discriminates rather than merely passing.
  ```powershell
  dotnet run --project src/HP-Attenuator.TestHarness -- --cal-selftest
  ```
- **Bench procedure** — this needs a front-panel observation, so use the beep-tell-act-ask protocol:
  1. Re-run the V10/#35 probe, which reproducibly raises Error 33 at 20 dB:
     ```powershell
     dotnet run --project src/HP-Attenuator.TestHarness -- --hardware --x-atten 8494 --cal-probe `
       --freq 3000 --debug --skip-sensor-cal
     ```
  2. **Expected:** at the depth where the panel shows **Error 33**, the log must now read
     `8902A CALIBRATE failed after <6.5-7.4> s, status = 0x04  <-- INSTRUMENT ERROR`, and the
     reported elapsed time must be **longer than 2.5 s**. A `status = 0x00` there means the fix did
     not take; a `poll failed` there means the real defect is the bus, not the sample point (#36).
  3. Confirm the elapsed time against the front panel: the cycle should complete at roughly the
     moment the log says it did.
- **Also expected now:** the log should name the code itself — `8902A CALIBRATE error code 33: ...` —
  captured from the read sentinel rather than asking you to read the panel. **Check the panel anyway
  on this one run**, precisely to confirm the captured code matches what the instrument displays; that
  is what licenses trusting it on every run afterwards. Note how long the failing CALIBRATE took: the
  capture read can in principle block up to 60 s if no sentinel is queued.
- **PASS =** a real Error 33 is reported as a failure, with an elapsed time matching the observed
  cycle, and no `0x00` standing in for a failed poll.

### Result — FAIL, 2026-10-15 (bench branch `bench-2026-10-15`; four runs, author at the panel)

Fresh sensor cal first (zero 0.0 nW, reference 1.000 mW). Every CALIBRATE was gated with
`--hold-before-cal` and observed on the panel. 3 GHz, 8494 on X.

| Run | Code | 0 dB | 20 dB | 60 dB |
|---|---|---|---|---|
| `v42-v17` | first #34 fix (poll every 250 ms) | panel clean · log UNCONFIRMED 31.9 s | panel clean · log UNCONFIRMED | **panel Error 33** · log UNCONFIRMED, 0x00 throughout |
| `v43-v17b` | SRQ wait + one poll (`issue-34-srq-completion`) | panel clean · log "no error raised" | panel clean · "no error raised" | **panel Error 33** · log "no error raised", 0x00 |
| `v44-calread` | `--cal-then-read`: C1 then an immediate triggered read | — | panel clean · reads -19.66 dBm | **panel Error 33** · both reads a normal -60.07 dBm |

**The 8902A does not report a TRFL CALIBRATE error over HP-IB at all** (firmware 94.199):
1. **Not in the status byte.** With mask `22.37SP` in force (Data Ready bits arrived all run) and no
   polling during the cycle, the single poll after it read 0x00, and the author confirmed the **SRQ
   lamp never lit**. So the Instrument Error bit (O&C 3-31: "asserted ... when an error is displayed")
   is not set for Error 33.
2. **Not on the read.** C1 followed at once by a triggered read returned a normal level while Error 33
   was on the display.
3. Continuous polling was a separate, real defect (O&C 3-32: the 8902A "cannot alter the Status Byte
   while in serial poll mode"). It is fixed on `issue-34-srq-completion`, but fixing it did not make
   Error 33 visible.

**Other findings from these runs:**
- **The VISA SRQ event is unavailable here.** `EnableEvent(ServiceRequest)` / `WaitOnEvent` threw
  `NativeVisaException` (message not captured). The fallback (wait out the budget, poll once) ran.
- **A CALIBRATE holds the bus.** After C1, the next bus transaction waited about 18 s at 20 dB (clean;
  the panel cycle looked 15 s or more) and about 6.7 s at 60 dB (Error 33). Two points only, but a
  failed cycle may abort early. **Candidate detector.**
- **A failed deep CALIBRATE damages the absolute reference.** After each 60 dB Error 33, the 0 dB
  reading moved (+1.0 dB in `v42`, +0.37 dB in `v43`). Relative sweeps were unaffected (both PASS,
  0-30 dB within 0.16 dB). `--trfl-recal` restored it (+0.355 -> -1.061 dBm, `v45`).
- **#35: 20 dB calibrated clean three times out of three** once nothing changed the level mid-cycle.
  The 9/15 Error 33 at 20 dB came from the old 2.5 s code stepping the attenuator while a 6.5-7.4 s
  CALIBRATE was still running ("maintain consistency in ... level at the SENSOR during calibration").
  The genuine limit lies between 20 and 60 dB.

**Consequence:** no polling or reading strategy can make V17 pass. A failed CALIBRATE has to be
inferred: from the bus-hold time, from the reference shift, or by not issuing deep CALIBRATEs.

---

## V18 — #36 bounded poll, per-poll timing, verified bus release  ⬜ built, awaiting bench

- **Issue:** [#36](https://github.com/TGoodhew/HP-Attenuator/issues/36). Branch
  `issue-36-bounded-poll`, STACKED on `issue-36-visa-scan-ambiguity` (top of the #34 stack).
- **What was wrong:** `ReadMeasurement` checked its 30 s budget only *between* serial polls, and each
  poll inherited the 60 s session timeout. One poll that did not answer overran the budget without
  bound — the 5 GHz hang logged `DataReady NOT set after 134.5 s` against 30 s. `Calibrate()` had the
  same shape. After the hang, the diagnostic poll ran unbounded on the bus that had just hung, and
  `ReleaseBus()` returned normally whether or not the device clear took effect — so the sweep
  reported "released it" and ran a probe that failed with `IOTimeoutException` both times.
- **What the old logs already say (inference, not a finding):** a poll that *succeeds* returning 0x00
  costs milliseconds, so a loop that only ever saw 0x00 would have exited at ~30 s. Reaching 134.5 s
  needs a single poll lasting **at least 104.5 s** — longer than the 60 s session timeout. That points
  at the bus not answering rather than the receiver answering 0x00, and suggests NI-VISA may not honour
  the timeout for a serial poll. This bench run is what confirms or kills both.
- **What changed:**
  1. Each poll is given only what is left of the budget (never less than 15 s), via a new
     `IInstrumentLink.SerialPoll(timeoutMs)` that narrows the VISA timeout for that one call. Same in
     `Calibrate()`. Diagnostic, RECAL and `--debug` trace polls are bounded at 15 s.
  2. The `--debug` summary line now reads, for example:
     `DataReady SET after 6.0 s (SB=0x41) — 24 polls, 0.1 s inside them, longest 0.01 s`, plus
     `N FAILED (first: <exception>)` and `30 s read budget spent` when they apply. Any single poll
     over 1 s is logged on its own as it happens. The retrieve is timed, and a failed retrieve is traced.
  3. `ReleaseBus()` polls after the device clear and throws if the 8902A still does not answer. The
     sweep then says `bus NOT released ... post-hang probe skipped` instead of probing a held bus.
  4. `--profile` gains a second table that splits every 8902A settled read into trigger / wait (and
     time inside polls) / retrieve / settle. This is the attribution **V9's 18.4 s/read** has lacked.
- **Verified off-bench:** `--cal-selftest` 11/11 PASS. The hung-poll case finishes in ~630 ms
  against a 600 ms budget; the legacy loop over the same script overruns to ~3 s
  (`old code: overran ... - this is #36`). Simulated sweep PASS.
- **Bench procedure** (no front-panel observation needed):
  1. Re-run the 5 GHz hang with `--debug` — the `v38`/`v39` command verbatim from their log headers,
     i.e. `--atten-sweep --freq 5000 --astop 30 --astep 1 --debug --skip-sensor-cal` plus whatever
     detector/ref flags those runs used. Read the lines around 13 dB:
     - `(SB=0x00) — N polls, ~0 s inside them` → the receiver really reports 0x00; suspect the
       over-range / status-mask mechanism (the `--ref-target -2` comparison in #36 is the next test).
     - `poll #k took 15.0 s and FAILED (...)` → the bus stopped answering; a GPIB-side fault.
     - `poll #k took` **well over 15 s** → NI-VISA is not honouring the poll timeout. Record the figure;
       the bound then has to move off the session timeout.
  2. Wherever it stops, the `DataReady NOT set after` figure should be **≤ ~45 s**, not 134.5 s.
  3. Read the release line: `bus released` (then the probe result means something) or
     `bus NOT released` (power-cycle the 8902A before anything else).
  4. Separately, for V9: any clean sweep with `--profile` and **without** `--debug`; read the new
     settled-read table.
- **PASS =** the hang (if it recurs) is bounded and attributed to one of the cases above, and the
  release line tells the truth about the bus.
- **2026-10-16 (`v50-v18`): 31/31 PASS, worst 0.09 dB, and the hang did NOT recur** (13 dB read 12.94 dB).
  So the bound and the release check were not exercised by a hang. Levelling needed the source at
  **+3.55 dBm**; the 9/15 run that passed needed +3.15, and the two that hung +2.35. The pattern fits a
  level dependence but proves nothing. Leave ⬜; retry opportunistically, as with V8.
- **Earlier partial evidence from the V17/V15 runs:**
  the per-poll lines worked on every read, and showed that **a settled read is ONE serial poll that
  blocks 6.0-9.0 s** (`poll #1 took 6.1 s (SB=0x41)`). The 8902A holds the poll until its cycle ends
  (O&C 3-22), so the old "DataReady SET after 6.0 s" was never many fast polls. **This confirms the 15 s
  per-poll floor was necessary:** a 5 s bound would have failed every read on this bench.

---

## V19 — #41 TRFL CALIBRATE without SRQ  ⬜ built, awaiting bench

- **Issue:** [#41](https://github.com/TGoodhew/HP-Attenuator/issues/41). Branch `issue-41-trfl-cal-errors`
  (on `bench-2026-10-15`). Follow-up diagnostic: [#42](https://github.com/TGoodhew/HP-Attenuator/issues/42).
- **What changed:** stability gate → C1 → triggered read → decode; success needs a valid reading and no
  RECAL/UNCAL; per-error handling (33/34/35 retry, 31 guided sensor cal, 32 report, 30 `1.0SP`+`9.0SP`);
  bus-hold time logged and short holds flagged SUSPECT; and nothing is stepped after an unconfirmed CALIBRATE.
- **Verified off-bench:** `dotnet test` 63/63; `--cal-selftest` 5/5; simulated sweeps PASS (plain and
  `--sim-uncal-first-read 1`).
- **Bench procedure** (fresh sensor cal first; the instruments were power-cycled after 2026-10-15):
  1. Re-run the V17 command with the panel watched at each hold:
     ```powershell
     dotnet run --project src/HP-Attenuator.TestHarness -- --hardware --x-atten 8494 --atten-sweep `
       --freq 3000 --astop 30 --astep 10 --force-range-cal --hold-before-cal <file> --debug `
       --skip-sensor-cal --out DebugResults/v47-v19.csv
     ```
  2. Read the aqua `TRFL CAL` lines at 0, 20 and 60 dB: gate spread, raw result, **held bus** time, verdict.
  3. **Expected:** 0 and 20 dB `CONFIRMED` with a hold of roughly 15-18 s; 60 dB (Error 33 on the panel)
     most likely still `CONFIRMED`, since the error does not reach HP-IB, but **SUSPECT** if its hold is
     again ~6.7 s. That hold-time split, over more points, is what decides whether SUSPECT can become a
     failure (`--cal-suspect-fails`).
  4. The gate's logged spread tests the source-wander hypothesis: expect a few thousandths of a dB.
- **PASS =** every CALIBRATE logged with raw strings and hold time, no attenuator step after a failure,
  and a SUSPECT flag on the 60 dB Error 33 (or a recorded counter-example that kills the heuristic).

### Result — PASS, 2026-10-16 (`v48-v19`, plus the #35 bisect `v51` and two recals)

Fresh sensor cal first (1.000 mW). V17's command, every CALIBRATE watched; one beep as C1 went out, two
when the cycle ended (the gate runs ~18 s before C1, so the hold release itself is silent).

| CALIBRATE | Panel | Gate spread | Held bus | Cycle | Log |
|---|---|---|---|---|---|
| 0 dB | clean | 0.006 dB | 15.4 s | 21.5 s | CONFIRMED |
| 20 dB | clean | 0.000 dB | 17.3 s | 23.4 s | CONFIRMED |
| 60 dB | **Error 33** | 0.000 dB | **6.8 s** | **14.3 s** | **CONFIRMED but SUSPECT** |
| 30 dB (bisect) | **Error 33** | 0.000 dB | 8.9 s | 16.4 s | SUSPECT |
| 40 dB (bisect) | **Error 33** | — | 6.8 s | 14.8 s | SUSPECT |
| 50 dB (bisect) | clean | — | 14.6 s | 20.9 s | CONFIRMED |
| 0 dB `--trfl-recal` ×2 | (not watched; scale restored) | 0.000 dB | **0.1 s** | 19.4-19.5 s | SUSPECT — **false alarm** |

- **Every CALIBRATE logged with raw strings, spread and timing; every Error 33 flagged SUSPECT.**
- **The gate's spread was 0.000-0.006 dB every time**, including on each Error 33, which rules out source
  wander as the cause of Error 33 on this bench.
- **Heuristic changed after the run:** in `--trfl-recal` the 8902A accepted the trigger write at once and
  made the poll wait, so the "hold" read 0.1 s on a clean CALIBRATE. The whole C1-to-result **cycle**
  separates every point (clean 19.4-24.0 s, Error 33 14.2-16.4 s), so SUSPECT now uses it
  (`--cal-suspect-cycle-ms`, default 18000). The recal re-run afterwards read plain CONFIRMED.
- "No attenuator step after a failure" was not exercised on hardware (no CALIBRATE was reported as
  failed); it is covered by the engine unit tests.
- The sweep after the forced CALIBRATEs passed (0-30 dB within 0.14 dB).

---

## #35 / #42 bench findings, 2026-10-16 (not ledger rows; recorded for the issues)

- **#35, the forced-CALIBRATE depth map** at 3 GHz (panel-watched): 0 clean, 20 clean, **30 Error 33,
  40 Error 33**, 50 clean, **60 Error 33**. Not a depth limit. **Hypothesis:** a CALIBRATE succeeds at the
  8902A's RF input-range boundaries and raises Error 33 in between, consistent with the manual's
  RECAL-driven procedure and "two input range-to-range calibration factors" (O&C p. 3-115).
  `ForceCalDepthsDb = {0, 20, 55}` lands on 60, off-boundary. Test {0, 20, 50}.
- **A clean 20 dB forced CALIBRATE shifts the next range ~+0.5 dB** (30 dB read -29.4 instead of -29.9,
  twice), and a failed one at 60 dB moves the 0 dB absolute reading (+0.42 dB today).
- **After the clean 50 dB CALIBRATE, the 0-110 sweep's deep error fell:** 80 dB +1.60 → +1.18, 90 dB
  +2.70 → +1.32 (`v53-check`, PASS), against V15's morning run. One comparison, but it is V4's question.
- **30 dB then calibrated clean** in the #42 run, an hour after raising Error 33. The one change in between
  was the clean 50 dB CALIBRATE (and a recal), so Error 33 may depend on the stored range factors, not
  depth alone. Speculative.
- **#42:** under mask 22.36SP, an Entry Error (24) read **0x46** and an Operating Error (01, source RF
  off) read **0x44**, with the SRQ lamp lit for both. No 30-series error occurred in that run, so the
  only 30-series observation remains V17's Error 33 with the SRQ lamp dark.
- **Lost status byte after a long hold:** in the *held* `--srq-diag` run (`v54`), both controls lit the
  SRQ lamp yet the harness read **0x00**, and the lamp then went out. Without holds (`v55`), the same
  events read 0x46/0x44. A plain 64-bit NI-VISA session and the 32-bit gpib-mcp both read 0x46 after a
  3 s delay. So something consumes the status byte while the harness sits idle at a hold with SRQ
  asserted (UNCAL raises it during the wait). Not yet explained. NI-488.2 automatic serial polling is the
  leading suspect. **Any status read made after a hold is suspect until this is found**, which includes
  V17's 0x00 readings (the SRQ-lamp observations are unaffected).

## Not on the bench queue (for reference)

- **#15 (per-section characterize + sum)** — the real path to a validated full 110 dB. New feature,
  will get its own `issue-15` branch + ledger rows when built.
- **#13 (floor/plateau detection)** — flag saturated deep points instead of failing them.
- **#16 adaptive leveling / Test 1 / Test 2** — already HW-validated (see SharedMemory.md); not here.
