$ErrorActionPreference = 'Stop'

$source = 'D:\CODEX_PRG\Breadth\Breeze\bin\Release\net8.0-windows\Breeze.dll'
$destination = 'C:\Program Files\Quantacula, LLC\WealthLab 8\Breeze.dll'
$helpSource = 'D:\CODEX_PRG\Breadth\Breeze\bin\Release\net8.0-windows\BreezeHelp.html'
$helpDestination = 'C:\Program Files\Quantacula, LLC\WealthLab 8\BreezeHelp.html'
$resultPath = 'D:\CODEX_PRG\Breadth\deployment-result.txt'

try {
    [System.IO.File]::Copy($source, $destination, $true)
    [System.IO.File]::Copy($helpSource, $helpDestination, $true)
    Unblock-File -LiteralPath $destination
    Unblock-File -LiteralPath $helpDestination

    $sourceHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $source).Hash
    $destinationHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $destination).Hash
    $version = (Get-Item -LiteralPath $destination).VersionInfo.FileVersion
    $match = $sourceHash -eq $destinationHash
    $helpSourceHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $helpSource).Hash
    $helpDestinationHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $helpDestination).Hash
    $helpMatch = $helpSourceHash -eq $helpDestinationHash

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
    ) | Set-Content -LiteralPath $resultPath -Encoding UTF8

    if (-not $match) {
        throw 'Installed Breeze.dll does not match the build.'
    }
    if (-not $helpMatch) {
        throw 'Installed BreezeHelp.html does not match the build.'
    }
}
catch {
    @(
        "Deployment failed: $($_.Exception.Message)"
        $_.ScriptStackTrace
    ) | Set-Content -LiteralPath $resultPath -Encoding UTF8
    exit 1
}
