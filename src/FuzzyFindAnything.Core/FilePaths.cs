using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FuzzyFindAnything.Core;

public static class FilePaths
{
    public static readonly HashSet<string> Documents = new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".rtf", ".pdf", ".doc", ".docx", ".ppt", ".pptx", ".xls", ".xlsx" };
    public static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".ico" };
    public static readonly HashSet<string> Code = new(StringComparer.OrdinalIgnoreCase)
    {
        ".py", ".pyw", ".pyi", ".js", ".jsx", ".mjs", ".cjs", ".ts", ".tsx", ".mts", ".cts",
        ".java", ".c", ".h", ".cc", ".cpp", ".cxx", ".c++", ".hh", ".hpp", ".hxx", ".h++",
        ".go", ".rs", ".sql", ".sh", ".bash", ".cs", ".csx", ".php", ".phtml", ".php3", ".php4",
        ".php5", ".php7", ".php8", ".phps", ".rb", ".rake", ".gemspec", ".rbs", ".swift", ".kt", ".kts", ".r"
    };
    public static readonly HashSet<string> CodeNames = new(StringComparer.OrdinalIgnoreCase)
        { ".bashrc", ".bash_profile", ".bash_login", ".bash_logout", ".rprofile", "gemfile", "rakefile" };
    private const uint ShareAll = 7;

    public static string? Kind(string path)
    {
        string extension = Path.GetExtension(path);
        if (Images.Contains(extension)) return "Image";
        if (Documents.Contains(extension)) return "Document";
        return Code.Contains(extension) || CodeNames.Contains(Path.GetFileName(path)) ? "Code" : null;
    }

    public static string Key(string path) => Path.GetFullPath(path).TrimEnd('\\').ToLowerInvariant();
    public static bool Within(string path, string root)
    {
        string value = Path.GetFullPath(path).TrimEnd('\\'), folder = Path.GetFullPath(root).TrimEnd('\\');
        return value.Equals(folder, StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase);
    }

    public static string Canonical(string path)
    {
        path = Path.GetFullPath(path);
        using var handle = CreateFile(path, 0, ShareAll, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            if (error is not (2 or 3)) throw new Win32Exception(error, $"Cannot resolve {path}.");
            string? parent = Path.GetDirectoryName(path);
            return parent is null ? path : Path.Combine(Canonical(parent), Path.GetFileName(path));
        }
        var buffer = new StringBuilder(32768);
        uint count = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (count == 0 || count >= buffer.Capacity) throw new Win32Exception(Marshal.GetLastWin32Error());
        string value = buffer.ToString();
        if (value.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\\" + value[8..];
        return value.StartsWith(@"\\?\", StringComparison.Ordinal) ? value[4..] : value;
    }

    public static Fingerprint Inspect(string path)
    {
        using var handle = CreateFile(path, 0, ShareAll, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            if (error is 2 or 3) throw new FileNotFoundException("The source file no longer exists.", path);
            throw new Win32Exception(error, $"Cannot read file identity: {path}");
        }
        if (!GetFileInformationByHandle(handle, out var information))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        long windowsTime = ((long)information.WriteHigh << 32) | information.WriteLow;
        long unixNs = checked((windowsTime - 116444736000000000L) * 100);
        long length = ((long)information.SizeHigh << 32) | information.SizeLow;
        UInt128 identity = ((ulong)information.IndexHigh << 32) | information.IndexLow;
        if (GetFileInformationByHandleEx(handle, 18, out var extended, (uint)Marshal.SizeOf<FileIdInformation>()))
            identity = ((UInt128)extended.High << 64) | extended.Low;
        else if (Marshal.GetLastWin32Error() is not (1 or 50 or 87))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read the extended file identity.");
        return new(unixNs, length, identity.ToString(CultureInfo.InvariantCulture));
    }

    public static uint ReparseTag(string path)
    {
        using var handle = CreateFile(path, 0, ShareAll, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        byte[] output = new byte[16384];
        if (!DeviceIoControl(handle, 0x900A8, null, 0, output, output.Length, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return BitConverter.ToUInt32(output);
    }

    public static bool IsNameSurrogate(string path) =>
        File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint) && (ReparseTag(path) & 0x20000000) != 0;
    public static bool IsOffline(string path) => ((uint)File.GetAttributes(path) & (0x1000 | 0x40000 | 0x400000)) != 0;
    public static FileStream OpenRead(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    public static string Hash(string path)
    {
        using var stream = OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static string ScalarPrefix(string text, int count)
    {
        int characters = 0, scalars = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (scalars++ == count) break;
            characters += rune.Utf16SequenceLength;
        }
        return text[..characters];
    }

    public static int ScalarLength(string text) => text.EnumerateRunes().Count();
    public static string CollapseWhitespace(string text)
    {
        var result = new StringBuilder(text.Length);
        bool spacing = false;
        foreach (char value in text)
        {
            if (char.IsWhiteSpace(value) || value is >= '\u001c' and <= '\u001f') { spacing = result.Length > 0; continue; }
            if (spacing) result.Append(' ');
            result.Append(value);
            spacing = false;
        }
        return result.ToString();
    }
    public static double UnixNow => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes, CreateLow, CreateHigh, AccessLow, AccessHigh, WriteLow, WriteHigh,
            VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInformation { public ulong Volume, Low, High; }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass, out FileIdInformation information, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[]? input, int inputSize, byte[] output,
        int outputSize, out int returned, IntPtr overlapped);
}
