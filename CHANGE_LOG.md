# Change Log

All notable changes to the HP-Attenuator project are recorded here.
Newest entries first. Format loosely follows [Keep a Changelog](https://keepachangelog.com).

Working process (updated 2026-07-09, author traveling away from the GPIB rig): one branch **per
issue**, a commit for **every change**, and a matching entry here. **`main` is the development
trunk** — issue work merges freely so combining solutions isn't blocked, so `main` may carry
HW-unvalidated code. Standing git default is **commit + push** (branches included); **branches are
kept alive** (not deleted/cleaned up) until their change is bench-validated and the author says so.
What's on `main` but not yet confirmed against the real hardware is tracked in
**[HardwareValidation.md](HardwareValidation.md)** — the step-by-step bench checklist for Renton.

## Unreleased - not yet merged

### BOTH EFFECTS RESOLVED: sections 7+8 are high; the deep-end collapse is the RECEIVER (2026-09-06, bench)
- Completed the discriminator sweep the previous session had to abandon at 40 dB: 500 MHz direct,
  sync detector, reference parked at **-12.71 dBm**, full 0-110 dB in 10 dB steps
  (`DebugResults/v23-ref-minus10-full.csv`). All 12 points landed.
- The 8496 is a 10/20/40/40 dB ladder on digits 5/6/7/8, so 12 commanded points over-determine 4
  section values. Solving the four singles and testing every combination against their sum is the
  clean discriminator: a **sectional** error must add exactly, a **receiver** error must track
  absolute level regardless of which sections are engaged.

  | Cmd dB | relays | abs dBm | measured | sum of sections | residual |
  |---|---|---|---|---|---|
  | 30 | 5+6 | -42.6 | 29.928 | 29.928 | **+0.000** |
  | 50 | 5+7 | -63.0 | 50.324 | 50.327 | -0.003 |
  | 60 | 6+7 | -73.1 | 60.369 | 60.371 | -0.002 |
  | 70 | 5+6+7 | -82.9 | 70.199 | 70.313 | -0.114 |
  | 90 | 5+7+8 | -103.0 | 90.318 | 90.585 | -0.267 |
  | 100 | 6+7+8 | -112.2 | 99.443 | 100.629 | **-1.186** |
  | 110 | 5+6+7+8 | -119.1 | 106.378 | 110.571 | **-4.193** |

- **The attenuator is additive to 0.003 dB through 60 dB.** Sections do not interact; the DUT sums.
- **The sectional error is real and confined to digits 7 and 8:** digit 5 = 9.942 (-0.058), digit 6 =
  19.986 (-0.014), digit 7 = **40.385 (+0.385)**, digit 8 = **40.258 (+0.258)**. This confirms and
  now *quantifies* yesterday's discriminator finding.
- **The collapse beyond 90 dB is NOT sectional — it is the receiver.** The clincher: 110 dB engages
  80 dB (digits 7+8, measured consistent) plus digits 5 and 6, both of which measured within 0.06 dB
  at high level. Two known-good sections cannot manufacture a -4.19 dB error. The residual instead
  tracks **absolute level** monotonically: -0.27 at -103 dBm, -1.19 at -112 dBm, -4.19 at -119 dBm.
  That is soft compression against the floor, not a hard cliff.
- **The real 500 MHz direct/sync floor is about -119 dBm**, and the harness's printed
  "floor -127 dBm -> usable 114.3 dB" is optimistic by ~8 dB. Usable linear range ends around
  **-100 dBm**, where the residual first exceeds 0.25 dB.
- **Caveat that sets the next step:** digit 8's +0.258 dB rests on the single 80 dB point, which sits
  at **-93.4 dBm** — already inside the region where compression is plausibly starting. If it is, the
  true digit 8 is *larger* than +0.258. Digit 8 is the one section never measured in the clean zone,
  because at this reference no combination puts it there.
- **Consequence for #15 (per-section summation):** it is no longer a >1300 MHz special case. It is the
  general method for any total that would push the receiver below ~-100 dBm — measure each section
  alone at a high reference where the receiver is linear, then sum. The 0-60 dB additivity result is
  the evidence that summing is legitimate for this DUT.
- Verdict line still reads FAIL (worst |error| 3.62 dB at 110 dB), but that number is now known to be
  a receiver artifact, not attenuator error, and should not be reported as DUT accuracy.
- Operational note: with the 11713A powered off the harness sat silently in setup (last traffic
  `4.0SP`) instead of reporting the attenuator unreachable. A pre-flight check on GPIB 27 would have
  caught it in a second.


### DISCRIMINATOR RESULT: the deep-end error is the ATTENUATOR, not the receiver (2026-09-05, bench)
- The +0.4 dB step at 40 dB and the growth beyond 80 dB had two candidate causes that every previous
  run confounded: the 8496's 40 dB sections (fixed vs **commanded dB**) or the receiver's uncalibrated
  range boundaries (fixed vs **absolute level**). With the reference always at 0 dBm those two are
  numerically identical, so no run so far could separate them.
- **Experiment:** repeat the 500 MHz sync sweep with the reference deliberately parked low
  (`--ref-target -10`; the leveller settled at **-12.772 dBm**), decoupling commanded dB from absolute
  level by ~12.8 dB.

  | Set dB | 0 | 10 | 20 | 30 | **40** |
  |---|---|---|---|---|---|
  | Error, ref 0 dBm | 0.00 | -0.11 | -0.07 | -0.09 | **+0.38** |
  | Error, ref -12.77 dBm | 0.00 | -0.06 | -0.02 | -0.07 | **+0.39** |

- **The step did NOT move.** It stays at commanded **40 dB** (+0.39) with 30 dB still clean (-0.07). Had
  it been a receiver range boundary at -40 dBm absolute, it would have shifted down to ~27-30 dB
  commanded. It follows the ATTENUATOR SETTING, so it is the **8496's first 40 dB section (digit 7)** -
  which is exactly what the per-step increment table (#24) fingered at +0.49 dB, alongside digit 8 at
  +0.62 dB.
- **Consequence: this is real DUT behaviour to be measured and reported, not calibrated away.** #17
  (range-to-range calibration) drops sharply in priority - it is not the dominant error term after all.
  The remaining question is whether the growth beyond 80 dB is also sectional (digits 7+8 together).
- Incidental: the streamed-CSV fix committed earlier paid off immediately - the interrupted run left 6
  usable rows instead of an empty file.
- Small follow-up noted: the leveller settled at -12.772 dBm against a -10 dBm target rather than
  converging closer. Harmless here (the actual reference is known and the decoupling is what mattered)
  but worth a look.

### 500 MHz direct path: 114 dB reached, and the 11793A was the binding constraint (2026-09-04, bench)
- **The converter, not the receiver, was the limit.** At 500 MHz the signal is below the 1300 MHz
  crossover, so it goes direct (no 11793A, no LO). With the synchronous detector the sweep reached
  **114 dB / about -114.9 dBm** before failing - **14 dB deeper** than the converted path managed at
  3 GHz (100 dB / -100.5 dBm, essentially the 11793A's published -100 dBm limit).

  | Set dB | 100 | 110 | 111 | 112 | 113 | 114 | 115 |
  |---|---|---|---|---|---|---|---|
  | Meas dB | 101.31 | 111.36 | 112.24 | 113.16 | 114.09 | 114.87 | Error 96 |
  | Error dB | +1.31 | +1.36 | +1.24 | +1.16 | +1.09 | +0.87 | - |

  Reference -0.024 dBm. Note the error was still SHRINKING at the limit (+1.36 -> +0.87) and each 1 dB
  step still moved ~0.9 dB, so it tracked cleanly right to the cliff - no compression, no noise plateau.
- **The failure mode differs from the converted path.** At 3 GHz it was **Error 01** (signal out of IF
  range - the converted signal wandering out of the passband). At 500 MHz it is **Error 96, "no input
  signal sensed"**: a genuine sensitivity limit rather than a tracking failure.
- **RECAL fired for the first time all session** - `range-cal: 2 CALIBRATE(s) fired`. This confirms the
  #17 mechanism: the descent was never broken, it is that RESIDENT range factors suppress RECAL, so
  nothing triggers. The direct path at 500 MHz with a freshly written sync calibration had no resident
  factors, UNCAL appeared, and it calibrated naturally.
- **Source at 500 MHz is even better than at 3 GHz** (`--source-check`): counted frequency exact (0 Hz
  error), RF Power -0.233 dBm and TRFL -0.217 dBm agreeing within **0.02 dB**, residual AM 0.24 %,
  **residual FM 7.0 Hz** (vs 18 Hz at 3 GHz - the 8673B LO was contributing most of it), level sd
  **0.001 dB**. Path loss is negligible: 0 dBm commanded reads -0.233 dBm.
- **-127 dBm is not reachable through the attenuator alone.** With the reference at ~0 dBm the
  attenuators' full 121 dB can only reach -121 dBm, and the receiver quit 6 dB before that at -115 dBm.
  Levelling to `--ref-target -6` would put 121 dB exactly on -127 dBm - costing 6 dB of top-end range,
  which is irrelevant for a floor-finding run.

### HP 11792A Sensor Module - specifications (8902A O&C, General Information)
- **Frequency range 50 MHz to 26.5 GHz**, "intended for use with the HP 11793A Down Converter". Contains
  an internal switch that automatically swaps between the receiver's SENSOR and RF INPUT connectors.
- "**A low SWR attenuator isolates the power sensor from the source-under-test**, reducing mismatch."
  That pad sits ahead of the 8902A's RF input, so its insertion loss raises the DUT-referred floor: the
  -127 dBm sensitivity is referred to the receiver's own input, not to the DUT. A candidate explanation
  for part of the 12 dB between the -114.9 dBm we measured and the -127 dBm spec. The exact insertion
  loss is in the 11792A manual, which is scanned images with no text layer.
- **Cal-factor caveat below 2 GHz.** The manual: "the instrument will not measure power at frequencies
  less than the lowest frequency entered in the cal factor table (**2 GHz** in the case of the HP
  11792A)" unless "the reference cal factor [is entered] as an entry in the table **at 50 MHz**". Our
  table runs 2-18 GHz and we enter the REF CF into the separate reference store, which is NOT a 50 MHz
  table entry. So the cal factor applied at 500 MHz is questionable. It did not block the measurement
  (RF Power and TRFL agreed to 0.02 dB) but that agreement does not validate the ABSOLUTE scale, since
  both paths share the cal factor. The relative attenuation sweep is unaffected - it is a substitution
  measurement anchored to its own 0 dB reference.

### Sweep CSV now streams (a killed run keeps its data)
- The 500 MHz run was interrupted and its CSV was **empty**: rows were only written after each frequency
  completed. The sweep now streams a provisional row per point with `AutoFlush`, and rewrites the file
  complete once the sweep finishes (the post-sweep columns - step deltas #24 and floor flags #13 - only
  exist once a frequency is done). An interrupted run keeps its measurements; a completed one is
  unchanged. Verified in sim.

### Synchronous detector re-tested and the "dead end" record corrected (2026-09-04, bench)
- **The sync detector is the better choice on this chain, and the old record was wrong.** SharedMemory
  had it as a dead end that "loses lock (Error 96), never re-try". That was recorded without ever
  measuring residual FM, the one quantity that decides it.
- **Residual FM must be measured in the specified bandwidth.** 447 Hz unfiltered vs **18.0 Hz** with the
  50 Hz high-pass (`H1`) and 3 kHz low-pass (`L1`) applied - the spec defines the 50 Hz threshold
  "measured over a 30 second period in a 3 kHz BW". The unfiltered number is what made sync look
  disqualified.
- **1 dB stepping from 90 dB settled the mechanism** (operator watching the panel throughout):

  | Set dB | 90 | 91 | 92 | 93 | 94 | 95 | 96 | 97 | 98 | 99 | 100 |
  |---|---|---|---|---|---|---|---|---|---|---|---|
  | Meas dB | 91.27 | 92.25 | 93.30 | 94.31 | 95.43 | 96.50 | 97.60 | 98.58 | 99.63 | 100.51 | Error 01 |
  | Step delta | - | 0.98 | 1.05 | 1.01 | 1.12 | 1.07 | 1.10 | 0.98 | 1.05 | 0.88 | - |
  | Error dB | +1.27 | +1.25 | +1.30 | +1.31 | +1.43 | +1.50 | +1.60 | +1.58 | +1.63 | +1.51 | - |

  Every step moves ~1 dB (mean 1.03) right to the edge, then a **clean cliff**. Linear tracking with a
  **constant** offset is a calibration error; noise-limited saturation looks like the average detector,
  whose steps shrink to 0.55-0.8 dB and plateau. **There is no unexplained noise floor.**
- **Depth comparison at 3 GHz:** average detector ~96 dB (-96 dBm, silent compression, 4 dB short of
  the converter spec); synchronous **99 dB (-100.5 dBm)**, meeting the 11793A's published -100 dBm
  limit, and failing with a flagged Error 01 rather than a plausible wrong number.
- **Sync needs its own first calibration** - "the first calibration factor will be different depending
  on the detector used when CALIBRATE is selected the first time". Switching detectors silently
  inherits the other one's factors. `--trfl-recal --detector sync` does it safely (0 dB, full signal).
- **The documentation does not recommend sync - it recommends against it, for a reason that does not
  apply to us.** The 11793A manual never mentions the detector. The Microwave Product Note prescribes
  the average detector inside Track Mode ("32.9 SPCL ... the same as entering 4.4 SPCL, 8.1 SPCL, Log
  units, Track Mode, and 27.3 SPCL") because it is written for a **drifting** source. Our synthesized
  sources are quiet (18 Hz residual FM, 0.004 dB level stability), so following that procedure costs
  ~4 dB of depth for no benefit.
- **Remaining error is #17.** The +1.5 dB offset is the uncalibrated range factors; RECAL has never
  fired on any run today, confirmed on the front panel three separate times by the operator.
- New: `--hold-before-steps <file>` and `--hold-at-db N` hold an attended run after setup (or at a
  chosen depth) until released, so the operator watches only the measurement rather than the setup.

### branch `issue-24-sf-matrix` - #24 bench findings (2026-09-04)
- **SF 31.1 is NOT the culprit, and is harmless here.** The first matrix run put an unvalidated special
  function and an unvalidated forced CALIBRATE into the same run, and left the absolute Tuned RF Level
  scale +42.9 dB high. Re-running configs A/B with the forced calibration removed made them
  **indistinguishable** (reference -0.020 vs -0.016 dBm; 10/20/30 dB within 0.01 dB of each other), so
  the corruption came from **`--force-range-cal`** - the deep forced CALIBRATEs at 20 and 55 dB, exactly
  the case the manual (and our own engine comment) warns stores a bad range factor. The matrix no longer
  forces the calibration.
- **Root cause and recovery for the corruption.** Reading RF Power through the same chain was the
  discriminator: it returned -1.14 dBm (correct) while TRFL read +41.8 dBm, localising the fault to the
  TRFL **first calibration factor**. Instrument preset, a full sensor zero+calibrate, and SF 39.9 all
  failed to clear it. One CALIBRATE at 0 dB with full signal rewrote it (+41.781 -> -1.136 dBm, matching
  RF Power). Shipped as **`--trfl-recal`**. It persisted because, since #17, the sweep only calibrates on
  UNCAL and UNCAL never appears - so no CALIBRATE had been issued since the damage.
- **This 8902A's firmware lacks the SF 31/38/39 family.** Date code 94.199; SF 38.1-38.3 read back
  unreadable and SF 39.9 has no effect. The manual gates several of these at "date codes 234.1985 and
  below". SF 31.1 is accepted but cannot do anything useful, since its documented mechanism is creating a
  Range 3 calibration factor and no Range 3 CALIBRATE fires.
- **Noise floor, with a caveat.** With the absolute scale repaired the receiver will sometimes report a
  floor with the source RF off: -93.74 / -93.75 dBm in the matrix run and -93.48 dBm once afterwards.
  But it is **not reliably reproducible** - a `--noise-floor` sweep across LO drives 8-13 dBm returned
  "below readable" in 5 of 6 cells, because with RF off the receiver has nothing to stay tuned to and
  throws Error 96 instead of reporting its noise. So the LO-drive comparison produced no usable data.
  The solid number remains the **-96 dBm plateau** from the deep sweeps, where the receiver stays
  locked; -93.5 dBm is consistent with it but should not be quoted as a measured floor.
- **Config A also showed the deep error is bias, not noise**: standard deviations of 0.014-0.026 dB
  while running -0.5 to -3.0 dB of error. Repeatable and biased - so more averaging cannot help.
- **Special functions reviewed for noise-floor benefit** (8902A O&C):
  - `1.9SP` - inserts a fixed 10 dB input pad for SWR, and explicitly *"decreases the sensitivity of
    Range 3 by 10 dB"*. Would make the floor **worse**. Rejected.
  - `3.1SP` / `3.7SP` - 455 kHz IF at 200 kHz / 30 kHz selectivity. The IF Average detector is already
    30 kHz, so there is no noise-bandwidth win. Rejected.
  - SF 4 display averaging - reduces variance, and our variance is already 0.02 dB against a >1 dB
    bias. Cannot address a bias. Rejected.
  - `4.0SP` IF synchronous (200 Hz BW, -127 dBm) - the right bandwidth, but a HW-proven dead end
    through the converter (loses lock, Error 96).
  - **LO drive remains the untested lever**: the Microwave Product Note specifies "+8 dBm leveled
    output from the LO" and we run the default +8. Cutting the 11793A's conversion loss raises the
    signal at the 8902A for the same DUT level, which lowers the DUT-referred noise floor directly.

### Source characterization (`--source-check`) - the 8340B is good, and residual FM was mis-measured
- Tony's suggestion: the source is never adjusted between runs, so any instability in it is a
  common-mode error that reads as attenuator error. New `--source-check` characterizes it through the
  measurement chain at 0 dB - counted frequency, level by BOTH the sensor (M4) and Tuned RF Level
  paths, residual AM (M1), residual FM (M2), and level stability over repeated reads.
- **Result at 3 GHz: the 8340B is excellent.** Counted frequency 3000.0001 MHz (+100 Hz); RF Power
  -1.096 dBm and Tuned RF Level -1.144 dBm agreeing within 0.05 dB (which also confirms the repaired
  TRFL scale); residual AM 0.17 %; level **sd 0.004 dB**, span 0.013 dB, drift 0.013 dB. Source
  instability is therefore NOT contributing to the 0.3-0.6 dB mid-range errors - which points back at
  the uncalibrated range boundaries (#17).
- **Residual FM must be measured in the specified bandwidth.** First readings were 447 Hz (converted)
  and 897 Hz (direct) - both apparently far over the 50 Hz threshold. But the specification defines it
  "measured over a 30 second period in a **3 kHz BW**", and no audio filters had been selected, so the
  reading was integrating far more noise than intended. Applying the 50 Hz high-pass (`H1`) and 3 kHz
  low-pass (`L1`) filters gives **18.0 Hz** - comfortably INSIDE the 50 Hz limit.
- **Consequence:** the IF synchronous detector should be able to hold lock after all. Its 200 Hz
  bandwidth is ~22 dB narrower than the average detector's 30 kHz, which is precisely where a lower
  noise floor would come from. The earlier "sync is a dead end" finding was made without this
  measurement; it is worth re-testing rather than assumed.

### branch `issue-23-zero-dbm-ref-adaptive-steps` (off `issue-22-fine-step-near-floor`) - #23
- **#23a - level the reference to 0 dBm, not -2 dBm.** The leveller targeted -2 dBm, so the 0 dB
  reference landed wherever the source power and cable loss put it (-1.14 dBm on the bench). It now
  drives the source until the **8902A itself reads 0 dBm**, making the reference an absolute anchor:
  the generator's own power error and the cabling loss drop out of every point, and the measured
  attenuation is just the negated reading. It also buys maximum dynamic range - every dB of reference
  below 0 is a dB of usable depth lost above the floor. `TargetReferenceDbm` 0.0, `LevelToleranceDb`
  1.0 -> **0.1**, `MaxLevelIterations` 5 -> 12.
  - 0 dBm **is** the Tuned RF Level ceiling, so acceptance is now **asymmetric**: a reading is accepted
    only when it sits in `[target - tolerance, target]`. A reading *above* target is always corrected
    down however small the excess - a symmetric window would have settled over-range. The last move is
    therefore always a reduction. Each iteration is traced under `--debug`.
- **#23b - derive the step plan from the attenuator and the path floor (`--adaptive-steps`).** A fixed
  grid does not match what the stack can do. Per frequency the plan is now: **(1)** 1 dB steps through
  the whole FINE attenuator (8494, 0-11 dB) so each of its steps is characterized; **(2)** the `--astep`
  coarse ladder onward to within `--floor-approach` (default 10) dB of the deepest measurable point;
  **(3)** 1 dB steps in to that limit, sampling the roll-off densely. The limit is
  `achieved reference - path floor` (#21), so a better reference automatically buys depth and the plan
  never proposes a point the #21 gate would refuse. The fine attenuator is identified as the smaller of
  the two configured groups, so `--x-atten` either way round works. Step 2 stays on the original coarse
  grid and so naturally skips the 10 dB point already covered by step 1; points are ascending and
  deduplicated. Falls back to the fixed grid when the reference is unknown.
- **Build clean; sim PASS** - levelling converged from below to -0.038 dBm (it read +0.042 and +0.001
  and correctly pushed back down); the plan came out as 29 points: 0,1,...,11 then 20,30,...,90 then
  91,...,99 (limit 99 from the -0.038 dBm reference). Worst |err| 0.04 dB. Bench: **V13**.

### branch `issue-22-fine-step-near-floor` (off `issue-21-device-level-limits`) — #22
- **#22 — variable sweep resolution: fine steps on the approach to the measurement floor.** With #21
  the sweep stops at the path's honest limit (~98.9 dB usable at 3 GHz from a −1.1 dBm reference), so
  the most interesting region — the last ~9 dB before the 11793A's −100 dBm floor, where accuracy is
  expected to degrade as the signal nears the noise — was characterized by a **single** coarse point.
  New `--fine-from dB` / `--fine-step dB` (default 1) / `--fine-to dB` (default `--astop`) let the
  sweep run coarse to the threshold, fine through the fine region, then **rejoin the ORIGINAL coarse
  grid** beyond it, so enabling the fine region never shifts the coarse points. `AttenuationSteps()`
  yields strictly ascending points with no duplicates; the progress total now comes from the new
  `AttenuationStepCount()` rather than assuming a uniform step. The detailed per-frequency table
  threshold rose 15 → 40 points so a single-frequency fine run still renders as a table.
- **Build clean; sim PASS** — `--astep 10 --fine-from 90 --fine-step 1 --fine-to 100` gives 21 points:
  0,10..80, then 90,91..98 measured, with 99/100/110 skipped by #21 against the sim's 98.0 dB usable
  depth. Worst |err| 0.04 dB, deepest measured 98.0 dB. Bench validation: HardwareValidation.md **V12**.

### branch `issue-21-device-level-limits` (off `main`) — #21
- **#21 — model each path's spec-derived measurable level window, and never sweep outside it.** The
  sweep was commanding attenuation whose resulting level lands *below the path's measurement floor*,
  then reporting the resulting under-read as a measurement error. On the 3 GHz bench run that meant
  attempting 100 dB and 110 dB from a −1.14 dBm reference (i.e. −101 dBm and −111 dBm), producing
  −3.22 dB / −12.16 dB "errors", a FAIL verdict, and a genuine **Error 01 (signal out of IF range)**
  on the 8902A front panel — an error condition the receiver cannot resolve because the signal simply
  isn't measurable there. #13 only caught this *after the fact*, classifying points against a **guessed
  flat −98 dBm**, which is a bench approximation for one path and wrong by 27 dB for another.
- New `LevelLimits` / `LevelWindow` (`Instruments/LevelLimits.cs`) carry the numbers the manuals
  actually state, keyed on **measurement regime × detector**, each with its citation in the source:
  - **11793A converted path: +0 to −100 dBm** — 8902A Microwave Product Note, verbatim: *"any power
    level may be measured between +0 dBm and -100 dBm without further calibration"*. Applies
    regardless of detector; the −127 dBm synchronous figure is a direct-path sensitivity and never
    applies through the converter (matches the HW-tested sync dead end in SharedMemory.md).
  - **8902A direct, IF average (4.4SP): −100 dBm** — O&C Table 1-1, Tuned RF Level, footnote 12,
    verbatim: *"The Tuned RF Level measurement sensitivity when using the IF average detector is
    -100 dBm."*
  - **8902A direct, IF synchronous (4.0SP): −127 dBm** — O&C General Information ("minimum sensitivity
    of -127 dBm"); stated TRFL range "0 to -127 dBm". Ceiling for all paths: 0 dBm.
- `MeasureFrequency` now derives the usable depth from the **achieved reference** (`reference − floor`)
  and **skips** points beyond it: no attenuator command, no read, marked `OutOfRange` with the
  `PredictedLevelDbm` it would have produced, excluded from the verdict, and reported as
  `SKIP — <level> dBm is below the path floor`. Enforcement needs a known reference level; when
  leveling is off or the reference can't be read it is skipped (traced) and #13's post-hoc detection
  still applies.
- `SweepOptions.FloorDbm` (flat −98) is replaced by `FloorDbmOverride` (NaN = use the spec limit for
  the path in use) plus `EffectiveFloorDbm(regime)`, which **#13's floor classifier now also uses** —
  so both mechanisms agree on one spec-derived number per path. `--floor-dbm` still overrides it for
  the empirical case; new `--no-level-limits` restores the pre-#21 attempt-everything behaviour.
- Reporting: the per-frequency header states the active path, its floor and the usable depth
  (`11793A converted floor -100 dBm -> usable 98.0 dB`); a new summary row counts out-of-range points;
  CSV gains `out_of_range` and `predicted_level_dbm` columns. `AttenPointResult.Excluded` centralises
  "this point yields no usable measurement" so the verdict aggregates stay consistent.
- Also fixes a pre-existing #13 accumulator bug this surfaced: the cross-frequency "Deepest measured"
  used `Math.Max(NaN, x)` (always NaN), so it always printed "—".
- **Build clean; sim PASS on all three paths** — converted @ 3 GHz and direct+average @ 1 GHz both
  floor at −100 dBm, usable 98.0 dB, skipping 100/110 dB (worst |err| 0.04 dB, deepest 90.0 dB);
  direct+synchronous @ 1 GHz floors at −127 dBm, usable 125.0 dB, and measures the full 110 dB
  (110 dB → 109.97, err −0.03). Bench validation per HardwareValidation.md row **V11**.

### branch `issue-8-calibrate-error-surface` (off `main`) — #8
- **#8 — surface a CALIBRATE error that was invisible during the post-calibrate settle.** On a hardware
  sweep the SRQ annunciator could light with every `--debug` status poll reading `0x00`, because the
  CALIBRATE runs for ~seconds and *completes during the 2.5 s post-calibrate sleep* — and nothing sampled
  the status in that window. So an **Error 35** ("level error during calibration") raised by a bad 0 dB /
  boundary cal was latched but invisible, and the sweep silently proceeded on a corrupt reference. Fix
  (issue's proposed fix #2, applied to every calibrate site): the ~2.5 s settle moved *into*
  `Hp8902A.Calibrate()` (as `CalibrateSettleMs`), which then **serial-polls after completion**, logs
  `CALIBRATE complete, status = 0x.. [<-- INSTRUMENT ERROR]` under `--debug`, and **throws**
  `Hp8902AException.CalibrateError(status)` when the instrument-error bit (0x04) is set — so the callers'
  existing `catch { ClearError(); }` cal-failure path actually runs instead of trusting the bad cal. The
  three engine calibrate sites (`CalibrateRfRanges`, `ReadStepWithBoundaryCal`, `MaybeCalibrateBoundary`)
  dropped their now-redundant `Thread.Sleep(PostCalibrateWaitMs)` (const removed); `SimulatedBench.Calibrate`
  stays a no-op (sim unaffected). **Build clean; sim `--atten-sweep` PASS (0.04 dB, no regression).** The
  latched-SRQ / Error-35 surfacing is hardware-only → bench-validated per HardwareValidation.md row **V10**.

### branch `issue-2-sweep-profiling` (off `main`) — #2
- **#2 — profile the sweep: attribute wall-clock by category before optimizing.** The sweep is slow, but
  the fix is to *measure the hotspot first* rather than guess. New `SweepTiming` accumulator + `--profile`
  flag: `MeasureFrequency` now times each block — **settled read**, **range-cal pre-pass**, **per-step
  settle**, **attenuator set**, and **setup/other** (the uninstrumented remainder, so the breakdown sums
  to real elapsed time) — via `Stopwatch` (monotonic, no measurable overhead). The harness aggregates it
  across frequencies and prints a breakdown table (time, %, count per category, busiest first) after the
  sweep. This turns "it's slow" into a measured attribution that drives the actual optimization:
  - **Settled reads** are expected to dominate and are hardware-bound (the 8902A settles).
  - **Fixed `Thread.Sleep` waits** — the range-cal pre-pass and per-step settle — are the tunable targets:
    replace blind sleeps with **poll-on-status** waits so we wait only as long as the hardware needs (the
    post-calibrate wait is exactly this, addressed in #8). The profile output calls these out.
  - **Per-command GPIB I/O (batching)** is negligible *unless* `--debug` is on (its per-command serial
    poll inflates everything) — the profile confirms this so we don't micro-optimize a non-hotspot.
  No measurement path changed — pure instrumentation. **Build clean; sim `--profile` PASS** (renders the
  breakdown with correct counts — 12 points, 1 range-cal pre-pass; sim times are ~0 since sim has no real
  waits). Run it on the bench to get the real breakdown → HardwareValidation.md row **V9**.

### branch `issue-6-empty-read-recovery` (off `main`) — #6
- **#6 — recover an empty/transient read instead of failing the point.** A sweep point could fail on an
  empty/garbage read (`Unrecognized 8902A reading: ''  [SB=0x41]`) that is really a transient GPIB race
  — the read raced Data Ready or an RF-range auto-range (the reported 15/16 dB double-empty at a range
  boundary, with the sweep recovering by itself at 17 dB). The three quick retries all caught the same
  glitch, so the point failed. Fix: (1) `ParseReading` now strips control characters and classifies a
  **no-numeric-content** read (including a stray control byte that renders as `''`) as a distinct
  **transient EMPTY read** — `Hp8902AException.EmptyRead()` / `IsEmpty` — not the same "unrecognized"
  bucket as genuinely bad data. (2) Both read-retry loops (`ReadRelativeDbWithRetry`,
  `ReadStepWithBoundaryCal`) give an empty read its **own** settle+re-trigger budget (`EmptyReadRetries`
  = 5, `TransientReadSettleMs` apart) that does **not** consume the main attempts and does **not** CL/
  calibrate — so a cluster of glitches across an auto-range recovers in place. Logged as
  `empty/short read — transient (retry n/5)` via the `--debug` trace, distinct from an unrecognized-data
  failure. Timeouts (genuine below-floor) stay a separate path and still stop the sweep. **Build clean;
  sim `--atten-sweep` PASS (0.04 dB, no regression).** The empty-read glitch is hardware-only (sim never
  produces one), so recovery is bench-validated per HardwareValidation.md row **V8**.

### branch `issue-3-tune-mode` (off `main`) — #3
- **#3 — selectable manual vs automatic Tuned RF Level tuning.** The 8902A can tune to the signal two
  ways (O&C manual): **manual** — enter the frequency directly (`<freq>MZ`), fast/deterministic when the
  frequency is known (our usual case, we command the source); **automatic** — let the receiver search
  for and acquire the signal, then drop to manual tune to hold it and re-enter TRFL, for an uncertain or
  drifting frequency. New `TrflTuning` enum (Manual/Auto); `IMeasuringReceiver.BeginAttenuationMeasurement`
  gains a `tuning` parameter (threaded from `SweepOptions.Tuning` through every engine call site);
  harness `--manual-tune` (default) / `--auto-tune`; the sweep header names the mode. `Hp8902A` implements
  the auto branch as *acquire (auto-tune SF) → wait `AutoTuneAcquireMs` → hold (`<freq>MZ`) → continue
  TRFL*, and logs the sequence under `--debug`. **The auto-tune HP-IB code (`AutoTuneSpecialFunction`,
  currently `7.1SP`) is BENCH-UNVERIFIED** — the Operation manual's SF-7 tuning codes are OCR-ambiguous in
  the scan, which is exactly why auto tuning is opt-in and manual stays the default (no behaviour change).
  **Build clean; sim `--auto-tune` PASS** (sim's SimulatedBench ignores tuning — the sim never drifts — so
  it exercises flag/threading/header only; the SF sequence runs against the real `Hp8902A`). Bench-verify
  the auto-tune codes per HardwareValidation.md row **V7**.

### branch `issue-13-floor-detection` (off `main`) — #13
- **#13 — floor/plateau detection: flag saturated deep points instead of failing them.** Past the
  ~95–98 dB usable depth of the 11793A path, a deep sweep point reads the −100 dBm converter floor and
  stops tracking — a 100/110 dB point saturates near −98 dBm and so **under-reads** its target (the
  −2.4 / −12 dB "errors" from the #14 run). Those aren't a DUT or sweep fault, they're the measurement
  floor, so charging them against the verdict wrongly FAILs an otherwise-good sweep. Now a classifier
  (`MeasurementEngine.ClassifyFloorLimited`) marks such points `AttenPointResult.FloorLimited`: a point
  is flagged only when it **under-reads its target by > `FloorMarginDb`** AND either (a) its absolute
  level (leveled reference + relative reading) sits at/below `FloorDbm` (default −98 dBm), or (b) it
  **plateaued** — the reading didn't rise past the deepest attenuation genuinely tracked so far. The AND
  with under-reading keeps an accurate deep point near the floor from being mistaken for saturation.
  Floor-limited points are **excluded** from `MaxAbsErrorDb` and the sweep verdict, shown as `FLOOR` in
  the table / `(N floor, deepest X dB)` on the summary line, carried in a new CSV `floor_limited` column,
  and summarised ("Floor-limited (#13): N point(s) … Deepest measured: X dB"). New `FreqPointResult`
  helpers `FloorLimitedCount` / `DeepestMeasuredDb`. Flags: `--floor-dbm dBm` (threshold, default −98)
  and `--no-floor-detect` (restore the pre-#13 count-everything behaviour). **Build clean; sim
  `--atten-sweep --astop 110` PASS with no false flags** (sim never saturates, so the AND-gate correctly
  flags nothing — the no-false-positive check). Floor-flagging itself needs a saturating read →
  bench-validated per HardwareValidation.md row **V6**.

### branch `issue-15-per-section-sum` (off `main`) — #15
- **#15 — per-section characterize + SUM: the path to a validated full 110/121 dB.** The full range
  can't be measured **directly** — the 11793A converter path floors at −100 dBm, so with the reference
  near −2 dBm anything past ~95–98 dB reads the floor (the #14 finding). New `--section-sum` mode
  sidesteps that: it measures **each attenuator section on its own** (the 8494's 1/2/4/4 dB and the
  8496's 10/20/40/40 dB — every section ≤40 dB, so each read stays comfortably above the floor), then
  **sums** the measured sections to synthesize any total, including the deep ones that can't be read
  directly. Sections add linearly (proved by `--section-test` to 0.01 dB), so the sum is a valid
  full-range measurement. Built entirely on the existing `MeasurementEngine.MeasureSettings` primitive
  (0 dB SET REF, engage one section's digits, read relative) + `CommandBuilder.Solve` to map each target
  total to its engaged sections. Output: a per-section characterization table (nominal vs measured vs
  error), a **characterized full scale** (Σ all sections), and a synthesized-totals table that marks
  which targets are directly measurable vs **"sum only"** (≥ 95 dB). Verdict passes when every section
  reads cleanly (a valid full-scale sum exists); absolute error vs the nominal labels folds in DUT pad
  tolerance, so it's surfaced, not failed. CSV carries both the section rows and the synthesized-total
  rows. **Build clean; sim `--section-sum` PASS** — full scale 120.83 dB (nominal 121), worst section
  |err| 0.04 dB, all 8 sections read, 100/110/120/121 dB correctly flagged "sum only". Bench-validated
  per HardwareValidation.md row **V5**.

### branch `issue-17-range-cal-observability` (off `main`) — #17
- **#17 — make the pre-`SET REF` 3-range CALIBRATE observable, and add an opt-in force.** The range-cal
  descent (`CalibrateRfRanges`) was a silent no-op: it only CALIBRATEs when a read throws UNCAL, but
  with **resident** range factors the 8902A never raises RECAL/UNCAL, so zero CALIBRATEs fired and the
  ~90 dB accuracy rode stale factors (proven earlier: `--panel-review` never prompted). Two changes,
  both sim-safe and off the validated path:
  - **Observability (the core of #17):** every descent step is now traced via a new engine
    `MeasurementEngine.Trace` sink (harness wires it on `--debug`, yellow) — commanded depth, absolute
    read, an off-trend *jump* marker (reused the previously-vestigial `RangeStepThresholdDb`), and
    whether a CALIBRATE fired — and the pass ends with an explicit summary. A descent that fires zero
    CALIBRATEs now prints a loud **"NO-OP — 0 CALIBRATEs fired … RESIDENT factors (issue #17)"** line,
    so the symptom is visible on the bench instead of silent.
  - **`--force-range-cal` (opt-in workaround):** `SweepOptions.ForceRangeCal` issues one *unconditional*
    CALIBRATE per RF range at approximate boundary depths (`ForceCalDepthsDb = {0, 20, 55}` dB — the
    AVG detector's ~0/−15/−50 dBm range breaks; bench-tunable), since with resident factors neither
    UNCAL nor an off-trend jump ever appears to gate on. Default OFF (preserves the validated
    Average-detector ≤90 dB path); when on, `--panel-review` should prompt 3× (once per range).
  - Removed the dead `RangeCalStepDb` const (the pass uses `SweepOptions.CalStepDb`). **Build clean;
    sim `--atten-sweep --force-range-cal --debug` PASS (worst |err| 0.04 dB — no regression).** The
    descent logic is hardware-only (sim disables the cal pass), so it is bench-validated per
    HardwareValidation.md rows **V2/V3/V4**; the flag/plumbing is what sim exercises.

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
  never block). First use: each individual CALIBRATE in the 3-range descent is wrapped **tightly** —
  the prompt pauses immediately BEFORE that CALIBRATE (RECAL/UNCAL should be lit right then) and
  immediately AFTER (confirm it cleared to a valid reading), so the operator confirms that exact step
  rather than one loose pause bracketing the whole multi-step descent. Sim: no-op, sweep PASS.
- **CORRECTION via `--panel-review` → filed #17.** The panel-review run **never prompted**, which means
  no read threw UNCAL during the descent, i.e. **`CalibrateRfRanges` fires zero CALIBRATEs — it's a
  no-op on the bench.** So the earlier "3-range cal flattened the drift" claim was wrong: the sync→AVG
  improvement was the detector change + **resident TRFL range factors** from earlier session runs, not
  a fresh calibration. The direct method is still usable to ~90 dB (AVG holds lock, floor ~−98.7 dBm),
  but the range calibration isn't actually running. See **#17** (clear TRFL range factors to force a
  fresh cal, and/or detect range crossings by reading-jump; add observability so a no-op descent shows).

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
