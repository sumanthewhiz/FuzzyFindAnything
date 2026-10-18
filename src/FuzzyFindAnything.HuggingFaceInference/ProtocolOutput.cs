using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FuzzyFindAnything.HuggingFaceInference;

internal static class ProtocolOutput
{
    public static TextWriter Open()
    {
        IntPtr current = GetCurrentProcess();
        if (!DuplicateHandle(current, GetStdHandle(-11), current, out IntPtr handle, 0, false, 2))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var stream = new FileStream(new SafeFileHandle(handle, true), FileAccess.Write);
        try
        {
            if (!SetStdHandle(-11, GetStdHandle(-12)) || DuplicateFileDescriptor(2, 1) != 0)
                throw new IOException("Native provider diagnostics could not be separated from the engine protocol.");
            Console.SetOut(Console.Error);
            return new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        }
        catch { stream.Dispose(); throw; }
    }
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GetStdHandle(int standard);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetStdHandle(int standard, IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess, out IntPtr target,
        uint access, bool inherit, uint options);
    [DllImport("ucrtbase.dll", EntryPoint = "_dup2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int DuplicateFileDescriptor(int source, int destination);
}
