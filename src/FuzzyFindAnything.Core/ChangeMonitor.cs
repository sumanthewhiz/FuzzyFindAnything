using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace FuzzyFindAnything.Core;

public sealed record UsnRecord(UInt128 FileId, UInt128 ParentId, string Name);
public sealed record UsnBatch(long Cursor, List<UsnRecord> Records, bool UnknownVersion);

public static class UsnParser
{
    public static UsnBatch Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8) throw new InvalidDataException("Truncated USN cursor.");
        long cursor = BinaryPrimitives.ReadInt64LittleEndian(data);
        var records = new List<UsnRecord>();
        bool unknown = false;
        var unicode = new UnicodeEncoding(false, false, true);
        for (int offset = 8; offset < data.Length;)
        {
            if (offset + 8 > data.Length) throw new InvalidDataException("Truncated USN header.");
            int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]));
            int version = BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 4)..]);
            if (length < 8 || length > data.Length - offset) throw new InvalidDataException("Invalid USN record length.");
            var row = data.Slice(offset, length);
            UInt128 file, parent;
            int nameLength, nameOffset;
            if (version == 2)
            {
                if (length < 60) throw new InvalidDataException("Truncated USN v2 record.");
                file = BinaryPrimitives.ReadUInt64LittleEndian(row[8..]);
                parent = BinaryPrimitives.ReadUInt64LittleEndian(row[16..]);
                nameLength = BinaryPrimitives.ReadUInt16LittleEndian(row[56..]);
                nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(row[58..]);
            }
            else if (version == 3)
            {
                if (length < 76) throw new InvalidDataException("Truncated USN v3 record.");
                file = ((UInt128)BinaryPrimitives.ReadUInt64LittleEndian(row[16..]) << 64) | BinaryPrimitives.ReadUInt64LittleEndian(row[8..]);
                parent = ((UInt128)BinaryPrimitives.ReadUInt64LittleEndian(row[32..]) << 64) | BinaryPrimitives.ReadUInt64LittleEndian(row[24..]);
                nameLength = BinaryPrimitives.ReadUInt16LittleEndian(row[72..]);
                nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(row[74..]);
            }
            else { unknown = true; offset += length; continue; }
            if ((nameLength & 1) != 0 || (nameOffset & 1) != 0 || nameOffset < (version == 2 ? 60 : 76) ||
                nameOffset + nameLength > length)
                throw new InvalidDataException("Invalid USN filename bounds.");
            records.Add(new(file, parent, unicode.GetString(row.Slice(nameOffset, nameLength))));
            offset += length;
        }
        return new(cursor, records, unknown);
    }

    internal static IEnumerable<string> Resolve(UsnRecord record, IReadOnlyDictionary<UInt128, string> known)
    {
        if (known.TryGetValue(record.FileId, out string? previous)) yield return previous;
        if (known.TryGetValue(record.ParentId, out string? parent))
        {
            // Unprivileged journal reads can redact names; reconcile that parent without resetting its jobs.
            string current = record.Name.Length == 0 ? parent : Path.Combine(parent, record.Name);
            if (!string.Equals(current, previous, StringComparison.OrdinalIgnoreCase)) yield return current;
        }
    }
}

public interface IChangeMonitor : IAsyncDisposable
{
    string Description { get; }
    void Configure(IReadOnlyList<FolderState> folders);
    void UpdateIdentities(int id, Dictionary<UInt128, string> values, bool complete = true);
    void Start();
    void Checkpoint();
}

public sealed class ChangeMonitor : IChangeMonitor
{
    private sealed record Root(string Path, string Drive);
    private readonly object gate = new();
    private readonly Catalog catalog;
    private readonly Action<int, string?, bool> changed;
    private readonly Action<string> log;
    private readonly Dictionary<int, Root> roots = [];
    private readonly Dictionary<int, FileSystemWatcher> watchers = [];
    private readonly Dictionary<int, Dictionary<UInt128, string>> identities = [];
    private readonly HashSet<int> initialized = [];
    private readonly Dictionary<string, Journal> journals = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> warnings = [];
    private readonly CancellationTokenSource lifetime = new();
    private Task? replay;
    private readonly bool directoryNotifications;
    private bool delivering;

    public ChangeMonitor(Catalog catalog, Action<int, string?, bool> changed, Action<string> log, bool directoryNotifications = true)
    {
        this.catalog = catalog; this.changed = changed; this.log = log; this.directoryNotifications = directoryNotifications;
    }
    public string Description
    {
        get
        {
            lock (gate)
            {
                string description = roots.Count == 0 ? "Add a folder to enable automatic change tracking." :
                    journals.Count > 0 ? (directoryNotifications ? "USN journal + directory notifications" : "USN journal") :
                    (directoryNotifications ? "Directory notifications + periodic reconciliation" : "Periodic reconciliation");
                return warnings.Count == 0 ? description : description + ". " + string.Join(" ", warnings.Values);
            }
        }
    }
    public void Configure(IReadOnlyList<FolderState> folders)
    {
        var dirty = new List<int>();
        lock (gate)
        {
            foreach (int id in roots.Keys.Where(id => folders.All(folder => folder.Id != id)).ToArray())
            {
                if (watchers.Remove(id, out var watcher)) watcher.Dispose();
                roots.Remove(id); identities.Remove(id); initialized.Remove(id); warnings.Remove(id.ToString());
            }
            foreach (var folder in folders)
            {
                if (roots.ContainsKey(folder.Id)) continue;
                string drive = Path.GetPathRoot(folder.Path)!.TrimEnd('\\').ToUpperInvariant();
                roots[folder.Id] = new(folder.Path, drive);
                try
                {
                    identities[folder.Id] = new() { [UInt128.Parse(FilePaths.Inspect(folder.Path).Identity)] = folder.Path };
                    if (directoryNotifications)
                    {
                        var watcher = new FileSystemWatcher(folder.Path)
                        {
                            IncludeSubdirectories = true,
                            InternalBufferSize = 65536,
                            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite |
                                NotifyFilters.Size | NotifyFilters.Attributes | NotifyFilters.CreationTime
                        };
                        int id = folder.Id;
                        watcher.Created += (_, value) => changed(id, value.FullPath, false);
                        watcher.Changed += (_, value) => changed(id, value.FullPath, false);
                        watcher.Deleted += (_, value) => changed(id, value.FullPath, false);
                        watcher.Renamed += (_, value) => { changed(id, value.OldFullPath, false); changed(id, value.FullPath, false); };
                        watcher.Error += (_, value) =>
                        {
                            lock (gate) warnings[id.ToString()] = "Directory notifications require reconciliation: " + value.GetException().Message;
                            changed(id, null, true);
                        };
                        watchers[id] = watcher;
                        watcher.EnableRaisingEvents = true;
                    }
                }
                catch (Exception error) when (error is IOException or Win32Exception or UnauthorizedAccessException)
                { warnings[folder.Id.ToString()] = "Directory notifications unavailable: " + error.Message; log(warnings[folder.Id.ToString()]); }
                if (!journals.ContainsKey(drive) && !warnings.ContainsKey(drive))
                {
                    try
                    {
                        var journal = new Journal(drive, catalog.Setting("usn:" + drive));
                        journals.Add(drive, journal);
                        if (journal.Gap) dirty.AddRange(roots.Where(pair => pair.Value.Drive == drive).Select(pair => pair.Key));
                    }
                    catch (Exception error) when (error is IOException or Win32Exception or JsonException or InvalidDataException)
                    { warnings[drive] = "USN unavailable: " + error.Message; log(warnings[drive]); }
                }
            }
            foreach (string drive in journals.Keys.Where(drive => !roots.Values.Any(root => root.Drive == drive)).ToArray())
            { journals[drive].Dispose(); journals.Remove(drive); warnings.Remove(drive); }
        }
        foreach (int id in dirty.Distinct()) changed(id, null, true);
    }
    public void UpdateIdentities(int id, Dictionary<UInt128, string> values, bool complete = true)
    {
        lock (gate)
            if (roots.ContainsKey(id))
            {
                identities[id] = new(values);
                if (complete) initialized.Add(id);
            }
    }
    public void Start() => replay = Task.Run(ReplayAsync);
    private async Task ReplayAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(2000, lifetime.Token);
                var events = new List<(int Root, string? Path, bool Force)>();
                lock (gate)
                {
                    delivering = true;
                    foreach (var pair in journals.ToArray())
                    {
                        var matching = roots.Where(root => root.Value.Drive == pair.Key).ToArray();
                        if (matching.Any(root => !initialized.Contains(root.Key))) continue;
                        try
                        {
                            for (int read = 0; read < 32; read++)
                            {
                                var batch = pair.Value.Read();
                                if (batch.UnknownVersion) events.AddRange(matching.Select(root => (root.Key, (string?)null, true)));
                                foreach (var record in batch.Records)
                                    foreach (var root in matching)
                                    {
                                        if (!identities.TryGetValue(root.Key, out var known)) continue;
                                        foreach (string path in UsnParser.Resolve(record, known))
                                            events.Add((root.Key, path, false));
                                    }
                                bool same = batch.Cursor == pair.Value.Cursor;
                                pair.Value.Cursor = batch.Cursor;
                                if (same) break;
                            }
                        }
                        catch (Exception error) when (error is IOException or Win32Exception or DecoderFallbackException or InvalidDataException)
                        {
                            log("USN replay requires reconciliation: " + error.Message);
                            events.AddRange(roots.Where(root => root.Value.Drive == pair.Key).Select(root => (root.Key, (string?)null, true)));
                            try { pair.Value.Reset(); }
                            catch (Win32Exception reset)
                            {
                                warnings[pair.Key] = "USN unavailable: " + reset.Message;
                                pair.Value.Dispose(); journals.Remove(pair.Key);
                            }
                        }
                    }
                }
                try { foreach (var value in events) changed(value.Root, value.Path, value.Force); }
                finally { lock (gate) delivering = false; }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }
    public void Checkpoint()
    {
        lock (gate)
        {
            if (delivering) return;
            foreach (var pair in journals)
                catalog.SetSetting("usn:" + pair.Key, JsonSerializer.Serialize(new { id = pair.Value.Id, cursor = pair.Value.Cursor }));
        }
    }
    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        if (replay is not null) await replay;
        lock (gate)
        {
            foreach (var watcher in watchers.Values) watcher.Dispose();
            foreach (var journal in journals.Values) journal.Dispose();
            watchers.Clear(); journals.Clear(); roots.Clear();
        }
        lifetime.Dispose();
    }

    private sealed class Journal : IDisposable
    {
        private readonly SafeFileHandle handle;
        private uint readCode = 0x903AB;
        public ulong Id { get; private set; }
        public long Cursor { get; set; }
        public bool Gap { get; }
        public Journal(string drive, string? saved)
        {
            if (drive.Length != 2 || drive[1] != ':') throw new IOException("USN requires a local volume.");
            handle = Open(drive + "\\", 0, 0x02000000);
            try
            {
                byte[] info = Ioctl(0x900F4);
                if (info.Length < 32) throw new InvalidDataException("Truncated USN journal information.");
                Id = BinaryPrimitives.ReadUInt64LittleEndian(info);
                long first = BitConverter.ToInt64(info, 8), next = BitConverter.ToInt64(info, 16), lowest = BitConverter.ToInt64(info, 24);
                Cursor = next;
                if (saved is not null)
                {
                    using var json = JsonDocument.Parse(saved);
                    bool valid = json.RootElement.TryGetProperty("cursor", out var cursorValue) && cursorValue.TryGetInt64(out long cursor) &&
                        json.RootElement.TryGetProperty("id", out var idValue) && idValue.TryGetUInt64(out ulong id) &&
                        id == Id && cursor >= Math.Max(first, lowest) && cursor <= next;
                    Gap = !valid;
                    if (valid) Cursor = cursorValue.GetInt64();
                }
                _ = Read();
            }
            catch (Win32Exception)
            {
                handle.Dispose();
                handle = Open(@"\\.\" + drive, 0x80000000, 0);
                readCode = 0x900BB;
                try { Reset(); _ = Read(); Gap = saved is not null; }
                catch { handle.Dispose(); throw; }
            }
            catch { handle.Dispose(); throw; }
        }
        public void Reset()
        {
            byte[] value = Ioctl(0x900F4);
            if (value.Length < 32) throw new InvalidDataException("Truncated USN journal information.");
            Id = BitConverter.ToUInt64(value, 0); Cursor = BitConverter.ToInt64(value, 16);
        }
        public UsnBatch Read()
        {
            byte[] request = new byte[48];
            BitConverter.GetBytes(Cursor).CopyTo(request, 0);
            BitConverter.GetBytes(uint.MaxValue).CopyTo(request, 8);
            BitConverter.GetBytes(Id).CopyTo(request, 32);
            BitConverter.GetBytes((ushort)2).CopyTo(request, 40);
            BitConverter.GetBytes((ushort)3).CopyTo(request, 42);
            return UsnParser.Parse(Ioctl(readCode, request));
        }
        private byte[] Ioctl(uint code, byte[]? input = null)
        {
            byte[] output = new byte[65536];
            if (!FilePaths.DeviceIoControl(handle, code, input, input?.Length ?? 0, output, output.Length, out int size, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return output.AsSpan(0, size).ToArray();
        }
        private static SafeFileHandle Open(string path, uint access, uint flags)
        {
            var handle = CreateFile(path, access, 7, IntPtr.Zero, 3, flags, IntPtr.Zero);
            if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
            return handle;
        }
        public void Dispose() => handle.Dispose();
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    }
}
