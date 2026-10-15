#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourceDirectory,
    [string]$Destination
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$SourceDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($SourceDirectory)
& (Join-Path $root 'Get-Model.ps1') -Destination $SourceDirectory -VerifyOnly
$manifest = Get-Content -LiteralPath (Join-Path $root 'model-assets\embeddinggemma-2.json') -Raw | ConvertFrom-Json
if (-not $Destination) { $Destination = Join-Path $root 'artifacts\model-bundles' }
$Destination = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Destination)
New-Item -ItemType Directory -Force -Path $Destination | Out-Null
$archive = Join-Path $Destination ($manifest.asset_id + '.zip')
if (Test-Path -LiteralPath $archive) { throw "Model bundle already exists: $archive" }
$temporary = $archive + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
try {
    $zip = [IO.Compression.ZipFile]::Open($temporary, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in $manifest.files) {
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $SourceDirectory $file.name), $file.name, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $root 'model-assets\embeddinggemma-2.json'), 'model-manifest.json') | Out-Null
    } finally { $zip.Dispose() }
    Move-Item -LiteralPath $temporary -Destination $archive
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($archive + '.sha256', "$hash  $([IO.Path]::GetFileName($archive))`n", [Text.Encoding]::ASCII)
    Write-Host "Verified local model bundle: $archive"
    Write-Host 'No files were published or uploaded.'
} finally {
    if (Test-Path -LiteralPath $temporary) { [IO.File]::Delete($temporary) }
}
