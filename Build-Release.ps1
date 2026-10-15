#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('win-x64', 'win-arm64')][string]$RuntimeIdentifier,
    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z][0-9A-Za-z.-]*)?$')][string]$Version,
    [switch]$SkipSetup,
    [string]$Destination
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not $Version) {
    [xml]$properties = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw
    $Version = [string]$properties.Project.PropertyGroup.Version
}
if (-not $Destination) { $Destination = Join-Path $root 'artifacts\releases' }
$Destination = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Destination)
New-Item -ItemType Directory -Force -Path $Destination | Out-Null
$archive = Join-Path $Destination "FuzzyFindAnything-v$Version-$RuntimeIdentifier.zip"
if (Test-Path -LiteralPath $archive) { throw "Release archive already exists: $archive. Choose another version or destination." }
if (-not $SkipSetup) { & (Join-Path $root 'Setup.ps1') -SkipModel }
$working = Join-Path $Destination ('package-' + [Guid]::NewGuid().ToString('N'))
$payload = Join-Path $working 'FuzzyFindAnything'
$temporaryArchive = $archive + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
try {
    & (Join-Path $root 'Build.ps1') -SkipSetup -RuntimeIdentifier $RuntimeIdentifier -Version $Version -OutputDirectory $payload
    foreach ($name in @('Get-Model.ps1', 'Reset-Index.ps1', 'QUICKSTART.md', 'README.md')) {
        Copy-Item -LiteralPath (Join-Path $root $name) -Destination $payload
    }
    Copy-Item -LiteralPath (Join-Path $root 'docs') -Destination (Join-Path $payload 'docs') -Recurse
    Copy-Item -LiteralPath (Join-Path $root 'model-assets\embeddinggemma-2.json') -Destination (Join-Path $payload 'model-manifest.json')
    foreach ($name in @('LICENSE', 'LICENSE.txt', 'LICENSE.md')) {
        if (Test-Path -LiteralPath (Join-Path $root $name)) {
            Copy-Item -LiteralPath (Join-Path $root $name) -Destination (Join-Path $payload ('APPLICATION-' + $name))
        }
    }
    @{
        engine='engine\FuzzyFindAnything.Engine.exe';inference='inference\FuzzyFindAnything.Inference.exe'
        model='models\embeddinggemma-2-onnx';extractor='extractor\FuzzyFindAnything.Extractor.exe'
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $payload 'runtime.json') -Encoding utf8
    & (Join-Path $root 'tools\Write-ReleaseMetadata.ps1') -Package $payload -RuntimeIdentifier $RuntimeIdentifier -Version $Version
    [IO.Compression.ZipFile]::CreateFromDirectory($payload, $temporaryArchive, [IO.Compression.CompressionLevel]::Optimal, $false)
    & (Join-Path $payload 'tools\FuzzyFindAnything.Tools.exe') verify-release --archive $temporaryArchive --runtime-id $RuntimeIdentifier --version $Version --smoke
    if ($LASTEXITCODE -ne 0) { throw 'Native release verification failed.' }
    Move-Item -LiteralPath $temporaryArchive -Destination $archive
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($archive + '.sha256', "$hash  $([IO.Path]::GetFileName($archive))`n", [Text.Encoding]::ASCII)
    Write-Host "Release: $archive"
    Write-Host "SHA256:  $hash"
} finally {
    if (Test-Path -LiteralPath $temporaryArchive) { [IO.File]::Delete($temporaryArchive) }
    for ($attempt = 0; Test-Path -LiteralPath $working; $attempt++) {
        try { [IO.Directory]::Delete($working, $true) }
        catch [IO.IOException], [UnauthorizedAccessException] {
            if ($attempt -ge 60) { throw "Release staging remains locked at $working. $($_.Exception.Message)" }
            Start-Sleep -Milliseconds 500
        }
    }
}
