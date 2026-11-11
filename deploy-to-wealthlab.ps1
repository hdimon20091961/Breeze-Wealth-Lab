param(
    [string]$WealthLabDirectory = 'C:\Program Files\Quantacula, LLC\WealthLab 8',
    [switch]$BreezeOnly,
    [string]$LayoutDirectory = (Join-Path $env:APPDATA 'WealthLab8')
)

$ErrorActionPreference = 'Stop'

$currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
$currentPrincipal = [Security.Principal.WindowsPrincipal]::new($currentIdentity)
if (-not $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $elevationArguments = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', ('"' + $PSCommandPath + '"')
    )
    if ($PSBoundParameters.ContainsKey('WealthLabDirectory')) {
        $elevationArguments += @('-WealthLabDirectory', ('"' + $WealthLabDirectory + '"'))
    }
    if ($BreezeOnly) { $elevationArguments += '-BreezeOnly' }
    if ($PSBoundParameters.ContainsKey('LayoutDirectory')) {
        $elevationArguments += @('-LayoutDirectory', ('"' + $LayoutDirectory + '"'))
    }

    $elevated = Start-Process powershell.exe -Verb RunAs -WindowStyle Hidden -Wait -PassThru -ArgumentList $elevationArguments
    exit $elevated.ExitCode
}

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
}, @{
    Source = Join-Path $PSScriptRoot 'Breeze\bin\x64\Release\net8.0-windows\BreezeEventCatalog.json'
    Destination = Join-Path $WealthLabDirectory 'BreezeEventCatalog.json'
}, @{
    Source = Join-Path $PSScriptRoot 'release\Indicators.JohnEhlers_8Wealth.dll'
    Destination = Join-Path $WealthLabDirectory 'Indicators.JohnEhlers_8Wealth.dll'
})
if ($BreezeOnly) { $files = @($files | Where-Object { [System.IO.Path]::GetFileName($_.Destination) -ne 'Breeze.Strategies.dll' }) }

$backupDirectory = $null
$backedUp = @()
try {
    # Validate the complete set before replacing the first installed file.
    foreach ($file in $files) {
        if (-not (Test-Path -LiteralPath $file.Source -PathType Leaf)) {
            throw "Build file missing: $($file.Source)"
        }
    }
    $catalogue = Get-Content -LiteralPath $files[-1].Source -Raw | ConvertFrom-Json
    if ($catalogue.entries.Count -ne $catalogue.counts.catalogue) {
        throw 'General catalogue entry count is inconsistent.'
    }
    $backupDirectory = Join-Path $WealthLabDirectory ('BreezeBackups\' + [Guid]::NewGuid().ToString('N'))
    [System.IO.Directory]::CreateDirectory($backupDirectory) | Out-Null
    $backedUp = @(foreach ($file in $files) {
        if (Test-Path -LiteralPath $file.Destination -PathType Leaf) {
            $backup = Join-Path $backupDirectory ([System.IO.Path]::GetFileName($file.Destination))
            [System.IO.File]::Copy($file.Destination, $backup, $false)
            if ((Get-FileHash -LiteralPath $file.Destination).Hash -ne (Get-FileHash -LiteralPath $backup).Hash) {
                throw "Backup verification failed: $($file.Destination)"
            }
            @{ Original = $file.Destination; Backup = $backup }
        }
    })
    $loadedBackups = @()
    foreach ($file in $files) {
        try {
            [System.IO.File]::Copy($file.Source, $file.Destination, $true)
        } catch {
            if (-not $file.Destination.EndsWith('.dll', [StringComparison]::OrdinalIgnoreCase) -or
                -not (Test-Path -LiteralPath $file.Destination -PathType Leaf)) { throw }
            # A loaded image may be renamed while the process retains its old mapping.
            # Preserve it in the verified backup directory; the new image loads after restart.
            $loadedPath = Join-Path $backupDirectory ('in-use-' + [System.IO.Path]::GetFileName($file.Destination))
            $expectedRoot = [System.IO.Path]::GetFullPath($WealthLabDirectory).TrimEnd('\') + '\'
            if (-not [System.IO.Path]::GetFullPath($file.Destination).StartsWith($expectedRoot, [StringComparison]::OrdinalIgnoreCase) -or
                -not [System.IO.Path]::GetFullPath($loadedPath).StartsWith($expectedRoot, [StringComparison]::OrdinalIgnoreCase)) {
                throw 'Loaded-image replacement paths are outside the selected installation.'
            }
            [System.IO.File]::Move($file.Destination, $loadedPath)
            $loadedBackups += $loadedPath
            [System.IO.File]::Copy($file.Source, $file.Destination, $false)
        }
        Unblock-File -LiteralPath $file.Destination
    }

    $layoutPath = Join-Path $LayoutDirectory 'BreezeLayout.json'
    if (-not (Test-Path -LiteralPath $layoutPath)) {
        [System.IO.Directory]::CreateDirectory($LayoutDirectory) | Out-Null
        @{
            Version = 1
            FuzzyAnalysisRatio = 0.25
            TradingAnalysisRatio = 0.25
            TradingPriceBoundary = 0.52
            TradingEnsembleBoundary = 0.63
            TradingDecileBoundary = 0.91
            OverviewCombinedRatio = 0.6666666667
            OverviewFullLeftRatio = 0.5
            OverviewFullTopRatio = 0.5
            ContangoLeftRatio = 0.5
            ContangoTopRatio = 0.6666666667
            TitanicTopRatio = 0.68
            ZweigTopRatio = 0.68
            SentimentTopRatio = 0.6666666667
            MarketSetupAnalysisRatio = 0.42
            MarketSetupTopRatio = 0.6666666667
            MarketSetupIndicatorsShare = 0.30
            MarketSetupRulesShare = 0.325
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
    $result += "BackupDirectory=$backupDirectory"
    $result += "RestartRequired=True"
    $result += "LoadedImageBackups=$($loadedBackups -join '|')"
    $result | Set-Content -LiteralPath $resultPath -Encoding UTF8
}
catch {
    $deploymentError = $_
    foreach ($backup in $backedUp) {
        try { [System.IO.File]::Copy($backup.Backup, $backup.Original, $true) }
        catch { Write-Warning "Could not restore $($backup.Original): $($_.Exception.Message)" }
    }
    @(
        "Deployment failed: $($deploymentError.Exception.Message)"
        "BackupDirectory=$backupDirectory"
        $deploymentError.ScriptStackTrace
    ) | Set-Content -LiteralPath $resultPath -Encoding UTF8
    exit 1
}
