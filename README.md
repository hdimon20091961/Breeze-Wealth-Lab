# Breeze Wealth-Lab

Prototype Wealth-Lab 8 Client Extension that renders four market-breadth panels.

- S&P 500 percentage of historically active constituents above SMA(200)
- S&P 500 percentage above SMA(100)
- S&P 500 percentage above SMA(50)
- Nasdaq-100 percentage above SMA(100)

The calculation covers the latest 12 years and honors Wealth-Lab dynamic DataSet membership through each loaded `BarHistory.ExecutableRanges`.

## Build

```powershell
dotnet build .\BreezeVL\BreezeVL.csproj -c Release -p:Platform=x64
```

Copy `BreezeVL.dll` into `C:\Program Files\Quantacula, LLC\WealthLab 8` while Wealth-Lab is closed. Wealth-Lab discovers Client Extensions during startup; there is no documented hot-reload mechanism for these assemblies.

The upstream reference project is in `reference/WL8ExtensionDemos`.
