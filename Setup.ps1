[CmdletBinding()]
param(
    [switch]$SkipModel,
    [string]$PackageIndex
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$runtime = Join-Path $root '.runtime'
$python = Join-Path $runtime 'python.exe'
$downloads = Join-Path $root '.downloads'

if (-not (Test-Path $python)) {
    New-Item -ItemType Directory -Force -Path $runtime, $downloads | Out-Null
    $archive = Join-Path $downloads 'python-3.11.9-embed-amd64.zip'
    if (-not (Test-Path $archive)) {
        Write-Host 'Downloading isolated CPython 3.11 x64 runtime...'
        Invoke-WebRequest 'https://www.python.org/ftp/python/3.11.9/python-3.11.9-embed-amd64.zip' -OutFile $archive
    }
    Expand-Archive -LiteralPath $archive -DestinationPath $runtime -Force
    $signature = Get-AuthenticodeSignature $python
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Python Software Foundation') {
        throw "Python signature validation failed: $($signature.Status)"
    }
}

# This is an isolated embedded distribution, never the user's global environment.
@('python311.zip', '.', 'Lib\site-packages', 'import site') |
    Set-Content -LiteralPath (Join-Path $runtime 'python311._pth') -Encoding ascii

$bootstrap = Get-Command python -ErrorAction Stop
$previousPipConfig = $env:PIP_CONFIG_FILE
try {
    [string[]]$indexArguments = @()
    if ($PackageIndex) {
        $env:PIP_CONFIG_FILE = 'NUL'
        $indexArguments = @('--index-url', $PackageIndex)
    }
    # Embedded Python ignores the temporary PYTHONPATH used by PEP 517 build isolation.
    & $bootstrap.Source -m pip --python $python install @indexArguments --disable-pip-version-check --no-warn-script-location -r (Join-Path $root 'engine\build-requirements.txt')
    if ($LASTEXITCODE -ne 0) { throw 'Installing the private build backend failed.' }
    & $bootstrap.Source -m pip --python $python install @indexArguments --no-build-isolation --disable-pip-version-check --no-warn-script-location -r (Join-Path $root 'engine\requirements.txt')
    $installExitCode = $LASTEXITCODE
} finally {
    $env:PIP_CONFIG_FILE = $previousPipConfig
}
if ($installExitCode -ne 0) { throw 'Installing the local engine dependencies failed.' }
& $python -c 'import sys, platform, numpy, torch; print("Runtime:", sys.version.split()[0], platform.machine()); print("Torch:", torch.__version__, "NumPy:", numpy.__version__)'
if ($LASTEXITCODE -ne 0) { throw 'The native inference libraries could not be loaded.' }
& $python -c 'import diskannpy; print("DiskANN loaded")'
if ($LASTEXITCODE -ne 0) { throw 'The native inference/index libraries could not be loaded.' }

if (-not $SkipModel) {
    & (Join-Path $root 'Get-Model.ps1')
}
