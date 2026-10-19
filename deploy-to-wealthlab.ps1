$ErrorActionPreference = 'Stop'

$wealthLabDirectory = 'C:\Program Files\Quantacula, LLC\WealthLab 8'
$resultPath = 'D:\CODEX_PRG\Breadth\deployment-result.txt'
$files = @(@{
    Source = 'D:\CODEX_PRG\Breadth\Breeze\bin\x64\Release\net8.0-windows\Breeze.dll'
    Destination = Join-Path $wealthLabDirectory 'Breeze.dll'
}, @{
    Source = 'D:\CODEX_PRG\Breadth\Breeze.Strategies\bin\Release\net8.0-windows\Breeze.Strategies.dll'
    Destination = Join-Path $wealthLabDirectory 'Breeze.Strategies.dll'
})

try {
    foreach ($file in $files) {
        [System.IO.File]::Copy($file.Source, $file.Destination, $true)
        Unblock-File -LiteralPath $file.Destination
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
    $result | Set-Content -LiteralPath $resultPath -Encoding UTF8
}
catch {
    @(
        "Deployment failed: $($_.Exception.Message)"
        $_.ScriptStackTrace
    ) | Set-Content -LiteralPath $resultPath -Encoding UTF8
    exit 1
}
