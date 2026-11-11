param(
    [Parameter(Mandatory = $true)]
    [string] $SourcePath,

    [Parameter(Mandatory = $true)]
    [string] $TemplatePath,

    [Parameter(Mandatory = $true)]
    [string] $OutputPath
)

$ErrorActionPreference = 'Stop'

$sourceFullPath = [System.IO.Path]::GetFullPath($SourcePath)
$templateFullPath = [System.IO.Path]::GetFullPath($TemplatePath)
$outputFullPath = [System.IO.Path]::GetFullPath($OutputPath)

if (-not [System.IO.File]::Exists($sourceFullPath)) {
    throw "Strategy source not found: $sourceFullPath"
}

if (-not [System.IO.File]::Exists($templateFullPath)) {
    throw "Wealth-Lab strategy template not found: $templateFullPath"
}

[System.Xml.Linq.XDocument] $document = [System.Xml.Linq.XDocument]::Load(
    $templateFullPath,
    [System.Xml.Linq.LoadOptions]::PreserveWhitespace)

$root = $document.Root
if ($null -eq $root -or $root.Name.LocalName -ne 'Strategy') {
    throw 'The template is not a Wealth-Lab Strategy XML document.'
}

$name = $root.Element('Name')
$strategyData = $root.Element('StrategyData')
$parameterValues = $root.Element('ParameterValues')
if ($null -eq $name -or $null -eq $strategyData -or $null -eq $parameterValues) {
    throw 'The template does not contain Name, StrategyData, or ParameterValues.'
}

$name.Value = 'Проторговка Torch Sharp_Rules'
$root.Element('Description').Value =
    'Копия NL Futures без MyRSI-управления. Панель показывает Rules 1 и Rules 2 по точным торговым датам SPY.'
$root.Element('CreationDate').Value = [DateTimeOffset]::Now.ToString('o')
$strategyData.Value = [System.IO.File]::ReadAllText($sourceFullPath, [System.Text.Encoding]::UTF8)

$parameterValues.RemoveNodes()
foreach ($value in @(1, 50, 1, 0, 0, 0, 5)) {
    $parameterValues.Add([System.Xml.Linq.XElement]::new('double', $value))
}

$root.Element('Benchmark').Value = 'SPY'
$root.Element('SingleSymbol').Value = 'SPY'
$root.Element('IsLinkedToExternalFile').Value = 'false'
$root.Element('LinkedFileName').Value = ''
$root.Element('CompiledStrategyTypeName').Value = ''

$outputDirectory = [System.IO.Path]::GetDirectoryName($outputFullPath)
if (-not [System.IO.Directory]::Exists($outputDirectory)) {
    [System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
}

$settings = [System.Xml.XmlWriterSettings]::new()
$settings.Encoding = [System.Text.UTF8Encoding]::new($false)
$settings.Indent = $true
$settings.NewLineChars = "`r`n"
$settings.NewLineHandling = [System.Xml.NewLineHandling]::Replace

$writer = [System.Xml.XmlWriter]::Create($outputFullPath, $settings)
try {
    $document.Save($writer)
}
finally {
    $writer.Dispose()
}

Write-Output $outputFullPath
