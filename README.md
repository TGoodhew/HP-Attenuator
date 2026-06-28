# HP-Attenuator

A small interactive console for driving an **HP/Agilent 11713A Attenuator/Switch
Driver** over GPIB. It turns desired attenuation (in dB) into the 11713A's
`A`/`B` relay data strings and sends them via **NI-VISA**, with a
[Spectre.Console](https://spectreconsole.net/) terminal UI.

```
   _   _ ____    _ _ _____ _ _____ _____
  | | | |  _ \  / / |___  / / |___  |___ /  __ _
  | |_| | |_) || | |  / / | | | / /   |_ \ / _` |
  |  _  |  __/ | | | / /  | | |/ /   ___) | (_| |
  |_| |_|_|   /_/|_|/_/   |_|_/_/   |____/ \__,_|

  Attenuator / Switch Driver controller — NI-VISA + Spectre.Console
```

## What it does

- Connects to a 11713A on a VISA resource (default `GPIB0::28::INSTR` — the
  driver's factory GPIB address is **28**), or runs against a built-in
  **simulator** so you can explore it with no hardware attached.
- Set a **total attenuation** target and it solves which sections to engage
  across both attenuator banks, then sends a single `A…B…` data string.
- Set **ATTEN X** or **ATTEN Y** independently.
- Toggle the two independent RF switches **S9** and **S0** (`A9`/`B9`, `A0`/`B0`).
- Send a **raw data string** for anything the helpers don't cover.
- Live state panel showing engaged sections, per-bank dB, total dB, and switch
  positions, plus a send-history view.

> The 11713A is a **listen-only** device — it cannot be queried. The displayed
> state is a software shadow of what has been sent.

## Default attenuator configuration

The default models the classic 4-section 11713A pairing from Table 6-3 of the
11713A manual:

| Bank    | Model              | Sections (digit : dB)        | Range        |
|---------|--------------------|------------------------------|--------------|
| ATTEN X | HP 8494 (0-11 dB)  | 1:1, 2:2, 3:4, 4:4           | 0–11 dB / 1 dB |
| ATTEN Y | HP 8496 (0-110 dB) | 5:10, 6:20, 7:40, 8:40       | 0–110 dB / 10 dB |

Combined range: **0–121 dB in 1 dB steps**. Edit
[`AttenuatorConfig.Default()`](src/HP-Attenuator/Model/AttenuatorConfig.cs) to
match your own 8494/8495/8496/8497 wiring.

## 11713A data-string format

From the 11713A manual, *Data Message Input Format*:

```
[Adm][Bdn]
  A / a  = general ON  (insert section)
  B / b  = general OFF (bypass section)
  d      = relay digit 0-9   (a digit in the A field may not also be in B)
```

ATTEN X = digits **1–4**, ATTEN Y = digits **5–8**, independent switches on
**S9** and **S0**. Example: `A12B34` engages X sections 1+2 (= 3 dB) and bypasses
3+4.

## Requirements

- **.NET Framework 4.7.2** (the project targets `net472`)
- **NI-VISA** with **VISA.NET** installed (this repo references NI VISA.NET 26.0
  and VISA.NET Shared Components 8.0.2)
- A GPIB interface and a 11713A — or just use the simulator

The project builds with the **.NET SDK** (`dotnet build`); the .NET Framework
reference assemblies are pulled in via the
`Microsoft.NETFramework.ReferenceAssemblies` NuGet package, so no targeting pack
install is required.

### VISA.NET reference paths

[`HP-Attenuator.csproj`](src/HP-Attenuator/HP-Attenuator.csproj) references the
VISA.NET assemblies by absolute path:

```
C:\Program Files\IVI Foundation\VISA\Microsoft.NET\Framework64\v4.0.30319\VISA.NET Shared Components 8.0.2\Ivi.Visa.dll
C:\Program Files\IVI Foundation\VISA\Microsoft.NET\Framework64\v4.0.30319\NI VISA.NET 26.0\NationalInstruments.Visa.dll
```

If your installed VISA.NET version differs, update those `HintPath` version
folders.

## Build & run

```powershell
dotnet build
dotnet run --project src/HP-Attenuator
```

Or run the built executable directly:

```powershell
.\src\HP-Attenuator\bin\Debug\HP-Attenuator.exe
```

## Project layout

```
HP-Attenuator.sln
src/HP-Attenuator/
  Program.cs                     Spectre.Console interactive UI
  Model/
    Section.cs                   one switchable attenuator section
    AttenuatorConfig.cs          bank/section wiring + default config
    CommandBuilder.cs            dB -> A/B data-string solver
    DeviceState.cs               software shadow of driver state
  Visa/
    IInstrumentLink.cs           transport abstraction
    VisaInstrumentLink.cs        live NI-VISA session (write-only)
    SimulatedInstrumentLink.cs   in-memory simulator
```

## License

[MIT](LICENSE)
