$ErrorActionPreference = 'Stop'

$source = 'D:\CODEX_PRG\Breadth\Breeze\bin\x64\Release\net8.0-windows\BreezeHelp.html'
$destinationDirectory = 'C:\Program Files\Quantacula, LLC\WealthLab 8'
$destination = Join-Path $destinationDirectory 'BreezeHelp.html'

if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
    throw "Source Help was not found: $source"
}
if (-not (Test-Path -LiteralPath $destinationDirectory -PathType Container)) {
    throw "Wealth-Lab directory was not found: $destinationDirectory"
}

$backup = $null
if (Test-Path -LiteralPath $destination -PathType Leaf) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $backup = Join-Path $destinationDirectory "BreezeHelp.before-$stamp.html"
    Copy-Item -LiteralPath $destination -Destination $backup -Force
}

Copy-Item -LiteralPath $source -Destination $destination -Force

$sourceHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $source).Hash
$destinationHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $destination).Hash
if ($sourceHash -ne $destinationHash) {
    throw "SHA256 mismatch after copying Help. Source=$sourceHash Destination=$destinationHash"
}

Write-Output "INSTALLED=$destination"
Write-Output "BACKUP=$backup"
Write-Output "SHA256=$destinationHash"
