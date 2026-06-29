# HP-Attenuator Test Plan

This document specifies the individual **tests** the harness runs against the
bench. It is the *what and why* of each test (objective, setup, procedure,
expected result, pass/fail); the harness mechanics — options, CSV, simulation —
live in [TEST-HARNESS.md](TEST-HARNESS.md).

The system under test is the **11713A** and its attached step attenuators
(HP 8494 0–11 dB and HP 8496 0–110 dB). Everything else — 8340B source, 8673B
LO, 11793A converter, 11792A sensor module, 8902A receiver — is the
*measurement reference* used to check that the attenuator did what the app
commanded.

## Conventions

- **Headless / foreground.** Every test is run by invoking the harness from the
  command line and letting it complete in the foreground. No background
  processes or ad-hoc tooling.
- **Simulation vs hardware.** Each test must pass in simulation (fast, no bench)
  before it is meaningful on hardware (`--hardware`). Physical/connection steps
  are never assumed done — the harness prompts the operator and waits.
- **Mandatory sensor cal.** Any hardware measurement is preceded by the
  interactive 8902A power-sensor calibration (zero, then calibrate against the
  50 MHz / 1 mW `CALIBRATION RF POWER OUTPUT`). See [TEST-HARNESS.md](TEST-HARNESS.md).

## Equipment chain

```
 8340B  ─►  step attenuator  ─►  11793A converter ─► 11792A sensor ─► 8902A
 source     (8494 + 8496,        (downconverter,      module           measuring
 GPIB 20     driven by 11713A     >1.3 GHz)           (detector)        receiver
             GPIB 27)                  ▲                                 GPIB 14
                                       │
                                    8673B LO
                                    GPIB 19
```

| Instrument | Role | GPIB | Key codes |
|---|---|---|---|
| HP 8340B | CW source | 20 | `CW <f> GZ/MZ`, `PL <p> DB`, `RF1`/`RF0`, `IP` |
| HP 11713A | attenuator/switch driver (DUT) | 27 | relay strings (see main README) |
| HP 11793A | microwave converter (downconvert >1.3 GHz) | — | passive (LO + IF) |
| HP 11792A | sensor module (detector into 8902A `SENSOR`) | — | passive; carries the cal-factor label (S/N 2407A00808) |
| HP 8673B | external LO for the converter | 19 | `FR <f> MZ`, `LE <p> DM`, `RF1`/`RF0`, `IP` |
| HP 8902A | measuring receiver | 14 | `M4` (RF Power), `S4` (Tuned RF Level), `<f> MZ`, `T3`, `27.3SP <LO> MZ`, `37.x SP` cal factors |

> The 8902A returns level data in **watts** (RF Power) or **dB** (Tuned RF Level
> relative) — the driver converts watts to dBm. Frequency offset for the
> converter path is `27.3SP <LO_MHz> MZ`; `27.0SP` returns to direct mode.

---

## Test 1 — RF power readback at 5 GHz, 0 dB attenuation

### Objective

Confirm the full microwave measurement chain reads a known signal and reports a
sensible **absolute RF power**. With the attenuator set to 0 dB and a 0 dBm
source at 5 GHz, the 8902A (via the 11793A/11792A + 8673B LO) should report a
power close to 0 dBm, less the fixed insertion loss of the through path
(cabling, connectors, attenuator at 0 dB). This is the baseline sanity test for
the converter path and absolute power — everything else builds on it.

This is an **absolute RF Power** measurement (`M4`), not the relative-dB
attenuation method used for the attenuation sweeps.

### Preconditions

1. The 11792A sensor module is connected to the 8902A `SENSOR` input.
2. **Sensor calibration done** (mandatory): zero the sensor, then calibrate
   against the 8902A 50 MHz / 1 mW `CALIBRATION RF POWER OUTPUT`, verify ~0 dBm,
   restore measurement connections. The harness drives this interactively.
3. The **offset cal-factor table** is loaded into the 8902A so absolute power is
   corrected for converter/sensor loss. At 5 GHz the cal factor is **92.9 %**
   (see [converter-cal-factors](../memory) — REF CF 100 % at 50 MHz).

### Instrument setup

| Step | Instrument | Action | Codes |
|---|---|---|---|
| 1 | 11713A | All sections out → **0 dB** (through path) | (open all relays) |
| 2 | 8340B | CW **5 GHz**, level **0 dBm**, RF on | `IP`, `CW 5 GZ`, `PL 0 DB`, `RF1` |
| 3 | 8673B (LO) | `f_LO = 5000 + 120.53 = ` **5120.53 MHz**, +8 dBm, RF on | `IP`, `FR 5120.53 MZ`, `LE 8 DM`, `RF1` |
| 4 | 8902A | RF Power mode, frequency-offset to the LO | `M4`, `27.3SP 5120.53 MZ`, `37.0SP` (auto cal factor) |

The converter mixes 5000 MHz with the 5120.53 MHz LO down to the recommended
**120.53 MHz IF**, which the 11792A/8902A detects. LO = f_RF + 120.53 MHz keeps
the LO well inside the 8673B's 2–26.5 GHz range.

### Procedure

1. Run the mandatory sensor calibration (preconditions 1–2).
2. Load the offset cal-factor table (precondition 3).
3. Apply the instrument setup above and let the source and LO settle.
4. Trigger a settled RF Power reading on the 8902A (`T3`), read the value in
   **watts**, convert to **dBm**.
5. Report the measured RF power in dBm alongside the commanded source level
   (0 dBm) and the implied through-path loss.

### Expected result

- A **valid reading** (not an error sentinel ≥ 9×10¹⁰; in particular not
  **Error 96 "no signal sensed"**, which would mean the chain isn't passing the
  signal — check LO, converter connections, and cabling).
- A power **near 0 dBm**, reduced by the fixed insertion loss of the cabling and
  the attenuator's 0 dB path. The exact figure depends on the bench cabling, so
  this test establishes the baseline rather than asserting an exact dBm.

### Pass / fail

- **Pass:** the chain returns a valid, stable RF-power reading that is
  physically plausible for a 0 dBm source through a low-loss path (e.g. within a
  few dB of 0 dBm), and repeats consistently.
- **Fail:** an 8902A error sentinel, no signal, or a reading that is implausible
  for the known source level (indicating a setup, LO, cal-factor, or
  connection fault).

### Notes

- Absolute accuracy here is bounded by source-level accuracy, cable/connector
  loss, attenuator 0 dB insertion loss, and cal-factor accuracy. The later
  attenuation tests use the **relative-dB** method precisely because it cancels
  these fixed terms.
- Do not change the 8902A RF-attenuation / detector settings between the cal and
  the reading — that invalidates the calibration.

---

## Subsequent tests

_To be specified. Likely follow-ups, building on Test 1's baseline:_

- **Test 2** — Attenuation accuracy at a fixed frequency: relative-dB sweep of
  the 11713A 0→110 dB at 5 GHz, error vs commanded.
- **Test 3** — Frequency coverage: repeat the attenuation check across the
  band (direct ≤1300 MHz and converted >1300 MHz regimes).
- **Test 4** — Attenuator identification (8494 vs 8496 on each 11713A port).
