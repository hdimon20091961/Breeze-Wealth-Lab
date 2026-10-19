# Breeze version archive

This is the single archive for preserved Breeze release binaries. Each version
has its own directory. Build output under `Breeze/bin` and
`Breeze.Strategies/bin` is temporary and is not the archive.

## Preserved binaries

| Version | Files | Origin |
| --- | --- | --- |
| 0.5.0 | `Breeze.dll` | former `release/0.5.0` |
| 0.5.1 | `Breeze.dll` | former `release/0.5.1` |
| 0.5.2 | `Breeze.dll` | former `release/0.5.2` |
| 0.6.0 | `Breeze.dll` | former `release/0.6.0`; duplicate root DLL removed after SHA-256 match |
| 0.9.15 | `Breeze.dll`, `BreezeHelp.html` | former `dist/Breeze-0.9.15` |
| 0.9.17 | `Breeze.dll` | snapshot of the installed Wealth-Lab version |
| 0.9.18 | `Breeze.dll`, `Breeze.Strategies.dll` | snapshot of the completed strategy-library build |
| 0.9.19 | `Breeze.dll`, `Breeze.Strategies.dll` | viewport-independent Last readout and relocated Hold legend |
| 0.9.20 | `Breeze.dll`, `Breeze.Strategies.dll` | plotted 0–10 target series and one-contract balancing loops |
| 0.10.0 | `Breeze.dll`, `BreezeHelp.html`, `BreezeHelp.generated.html`, `validation.json` | 74-state Soft/Standard/Strong position model, fixed 50% level, revised cursor explanation and complete research Help |
| 0.10.1 | `Breeze.dll`, `BreezeHelp.html`, `BreezeHelp.generated.html` | pinned blue crosshair, SizeWE pressed cursor, and correct wheel routing to the Fuzzy explanation panel |
| 0.10.2 | `Breeze.dll`, `BreezeHelp.html`, `BreezeHelp.generated.html` | restored 25-bar chart-wheel zoom while pinned and moved the model selector into the left cursor panel |
| 0.10.3 | `Breeze.dll`, `BreezeHelp.html`, `BreezeHelp.generated.html` | one-line cursor/model header and persistent draggable brown divider between the Fuzzy panels |
| 0.10.4 | `Breeze.dll`, `BreezeHelp.html` | standalone browser Help beside the DLL, with an embedded and visibly identified fallback copy |

## Autonomous Wealth-Lab research strategy

| Version | Files | Origin |
| --- | --- | --- |
| 0.10.0-alpha.13 | `BreezeHoldPositionStrategy_new.cs`, `Breeze.Strategies.New.dll` | frozen Soft/Standard/Strong profiles and user-formatted Strong factor stress table; independent of `Breeze.dll` |

## Known version iterations

Thirty-seven unique versions are confirmed by preserved DLL metadata or cached
generated `AssemblyInfo` files. Version 0.2.0 is additionally documented in the
project README, although its binary and cached generated file no longer exist.
Consequently, there are 38 known version iterations through 0.9.18; 0.9.19 is
the 39th, 0.9.20 is the 40th, 0.10.0 is the 41st, 0.10.1 is the 42nd, and
0.10.2 is the 43rd, 0.10.3 is the 44th, and 0.10.4 is the 45th known iteration.

Known history:

`0.1.0`, `0.2.0` (documented only), `0.3.0`, `0.3.1`, `0.3.2`, `0.3.3`,
`0.4.0`, `0.4.1`, `0.4.2`, `0.5.0`, `0.5.1`, `0.5.2`, `0.6.0`, `0.7.0`,
`0.7.2`, `0.7.3`, `0.8.0`, `0.8.2`, `0.8.3`, `0.9.0`, `0.9.1`, `0.9.2`,
`0.9.3`, `0.9.4`, `0.9.5`, `0.9.6`, `0.9.7`, `0.9.8`, `0.9.9`, `0.9.10`,
`0.9.11`, `0.9.12`, `0.9.13`, `0.9.14`, `0.9.15`, `0.9.16`, `0.9.17`,
`0.9.18`, `0.9.19`, `0.9.20`, `0.10.0`, `0.10.1`, `0.10.2`, `0.10.3`,
`0.10.4`.

Versions 0.2.0, 0.7.1, 0.8.1 and 0.9.15 do not all appear in the cached
generated build files; only 0.2.0 is explicitly documented and 0.9.15 has a
preserved DLL. Repeated compilations with an unchanged version number are not
counted as separate version iterations.
