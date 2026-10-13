[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$Destination,
    [switch]$VerifyOnly
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$python = Join-Path $root '.runtime\python.exe'
if (-not (Test-Path -LiteralPath $python -PathType Leaf)) {
    if (Test-Path -LiteralPath (Join-Path $root 'Setup.ps1')) {
        throw 'The isolated Python runtime is missing. Run .\Setup.ps1 -SkipModel first.'
    }
    throw 'The bundled runtime is missing. Extract the complete application release, including its .runtime directory.'
}

if (-not $Destination) {
    $Destination = Join-Path $root 'models\embeddinggemma-2'
}
$Destination = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Destination)
$downloader = Join-Path $root 'engine\download_model.py'

if ($VerifyOnly) {
    & $python $downloader --destination $Destination --verify-only
    if ($LASTEXITCODE -ne 0) { throw 'Local model readiness checks failed.' }
    return
}

if ($PSCmdlet.ShouldProcess($Destination, 'Acquire the pinned EmbeddingGemma 2 model from Hugging Face')) {
    & $python $downloader --destination $Destination
    if ($LASTEXITCODE -ne 0) {
        throw 'Model acquisition failed. Re-run Get-Model.ps1 to resume the download.'
    }
}
