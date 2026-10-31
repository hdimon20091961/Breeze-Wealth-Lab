# Integration tests

This directory contains four maintained integration checks:

- `Contango/` validates the VIX term-structure store and SVXY state sequence.
- `EventModel/` validates all 75 events, the three frozen profiles, causality,
  scoring and the generated feature report.
- `Fuzzy/` validates the fuzzy phase model, operational text, chart-model
  calculations and embedded Help generation.
- `MarketSetup/` validates indicators, the 141-entry catalogue, eleven added EMA states, frozen runtime weights,
  compact production-XAML layout and the dual-axis WPF renderer.

Run them from the repository root so relative documentation and artifact paths
resolve consistently. These are integration tests and require the historical
data under `D:\fear_greed_close`.

EventModel and MarketSetup write new test output to `tests/outputs/` by default,
not to preserved research folders. Pass an explicit output directory as the
first argument to keep a named run. EventModel accepts optional data-directory
and VIX-curve paths as the second and third arguments. For example:

```powershell
dotnet run --project .\tests\MarketSetup\Breeze.MarketSetupSmokeTest.csproj -c Release -- docs/review-2026-10-25/test-runs/market-setup
dotnet run --project .\tests\EventModel\Breeze.EventModel.SmokeTest.csproj -c Release -- docs/review-2026-10-25/test-runs/event-model-live
```

To regenerate the standalone Help after changing the embedded research text:

```powershell
dotnet run --project .\tests\Fuzzy\Breeze.FuzzySmokeTest.csproj -c Release -- --write-help D:\CODEX_PRG\Breadth
```
