[CmdletBinding()]
param(
    [switch]$Enhanced,
    [switch]$Diagnose,
    [string]$ApplicationDirectory,
    [string]$ModelCache,
    [string]$EmbeddingModelCache,
    [string]$ImageModelCache,
    [ValidateRange(60, 1800)][int]$TimeoutSeconds = 900
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$output = Join-Path $root '.downloads\ui-isolated.log'
New-Item -ItemType Directory -Force -Path (Split-Path $output -Parent) | Out-Null
Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

public sealed class IsolatedUiRun : IDisposable {
    private IntPtr desktop, job, process;

    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
    private struct StartupInfo {
        public int Size;
        public string Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCount, YCount, Fill, Flags;
        public short ShowWindow, ReservedSize;
        public IntPtr ReservedData, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo {
        public IntPtr Process, Thread;
        public int ProcessId, ThreadId;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits {
        public long ProcessTime, JobTime;
        public uint Flags;
        public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint Priority, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters {
        public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits {
        public BasicLimits Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }

    public IsolatedUiRun(string executable, string command, string directory) {
        try {
            string name = "FuzzyFindAnything-Test-" + Guid.NewGuid().ToString("N");
            desktop = CreateDesktop(name, null, IntPtr.Zero, 0, 0x000F01FF, IntPtr.Zero);
            if (desktop == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            var limits = new ExtendedLimits();
            limits.Basic.Flags = 0x2000;
            if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf(typeof(ExtendedLimits))))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var startup = new StartupInfo { Size=Marshal.SizeOf(typeof(StartupInfo)), Desktop=name };
            ProcessInfo child;
            if (!CreateProcess(executable, new StringBuilder(command), IntPtr.Zero, IntPtr.Zero, false,
                               0x08000004, IntPtr.Zero, directory, ref startup, out child))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            process = child.Process;
            try {
                if (!AssignProcessToJobObject(job, process)) {
                    TerminateProcess(process, 1);
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                if (ResumeThread(child.Thread) == uint.MaxValue)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            } finally { CloseHandle(child.Thread); }
        } catch { Dispose(); throw; }
    }

    public int Wait(int milliseconds) {
        uint result = WaitForSingleObject(process, (uint)milliseconds);
        if (result == 258) throw new TimeoutException("The isolated UI test exceeded its time limit.");
        if (result != 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        uint exitCode;
        if (!GetExitCodeProcess(process, out exitCode)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return (int)exitCode;
    }

    public void Dispose() {
        if (job != IntPtr.Zero) { CloseHandle(job); job = IntPtr.Zero; }
        if (process != IntPtr.Zero) { CloseHandle(process); process = IntPtr.Zero; }
        if (desktop != IntPtr.Zero) { CloseDesktop(desktop); desktop = IntPtr.Zero; }
    }

    [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern IntPtr CreateDesktop(string name, string device, IntPtr mode, uint flags, uint access, IntPtr security);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern IntPtr CreateJobObject(IntPtr security, string name);
    [DllImport("kernel32.dll", SetLastError=true)]
    private static extern bool SetInformationJobObject(IntPtr job, int kind, ref ExtendedLimits limits, uint size);
    [DllImport("kernel32.dll", SetLastError=true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern bool CreateProcess(string app, StringBuilder command, IntPtr processSecurity,
        IntPtr threadSecurity, bool inherit, uint flags, IntPtr environment, string directory,
        ref StartupInfo startup, out ProcessInfo process);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool GetExitCodeProcess(IntPtr process, out uint code);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
'@

$executable = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$script = (Join-Path $PSScriptRoot 'UiSmoke.ps1').Replace("'", "''")
$log = $output.Replace("'", "''")
$options = $(if ($Enhanced) { ' -Enhanced' }) + $(if ($Diagnose) { ' -Diagnose' })
if ($ApplicationDirectory) {
    $ApplicationDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ApplicationDirectory)
    $options += " -ApplicationDirectory '" + $ApplicationDirectory.Replace("'", "''") + "'"
}
if ($ModelCache) {
    $ModelCache = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ModelCache)
    $options += " -ModelCache '" + $ModelCache.Replace("'", "''") + "'"
}
if ($EmbeddingModelCache) {
    $EmbeddingModelCache = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($EmbeddingModelCache)
    $options += " -EmbeddingModelCache '" + $EmbeddingModelCache.Replace("'", "''") + "'"
}
if ($ImageModelCache) {
    $ImageModelCache = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ImageModelCache)
    $options += " -ImageModelCache '" + $ImageModelCache.Replace("'", "''") + "'"
}
$command = "`"$executable`" -NoProfile -Command `"& { try { & '$script'$options } catch { Write-Output (`$_ | Out-String); Write-Output `$_.ScriptStackTrace; Write-Output `$_.Exception.ToString(); exit 1 } } *> '$log'`""
$run = $null
try {
    $run = New-Object IsolatedUiRun($executable, $command, $root)
    $exitCode = $run.Wait($TimeoutSeconds * 1000)
    Get-Content -LiteralPath $output
    if ($exitCode -ne 0) { throw "Isolated UI checks failed with exit code $exitCode." }
} finally {
    if ($run) { $run.Dispose() }
}
