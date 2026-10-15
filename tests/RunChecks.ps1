#requires -Version 7.0
[CmdletBinding()]
param(
    [switch]$WithModel,
    [switch]$Integration,
    [string]$ModelDirectory,
    [string]$ApplicationDirectory
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$tests = Join-Path $root 'tests\FuzzyFindAnything.NativeTests\FuzzyFindAnything.NativeTests.csproj'
$headless = Join-Path $root 'tests\FuzzyFindAnything.HeadlessTests\FuzzyFindAnything.HeadlessTests.csproj'
if (-not (Test-Path -LiteralPath (Join-Path $root 'artifacts\native\ffa_diskann.dll'))) {
    throw 'Native test dependencies are missing. Run Setup.ps1 -SkipModel.'
}
dotnet run --project $tests -c Release -r win-x64 -p:PlatformTarget=x64 -p:SelfContained=true -- core
if ($LASTEXITCODE -ne 0) { throw 'Native engine regression checks failed.' }
$runtime = if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'win-arm64' } else { 'win-x64' }
$platform = if ($runtime -eq 'win-arm64') { 'ARM64' } else { 'x64' }
dotnet run --project $headless -c Release -r $runtime "-p:PlatformTarget=$platform" -p:SelfContained=true -- $root
if ($LASTEXITCODE -ne 0) { throw 'Headless UI/settings checks failed.' }
if ($WithModel -or $Integration) {
    if (-not $ModelDirectory) { $ModelDirectory = Join-Path $root 'models\embeddinggemma-2-onnx' }
    $output = Join-Path $root ".test-data\acceptance-$runtime"
    dotnet run --project $tests -c Release -r $runtime "-p:PlatformTarget=$platform" -p:SelfContained=true -- `
        product-parity $ModelDirectory (Join-Path $root 'tests\Fixtures') $output
    if ($LASTEXITCODE -ne 0) { throw 'Native model parity checks failed.' }
}
if ($Integration) {
    if (-not $ApplicationDirectory) { $ApplicationDirectory = Join-Path $root 'artifacts\FuzzyFindAnything' }
    & (Join-Path $root 'tests\FuzzyFindAnything.NativeTests\bin\Release\net10.0-windows\win-x64\FuzzyFindAnything.NativeTests.exe') `
        integration $ApplicationDirectory $ModelDirectory (Join-Path $root ".test-data\integration-$runtime")
    if ($LASTEXITCODE -ne 0) { throw 'Packaged engine integration checks failed.' }
}
Write-Host 'Requested native checks passed. The production application and data were not used.'
