[CmdletBinding()]
param(
    [switch]$SkipSetup,
    [switch]$SkipModel,
    [string]$PackageIndex,
    [ValidateSet('win-x64', 'win-arm64')][string]$RuntimeIdentifier,
    [string]$OutputDirectory,
    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z][0-9A-Za-z.-]*)?$')][string]$Version
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not $SkipSetup) {
    & (Join-Path $root 'Setup.ps1') -SkipModel:$SkipModel -PackageIndex $PackageIndex
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
[string[]]$versionArguments = @()
if ($Version) { $versionArguments = @("-p:Version=$Version") }
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

    New-Item -ItemType Directory -Force -Path (Join-Path $staging 'engine') | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $root 'engine') -File | Where-Object { $_.Extension -eq '.py' -or $_.Name -eq 'requirements.txt' } |
        Copy-Item -Destination (Join-Path $staging 'engine') -Force

    # Keep the multi-gigabyte runtime/model in one place beside the project, not in every build.
    @{
        python = (Join-Path $root '.runtime\python.exe')
        model = (Join-Path $root 'models\embeddinggemma-2')
        extractor = (Join-Path $output 'extractor\FuzzyFindAnything.Extractor.exe')
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $staging 'runtime.json') -Encoding utf8

    if (Test-Path $output) {
        Move-Item -LiteralPath $output -Destination $previous
    }
    try {
        Move-Item -LiteralPath $staging -Destination $output
    } catch {
        if ((Test-Path $previous) -and -not (Test-Path $output)) {
            Move-Item -LiteralPath $previous -Destination $output
        }
        throw
    }
    if (Test-Path $previous) {
        [IO.Directory]::Delete($previous, $true)
    }
} finally {
    if (Test-Path $staging) {
        [IO.Directory]::Delete($staging, $true)
    }
}

Write-Host "`nBuilt: $(Join-Path $output 'FuzzyFindAnything.exe')"
