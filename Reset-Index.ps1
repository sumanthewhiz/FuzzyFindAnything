[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)][string]$DataDirectory,
    [switch]$ClearFolders
)

$ErrorActionPreference = 'Stop'
$tool = Join-Path $PSScriptRoot 'tools\FuzzyFindAnything.Tools.exe'
if (-not (Test-Path -LiteralPath $tool)) { $tool = Join-Path $PSScriptRoot 'artifacts\tools\FuzzyFindAnything.Tools.exe' }
if (-not (Test-Path -LiteralPath $tool)) { throw 'Native maintenance utilities are missing. Run Setup.ps1 or extract the complete release.' }
$DataDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($DataDirectory)
$action = if ($ClearFolders) { 'Clear the index, pending work and configured folder list' } else { 'Rebuild the index, retaining configured folders' }
if ($PSCmdlet.ShouldProcess($DataDirectory, $action)) {
    $arguments = @('reset-index', '--data-dir', $DataDirectory)
    if ($ClearFolders) { $arguments += '--clear-folders' }
    & $tool @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Index maintenance failed. Review the error before restarting the application.' }
}
