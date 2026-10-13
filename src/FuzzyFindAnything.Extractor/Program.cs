using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using Microsoft.Win32;

namespace FuzzyFindAnything.Extractor;

internal static class Program
{
    private const int MaxCharacters = 8_000_000;
    private const int EndOfChunks = unchecked((int)0x80041700);
    private const int NoMoreText = unchecked((int)0x80041701);
    private const int NoText = unchecked((int)0x80041705);
    private const int LastText = 0x00041709;
    private static readonly bool TraceFilter = Environment.GetEnvironmentVariable("FFA_IFILTER_TRACE") == "1";
    private const int MaxChunks = 1_000_000;

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args.Length != 1)
        {
            Console.Error.WriteLine("Usage: FuzzyFindAnything.Extractor.exe <document path> | --filters");
            return 2;
        }

        if (args[0] == "--filters")
        {
            string[] extensions = [".rtf", ".pdf", ".doc", ".docx", ".ppt", ".pptx", ".xls", ".xlsx"];
            var registrations = extensions.ToDictionary(extension => extension, extension =>
            {
                using var key = Registry.ClassesRoot.OpenSubKey($@"{extension}\PersistentHandler");
                return key?.GetValue(null)?.ToString() ?? "No direct PersistentHandler registration";
            });
            Console.WriteLine(JsonSerializer.Serialize(registrations));
            return 0;
        }

        try
        {
            using var watchdog = new Timer(_ =>
            {
                Console.Error.WriteLine("Windows IFilter exceeded its 55-second processing limit.");
                Environment.Exit(124);
            }, null, TimeSpan.FromSeconds(55), Timeout.InfiniteTimeSpan);
            string path = Path.GetFullPath(args[0]);
            if (!File.Exists(path))
                throw new FileNotFoundException("The document was deleted or is not accessible.", path);
            Marshal.ThrowExceptionForHR(CoInitializeEx(IntPtr.Zero, 2));
            try
            {
                var timer = Stopwatch.StartNew();
                string format = FileFormatProbe.Detect(path);
                var warnings = new List<string>();
                if (!format.Equals(Path.GetExtension(path), StringComparison.OrdinalIgnoreCase))
                    warnings.Add($"Detected {format} content in a {Path.GetExtension(path)} file; used the matching IFilter.");
                string text = Extract(path, format, warnings);
                if (string.IsNullOrWhiteSpace(text))
                    throw new FilterFailure("no_text", "The document has no extractable text. Scanned PDFs require an OCR-capable IFilter.");
                Console.WriteLine(JsonSerializer.Serialize(new { text, format, warnings, elapsed_seconds = timer.Elapsed.TotalSeconds }));
                return 0;
            }
            finally
            {
                CoUninitialize();
            }
        }
        catch (Exception exception)
        {
            var failure = exception as FilterFailure ?? FilterFailure.AtStage("reading the document", Path.GetExtension(args[0]), exception);
            Console.WriteLine(JsonSerializer.Serialize(new { error = new { code = failure.Code, message = failure.Message, retryable = failure.Retryable } }));
            Console.Error.WriteLine($"Windows IFilter: {failure.Message}");
            if (TraceFilter)
                Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static string Extract(string path, string extension, List<string> warnings)
    {
        using FilterSession session = OpenFilter(path, extension);
        IFilter filter = session.Filter;
        int result;
        IntPtr buffer = Marshal.AllocCoTaskMem(8192 * sizeof(char));
        IntPtr attributes = IntPtr.Zero;
        try
        {
            attributes = Marshal.AllocCoTaskMem(Marshal.SizeOf<FullPropSpec>());
            Marshal.StructureToPtr(new FullPropSpec
            {
                PropertySet = new Guid("B725F130-47EF-101A-A5F1-02608C9EEBAC"),
                Property = new PropSpec { Kind = 1, NameOrId = new IntPtr(19) }
            }, attributes, false);
            // INDEXING_ONLY allows streaming. Deliberately omit SEARCH_LINKS to avoid external content.
            try
            {
                Marshal.ThrowExceptionForHR(filter.Init(0x1 | 0x2 | 0x4 | 0x8 | 0x40, 1, attributes, out _));
            }
            catch (Exception error)
            {
                throw FilterFailure.AtStage("initializing content extraction", extension, error);
            }
            var text = new StringBuilder();
            var chunk = new StatChunk();
            int chunks = 0;
            while (true)
            {
                result = filter.GetChunk(ref chunk);
                if (TraceFilter)
                    Console.Error.WriteLine($"GetChunk=0x{result:X8} id={chunk.Id} flags={chunk.Flags} break={chunk.BreakType} property={chunk.Attribute.Property.NameOrId}");
                if (result == EndOfChunks)
                    break;
                if (++chunks > MaxChunks)
                    throw new FilterFailure("resource_limit", "The IFilter exceeded the chunk-count safety limit.");
                if (result is unchecked((int)0x80041707) or unchecked((int)0x80041708))
                {
                    if (!warnings.Contains("An embedded object or external link could not be filtered; the main document text was retained."))
                        warnings.Add("An embedded object or external link could not be filtered; the main document text was retained.");
                    continue;
                }
                Marshal.ThrowExceptionForHR(result);
                if ((chunk.Flags & 1) == 0)
                    continue;
                if (chunk.BreakType != 0 && text.Length > 0)
                    text.Append(chunk.BreakType == 1 ? ' ' : '\n');
                while (true)
                {
                    uint length = 8192;
                    result = filter.GetText(ref length, buffer);
                    if (result is NoMoreText or NoText)
                        break;
                    Marshal.ThrowExceptionForHR(result);
                    if (length > 8192)
                        throw new InvalidDataException("The IFilter returned an invalid text buffer length.");
                    text.Append(Marshal.PtrToStringUni(buffer, (int)length)?.TrimEnd('\0'));
                    if (text.Length > MaxCharacters)
                        throw new FilterFailure("resource_limit", "Document exceeds the 8 million character extraction safety limit.");
                    if (result == LastText)
                        break;
                    if (length == 0)
                        throw new FilterFailure("filter_no_progress", "The IFilter returned success without advancing its text stream.", true);
                }
            }
            return text.ToString();
        }
        finally
        {
            Marshal.FreeCoTaskMem(buffer);
            if (attributes != IntPtr.Zero)
                Marshal.FreeCoTaskMem(attributes);
        }
    }

    private static FilterSession OpenFilter(string path, string extension)
    {
        const string filterInterface = "{89BCB740-6119-101A-BCB7-00DD010655AF}";
        const string virtualClasses = @"SOFTWARE\Microsoft\Office\ClickToRun\REGISTRY\MACHINE\Software\Classes";
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var officeClasses = machine.OpenSubKey(virtualClasses);
        if (officeClasses is not null && FindFilter(officeClasses, extension) is { } office)
        {
            using var configuration = machine.OpenSubKey(@"SOFTWARE\Microsoft\Office\ClickToRun\Configuration");
            string installation = configuration?.GetValue("InstallationPath") as string
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft Office");
            string virtualDll = Path.Combine(installation, "root", "vfs", "ProgramFilesCommonX64",
                "Microsoft Shared", "Filters", Path.GetFileName(office.Library));
            string libraryPath = File.Exists(office.Library) ? office.Library : virtualDll;
            if (File.Exists(libraryPath))
            {
                IntPtr library = LoadLibraryEx(libraryPath, IntPtr.Zero, 0x00000100 | 0x00001000);
                if (library == IntPtr.Zero)
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), $"Cannot load the installed Office IFilter: {libraryPath}");
                try
                {
                    var getFactory = Marshal.GetDelegateForFunctionPointer<GetClassObject>(
                        NativeLibrary.GetExport(library, "DllGetClassObject"));
                    Guid classId = office.ClassId;
                    Guid factoryId = typeof(IClassFactory).GUID;
                    Marshal.ThrowExceptionForHR(getFactory(ref classId, ref factoryId, out IClassFactory factory));
                    IFilter filter;
                    try
                    {
                        Guid filterId = typeof(IFilter).GUID;
                        Marshal.ThrowExceptionForHR(factory.CreateInstance(IntPtr.Zero, ref filterId, out filter));
                    }
                    finally
                    {
                        Marshal.FinalReleaseComObject(factory);
                    }
                    var session = new FilterSession(filter, library);
                    library = IntPtr.Zero;
                    try
                    {
                        session.Initialize(path);
                        return session;
                    }
                    catch (Exception error)
                    {
                        session.Dispose();
                        throw FilterFailure.AtStage("loading the document stream", extension, error);
                    }
                }
                finally
                {
                    if (library != IntPtr.Zero)
                        NativeLibrary.Free(library);
                }
            }
        }

        using var classes = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Registry64);
        if (FindFilter(classes, extension) is { } registration)
        {
            Guid classId = registration.ClassId;
            Guid filterId = typeof(IFilter).GUID;
            int createResult = CoCreateInstance(ref classId, IntPtr.Zero, 1, ref filterId, out IFilter? filter);
            if (TraceFilter)
                Console.Error.WriteLine($"CoCreateInstance({classId})=0x{createResult:X8}");
            if (createResult >= 0 && filter is not null)
            {
                var session = new FilterSession(filter, IntPtr.Zero);
                try
                {
                    session.Initialize(path);
                    return session;
                }
                catch (Exception exception)
                {
                    if (TraceFilter)
                        Console.Error.WriteLine($"Stream initialization: {exception}");
                    session.Dispose();
                    throw FilterFailure.AtStage("loading the document stream", extension, exception);
                }
            }
        }
        int result = LoadIFilter(path, IntPtr.Zero, out IFilter? legacyFilter);
        if (result < 0 || legacyFilter is null)
            throw new FilterFailure("filter_unavailable",
                $"No usable {extension} IFilter could be activated (0x{result:X8}). Check the installed filter's architecture and registration.", true);
        return new FilterSession(legacyFilter, IntPtr.Zero);

        static (Guid ClassId, string Library)? FindFilter(RegistryKey root, string suffix)
        {
            using var handlerKey = root.OpenSubKey($@"{suffix}\PersistentHandler");
            if (handlerKey?.GetValue(null) is not string handler)
                return null;
            using var addinKey = root.OpenSubKey($@"CLSID\{handler}\PersistentAddinsRegistered\{filterInterface}");
            if (addinKey?.GetValue(null) is not string id || !Guid.TryParse(id, out Guid classId))
                return null;
            using var serverKey = root.OpenSubKey($@"CLSID\{id}\InprocServer32");
            return serverKey?.GetValue(null) is string library ? (classId, Environment.ExpandEnvironmentVariables(library)) : null;
        }
    }

    private sealed class FilterSession(IFilter filter, IntPtr library) : IDisposable
    {
        public IFilter Filter { get; } = filter;
        private IStream? stream;

        public void Initialize(string path)
        {
            if (Filter is IInitializeWithStream streamInitializer)
            {
                Marshal.ThrowExceptionForHR(SHCreateStreamOnFileEx(path, 0x40, 0x80, false, IntPtr.Zero, out stream));
                streamInitializer.Initialize(stream, 0);
            }
            else if (Filter is IPersistStream persistentStream)
            {
                Marshal.ThrowExceptionForHR(SHCreateStreamOnFileEx(path, 0x40, 0x80, false, IntPtr.Zero, out stream));
                persistentStream.Load(stream);
            }
            else if (Filter is IPersistFile persistentFile)
            {
                persistentFile.Load(path, 0);
            }
            else
            {
                throw new COMException("The registered IFilter cannot be initialized from a file or stream.", unchecked((int)0x80004002));
            }
        }

        public void Dispose()
        {
            Marshal.FinalReleaseComObject(Filter);
            if (stream is not null)
                Marshal.FinalReleaseComObject(stream);
            if (library != IntPtr.Zero)
                NativeLibrary.Free(library);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetClassObject(ref Guid classId, ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out IClassFactory factory);

    [ComImport, Guid("00000001-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IClassFactory
    {
        [PreserveSig] int CreateInstance(IntPtr outer, ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out IFilter filter);
        [PreserveSig] int LockServer([MarshalAs(UnmanagedType.Bool)] bool locked);
    }

    [ComImport, Guid("B824B49D-22AC-4161-AC8A-9916E8FA3F7F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeWithStream
    {
        void Initialize([MarshalAs(UnmanagedType.Interface)] IStream stream, uint mode);
    }

    [ComImport, Guid("00000109-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistStream
    {
        void GetClassID(out Guid classId);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.Interface)] IStream stream);
        void Save([MarshalAs(UnmanagedType.Interface)] IStream stream, [MarshalAs(UnmanagedType.Bool)] bool clearDirty);
        void GetSizeMax(out ulong size);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "LoadLibraryExW", SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid classId, IntPtr outer, uint context, ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IFilter? filter);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateStreamOnFileEx(string path, uint mode, uint attributes,
        [MarshalAs(UnmanagedType.Bool)] bool create, IntPtr reserved, [MarshalAs(UnmanagedType.Interface)] out IStream stream);

    [DllImport("query.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int LoadIFilter(string path, IntPtr outer, [MarshalAs(UnmanagedType.Interface)] out IFilter? filter);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [ComImport, Guid("89BCB740-6119-101A-BCB7-00DD010655AF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFilter
    {
        [PreserveSig] int Init(uint flags, uint attributeCount, IntPtr attributes, out uint outputFlags);
        [PreserveSig] int GetChunk(ref StatChunk chunk);
        [PreserveSig] int GetText(ref uint characterCount, IntPtr buffer);
        [PreserveSig] int GetValue(out IntPtr value);
        [PreserveSig] int BindRegion(IntPtr region, ref Guid interfaceId, out IntPtr result);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropSpec
    {
        public uint Kind;
        public IntPtr NameOrId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FullPropSpec
    {
        public Guid PropertySet;
        public PropSpec Property;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StatChunk
    {
        public uint Id;
        public uint BreakType;
        public uint Flags;
        public uint Locale;
        public FullPropSpec Attribute;
        public uint SourceId;
        public uint SourceStart;
        public uint SourceLength;
    }
}
