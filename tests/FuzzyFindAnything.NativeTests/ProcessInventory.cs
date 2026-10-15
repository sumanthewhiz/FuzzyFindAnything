using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FuzzyFindAnything.NativeTests;

internal static class ProcessInventory
{
    public sealed record Snapshot(int Id, string Name, long Affinity, double CpuSeconds, long WorkingSet, long PeakWorkingSet, string[] Modules);
    public static List<Snapshot> Capture(int root, bool modules = true)
    {
        using var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
        var entries = new List<(int Id, int Parent)>();
        if (!Process32First(snapshot, ref entry)) throw new Win32Exception(Marshal.GetLastWin32Error());
        do { entries.Add((checked((int)entry.Id), checked((int)entry.Parent))); } while (Process32Next(snapshot, ref entry));
        var ids = new HashSet<int> { root };
        while (true)
        {
            int previous = ids.Count;
            foreach (var item in entries) if (ids.Contains(item.Parent)) ids.Add(item.Id);
            if (ids.Count == previous) break;
        }
        var result = new List<Snapshot>();
        foreach (int id in ids)
        {
            try
            {
                using var process = Process.GetProcessById(id);
                if (process.HasExited) continue;
                result.Add(new(id, process.ProcessName, process.ProcessorAffinity.ToInt64(), process.TotalProcessorTime.TotalSeconds,
                    process.WorkingSet64, process.PeakWorkingSet64,
                    modules ? process.Modules.Cast<ProcessModule>().Select(module => module.FileName).ToArray() : []));
            }
            catch (ArgumentException) { }
        }
        return result;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, Id;
        public nuint Heap;
        public uint Module, Threads, Parent;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint id);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry entry);
}
