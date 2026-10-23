#requires -Version 7.0
[CmdletBinding()]
param(
    [switch]$SkipSetup,
    [switch]$SkipModel,
    [ValidateSet('win-x64', 'win-arm64')][string]$RuntimeIdentifier,
    [string]$OutputDirectory,
    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z][0-9A-Za-z.-]*)?$')][string]$Version
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not $SkipSetup) {
    & (Join-Path $root 'Setup.ps1') -SkipModel
}
foreach ($name in @('ffa_diskann.dll', 'libiomp5md.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $root "artifacts\native\$name"))) {
        throw 'The native DiskANN build is missing. Run Setup.ps1 before publishing.'
    }
}

$output = if ($OutputDirectory) { $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory) } else { Join-Path $root 'artifacts\FuzzyFindAnything' }
if ($output.TrimEnd('\') -eq [IO.Path]::GetPathRoot($output).TrimEnd('\') -or
    $root.Equals($output.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase) -or
    $root.StartsWith($output.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The publish output must not be a filesystem root or contain the source repository.'
}
$parent = Split-Path $output -Parent
New-Item -ItemType Directory -Force -Path $parent | Out-Null
$staging = Join-Path $parent ('publish-' + [Guid]::NewGuid().ToString('N'))
$previous = Join-Path $parent ('retired-' + [Guid]::NewGuid().ToString('N'))
if (-not $RuntimeIdentifier) {
    $RuntimeIdentifier = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'win-arm64' } else { 'win-x64' }
}
$uiArchitecture = if ($RuntimeIdentifier -eq 'win-arm64') { 'ARM64' } else { 'x64' }
[string[]]$versionArguments = @('-p:DebugType=None', '-p:DebugSymbols=false')
if ($Version) { $versionArguments += "-p:Version=$Version" }
$executable = Join-Path $output 'FuzzyFindAnything.exe'
if (Get-CimInstance Win32_Process -Filter "Name='FuzzyFindAnything.exe'" | Where-Object { $_.ExecutablePath -eq $executable }) {
    throw 'The output application is running. Quit it or choose a separate -OutputDirectory.'
}

try {
    # A fresh directory prevents PreserveNewest from mixing x64 and ARM64 runtime binaries.
    dotnet publish (Join-Path $root 'src\FuzzyFindAnything.App\FuzzyFindAnything.App.csproj') -c Release -r $RuntimeIdentifier "-p:PlatformTarget=$uiArchitecture" @versionArguments --self-contained true -o $staging --nologo
    if ($LASTEXITCODE -ne 0) { throw 'The WPF publish failed.' }
    dotnet publish (Join-Path $root 'src\FuzzyFindAnything.Extractor\FuzzyFindAnything.Extractor.csproj') -c Release -r win-x64 @versionArguments --self-contained true -o (Join-Path $staging 'extractor') --nologo
    if ($LASTEXITCODE -ne 0) { throw 'The IFilter extractor publish failed.' }
    dotnet publish (Join-Path $root 'src\FuzzyFindAnything.Engine\FuzzyFindAnything.Engine.csproj') -c Release -r win-x64 -p:PlatformTarget=x64 @versionArguments --self-contained true -o (Join-Path $staging 'engine') --nologo
    if ($LASTEXITCODE -ne 0) { throw 'The native coordinator publish failed.' }
    dotnet publish (Join-Path $root 'src\FuzzyFindAnything.Inference\FuzzyFindAnything.Inference.csproj') -c Release -r $RuntimeIdentifier "-p:PlatformTarget=$uiArchitecture" @versionArguments --self-contained true -o (Join-Path $staging 'inference') --nologo
    if ($LASTEXITCODE -ne 0) { throw 'The native inference host publish failed.' }
    dotnet publish (Join-Path $root 'src\FuzzyFindAnything.HuggingFaceInference\FuzzyFindAnything.HuggingFaceInference.csproj') -c Release -r $RuntimeIdentifier "-p:PlatformTarget=$uiArchitecture" @versionArguments --self-contained true -o (Join-Path $staging 'hf-inference') --nologo
    if ($LASTEXITCODE -ne 0) { throw 'The compatible Hugging Face inference host publish failed.' }
    $grammars = @('bash','c','c-sharp','cpp','go','java','javascript','php','python','ruby','rust','tsx','typescript') |
        ForEach-Object { "tree-sitter-$_.dll" }
    foreach ($component in @('inference','hf-inference')) {
        Get-ChildItem -LiteralPath (Join-Path $staging $component) -File -Filter 'tree-sitter-*.dll' |
            Where-Object Name -NotIn $grammars | ForEach-Object { [IO.File]::Delete($_.FullName) }
    }
    dotnet publish (Join-Path $root 'src\FuzzyFindAnything.Tools\FuzzyFindAnything.Tools.csproj') -c Release -r win-x64 -p:PlatformTarget=x64 @versionArguments --self-contained true -o (Join-Path $staging 'tools') --nologo
    if ($LASTEXITCODE -ne 0) { throw 'The model/maintenance tools publish failed.' }
    foreach ($directory in @('engine', 'tools')) {
        foreach ($name in @('ffa_diskann.dll', 'libiomp5md.dll')) {
            Copy-Item -LiteralPath (Join-Path $root "artifacts\native\$name") -Destination (Join-Path $staging $directory)
        }
    }
    foreach ($component in @(
        @{ Directory = ''; Architecture = $uiArchitecture.ToLowerInvariant() },
        @{ Directory = 'inference'; Architecture = $uiArchitecture.ToLowerInvariant() },
        @{ Directory = 'hf-inference'; Architecture = $uiArchitecture.ToLowerInvariant() },
        @{ Directory = 'engine'; Architecture = 'x64' },
        @{ Directory = 'extractor'; Architecture = 'x64' },
        @{ Directory = 'tools'; Architecture = 'x64' }
    )) {
        $crt = Join-Path $root "artifacts\native\crt\$($component.Architecture)"
        if (-not (Test-Path -LiteralPath (Join-Path $crt 'msvcp140.dll'))) {
            throw "The $($component.Architecture) app-local CRT is missing. Install the matching MSVC tools and rerun Setup.ps1."
        }
        Get-ChildItem -LiteralPath $crt -File -Filter '*.dll' |
            Copy-Item -Destination (Join-Path $staging $component.Directory) -Force
    }

    @{
        engine = 'engine\FuzzyFindAnything.Engine.exe'
        inference = 'inference\FuzzyFindAnything.Inference.exe'
        model = (Join-Path $root 'models\embeddinggemma-2-onnx')
        extractor = 'extractor\FuzzyFindAnything.Extractor.exe'
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $staging 'runtime.json') -Encoding utf8

    if (Test-Path $output) {
        [IO.Directory]::Move($output, $previous)
    }
    try {
        [IO.Directory]::Move($staging, $output)
    } catch {
        if ((Test-Path $previous) -and -not (Test-Path $output)) {
            [IO.Directory]::Move($previous, $output)
        }
        throw
    }
    if (Test-Path $previous) {
        try {
            [IO.Directory]::Delete($previous, $true)
        } catch [IO.IOException], [UnauthorizedAccessException] {
            Write-Warning "The new build is published, but the retired output is still in use and was retained at $previous. $($_.Exception.Message)"
        }
    }
} finally {
    if (Test-Path $staging) {
        [IO.Directory]::Delete($staging, $true)
    }
}

Write-Host "`nBuilt: $(Join-Path $output 'FuzzyFindAnything.exe')"
