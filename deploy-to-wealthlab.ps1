param(
    [string]$WealthLabDirectory = 'C:\Program Files\Quantacula, LLC\WealthLab 8'
)

$ErrorActionPreference = 'Stop'

$resultPath = Join-Path $PSScriptRoot 'deployment-result.txt'
$files = @(@{
    Source = Join-Path $PSScriptRoot 'Breeze\bin\x64\Release\net8.0-windows\Breeze.dll'
    Destination = Join-Path $WealthLabDirectory 'Breeze.dll'
}, @{
    Source = Join-Path $PSScriptRoot 'Breeze.Strategies\bin\Release\net8.0-windows\Breeze.Strategies.dll'
    Destination = Join-Path $WealthLabDirectory 'Breeze.Strategies.dll'
}, @{
    Source = Join-Path $PSScriptRoot 'Breeze\bin\x64\Release\net8.0-windows\BreezeHelp.html'
    Destination = Join-Path $WealthLabDirectory 'BreezeHelp.html'
}, @{
    Source = Join-Path $PSScriptRoot 'Breeze\bin\x64\Release\net8.0-windows\BreezeRules.json'
    Destination = Join-Path $WealthLabDirectory 'BreezeRules.json'
})

try {
    foreach ($file in $files) {
        [System.IO.File]::Copy($file.Source, $file.Destination, $true)
        Unblock-File -LiteralPath $file.Destination
    }

    $layoutDirectory = Join-Path $env:APPDATA 'WealthLab8'
    $layoutPath = Join-Path $layoutDirectory 'BreezeLayout.json'
    if (-not (Test-Path -LiteralPath $layoutPath)) {
        [System.IO.Directory]::CreateDirectory($layoutDirectory) | Out-Null
        @{
            Version = 1
            FuzzyAnalysisRatio = 0.25
            TradingPriceBoundary = 0.52
            TradingEnsembleBoundary = 0.63
            TradingDecileBoundary = 0.91
            OverviewCombinedRatio = 0.6666666667
            ContangoLeftRatio = 0.5
            ContangoTopRatio = 0.6666666667
        } | ConvertTo-Json | Set-Content -LiteralPath $layoutPath -Encoding UTF8
    }

    $result = foreach ($file in $files) {
        $sourceHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $file.Source).Hash
        $destinationHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $file.Destination).Hash
        $version = (Get-Item -LiteralPath $file.Destination).VersionInfo.FileVersion
        $match = $sourceHash -eq $destinationHash
        "File=$([System.IO.Path]::GetFileName($file.Destination))"
        "Version=$version"
        "SourceHash=$sourceHash"
        "DestinationHash=$destinationHash"
        "Match=$match"

        if (-not $match) {
            throw "Installed $([System.IO.Path]::GetFileName($file.Destination)) does not match the build."
        }
    }
    $result += "LayoutFile=$layoutPath"
    $result += "LayoutExists=$(Test-Path -LiteralPath $layoutPath)"
    $result | Set-Content -LiteralPath $resultPath -Encoding UTF8
}
catch {
    @(
        "Deployment failed: $($_.Exception.Message)"
        $_.ScriptStackTrace
    ) | Set-Content -LiteralPath $resultPath -Encoding UTF8
    exit 1
}
