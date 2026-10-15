#requires -Version 7.0
[CmdletBinding()]
param([ValidateSet('win-x64', 'win-arm64')][string]$RuntimeIdentifier = 'win-x64')

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('ffa-publish-' + [Guid]::NewGuid().ToString('N'))
$output = Join-Path $temporary 'current'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$sentinel = Join-Path $output 'existing-deployment.txt'
[IO.File]::WriteAllText($sentinel, 'Keep the existing deployment intact on failure.')
Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class FfaPublishLock {
    [DllImport("kernel32.dll", EntryPoint="CreateFileW", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);
    public static SafeFileHandle Open(string path) {
        var handle=CreateFile(path,1,1,IntPtr.Zero,3,0x02000000,IntPtr.Zero);
        if(handle.IsInvalid) { int error=Marshal.GetLastWin32Error();handle.Dispose();throw new Win32Exception(error); }
        return handle;
    }
}
'@
$handle = $null
try {
    $handle = [FfaPublishLock]::Open($output)
    $rejected = $false
    try { & (Join-Path $root 'Build.ps1') -SkipSetup -RuntimeIdentifier $RuntimeIdentifier -OutputDirectory $output }
    catch { $rejected = $true; Write-Host "Expected locked-output refusal: $($_.Exception.Message)" }
    if (-not $rejected -or -not (Test-Path -LiteralPath $sentinel) -or
        [IO.File]::ReadAllText($sentinel) -cne 'Keep the existing deployment intact on failure.' -or
        (Test-Path -LiteralPath (Join-Path $output 'FuzzyFindAnything.exe'))) {
        throw 'Locked publication altered or partially replaced the original deployment.'
    }
    $handle.Dispose(); $handle = $null
    & (Join-Path $root 'Build.ps1') -SkipSetup -RuntimeIdentifier $RuntimeIdentifier -OutputDirectory $output
    if (-not (Test-Path -LiteralPath (Join-Path $output 'FuzzyFindAnything.exe')) -or (Test-Path -LiteralPath $sentinel)) {
        throw 'Unlocked publication did not replace the deployment as a complete directory.'
    }
    Write-Host 'PASS atomic publication: locked output retained, unlocked replacement complete.'
} finally {
    if ($handle) { $handle.Dispose() }
    for ($attempt = 0; Test-Path -LiteralPath $temporary; $attempt++) {
        try { [IO.Directory]::Delete($temporary, $true) }
        catch [IO.IOException], [UnauthorizedAccessException] {
            if ($attempt -ge 60) { throw "Publish test cleanup remains locked at $temporary." }
            Start-Sleep -Milliseconds 500
        }
    }
}
