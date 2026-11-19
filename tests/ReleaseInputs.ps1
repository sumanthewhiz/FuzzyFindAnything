#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('ffa-release-inputs-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary | Out-Null
$archive = Join-Path $temporary 'FuzzyFindAnything-v2.0.0-win-x64.zip'
[IO.File]::WriteAllText($archive, 'existing archive sentinel')
$original = (Get-FileHash -LiteralPath $archive).Hash
try {
    foreach ($case in @(
        @{ Name = 'absent'; Arguments = @{}; Valid = $true },
        @{ Name = 'empty'; Arguments = @{ ModelBaseUri = [uri]'' }; Valid = $true },
        @{ Name = 'whitespace'; Arguments = @{ ModelBaseUri = [uri]'   ' }; Valid = $true },
        @{ Name = 'https'; Arguments = @{ ModelBaseUri = [uri]'https://example.org/models' }; Valid = $true },
        @{ Name = 'relative'; Arguments = @{ ModelBaseUri = [uri]'models' }; Valid = $false },
        @{ Name = 'http'; Arguments = @{ ModelBaseUri = [uri]'http://example.org/models' }; Valid = $false },
        @{ Name = 'credentials'; Arguments = @{ ModelBaseUri = [uri]'https://name:password@example.org/models' }; Valid = $false },
        @{ Name = 'query'; Arguments = @{ ModelBaseUri = [uri]'https://example.org/models?token=fixture' }; Valid = $false },
        @{ Name = 'fragment'; Arguments = @{ ModelBaseUri = [uri]'https://example.org/models#fragment' }; Valid = $false }
    )) {
        $arguments = $case.Arguments
        $failure = $null
        try { & (Join-Path $root 'Build-Release.ps1') -RuntimeIdentifier win-x64 -Version 2.0.0 -SkipSetup -Destination $temporary @arguments }
        catch { $failure = $_.Exception.Message }
        $expected = if ($case.Valid) { 'Release archive already exists:' } else { 'An optional packaged model source must be HTTPS' }
        if (-not $failure -or -not $failure.StartsWith($expected)) { throw "Release source case '$($case.Name)' failed: $failure" }
        if ((Get-FileHash -LiteralPath $archive).Hash -ne $original) { throw 'Input validation overwrote an existing archive.' }
        Write-Host "PASS release entry point: $($case.Name)"
    }
} finally {
    [IO.File]::Delete($archive)
    [IO.Directory]::Delete($temporary)
}
