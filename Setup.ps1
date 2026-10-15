#requires -Version 7.0
[CmdletBinding()]
param(
    [switch]$SkipModel,
    [string]$ModelSourceDirectory,
    [uri]$ModelBaseUri
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$sdk = & dotnet --version
if ($LASTEXITCODE -ne 0 -or $sdk -notmatch '^10\.') { throw 'Install the .NET 10 SDK before building.' }
& (Join-Path $root 'native\Build-Native.ps1')
dotnet publish (Join-Path $root 'src\FuzzyFindAnything.Tools\FuzzyFindAnything.Tools.csproj') `
    -c Release -r win-x64 -p:PlatformTarget=x64 --self-contained true -o (Join-Path $root 'artifacts\tools') --nologo
if ($LASTEXITCODE -ne 0) { throw 'Building the native model/maintenance utilities failed.' }
foreach ($name in @('ffa_diskann.dll', 'libiomp5md.dll')) {
    Copy-Item -LiteralPath (Join-Path $root "artifacts\native\$name") -Destination (Join-Path $root 'artifacts\tools') -Force
}
Get-ChildItem -LiteralPath (Join-Path $root 'artifacts\native\crt\x64') -File -Filter '*.dll' |
    Copy-Item -Destination (Join-Path $root 'artifacts\tools') -Force
if (-not $SkipModel -and ($ModelSourceDirectory -or $ModelBaseUri)) {
    & (Join-Path $root 'Get-Model.ps1') -SourceDirectory $ModelSourceDirectory -BaseUri $ModelBaseUri
}
Write-Host 'Native dependencies and model utilities are ready. Model acquisition is a separate, explicit step.'
