[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$Destination,
    [string]$SourceDirectory,
    [uri]$BaseUri,
    [switch]$VerifyOnly
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$tool = Join-Path $root 'tools\FuzzyFindAnything.Tools.exe'
if (-not (Test-Path -LiteralPath $tool)) { $tool = Join-Path $root 'artifacts\tools\FuzzyFindAnything.Tools.exe' }
if (-not (Test-Path -LiteralPath $tool)) {
    throw 'Native model utilities are missing. Extract the complete release, or run Setup.ps1 -SkipModel for a source checkout.'
}
if (-not $Destination) { $Destination = Join-Path $root 'models\embeddinggemma-2-onnx' }
$Destination = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Destination)
if ($VerifyOnly) {
    & $tool verify-model --destination $Destination
    if ($LASTEXITCODE -ne 0) { throw 'Native model verification failed. No files were modified.' }
    return
}
if ([bool]$SourceDirectory -eq [bool]$BaseUri) {
    throw 'Specify exactly one -SourceDirectory containing the approved ONNX bundle or -BaseUri for its approved HTTPS directory. See QUICKSTART.md.'
}
if ($PSCmdlet.ShouldProcess($Destination, 'Acquire and verify the pinned native EmbeddingGemma 2 assets')) {
    $arguments = @('acquire-model', '--destination', $Destination)
    if ($SourceDirectory) {
        $arguments += @('--source-directory', $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($SourceDirectory))
    } else { $arguments += @('--base-uri', $BaseUri.AbsoluteUri) }
    & $tool @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Model acquisition failed. Resolve the reported error and rerun this command to resume.' }
}
