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
| V2 | #17 — real pre-`SET REF` 3-range CALIBRATE (fix the no-op descent) | _needs branch_ | 🔨 | — |
| V3 | `--panel-review` actually pauses on each CALIBRATE | `issue-14-synchronous-deep-sweep` | 🔒 | V2 |
| V4 | #14 — 3-range cal genuinely improves 80–95 dB accuracy | `issue-14-synchronous-deep-sweep` | 🔒 | V2 |
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

## V2 — #17 real 3-range CALIBRATE  🔨 needs code

- **Branch:** _create `issue-17-...` off `main` when building._
- **Problem (from #17):** `CalibrateRfRanges` fires nothing — no read throws UNCAL during the
  pre-`SET REF` descent, so zero CALIBRATEs happen; the ~90 dB accuracy is riding on **resident**
  range factors, not a fresh calibration. `--panel-review` proved it never prompts.
- **Build target:** clear the TRFL range cal factors to force a fresh CALIBRATE on each of the 3 RF
  ranges (and/or detect range crossings by reading-jump); add observability so a no-op descent is
  visible in the trace.
- **Isolate & run** (validate with `--panel-review` so you can *watch* each CALIBRATE on the 8902A
  front panel):
  ```powershell
  git checkout issue-17-...   # the branch you build it on
  dotnet run --project src/HP-Attenuator.TestHarness -- --hardware --x-atten 8494 --atten-sweep `
    --freq 3000 --astop 110 --astep 10 --panel-review --debug --out DebugResults/v2-cal.csv
  ```
- **Expect (PASS):** `--panel-review` prompts **3 times** (one per RF range); the debug trace shows 3
  CALIBRATEs actually firing (RECAL bit set), not an empty descent.
- **If it fails:** fix on the `issue-17` branch, commit + push, re-run.

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

---

## Not on the bench queue (for reference)

- **#15 (per-section characterize + sum)** — the real path to a validated full 110 dB. New feature,
  will get its own `issue-15` branch + ledger rows when built.
- **#13 (floor/plateau detection)** — flag saturated deep points instead of failing them.
- **#16 adaptive leveling / Test 1 / Test 2** — already HW-validated (see SharedMemory.md); not here.
