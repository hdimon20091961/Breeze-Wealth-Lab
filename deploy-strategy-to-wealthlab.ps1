param(
    [string]$WealthLabDirectory = 'C:\Program Files\Quantacula, LLC\WealthLab 8'
)

$ErrorActionPreference = 'Stop'

$currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
$currentPrincipal = [Security.Principal.WindowsPrincipal]::new($currentIdentity)
if (-not $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $arguments = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', ('"' + $PSCommandPath + '"'),
        '-WealthLabDirectory', ('"' + $WealthLabDirectory + '"')
    )
    $elevated = Start-Process powershell.exe -Verb RunAs -WindowStyle Hidden -Wait -PassThru -ArgumentList $arguments
    exit $elevated.ExitCode
}

$source = Join-Path $PSScriptRoot 'Breeze.Strategies\bin\Release\net8.0-windows\Breeze.Strategies.dll'
$destination = Join-Path $WealthLabDirectory 'Breeze.Strategies.dll'
$backupDirectory = Join-Path $WealthLabDirectory ('BreezeBackups\Strategy-' + [Guid]::NewGuid().ToString('N'))
$resultPath = Join-Path $PSScriptRoot 'strategy-deployment-result.txt'

if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
    throw "Build file missing: $source"
}

[System.IO.Directory]::CreateDirectory($backupDirectory) | Out-Null
if (Test-Path -LiteralPath $destination -PathType Leaf) {
    [System.IO.File]::Copy($destination, (Join-Path $backupDirectory 'Breeze.Strategies.dll'), $false)
}

try {
    [System.IO.File]::Copy($source, $destination, $true)
}
catch {
    if (-not (Test-Path -LiteralPath $destination -PathType Leaf)) { throw }
    $loadedPath = Join-Path $backupDirectory 'in-use-Breeze.Strategies.dll'
    [System.IO.File]::Move($destination, $loadedPath)
    [System.IO.File]::Copy($source, $destination, $false)
}

Unblock-File -LiteralPath $destination
$sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
$destinationHash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
if ($sourceHash -ne $destinationHash) {
    throw 'Installed Breeze.Strategies.dll does not match the build.'
}

@(
    "SourceHash=$sourceHash"
    "DestinationHash=$destinationHash"
    'Match=True'
    "Version=$((Get-Item -LiteralPath $destination).VersionInfo.FileVersion)"
    "BackupDirectory=$backupDirectory"
    'RestartRequired=True'
) | Set-Content -LiteralPath $resultPath -Encoding UTF8
