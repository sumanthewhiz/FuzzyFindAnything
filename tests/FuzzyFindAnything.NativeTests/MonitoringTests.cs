using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using FuzzyFindAnything.Core;

namespace FuzzyFindAnything.NativeTests;

internal static class MonitoringTests
{
    public static async Task RunAsync()
    {
        Program.Run("USN v2/v3, names, malformed data and rename resolution", Parsing);
        await NotificationsAsync();
        await JournalAsync();
        await DeltasAsync();
        await RestartDeltasAsync();
        await BackgroundStartupAsync();
        await FilterLifecycleAsync();
    }
    private static byte[] Record(ushort version, UInt128 file, UInt128 parent, string name)
    {
        int header = version == 2 ? 60 : 76;
        byte[] text = Encoding.Unicode.GetBytes(name), data = new byte[8 + header + text.Length];
        BinaryPrimitives.WriteInt64LittleEndian(data, 12345);
        var record = data.AsSpan(8);
        BinaryPrimitives.WriteInt32LittleEndian(record, record.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(record[4..], version);
        BinaryPrimitives.WriteUInt64LittleEndian(record[8..], (ulong)file);
        if (version == 2) BinaryPrimitives.WriteUInt64LittleEndian(record[16..], (ulong)parent);
        else
        {
            BinaryPrimitives.WriteUInt64LittleEndian(record[16..], (ulong)(file >> 64));
            BinaryPrimitives.WriteUInt64LittleEndian(record[24..], (ulong)parent);
            BinaryPrimitives.WriteUInt64LittleEndian(record[32..], (ulong)(parent >> 64));
        }
        BinaryPrimitives.WriteUInt16LittleEndian(record[(header - 4)..], (ushort)text.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(record[(header - 2)..], (ushort)header);
        text.CopyTo(record[header..]);
        return data;
    }
    private static void Parsing()
    {
        foreach (ushort version in new ushort[] { 2, 3 })
        {
            UInt128 id = version == 2 ? ulong.MaxValue - 1 : ((UInt128)12 << 96) | 1234;
            var parsed = UsnParser.Parse(Record(version, id, 42, "\u7d22\u5f15-\U0001f680.txt"));
            Program.Require(parsed.Cursor == 12345 && parsed.Records.Single() == new UsnRecord(id, 42, "\u7d22\u5f15-\U0001f680.txt"),
                "USN identity or Unicode filename was truncated.");
        }
        byte[] unknown = Record(2, 1, 2, "file");
        BinaryPrimitives.WriteUInt16LittleEndian(unknown.AsSpan(12), 9);
        Program.Require(UsnParser.Parse(unknown).UnknownVersion, "Unknown journal versions did not request reconciliation.");
        Check.Throws<InvalidDataException>(() => UsnParser.Parse(new byte[4]));
        byte[] broken = Record(2, 1, 2, "file");
        BinaryPrimitives.WriteUInt16LittleEndian(broken.AsSpan(8 + 58), 2);
        Check.Throws<InvalidDataException>(() => UsnParser.Parse(broken));
        var known = new Dictionary<UInt128, string> { [1] = @"C:\scope", [2] = @"C:\scope\old.txt" };
        Program.Require(UsnParser.Resolve(new(2, 1, "new.txt"), known).SequenceEqual([@"C:\scope\old.txt", @"C:\scope\new.txt"]),
            "USN rename processing lost the new path for a known file identity.");
        Program.Require(UsnParser.Resolve(new(3, 1, ""), known).SequenceEqual([@"C:\scope"]) &&
            UsnParser.Resolve(new(2, 1, ""), known).SequenceEqual([@"C:\scope\old.txt", @"C:\scope"]),
            "Redacted USN names did not reconcile their containing directory.");
    }
    private static async Task NotificationsAsync() => await Check.TemporaryAsync(async root =>
    {
        string source = Path.Combine(root, "files"); Directory.CreateDirectory(source);
        using var store = new Catalog(Path.Combine(root, "index"), 1);
        int id = store.AddFolder(source);
        var events = new ConcurrentQueue<string?>();
        await using var monitor = new ChangeMonitor(store, (_, path, _) => events.Enqueue(path), _ => { });
        monitor.Configure(store.Folders());
        string path = Path.Combine(source, "\u7d22\u5f15.txt");
        await File.WriteAllTextAsync(path, "apple");
        await Check.UntilAsync(() => events.Contains(path), "Directory notifications missed a Unicode file.");
        string renamed = Path.Combine(source, "Renamed.TXT");
        while (events.TryDequeue(out _)) { }
        File.Move(path, renamed);
        await Check.UntilAsync(() => events.Contains(path) && events.Contains(renamed), "Directory rename did not report both paths.");
        Program.Require(id > 0, "The test scope was not persisted.");
        Console.WriteLine("PASS native recursive directory notifications without journal polling");
    });
    private static async Task JournalAsync() => await Check.TemporaryAsync(async root =>
    {
        string source = Path.Combine(root, "files"); Directory.CreateDirectory(source);
        using var store = new Catalog(Path.Combine(root, "index"), 1);
        int id = store.AddFolder(source);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        bool block = true;
        var events = new ConcurrentQueue<string?>();
        await using var monitor = new ChangeMonitor(store, (folder, path, _) =>
        {
            if (path is null) return;
            if (block) { entered.Set(); if (!release.Wait(5000)) throw new TimeoutException("Test delivery was not released."); }
            bool directory = Directory.Exists(path);
            string[] paths = directory ? Directory.GetFiles(path, "*", SearchOption.AllDirectories) : [path];
            foreach (string file in paths.Where(value => FilePaths.Kind(value) is not null))
                try
                {
                    if (File.Exists(file))
                        store.Enqueue(folder, file, FilePaths.Inspect(file), FilePaths.Kind(file)!, FilePaths.Hash(file), true);
                    else store.RemovePath(file);
                }
                catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
                { store.RemovePath(file); }
            if (directory) store.Prune(folder, path, paths.Select(FilePaths.Key).ToHashSet());
            events.Enqueue(path);
        }, Console.Error.WriteLine, directoryNotifications: false);
        monitor.Configure(store.Folders());
        if (!monitor.Description.StartsWith("USN journal", StringComparison.Ordinal))
        {
            Program.Require(!Check.RequireUsn, "This host cannot run the required USN-only acceptance test: " + monitor.Description);
            Console.WriteLine("SKIP live USN-only test on this volume: " + monitor.Description);
            return;
        }
        monitor.UpdateIdentities(id, new() { [UInt128.Parse(FilePaths.Inspect(source).Identity)] = source });
        monitor.Checkpoint();
        string key = "usn:" + Path.GetPathRoot(source)!.TrimEnd('\\').ToUpperInvariant();
        string? before = store.Setting(key);
        monitor.Start();
        string path = Path.Combine(source, "Journal only.txt");
        await File.WriteAllTextAsync(path, "apple");
        await Check.UntilAsync(() => entered.IsSet, "USN-only creation was not observed.", 15000);
        monitor.Checkpoint();
        Program.Require(store.Setting(key) == before, "The USN cursor advanced before change delivery was durable.");
        block = false; release.Set();
        await Check.UntilAsync(() => store.QueueCounts().Queued == 1, "USN callback did not persist its job.");
        var first = store.Claim()!;
        Program.Complete(store, first);
        monitor.UpdateIdentities(id, new()
        {
            [UInt128.Parse(FilePaths.Inspect(source).Identity)] = source,
            [UInt128.Parse(FilePaths.Inspect(path).Identity)] = path
        });
        await Check.UntilAsync(() => { monitor.Checkpoint(); return store.Setting(key) != before; },
            "The completed change did not advance the journal cursor.");
        var stamp = File.GetLastWriteTimeUtc(path);
        await File.WriteAllTextAsync(path, "boats"); File.SetLastWriteTimeUtc(path, stamp);
        await Check.UntilAsync(() => store.QueueCounts().Queued == 1, "USN missed a same-size preserved-timestamp edit.", 15000);
        Program.Complete(store, store.Claim()!, 1);
        string renamed = Path.Combine(source, "New journal name.txt");
        File.Move(path, renamed);
        await Check.UntilAsync(() => store.FindFile(path) is null && store.QueueCounts().Queued == 1,
            "USN-only rename did not replace the old path.", 15000);
        var rename = store.Claim()!;
        Program.Require(rename.Path == renamed, "USN rename routed the vector to an old path.");
        Program.Complete(store, rename, 1);
        File.Delete(renamed);
        await Check.UntilAsync(() => store.Counts() == (0, 0), "USN-only deletion retained vectors.", 15000);
        Console.WriteLine("PASS USN-only creation, preserved-time edits, renames, deletion and durable cursor ordering");
    });
    private static async Task DeltasAsync() => await Check.TemporaryAsync(async root =>
    {
        string source = Path.Combine(root, "files"); Directory.CreateDirectory(source);
        string data = Path.Combine(root, "index");
        string document = Path.Combine(source, "Quarterly Report.TXT");
        await File.WriteAllTextAsync(document, "apple");
        await using var engine = new IndexingEngine(data, Path.Combine(root, "models"), Path.Combine(root, "missing.exe"),
            Path.Combine(root, "app"), new(12, 6, 63), new FakeEmbeddings(), new EngineLog(data), new(), watch: false);
        int folder = engine.AddFolder(source);
        engine.Schedule(folder, document.ToLowerInvariant());
        engine.Start();
        await Check.UntilAsync(() => engine.Store.Counts() == (1, 1), "Initial discovery did not finish.");
        long originalVector = Check.Scalar(data, "SELECT id FROM vectors LIMIT 1");
        string sourceFile = Path.Combine(source, "parser.py");
        await File.WriteAllTextAsync(sourceFile, "def apple():\n    return 'apple'");
        engine.Schedule(folder, source, immediate: true);
        await Check.UntilAsync(() => engine.Store.Counts().Files == 2, "Code was not added beside existing documents.");
        Program.Require(Check.Scalar(data, "SELECT MIN(id) FROM vectors") == originalVector &&
            engine.Store.Metadata().TotalCodeFiles == 1 && engine.Store.FindFile(document)!.Path == document,
            "Code additions re-embedded stable documents or changed filename casing.");
        string generation = engine.Store.Setting("generation")!;
        engine.Schedule(folder, source, immediate: true);
        await Check.UntilAsync(() => engine.Status().Folders.All(value => value.Status == "Indexed"), "Directory reconciliation did not finish.");
        Program.Require(engine.Store.Setting("generation") == generation, "A directory notification re-embedded unchanged files.");
        var stamp = File.GetLastWriteTimeUtc(document);
        await File.WriteAllTextAsync(document, "boats"); File.SetLastWriteTimeUtc(document, stamp);
        engine.Schedule(folder, document, immediate: true);
        await Check.UntilAsync(() => engine.Store.FindFile(document)?.ContentHash == FilePaths.Hash(document), "Preserved-time content was not revised.");
        Program.Require((await engine.SearchAsync("boats")).Any(value => value.Path == document), "Changed content is not searchable.");
        string unsupported = Path.Combine(source, "renamed.exe");
        File.Move(document, unsupported);
        engine.Schedule(folder, document, immediate: true); engine.Schedule(folder, unsupported, immediate: true);
        await Check.UntilAsync(() => engine.Store.Counts().Files == 1, "Unsupported rename retained old vectors.");
        string empty = Path.Combine(source, "empty.txt"); File.WriteAllBytes(empty, []);
        engine.Schedule(folder, empty, immediate: true);
        await Check.UntilAsync(() => engine.Store.FindFile(empty)?.ErrorCode == "empty_file", "Empty content was not classified.");
        engine.Schedule(folder, source, immediate: true);
        await Check.UntilAsync(() => engine.Status().Folders.All(value => value.Status == "Indexed"), "Permanent failure did not leave a completed folder.");
        Program.Require(engine.Store.FindFile(empty)!.FailureCount == 1, "An unchanged permanent error was retried.");
        string dependency = Path.Combine(source, "filter.pdf"); File.WriteAllText(dependency, "filter fixture");
        engine.Schedule(folder, dependency, immediate: true);
        await Check.UntilAsync(() => engine.Store.FindFile(dependency)?.ErrorCode == "missing_dependency", "Missing extractor was misclassified.");
        Program.Require(File.Exists(dependency) && engine.Store.FindFile(dependency)!.RetryAfter > FilePaths.UnixNow,
            "Missing dependency deleted source state or became a permanent content error.");
        Check.Throws<ArgumentException>(() => engine.AddFolder(data));
        Console.WriteLine("PASS delta-only discovery, source-code additions, preserved casing and classified content/dependency failures");
    });
    private static async Task FilterLifecycleAsync() => await Check.TemporaryAsync(async root =>
    {
        string path = Path.Combine(root, "blocked.pdf"); File.WriteAllText(path, "fixture");
        File.WriteAllText(Path.Combine(root, "hang"), "wait");
        var extractor = new TextExtractor(Check.Executable, TimeSpan.FromSeconds(1));
        try { await extractor.ExtractAsync(path, default); throw new InvalidOperationException("The hung helper completed."); }
        catch (ContentException error)
        { Program.Require(error.Code == "filter_timeout" && error.Retryable, "The helper timeout was not retryable."); }
        using var cancellation = new CancellationTokenSource(400);
        await Check.ThrowsAsync<OperationCanceledException>(async () => await extractor.ExtractAsync(path, cancellation.Token));
        int pid = int.Parse(File.ReadAllText(Path.Combine(root, "blocked.entered")));
        try { using var process = System.Diagnostics.Process.GetProcessById(pid); Program.Require(process.HasExited, "A cancelled helper survived."); }
        catch (ArgumentException) { }
        Console.WriteLine("PASS IFilter timeout/cancellation and helper reaping");
    });

    private static async Task RestartDeltasAsync() => await Check.TemporaryAsync(async root =>
    {
        string source = Path.Combine(root, "files"), nested = Path.Combine(source, "removed");
        Directory.CreateDirectory(nested);
        string data = Path.Combine(root, "index"), keep = Path.Combine(source, "keep.txt"),
            touched = Path.Combine(source, "touched.txt"), changed = Path.Combine(source, "changed.txt"),
            gone = Path.Combine(nested, "deleted.txt"), queued = Path.Combine(source, "queued.txt");
        long keepId, touchedId, keepVector, touchedVector;
        int folder;
        using (var catalog = new Catalog(data, 1))
        {
            folder = catalog.AddFolder(source);
            foreach (string path in new[] { keep, touched, changed, gone })
                Program.Complete(catalog, Program.Enqueue(catalog, folder, path, "apple source"));
            keepId = catalog.FindFile(keep)!.Id; touchedId = catalog.FindFile(touched)!.Id;
            keepVector = Check.Scalar(data, $"SELECT id FROM vectors WHERE file_id={keepId}");
            touchedVector = Check.Scalar(data, $"SELECT id FROM vectors WHERE file_id={touchedId}");
            var job = Program.Enqueue(catalog, folder, queued, "apple zero\napple one\napple two");
            catalog.Prepare(job, ["apple zero", "apple one", "apple two"], null);
            catalog.SaveBatch(job, catalog.Remaining(job, 1), [Program.Vector(0)]);
        }
        var changedTime = File.GetLastWriteTimeUtc(changed);
        await File.WriteAllTextAsync(changed, "boats source");
        File.SetLastWriteTimeUtc(changed, changedTime.AddSeconds(20));
        File.SetLastWriteTimeUtc(touched, File.GetLastWriteTimeUtc(touched).AddSeconds(20));
        File.Delete(gone);
        Directory.Delete(nested);
        string addedDirectory = Path.Combine(source, "new-subfolder");
        Directory.CreateDirectory(addedDirectory);
        string added = Path.Combine(addedDirectory, "added.py");
        await File.WriteAllTextAsync(added, "apple added");

        IndexingEngine Engine(IEmbeddings model) => new(data, Path.Combine(root, "models"), Path.Combine(root, "unused.exe"),
            Path.Combine(root, "app"), new(12, 6, 63), model, new EngineLog(data), new(), watch: false);
        var replacement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new FakeEmbeddings
        {
            BeforeEmbedding = (passages, cancellation) => passages.Contains("boats source")
                ? replacement.Task.WaitAsync(cancellation) : Task.CompletedTask
        };
        await using (var resumed = Engine(model))
        {
            resumed.Start();
            await Check.UntilAsync(() => resumed.Store.Counts() == (5, 7) && resumed.Status().ReconcilingFolders == 0,
                "Startup did not reconcile offline additions, deletions and timestamp changes.");
            Program.Require(resumed.Store.QueueCounts().Queued > 0 && !model.Embedded.Contains("boats source"),
                "The fixture did not expose the same-count replacement before it completed.");
            replacement.SetResult();
            await Check.UntilAsync(() => resumed.Status() is { ModelState: "Ready", QueueCount: 0, ReconcilingFolders: 0 } &&
                resumed.Store.Counts() == (5, 7), "Startup did not finish replacing changed content.");
            Program.Require(resumed.Store.Folders().Single().Id == folder && resumed.Store.FindFile(gone) is null,
                "Restart lost folder configuration or retained a deleted subtree.");
            Program.Require(resumed.Store.FindFile(keep)!.Id == keepId && resumed.Store.FindFile(touched)!.Id == touchedId &&
                Check.Scalar(data, $"SELECT id FROM vectors WHERE file_id={keepId}") == keepVector &&
                Check.Scalar(data, $"SELECT id FROM vectors WHERE file_id={touchedId}") == touchedVector,
                "Startup reindexed unchanged content.");
            Program.Require(model.Embedded.Order().SequenceEqual(new[] { "boats source", "apple added", "apple one", "apple two" }.Order()),
                "Startup recomputed committed passages or skipped changed content.");
            Program.Require((await resumed.SearchAsync("boats")).Single().Path == changed,
                "The timestamp-updated file did not replace its old searchable content.");
        }
        string generation = Check.Scalar(data, "SELECT CAST(value AS INTEGER) FROM settings WHERE key='generation'").ToString();
        var unchanged = new FakeEmbeddings();
        await using (var next = Engine(unchanged))
        {
            next.Start();
            await Check.UntilAsync(() => next.Status() is { ModelState: "Ready", ReconcilingFolders: 0, QueueCount: 0 },
                "The unchanged restart did not finish reconciliation.");
            Program.Require(unchanged.Embedded.IsEmpty && next.Store.Setting("generation") == generation,
                "Restart without source changes rebuilt existing embeddings.");
        }
        Console.WriteLine("PASS timestamp-based offline deltas, deleted subfolders and unchanged-passage resume on restart");
    });

    private static async Task BackgroundStartupAsync() => await Check.TemporaryAsync(async root =>
    {
        string source = Path.Combine(root, "files"), data = Path.Combine(root, "index");
        Directory.CreateDirectory(source);
        using (var catalog = new Catalog(data, 1))
        {
            int folder = catalog.AddFolder(source);
            Program.Complete(catalog, Program.Enqueue(catalog, folder, Path.Combine(source, "ready.txt"), "apple"));
        }
        var monitor = new BlockingMonitor();
        var model = new FakeEmbeddings();
        var engine = new IndexingEngine(data, Path.Combine(root, "models"), Path.Combine(root, "unused.exe"),
            Path.Combine(root, "app"), new(12, 6, 63), model, new EngineLog(data), new(),
            monitorFactory: (_, _, _) => monitor);
        Task start = Task.Run(engine.Start);
        try
        {
            await Check.UntilAsync(() => monitor.Entered.IsSet, "Background monitoring setup did not begin.");
            await start.WaitAsync(TimeSpan.FromSeconds(2));
            await Check.UntilAsync(() => engine.Startup.Snapshot.State == "Ready",
                "A slow folder delayed model readiness.");
            Program.Require((await engine.SearchAsync("apple").WaitAsync(TimeSpan.FromSeconds(2))).Single().Name == "ready.txt" &&
                engine.Status().ReconcilingFolders == 1 && model.Embedded.IsEmpty,
                "Existing search was blocked by startup reconciliation.");
        }
        finally
        {
            monitor.Continue.Set();
            await start;
            await engine.DisposeAsync();
            monitor.Entered.Dispose(); monitor.Continue.Dispose();
        }
        Program.Require(monitor.Disposed, "Shutdown leaked the background monitor.");
        Console.WriteLine("PASS slow startup folder checks do not block readiness or existing-index search");
    });

    private sealed class BlockingMonitor : IChangeMonitor
    {
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Continue { get; } = new();
        public bool Disposed { get; private set; }
        public string Description
        {
            get
            {
                Program.Require(Continue.IsSet, "Status attempted to inspect a monitor while its initialization was blocked.");
                return "Test monitoring ready";
            }
        }
        public void Configure(IReadOnlyList<FolderState> folders)
        {
            Entered.Set();
            if (!Continue.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Monitoring fixture was not released.");
        }
        public void UpdateIdentities(int id, Dictionary<UInt128, string> values, bool complete = true) { }
        public void Start() { Program.Require(!Disposed, "Monitoring started after disposal."); }
        public void Checkpoint() { }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
