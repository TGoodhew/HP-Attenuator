# HP-Attenuator Test Harness

`HP-Attenuator.TestHarness` characterises the 11713A-driven step attenuator
across frequency on a real measurement bench, and doubles as the
build-verification for the app: it drives the attenuator through the **same
`HP-Attenuator.Core` code** the interactive app uses, so running it confirms the
control path still works.

It runs in **simulation by default** (no instruments needed) and against the
real bench with `--hardware`.

## What it measures

For each source frequency it:

1. Sets the **8340B** source to the frequency at the configured power (default
   0 dBm).
2. Establishes the measurement regime (direct or via the converter — see below).
3. Configures the **8902A** for a **Tuned RF Level** measurement and, on hardware,
   runs the **3-point range calibration** (steps the attenuator down, issuing
   `CALIBRATE` so the receiver calibrates each of its RF ranges).
4. Takes a **0 dB reference** (`SET REF`) at the lowest attenuation.
5. Steps the **11713A** attenuator from 0 to 110 dB (default 10 dB steps) and reads
   each level as **dB relative to the reference** — the manual-recommended method
   for attenuator measurement (≈±0.015 dB vs ±0.12 dB for absolute).
6. Reports the **measured attenuation** (the negated relative reading) and the
   error versus the commanded value. If the 8902A returns an error (see below),
   the point is flagged.

### 8902A readings and errors

The 8902A returns a 17-character implicit-point value in *fundamental units*; in
LOG relative mode that is **dB** directly. Any value ≥ 9×10¹⁰ is an **error
sentinel** of the form `+900000NNNNE+01`, where `NN` is the error code
(`code = (value − 9×10¹⁰) / 1000`). The driver decodes these and surfaces them —
e.g. **Error 96 = "no signal sensed"** (the receiver can't tune to a signal),
**15 = cal-factor error**, **01/02 = input level too high/low**. (The earlier
"+139.5 dBm" reading was an undecoded Error 96.)

### Mandatory power-sensor calibration (hardware)

A 8902A Tuned RF Level measurement requires a calibrated RF Power sensor first
(Operation manual, p.3-95). So **every `--hardware` measurement run begins with an
interactive sensor calibration** that you cannot skip without `--skip-sensor-cal`:

1. It uploads the cal factors and **zeroes** the sensor (sensor sees no RF — not on
   the CAL output yet).
2. It **pauses and prompts you to connect the sensor to the `CALIBRATION RF POWER
   OUTPUT`** (50 MHz / 1 mW) and waits for you.
3. It **calibrates** and verifies the reference reads ~0 dBm (Error 18 and abort if
   the sensor isn't on the output — it will not save a bad cal).
4. It prompts you to restore the measurement connections, then runs the sweep.

This is built into the harness because the connect step is physical — it must ask
the operator. The standalone `--sensor-cal` runs just this flow.

### Calibration factors and reference sync (for absolute / converter accuracy)

Relative attenuation cancels the calibration, but for the converter path and any
absolute work the 8902A needs the sensor cal factors and a sensor calibration:

- `--load-cal` loads the converter cal factors (from the sensor label, S/N
  2407A00808, 2–18 GHz) into **both** of the 8902A's RF-Power cal-factor tables —
  Normal and Frequency-Offset — which are **both** required for RF-Power
  measurements (8902A Microwave Product Note). The sequence clears all cal-factor
  storage **once** (`37.9SP` clears *both* tables), loads the Normal table
  (`27.0SP`, then `37.3SP100CF` / `37.3SP<f>MZ<cf>CF` / `37.0SP`), then enters
  Frequency-Offset mode (`27.1SP` — *not* `27.3SP<LO>MZ`, which enables the
  external LO for a measurement) and loads the offset table the same way. Loading
  more than once re-clears a filled table and yields **Error 15** at measurement.
  This load happens automatically as part of the mandatory sensor cal, so
  `--load-cal` is only needed with `--skip-sensor-cal`.
- The **sensor reference sync** is a bench step: connect the sensor between the
  8902A `SENSOR` input and its **50 MHz / 1 mW CALIBRATION RF POWER OUTPUT**, then
  `ZR` (zero), `C1 T3 SC` (calibrate + save), `C0`. The 11793A itself is passive;
  its loss is corrected by the cal-factor table.

## Equipment chain

```
 8340B  ─►  step attenuator  ─►  11793A converter  ─►  8902A
 source     (8494 + 8496,        (downconverter)       measuring
 GPIB 20     driven by 11713A          ▲               receiver
             GPIB 28)                  │               GPIB 14
                                    8673B LO
                                    GPIB 19
```

| Instrument | Role | GPIB | Key codes |
|---|---|---|---|
| HP 8340B | CW source | 20 | `CW <f> MZ`, `PL <p> DB`, `RF1`/`RF0`, `IP` |
| HP 11713A | attenuator/switch driver | 28 | `A…B…` relay strings (see main README) |
| HP 11793A | microwave converter | — | passive (LO + IF) |
| HP 8673B | external LO | 19 | `FR <f> MZ`, `LE <p> DM`, `RF1`/`RF0`, `IP` |
| HP 8902A | measuring receiver | 14 | `S4` (Tuned RF Level), `<f> MZ`, `T3`, `27.3SP <LO> MZ` |

> The 8902A returns level data in **watts** regardless of the front-panel
> display; the driver converts to dBm.

## Direct vs converter regimes

The 8902A measures directly up to ~1300 MHz; above that the 11793A downconverts.

- **≤ 1300 MHz — direct.** No LO. The 8902A is put in normal mode (`27.0SP`) and
  tuned to the frequency.
- **> 1300 MHz — converted.** The 8673B LO drives the 11793A (fundamental mixing,
  N = 1) and the 8902A runs in Frequency-Offset mode (`27.3SP <LO_MHz> MZ`).

### LO / IF maths

The converter mixes the signal with the LO to a tunable IF (must stay within the
converter's **10–700 MHz** window):

```
f_RF = f_LO − f_IF          (LO above signal, the normal case)
```

The harness picks the IF to keep the LO inside the generator's range
(2–26.5 GHz for the 8673B), preferring the recommended **120.53 MHz** IF:

- Normal case: `f_LO = f_RF + 120.53 MHz`.
  (e.g. 5000 MHz → LO 5120.53 MHz; 18000 MHz → LO 18120.53 MHz.)
- Just above the 1300 MHz crossover the preferred IF would put the LO below the
  generator's 2 GHz floor, so the IF is widened to hold the LO at 2000 MHz
  (e.g. 1310 MHz → LO 2000 MHz, IF 690 MHz).
- If the LO would exceed its ceiling (only relevant for sources beyond the
  8340B's 18 GHz), the harness falls back to LO-below and flags the point for
  verification.

LO drive level defaults to **+8 dBm** (the 11793A's requirement for 2–18 GHz).

The logic lives in
[`MicrowaveConverter.Plan`](../src/HP-Attenuator.Core/Instruments/MicrowaveConverter.cs).

## Attenuator identification

The 8494 (1 dB, 0–11) and 8496 (10 dB, 0–110) can be cabled to **either** 11713A
port, so the harness does not assume which is which. It engages each bank fully
and measures:

- Full ATTEN X ≈ **11 dB** and full ATTEN Y ≈ **110 dB** → X is the 8494.
- The reverse → the two are swapped.

This runs automatically when the bench is present. You can also declare it
(`--x-atten 8494|8496`) or be prompted (`--ask`). See
[`AttenuatorIdentifier`](../src/HP-Attenuator.Core/Measurement/AttenuatorIdentifier.cs).

## Usage

```powershell
# Fast simulation over a representative frequency set (the routine check):
dotnet run --project src/HP-Attenuator.TestHarness

# Simulate the swapped wiring (8496 on ATTEN X) to exercise identification:
dotnet run --project src/HP-Attenuator.TestHarness -- --swapped-sim

# Full spec sweep on the real bench: 1 MHz-18 GHz / 10 MHz / 0-110 dB:
dotnet run --project src/HP-Attenuator.TestHarness -- --hardware --full

# A custom hardware range:
dotnet run --project src/HP-Attenuator.TestHarness -- --hardware `
  --fstart 2000 --fstop 6000 --fstep 100 --astep 10 --x-atten 8494
```

### Options

| Option | Meaning |
|---|---|
| *(none)* | Fast **simulation** over a representative frequency set |
| `--hardware` | Drive the real bench over NI-VISA |
| `--detect` | Signal-presence check only (8902A RF-freq, source RF on vs off) |
| `--rf-power` | **Test 1** — single-point absolute RF power readback (see [TEST-PLAN.md](TEST-PLAN.md)) |
| `--freq <MHz>` | Frequency for `--rf-power` (default 5000 = 5 GHz) |
| `--atten <dB>` | Attenuation for `--rf-power` (default 0) |
| `--load-cal` | Load the converter cal factors into the 8902A first (hardware) |
| `--no-cal-pass` | Skip the 8902A 3-point range-calibration pass |
| `--full` | Full spec sweep: 1 MHz–18 GHz, 10 MHz steps, 0–110 dB |
| `--swapped-sim` | Simulate the 8496 wired to ATTEN X (tests auto-id) |
| `--x-atten 8494\|8496` | Declare which attenuator is on ATTEN X (skip auto-id) |
| `--ask` | Prompt for the X/Y attenuator assignment |
| `--fstart/--fstop/--fstep` | Frequency range/step (MHz) |
| `--astart/--astop/--astep` | Attenuation range/step (dB) |
| `--power` | Source power, dBm (default 0) |
| `--settle` | Settle per attenuator step, ms (default 100; forced 0 in simulation) |
| `--tolerance` | Pass/fail threshold, dB (default 1.5) |
| `--out` | CSV results path (default `harness-results.csv`) |
| `--addr-source/-lo/-receiver/-attenuator` | VISA resource overrides |

VISA defaults: source `GPIB0::20::INSTR`, LO `GPIB0::19::INSTR`, receiver
`GPIB0::14::INSTR`, attenuator `GPIB0::28::INSTR`.

## Output

- A per-frequency table (for short sweeps) or one compact line per frequency
  (for long sweeps), plus a final summary with the worst error and a
  **PASS/FAIL** verdict (worst `|error|` vs `--tolerance`).
- A **CSV** (`harness-results.csv` by default) with one row per
  frequency × attenuation point:

  ```
  freq_mhz,regime,lo_mhz,if_mhz,commanded_db,command,measured_rel_db,
  measured_atten_db,expected_atten_db,error_db,error
  ```

The process exit code is **0** on PASS, **1** on FAIL, **2** on error — so it can
gate a build or CI step.

## Simulation model

The simulator ([`SimulatedBench`](../src/HP-Attenuator.Core/Instruments/SimulatedBench.cs))
holds the source/LO state, the engaged attenuator sections, and a *true* section
wiring (which `--swapped-sim` flips). The simulated receiver returns
`source power − true attenuation − small frequency-dependent loss + noise`, so the
whole harness — including identification — is exercised end to end with no
hardware. Because the relative measurement cancels the path loss, simulated
errors stay within a few hundredths of a dB.

## Hardware notes / caveats

- 1 MHz is below the receiver's level floor (~150 kHz–1300 MHz direct range, with
  reduced sensitivity at the bottom); the very lowest points may read poorly on
  real hardware.
- The 8902A `27.x SP` Frequency-Offset codes and the converter cal-factor tables
  come from the 8902A Operation manual and the *8902A Microwave Product Note*;
  for best **absolute** accuracy load the offset cal-factor table. **Relative**
  attenuation (what this harness reports) is robust to cal because the cal
  factor cancels between the reference and attenuated readings at one frequency.
- A full hardware sweep is long (1801 frequencies × 12 steps); use a reduced
  range during development and `--full` for a complete characterisation.
