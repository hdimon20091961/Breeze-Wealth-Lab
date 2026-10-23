param(
    [string]$WealthLabDirectory = 'C:\Program Files\Quantacula, LLC\WealthLab 8'
)

$ErrorActionPreference = 'Stop'

$source = Join-Path $PSScriptRoot 'Breeze\bin\x64\Release\net8.0-windows\Breeze.dll'
$destination = Join-Path $WealthLabDirectory 'Breeze.dll'
$helpSource = Join-Path $PSScriptRoot 'Breeze\bin\x64\Release\net8.0-windows\BreezeHelp.html'
$helpDestination = Join-Path $WealthLabDirectory 'BreezeHelp.html'
$rulesSource = Join-Path $PSScriptRoot 'Breeze\bin\x64\Release\net8.0-windows\BreezeRules.json'
$rulesDestination = Join-Path $WealthLabDirectory 'BreezeRules.json'
$resultPath = Join-Path $PSScriptRoot 'deployment-result.txt'

try {
    [System.IO.File]::Copy($source, $destination, $true)
    [System.IO.File]::Copy($helpSource, $helpDestination, $true)
    [System.IO.File]::Copy($rulesSource, $rulesDestination, $true)
    Unblock-File -LiteralPath $destination
    Unblock-File -LiteralPath $helpDestination
    Unblock-File -LiteralPath $rulesDestination

    $sourceHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $source).Hash
    $destinationHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $destination).Hash
    $version = (Get-Item -LiteralPath $destination).VersionInfo.FileVersion
    $match = $sourceHash -eq $destinationHash
    $helpSourceHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $helpSource).Hash
    $helpDestinationHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $helpDestination).Hash
    $helpMatch = $helpSourceHash -eq $helpDestinationHash
    $rulesSourceHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $rulesSource).Hash
    $rulesDestinationHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $rulesDestination).Hash
    $rulesMatch = $rulesSourceHash -eq $rulesDestinationHash

    @(
        'File=Breeze.dll'
        "Version=$version"
        "SourceHash=$sourceHash"
        "DestinationHash=$destinationHash"
        "Match=$match"
        'HelpFile=BreezeHelp.html'
        "HelpSourceHash=$helpSourceHash"
        "HelpDestinationHash=$helpDestinationHash"
        "HelpMatch=$helpMatch"
        'RulesFile=BreezeRules.json'
        "RulesSourceHash=$rulesSourceHash"
        "RulesDestinationHash=$rulesDestinationHash"
        "RulesMatch=$rulesMatch"
    ) | Set-Content -LiteralPath $resultPath -Encoding UTF8

    if (-not $match) {
        throw 'Installed Breeze.dll does not match the build.'
    }
    if (-not $helpMatch) {
        throw 'Installed BreezeHelp.html does not match the build.'
    }
    if (-not $rulesMatch) {
        throw 'Installed BreezeRules.json does not match the build.'
    }
}
catch {
    @(
        "Deployment failed: $($_.Exception.Message)"
        $_.ScriptStackTrace
    ) | Set-Content -LiteralPath $resultPath -Encoding UTF8
    exit 1
}
