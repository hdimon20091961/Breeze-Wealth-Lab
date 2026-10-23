# Integration tests

This directory contains the three maintained end-to-end checks:

- `Contango/` validates the VIX term-structure store and SVXY state sequence.
- `EventModel/` validates all 75 events, the three frozen profiles, causality,
  scoring and the generated feature report.
- `Fuzzy/` validates the fuzzy phase model, operational text, chart-model
  calculations and embedded Help generation.

Run them from the repository root so relative documentation and artifact paths
resolve consistently. These are integration tests and require the historical
data under `D:\fear_greed_close`.

To regenerate the standalone Help after changing the embedded research text:

```powershell
dotnet run --project .\tests\Fuzzy\Breeze.FuzzySmokeTest.csproj -c Release -- --write-help D:\CODEX_PRG\Breadth
```
