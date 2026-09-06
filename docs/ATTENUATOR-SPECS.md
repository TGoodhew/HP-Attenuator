# HP/Agilent/Keysight 8494G + 8496G — published accuracy specification

Source: *Keysight 8494/95/96G/H Operating and Service Manual*, Table 1-6 "Attenuation accuracy",
p.18–19 (local copy: `Manuals/8494-95-96G-H.pdf`). Both units on this bench are **G** variants
(DC–4 GHz), which is why the harness defaults to a 3 GHz test frequency.

## The specification is cumulative, not per section

Table 1-6 is headed **"(±dB): (Referenced from 0 dB)"**. The limit applies to the *indicated
attenuation setting* measured against the 0 dB state — it is **not** a per-section tolerance, and it
**widens with the setting**. This is the single most important thing about the table: a +0.4 dB error
means very different things at a 10 dB setting and at a 40 dB setting.

## DC–4 GHz limits (the only column that applies to this bench)

| 8494G setting (dB) | ± dB | | 8496G setting (dB) | ± dB |
|---|---|---|---|---|
| 1 | 0.2 | | 10 | 0.2 |
| 2 | 0.3 | | 20 | 0.4 |
| 3 | 0.3 | | 30 | 0.5 |
| 4 | 0.3 | | 40 | **0.7** |
| 5 | 0.3 | | 50 | 0.8 |
| 6 | 0.3 | | 60 | 1.0 |
| 7 | 0.4 | | 70 | 1.2 |
| 8 | 0.4 | | 80 | 1.3 |
| 9 | 0.4 | | 90 | 1.5 |
| 10 | 0.4 | | 100 | 1.6 |
| 11 | 0.5 | | 110 | **1.8** |

## Applying it to individual sections

The 8496G ladder is 10/20/40/40 dB on digits 5/6/7/8; the 8494G is 1/2/4/4 dB on digits 1/2/3/4.
A section engaged **alone** is just a setting of that value, so it takes that setting's limit:

| Digit | Unit | Nominal | Limit |
|---|---|---|---|
| 1 | 8494G | 1 dB | ±0.2 |
| 2 | 8494G | 2 dB | ±0.3 |
| 3 | 8494G | 4 dB | ±0.3 |
| 4 | 8494G | 4 dB | ±0.3 |
| 5 | 8496G | 10 dB | ±0.2 |
| 6 | 8496G | 20 dB | ±0.4 |
| 7 | 8496G | 40 dB | ±0.7 |
| 8 | 8496G | 40 dB | ±0.7 |

## Other limits worth having on hand

- **Maximum residual attenuation** (Table 1-8) — the 0 dB insertion loss: 8494G and 8496G are both
  **0.6 dB + 0.09 dB/GHz** (0.65 dB at 500 MHz, 0.87 dB at 3 GHz). A relative measurement referenced
  to the 0 dB state normalises this out, so it does not enter the accuracy numbers above.
- **Maximum SWR** (Table 1-7): 8494G and 8496G, DC–4 GHz, **1.5**.
- **Frequency range** (Table 1-5): 8494G 0–11 dB in 1 dB steps, 8496G 0–110 dB in 10 dB steps, both
  DC–4 GHz.
