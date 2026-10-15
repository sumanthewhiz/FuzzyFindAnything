using System.Diagnostics;
using System.IO;
using FuzzyFindAnything.Core;

namespace FuzzyFindAnything.NativeTests;

internal static class StorageTests
{
    public static void Run()
    {
        Program.Run("Native vector bounds and ABI errors", VectorBounds);
        Program.Run("Updates, invalid vectors, permanent errors and metadata", Updates);
        Program.Run("Durable duplicate notifications, delta revisions and fair claims", Deltas);
        Program.Run("Retry backoff retains prepared passage progress", Retries);
        Program.Run("Native graph growth and old snapshot recovery", Growth);
        Program.Run("Interrupted checkpoint retains searchable committed data", InterruptedCheckpoint);
        Program.Run("Crash between durable batches and publication", CrashRecovery);
        Program.Run("Offline reset modes, preferences and active engine lock", Maintenance);
        Program.Run("Schema/model rejection preserves existing records", Schema);
        Program.Run("Source formats, Unicode scalars and lexical scope", Paths);
    }
    private static void VectorBounds()
    {
        using var graph = new NativeIndex(768, 8, 1);
        float[] invalid = Program.Vector(0); invalid[1] = float.NaN;
        Check.Throws<IOException>(() => graph.Insert(1, invalid));
        Check.Throws<ArgumentException>(() => graph.Insert(1, [1]));
        Check.Throws<InvalidDataException>(() => ModelContract.Normalize(new float[768]));
        Check.Throws<InvalidDataException>(() => ModelContract.Normalize(invalid));
        graph.Insert(uint.MaxValue, Program.Vector(3));
        Program.Require(graph.Search(Program.Vector(3), 1).Single() == uint.MaxValue, "The uint32 tag was truncated.");
        graph.Dispose();
        Check.Throws<ObjectDisposedException>(() => graph.Search(Program.Vector(3), 1));
    }
    private static void Updates() => Program.Temporary(root =>
    {
        string source = Path.Combine(root, "files"); Directory.CreateDirectory(source);
        using var store = new Catalog(Path.Combine(root, "index"), 1);
        int folder = store.AddFolder(source);
        string path = Path.Combine(source, "source.cs");
        var first = Program.Enqueue(store, folder, path, "apple", "Code");
        store.Prepare(first, ["old", "old duplicate"], null);
        store.SaveBatch(first, store.Remaining(first), [Program.Vector(0), Program.Vector(0)]);
        store.Complete(first);
        Program.Require(store.Search(Program.Vector(0), 50, 0.6).Count == 1, "File-level deduplication failed.");
        var second = Program.Enqueue(store, folder, path, "boats", "Code");
        Program.Complete(store, second, 1);
        Program.Require(store.Search(Program.Vector(0), 50, 0.6).Count == 0 &&
            store.Search(Program.Vector(1), 50, 0.6).Count == 1, "An update returned stale vectors.");
        var failing = Program.Enqueue(store, folder, path, "bad content", "Code");
        store.Prepare(failing, ["invalid"], null);
        Check.Throws<InvalidDataException>(() => store.SaveBatch(failing, store.Remaining(failing), [new float[768]]));
        Program.Require(store.Progress(failing) == (0, 1), "An invalid vector was committed.");
        store.Fail(failing, "bad", "invalid_document", false, 42);
        Program.Require(store.QueueCounts().Queued == 0 && store.Counts() == (0, 0) &&
            store.Search(Program.Vector(1), 10, 0).Count == 0, "A failed reindex retained stale results.");
        Program.Require(!store.Enqueue(folder, path, FilePaths.Inspect(path), "Code", FilePaths.Hash(path), false),
            "Permanent failure retried unchanged content.");
        Program.Require(store.FindFile(path)!.FailureCount == 1 && store.Metadata().LastRefreshed is not null,
            "Outcome metadata was lost.");
        var recovered = Program.Enqueue(store, folder, path, "repaired");
        Program.Complete(store, recovered, 2);
        File.Delete(path);
        Program.Require(store.Search(Program.Vector(2), 10, 0).Count == 0, "Deleted sources were returned.");
        store.Prune(folder, source, []);
        Program.Require(store.Counts() == (0, 0), "Missing sources were not pruned.");
    });
    private static void Deltas() => Program.Temporary(root =>
    {
        string source = Path.Combine(root, "files"); Directory.CreateDirectory(source);
        using var store = new Catalog(Path.Combine(root, "index"), 1);
        int folder = store.AddFolder(source);
        string path = Path.Combine(source, "large.txt");
        var job = Program.Enqueue(store, folder, path, "apple");
        store.Prepare(job, Enumerable.Range(0, 20).Select(index => "passage " + index).ToArray(), null);
        store.SaveBatch(job, store.Remaining(job, 4), Enumerable.Repeat(Program.Vector(0), 4).ToArray());
        store.Enqueue(folder, path, FilePaths.Inspect(path), "Document", FilePaths.Hash(path), true);
        Program.Require(store.IsCurrent(job) && store.Progress(job) == (4, 20), "Repeated notifications reset saved batches.");
        string added = Path.Combine(source, "added.txt"); File.WriteAllText(added, "boats");
        store.Enqueue(folder, added, FilePaths.Inspect(added), "Document", FilePaths.Hash(added), false);
        store.Release(job, 30);
        var small = store.Claim()!;
        Program.Require(small.Path == added, "A large job did not yield to another file.");
        Program.Complete(store, small, 1);
        job = store.Claim()!;
        Program.Require(job.ElapsedMs == 30 && store.Progress(job) == (4, 20), "Yield lost progress or elapsed time.");
        var stamp = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, "boats"); File.SetLastWriteTimeUtc(path, stamp);
        store.Enqueue(folder, path, FilePaths.Inspect(path), "Document", FilePaths.Hash(path), true);
        Program.Require(!store.IsCurrent(job) && !store.Complete(job) &&
            !store.SaveBatch(job, [new Passage(4, "stale")], [Program.Vector(0)]), "Preserved-time edits accepted stale results.");
        var current = store.Claim()!;
        Program.Complete(store, current, 1);
        string generation = store.Setting("generation")!;
        File.WriteAllText(path, "apple");
        store.Enqueue(folder, path, FilePaths.Inspect(path), "Document", FilePaths.Hash(path), true);
        var obsolete = store.Claim()!;
        File.WriteAllText(path, "boats");
        store.Enqueue(folder, path, FilePaths.Inspect(path), "Document", FilePaths.Hash(path), true);
        Program.Require(!store.IsCurrent(obsolete) && store.QueueCounts().Queued == 0 && store.Setting("generation") == generation,
            "Reverting to committed content retained obsolete work.");
        var removed = Program.Enqueue(store, folder, path, "pending");
        store.RemoveFolders([folder]);
        Program.Require(!store.Complete(removed), "A removed root committed an in-flight file.");
    });
    private static void Retries() => Program.Temporary(root =>
    {
        string source = Path.Combine(root, "files"); Directory.CreateDirectory(source);
        string data = Path.Combine(root, "index");
        using var store = new Catalog(data, 1);
        int folder = store.AddFolder(source);
        var job = Program.Enqueue(store, folder, Path.Combine(source, "retry.txt"), "apple");
        store.Prepare(job, ["one", "two"], null);
        store.SaveBatch(job, store.Remaining(job, 1), [Program.Vector(0)]);
        store.Fail(job, "temporary problem", "inference_failure", true, 15);
        Program.Require(store.Claim() is null && store.Progress(job) == (1, 2) &&
            store.FindFile(job.Path)!.RetryAfter > FilePaths.UnixNow, "Retry discarded progress or ignored backoff.");
        Check.Execute(data, "UPDATE index_jobs SET retry_at=0");
        var retry = store.Claim()!;
        Program.Require(retry.Attempts == 1 && retry.ElapsedMs == 15 && retry.Prepared, "Retry state was not retained.");
        store.SaveBatch(retry, store.Remaining(retry), [Program.Vector(1)]);
        store.Complete(retry);
        Program.Require(store.Counts() == (1, 2) && store.QueueCounts().Queued == 0, "Retried file did not publish atomically.");
    });
    private static void Growth() => Program.Temporary(root =>
    {
        string source = Path.Combine(root, "files"); Directory.CreateDirectory(source);
        string data = Path.Combine(root, "index");
        using (var store = new Catalog(data, 2))
        {
            int folder = store.AddFolder(source);
            var job = Program.Enqueue(store, folder, Path.Combine(source, "large.txt"), "many passages");
            var passages = Enumerable.Range(0, 140).Select(index => "passage " + index).ToArray();
            store.Prepare(job, passages, null);
            store.SaveBatch(job, passages.Select((text, index) => new Passage(index, text)).ToArray(),
                Enumerable.Range(0, 140).Select(Program.Vector).ToArray());
            store.Complete(job);
            Program.Require(store.Search(Program.Vector(139), 1, 0.9).Single().Snippet == "passage 139", "Capacity growth lost a vector.");
        }
        using (var store = new Catalog(data, 2))
            Program.Require(store.Counts() == (1, 140) && store.Recovery.Length == 0, "A valid snapshot was not restored.");
        File.Delete(Path.Combine(data, "diskann", "current.json"));
        using var restored = new Catalog(data, 2);
        Program.Require(restored.Search(Program.Vector(120), 1, 0.9).Single().Snippet == "passage 120",
            "Missing native snapshots did not rebuild from committed embeddings.");
    });
    private static void InterruptedCheckpoint() => Program.Temporary(root =>
    {
        string source = Path.Combine(root, "files"); Directory.CreateDirectory(source);
        using var store = new Catalog(Path.Combine(root, "index"), 1);
        int folder = store.AddFolder(source);
        Program.Complete(store, Program.Enqueue(store, folder, Path.Combine(source, "saved.txt"), "apple"));
        using (var locked = new FileStream(Path.Combine(store.IndexDirectory, "current.json.tmp"), FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            Check.Throws<IOException>(store.Checkpoint);
        Program.Require(store.Search(Program.Vector(0), 1, 0.9).Count == 1 && store.QueueCounts().Queued == 0,
            "Checkpoint failure hid a committed file or restored completed work to the queue.");
        store.Checkpoint();
    });
    public static int CrashFixture(string root, string stage)
    {
        string source = Path.Combine(root, "files"); Directory.CreateDirectory(source);
        var store = new Catalog(Path.Combine(root, "index"), 1);
        int folder = store.AddFolder(source);
        Program.Complete(store, Program.Enqueue(store, folder, Path.Combine(source, "completed.txt"), "finished"), 2);
        store.Checkpoint();
        var first = Program.Enqueue(store, folder, Path.Combine(source, "first.txt"), "first");
        store.Prepare(first, ["one", "two"], null);
        var second = Program.Enqueue(store, folder, Path.Combine(source, "second.txt"), "second");
        store.Prepare(second, ["three"], null);
        store.SaveBatch(first, [new Passage(0, "one")], [Program.Vector(0)]);
        if (stage == "published")
        {
            store.SaveBatch(second, store.Remaining(second), [Program.Vector(1)]);
            store.Complete(second);
        }
        Process.GetCurrentProcess().Kill();
        return 25;
    }
    private static void CrashRecovery()
    {
        foreach (string stage in new[] { "batched", "published" })
            Program.Temporary(root =>
            {
                var start = new ProcessStartInfo(Check.Executable) { UseShellExecute = false, CreateNoWindow = true };
                foreach (string argument in new[] { "crash-fixture", root, stage }) start.ArgumentList.Add(argument);
                using var process = Process.Start(start)!;
                Program.Require(process.WaitForExit(30000) && process.ExitCode != 0, "Crash fixture did not terminate.");
                using var store = new Catalog(Path.Combine(root, "index"), 1);
                Program.Require(store.Counts().Files == (stage == "published" ? 2 : 1), "Crash lost a completed file.");
                var resumed = store.Claim()!;
                Program.Require(Path.GetFileName(resumed.Path) == "first.txt" && store.Progress(resumed) == (1, 2),
                    "Crash lost a committed member of a cross-file batch.");
                store.SaveBatch(resumed, store.Remaining(resumed), [Program.Vector(0)]);
                store.Complete(resumed);
                var pending = store.Claim();
                if (stage == "batched") { Program.Require(pending is not null, "Uncommitted neighbor disappeared."); Program.Complete(store, pending!, 1); }
                Program.Require(store.Counts() == (3, 4) && store.QueueCounts().Queued == 0, "Resume recomputed or lost staged work.");
            });
    }
    private static void Maintenance() => Program.Temporary(root =>
    {
        string source = Path.Combine(root, "files"); Directory.CreateDirectory(source);
        string data = Path.Combine(root, "index");
        int folder;
        using (var store = new Catalog(data, 1))
        {
            folder = store.AddFolder(source);
            Program.Complete(store, Program.Enqueue(store, folder, Path.Combine(source, "keep.txt"), "source"));
            var pending = Program.Enqueue(store, folder, Path.Combine(source, "pending.txt"), "pending");
            store.Prepare(pending, ["prepared"], null);
            store.SetSetting("usn:C:", "{\"cursor\":1,\"id\":1}");
        }
        string preferences = Path.Combine(data, "settings.xml");
        File.WriteAllText(preferences, "<preferences unchanged=\"true\"/>");
        using (var lease = new EngineLease(data)) Check.Throws<IOException>(() => CatalogMaintenance.Reset(data, false));
        var reset = CatalogMaintenance.Reset(data, false);
        using (var store = new Catalog(data, 1))
            Program.Require(store.Folders().Single().Id == folder && store.Counts() == (0, 0) &&
                store.QueueCounts().Queued == 0 && store.Setting("usn:C:") is null, "Retained-scope reset lost scope or retained generated state.");
        Program.Require(File.Exists(reset.FolderBackup), "Maintenance did not save scope backup.");
        CatalogMaintenance.Reset(data, true);
        using var cleared = new Catalog(data, 1);
        Program.Require(cleared.Folders().Count == 0 && File.ReadAllText(preferences) == "<preferences unchanged=\"true\"/>" &&
            File.Exists(Path.Combine(source, "keep.txt")), "Explicit reset modified sources/preferences or retained roots.");
    });
    private static void Schema() => Program.Temporary(root =>
    {
        using (var store = new Catalog(root, 1)) store.AddFolder(root);
        Check.Execute(root, "UPDATE settings SET value='99' WHERE key='schema_version'");
        Check.Throws<InvalidDataException>(() => { using var rejected = new Catalog(root, 1); });
        Program.Require(Check.Scalar(root, "SELECT COUNT(*) FROM folders") == 1, "Schema rejection lost folder data.");
        Check.Execute(root, "UPDATE settings SET value='4' WHERE key='schema_version'; UPDATE settings SET value='different-model' WHERE key='model'");
        Check.Throws<InvalidDataException>(() => { using var rejected = new Catalog(root, 1); });
        Program.Require(Check.Scalar(root, "SELECT COUNT(*) FROM folders") == 1, "Model rejection reset the catalog.");
    });
    private static void Paths()
    {
        foreach (string extension in FilePaths.Code) Program.Require(FilePaths.Kind("source" + extension.ToUpperInvariant()) == "Code", extension);
        foreach (string name in FilePaths.CodeNames) Program.Require(FilePaths.Kind(name.ToUpperInvariant()) == "Code", name);
        foreach (string extension in FilePaths.Documents) Program.Require(FilePaths.Kind("file" + extension) == "Document", extension);
        foreach (string extension in FilePaths.Images) Program.Require(FilePaths.Kind("file" + extension) == "Image", extension);
        foreach (string name in new[] { "a.exe", "a.pyc", "a.class", "a.dll", "a.jar", "a.RData" })
            Program.Require(FilePaths.Kind(name) is null, "Compiled data was treated as source.");
        Program.Require(FilePaths.Within(@"C:\Files\A.txt", @"c:\files") && !FilePaths.Within(@"C:\Files2\A.txt", @"C:\Files") &&
            FilePaths.Within(@"\\server\share\a.txt", @"\\server\share"), "Lexical scope boundaries differ.");
        string title = string.Concat(Enumerable.Repeat("\U0001f680", 170));
        Program.Require(FilePaths.ScalarPrefix(title, 160).Length == 320 &&
            FilePaths.CollapseWhitespace(" a\u001c\u001d\u001e\u001f b\t ") == "a b", "Unicode scalar/whitespace semantics differ.");
    }
}
