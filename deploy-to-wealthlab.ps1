$ErrorActionPreference = 'Stop'

$wealthLabDirectory = 'C:\Program Files\Quantacula, LLC\WealthLab 8'
$files = @(
    @{
        Source = 'D:\CODEX_PRG\Breadth\reference\WL8ExtensionDemos\WL8ExtensionDemos\bin\x64\Release\net8.0-windows7.0\WL8ExtensionDemos.dll'
        Destination = Join-Path $wealthLabDirectory 'WL8ExtensionDemos.dll'
    },
    @{
        Source = 'D:\CODEX_PRG\Breadth\Breeze\bin\x64\Release\net8.0-windows\Breeze.dll'
        Destination = Join-Path $wealthLabDirectory 'Breeze.dll'
    }
)

foreach ($file in $files) {
    Copy-Item -LiteralPath $file.Source -Destination $file.Destination -Force
    Unblock-File -LiteralPath $file.Destination
}

$files | ForEach-Object { Get-Item -LiteralPath $_.Destination } |
    Select-Object Name, Length, LastWriteTime, FullName
