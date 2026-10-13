[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$python = Join-Path $root '.runtime\python.exe'
$output = Join-Path $root 'artifacts\checks.log'
New-Item -ItemType Directory -Force -Path (Split-Path $output -Parent) | Out-Null
$previousPreference = $ErrorActionPreference
try {
    $ErrorActionPreference = 'Continue'
    @'
from pathlib import Path
import sys
import unittest
sys.path.insert(0, str(Path(sys.argv.pop(1)) / "tests"))
unittest.main(module=None, argv=[
    "unittest", "test_engine", "test_cpu_pool", "test_logging", "test_model_acquisition",
    "test_ifilters.ExtractionLifecycleTests", "test_release", "-v"
])
'@ | & $python - $root *> $output
    $result = $LASTEXITCODE
} finally {
    $ErrorActionPreference = $previousPreference
}
Get-Content -LiteralPath $output -Tail 70
if ($result -ne 0) { throw "Regression checks failed. Full output: $output" }
