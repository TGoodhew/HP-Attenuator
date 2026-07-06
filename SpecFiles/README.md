# SpecFiles

Reference specification data for the devices under test — used to check measured
results against the manufacturer's published limits.

| File | What it is |
|---|---|
| `8494G_8496G_series_attenuation_ranges.csv` | Per-dB attenuation table for the HP 8494G (0–11 dB, 1 dB steps) + 8496G (0–110 dB, 10 dB steps) stack, 0–121 dB nominal. Columns: `Nominal_dB`, `Lower_Limit_dB` / `Upper_Limit_dB` (the pass/fail window), `Tolerance_dB` (± about nominal), and the `8496G_Setting_dB` / `8494G_Setting_dB` that produce each nominal value. This is the spec the attenuation sweep (Test 2 / #14 segmented sweep) validates against. |
