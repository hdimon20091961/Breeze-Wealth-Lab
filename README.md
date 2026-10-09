# Breeze

Prototype Wealth-Lab 8 Client Extension that renders four market-breadth panels.

- S&P 500 percentage of historically active constituents above SMA(200)
- S&P 500 percentage above SMA(100)
- S&P 500 percentage above SMA(50)
- Nasdaq-100 percentage above SMA(100)

The calculation covers the latest 12 years and honors Wealth-Lab dynamic DataSet membership through each loaded `BarHistory.ExecutableRanges`.

## Build

The project is source-only. It does not include a prebuilt DLL because the
Wealth-Lab assemblies are installed with Wealth-Lab and are not redistributed
by this repository.

### Requirements

- Windows x64
- .NET 8 SDK (the project targets `net8.0-windows`)
- Wealth-Lab 8 installed in `C:\Program Files\Quantacula, LLC\WealthLab 8`

`WealthLab.Core.dll` and `WealthLab.WPF.dll` are resolved from the Wealth-Lab
installation directory during the build. If Wealth-Lab is installed elsewhere,
override the path, for example:

```powershell
dotnet build .\Breeze\Breeze.csproj -c Release -p:Platform=x64 -p:WealthLabInstallDir="D:\\Path\\To\\WealthLab 8"
```

If the .NET 8 SDK is missing, install it from the official [.NET download
page](https://dotnet.microsoft.com/download/dotnet/8.0) before running the build.

```powershell
dotnet build .\Breeze\Breeze.csproj -c Release -p:Platform=x64
```

The resulting assembly is:

`Breeze\\bin\\x64\\Release\\net8.0-windows\\Breeze.dll`

Copy the ready-made `Breeze.dll` into `C:\Program Files\Quantacula, LLC\WealthLab 8` while Wealth-Lab is closed. Wealth-Lab discovers Client Extensions during startup; there is no documented hot-reload mechanism for these assemblies.

The GitHub source repository also contains the compiled DLL in the release
artifacts, so users who only want to install the extension do not need to
install the .NET SDK or compile the project themselves.

The upstream reference project is in `reference/WL8ExtensionDemos`.
