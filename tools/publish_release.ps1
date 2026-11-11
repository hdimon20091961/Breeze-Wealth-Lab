param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
[xml]$project = Get-Content -LiteralPath (Join-Path $root 'Breeze\Breeze.csproj') -Raw
$version = [string]$project.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Invalid release version: $version" }
$archive = Join-Path $root "versions\$version"
if (Test-Path -LiteralPath $archive) { throw "Historical version already exists; refusing to overwrite: $archive" }
$release = Join-Path $root 'release'
$sources = [ordered]@{
    'Breeze.dll' = Join-Path $root 'Breeze\bin\x64\Release\net8.0-windows\Breeze.dll'
    'Breeze.Strategies.dll' = Join-Path $root 'Breeze.Strategies\bin\Release\net8.0-windows\Breeze.Strategies.dll'
    'BreezeHelp.html' = Join-Path $root 'Breeze\bin\x64\Release\net8.0-windows\BreezeHelp.html'
    'BreezeRules.json' = Join-Path $root 'Breeze\bin\x64\Release\net8.0-windows\BreezeRules.json'
    'BreezeEventCatalog.json' = Join-Path $root 'Breeze\bin\x64\Release\net8.0-windows\BreezeEventCatalog.json'
    'Indicators.JohnEhlers_8Wealth.dll' = Join-Path $root 'release\Indicators.JohnEhlers_8Wealth.dll'
    'Проторговка Torch Sharp_Rules.xml' = Join-Path $root 'release\Проторговка Torch Sharp_Rules.xml'
}
foreach ($name in $sources.Keys) {
    if (-not (Test-Path -LiteralPath $sources[$name] -PathType Leaf)) { throw "Missing build output: $name" }
    if ($name -in @('Breeze.dll', 'Breeze.Strategies.dll')) {
        $actual = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($sources[$name]).FileVersion
        if ($actual -ne "$version.0") { throw "Build version mismatch: $name $actual / $version" }
    }
}
foreach ($name in @('BreezeRules.json', 'BreezeEventCatalog.json')) {
    if ((Get-FileHash -LiteralPath $sources[$name]).Hash -ne
        (Get-FileHash -LiteralPath (Join-Path $root "Breeze\$name")).Hash) {
        throw "Build copy is stale: $name"
    }
}
$catalogue = Get-Content -LiteralPath $sources['BreezeEventCatalog.json'] -Raw | ConvertFrom-Json
foreach ($source in $catalogue.sources) {
    if ((Get-FileHash -LiteralPath (Join-Path $root $source.path)).Hash.ToLowerInvariant() -ne $source.sha256) {
        throw "Catalogue source changed: $($source.path). Regenerate the catalogue and rebuild."
    }
}
$readme = Join-Path $release 'README.md'
if ((Get-Content -LiteralPath $readme -First 1) -notin @("# Breeze $version", "# Market Breeze $version")) { throw 'Release README version is stale.' }
[System.IO.Directory]::CreateDirectory($archive) | Out-Null
$checksums = foreach ($name in $sources.Keys) {
    $target = Join-Path $archive $name
    [System.IO.File]::Copy($sources[$name], $target, $false)
    $hash = (Get-FileHash -LiteralPath $sources[$name]).Hash
    if ((Get-FileHash -LiteralPath $target).Hash -ne $hash) { throw "Archive verification failed: $name" }
    "$($hash.ToLowerInvariant())  $name"
}
[System.IO.File]::Copy($readme, (Join-Path $archive 'README.md'), $false)
$checksums | Set-Content -LiteralPath (Join-Path $archive 'SHA256SUMS.txt') -Encoding utf8
foreach ($name in @($sources.Keys) + @('SHA256SUMS.txt')) {
    [System.IO.File]::Copy((Join-Path $archive $name), (Join-Path $release $name), $true)
    if ((Get-FileHash -LiteralPath (Join-Path $archive $name)).Hash -ne
        (Get-FileHash -LiteralPath (Join-Path $release $name)).Hash) { throw "Release verification failed: $name" }
}
Write-Output "Published ${version}: complete runtime and research package, verified archive $archive"
