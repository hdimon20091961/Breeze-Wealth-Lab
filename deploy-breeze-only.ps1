param(
    [string]$WealthLabDirectory = 'C:\Program Files\Quantacula, LLC\WealthLab 8',
    [string]$LayoutDirectory = (Join-Path $env:APPDATA 'WealthLab8')
)

# Keep one deployment implementation, including preflight, backup and hashes.
& (Join-Path $PSScriptRoot 'deploy-to-wealthlab.ps1') -WealthLabDirectory $WealthLabDirectory -LayoutDirectory $LayoutDirectory -BreezeOnly
