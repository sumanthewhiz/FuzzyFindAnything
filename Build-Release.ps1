[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('win-x64', 'win-arm64')][string]$RuntimeIdentifier,
    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z][0-9A-Za-z.-]*)?$')][string]$Version,
    [switch]$SkipSetup,
    [string]$PackageIndex,
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
if (Test-Path -LiteralPath $archive) {
    throw "Release archive already exists: $archive. Choose a new version or destination."
}
if (-not $SkipSetup) {
    & (Join-Path $root 'Setup.ps1') -SkipModel -PackageIndex $PackageIndex
}
$python = Join-Path $root '.runtime\python.exe'
if (-not (Test-Path -LiteralPath $python)) { throw 'The private runtime is missing. Run Setup.ps1 -SkipModel.' }
$working = Join-Path $Destination ('package-' + [Guid]::NewGuid().ToString('N'))
$payload = Join-Path $working 'FuzzyFindAnything'
$temporaryArchive = Join-Path $Destination ([IO.Path]::GetFileName($archive) + '.' + [Guid]::NewGuid().ToString('N') + '.tmp')
try {
    & (Join-Path $root 'Build.ps1') -SkipSetup -RuntimeIdentifier $RuntimeIdentifier -Version $Version -OutputDirectory $payload
    & robocopy (Join-Path $root '.runtime') (Join-Path $payload '.runtime') /E /R:1 /W:1 /NFL /NDL /NJH /NJS /NP /XD __pycache__ (Join-Path $root '.runtime\Scripts') /XF *.pyc *.pyo *.pdb | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Copying the private runtime failed: robocopy exit $LASTEXITCODE." }
    Copy-Item -LiteralPath (Join-Path $root 'Get-Model.ps1'), (Join-Path $root 'QUICKSTART.md') -Destination $payload
    foreach ($name in @('LICENSE', 'LICENSE.txt', 'LICENSE.md')) {
        $license = Join-Path $root $name
        if (Test-Path -LiteralPath $license -PathType Leaf) {
            Copy-Item -LiteralPath $license -Destination (Join-Path $payload ('APPLICATION-' + $name))
        }
    }
    @{
        python = '.runtime\python.exe'
        model = 'models\embeddinggemma-2'
        extractor = 'extractor\FuzzyFindAnything.Extractor.exe'
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $payload 'runtime.json') -Encoding utf8
    & $python (Join-Path $root 'tools\release_metadata.py') --package $payload --runtime-id $RuntimeIdentifier --version $Version
    if ($LASTEXITCODE -ne 0) { throw 'Release metadata generation failed.' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($payload, $temporaryArchive, [IO.Compression.CompressionLevel]::Optimal, $false)
    & $python (Join-Path $root 'tools\verify_release.py') --archive $temporaryArchive --runtime-id $RuntimeIdentifier --version $Version
    if ($LASTEXITCODE -ne 0) { throw 'The release archive failed validation.' }
    Move-Item -LiteralPath $temporaryArchive -Destination $archive
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($archive + '.sha256', "$hash  $([IO.Path]::GetFileName($archive))`n", [Text.Encoding]::ASCII)
    Write-Host "Release: $archive"
    Write-Host "SHA256:  $hash"
} finally {
    if (Test-Path -LiteralPath $temporaryArchive) { [IO.File]::Delete($temporaryArchive) }
    if (Test-Path -LiteralPath $working) { [IO.Directory]::Delete($working, $true) }
}
