using System.ComponentModel;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;

namespace FuzzyFindAnything.Core;

public sealed record CpuBudget(int TotalCores, int Processors, nuint AffinityMask)
{
    public static CpuBudget Apply()
    {
        using var process = Process.GetCurrentProcess();
        if (!GetProcessAffinityMask(process.Handle, out nuint allowed, out nuint system))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        ushort count = 64;
        ushort[] groups = new ushort[count];
        if (!GetProcessGroupAffinity(process.Handle, ref count, groups))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        uint length = 0;
        GetLogicalProcessorInformationEx(0, IntPtr.Zero, ref length);
        IntPtr buffer = Marshal.AllocHGlobal(checked((int)length));
        try
        {
            if (!GetLogicalProcessorInformationEx(0, buffer, ref length))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var masks = new List<ulong>();
            int total = 0;
            for (int offset = 0; offset < length;)
            {
                int relationship = Marshal.ReadInt32(buffer, offset);
                int size = Marshal.ReadInt32(buffer, offset + 4);
                if (size < 32 || offset + size > length)
                    throw new InvalidOperationException("Windows returned invalid CPU topology data.");
                if (relationship == 0)
                {
                    total++;
                    int groupCount = (ushort)Marshal.ReadInt16(buffer, offset + 30);
                    for (int i = 0; i < groupCount; i++)
                    {
                        ulong mask = unchecked((ulong)Marshal.ReadInt64(buffer, offset + 32 + i * 16));
                        ushort group = (ushort)Marshal.ReadInt16(buffer, offset + 40 + i * 16);
                        if (group == groups[0])
                            masks.Add(mask);
                    }
                }
                offset += size;
            }
            if (total < 2)
                throw new InvalidOperationException("At least two physical CPU cores are required.");
            ulong selected = Select(masks, (ulong)(allowed == 0 ? system : allowed), total);
            if (selected == 0 || !SetProcessAffinityMask(process.Handle, (nuint)selected))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not enforce the application CPU limit.");
            Environment.SetEnvironmentVariable("FFA_CPU_MASK", selected.ToString("X"));
            Environment.SetEnvironmentVariable("FFA_CPU_LIMIT", BitOperations.PopCount(selected).ToString());
            Environment.SetEnvironmentVariable("FFA_TOTAL_CORES", total.ToString());
            return new CpuBudget(total, BitOperations.PopCount(selected), (nuint)selected);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal static ulong Select(IEnumerable<ulong> coreMasks, ulong allowed, int totalCores)
    {
        if (totalCores < 2) throw new InvalidOperationException("At least two physical CPU cores are required.");
        ulong selected = 0;
        foreach (ulong core in coreMasks)
        {
            ulong available = core & allowed;
            if (available == 0) continue;
            selected |= available & (~available + 1);
            if (BitOperations.PopCount(selected) >= totalCores / 2) break;
        }
        if (selected == 0) throw new InvalidOperationException("No processor is eligible under the inherited CPU restriction.");
        return selected;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessAffinityMask(IntPtr process, out nuint processMask, out nuint systemMask);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessAffinityMask(IntPtr process, nuint mask);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessGroupAffinity(IntPtr process, ref ushort count, [Out] ushort[] groups);
}
