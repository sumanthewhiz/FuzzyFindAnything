using System.IO;
using System.Text;
using System.Text.Json;
using FuzzyFindAnything.Core;

namespace FuzzyFindAnything.NativeTests;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--model") return TestWorker.Run(args);
        if (args.Length == 1 && Path.GetExtension(args[0]) == ".pdf") return TestWorker.Filter(args[0]);
        if (args.Length > 0 && args[0] == "crash-fixture") return StorageTests.CrashFixture(args[1], args[2]);
        CpuBudget.Apply();
        Check.RequireUsn = args.Contains("--require-usn");
        if (args.Length > 0 && args[0] == "parity") return Parity.Run(args[1], args[2], args[3]);
        if (args.Length > 0 && args[0] == "product-parity") return ProductParity.Run(args[1], args[2], args[3]);
        if (args.Length > 0 && args[0] == "monitoring") { MonitoringTests.RunAsync().GetAwaiter().GetResult(); return 0; }
        if (args.Length > 0 && args[0] == "assets") { AssetTests.RunAsync().GetAwaiter().GetResult(); return 0; }
        if (args.Length > 0 && args[0] == "ui-fixture") { Fixtures.Ui(args[1]); return 0; }
        if (args.Length > 0 && args[0] == "fixtures") { Fixtures.Corpus(args[1]); return 0; }
        if (args.Length > 0 && args[0] == "numeric-fixture") { Fixtures.NumericWorkbook(args[1]); return 0; }
        if (args.Length > 0 && args[0] == "integration") { Integration.RunAsync(args[1], args[2], args[3]).GetAwaiter().GetResult(); return 0; }
        if (args.Length > 0 && args[0] == "compatibility") { Compatibility.Run(args[1], args[2]); return 0; }
        if (args.Length > 0 && args[0] == "benchmark") { Throughput.RunAsync(args[1], args[2], args[3]).GetAwaiter().GetResult(); return 0; }
        if (args.Length > 0 && args[0] == "lookup-file") { FileValidation.Lookup(args[1], args[2]); return 0; }
        if (args.Length > 0 && args[0] == "verify-files") { FileValidation.RunAsync(args[1], args[2], args[3], args[4..]).GetAwaiter().GetResult(); return 0; }
        Run("Native graph and collision-safe tags", Graph);
        Run("Catalog persistence, Unicode and recovery", CatalogPersistence);
        Run("Durable batch resume and source revisions", QueueRecovery);
        Run("Overlapping folder ownership", Ownership);
        Run("Native source extraction", () => Extraction().GetAwaiter().GetResult());
        StorageTests.Run();
        SchedulingTests.RunAsync().GetAwaiter().GetResult();
        MonitoringTests.RunAsync().GetAwaiter().GetResult();
        AssetTests.RunAsync().GetAwaiter().GetResult();
        ReleaseTests.Run();
        Console.WriteLine("NATIVE CORE CHECKS PASSED");
        return 0;
    }
    internal static void Run(string name, Action action) { action(); Console.WriteLine("PASS " + name); }
    internal static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    internal static float[] Vector(int axis)
    {
        float[] value = new float[768]; value[axis] = 1; return value;
    }
    internal static void Temporary(Action<string> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "ffa-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { action(root); } finally { Directory.Delete(root, true); }
    }
    private static void Graph()
    {
        using var graph = new NativeIndex(768, 512, 2);
        float[] Point(uint index)
        {
            float[] point = new float[768];
            point[0] = (float)Math.Cos(index * 2 * Math.PI / 200);
            point[1] = (float)Math.Sin(index * 2 * Math.PI / 200);
            point[2] = (float)(0.1 * Math.Sin(index * 6 * Math.PI / 200));
            return ModelContract.Normalize(point);
        }
        for (uint index = 0; index < 200; index++) graph.Insert(1 + index * 65536, Point(index));
        for (uint index = 0; index < 200; index += 7)
        {
            var matches = graph.Search(Point(index), 32);
            Require(matches[0] == 1 + index * 65536,
                $"Native search lost sparse tag {1 + index * 65536}; returned {matches[0]} from {matches.Length} candidates.");
        }
        for (uint index = 0; index < 100; index++) graph.Delete(1 + index * 65536);
        Require(graph.Search(Point(199), 1)[0] == 1 + 199 * 65536, "Delete damaged another vector.");
        try { graph.Insert(0, Vector(1)); throw new InvalidOperationException("Reserved tag zero was accepted."); }
        catch (IOException) { }
    }
    internal static IndexJob Enqueue(Catalog catalog, int folder, string path, string text, string kind = "Document")
    {
        File.WriteAllText(path, text, new UTF8Encoding(false));
        catalog.Enqueue(folder, path, FilePaths.Inspect(path), kind, FilePaths.Hash(path), false);
        return catalog.Claim() ?? throw new InvalidOperationException("The job was not queued.");
    }
    internal static void Complete(Catalog catalog, IndexJob job, int axis = 0)
    {
        catalog.Prepare(job, ["content"], null);
        catalog.SaveBatch(job, [new Passage(0, "content")], [Vector(axis)]);
        Require(catalog.Complete(job), "The file was not published.");
    }
    private static void CatalogPersistence() => Temporary(root =>
    {
        string data = Path.Combine(root, "\u7d22\u5f15-\u03b1");
        string source = Path.Combine(root, "files"); Directory.CreateDirectory(source);
        int id;
        using (var catalog = new Catalog(data, 2))
        {
            id = catalog.AddFolder(source);
            Complete(catalog, Enqueue(catalog, id, Path.Combine(source, "source.py"), "apple", "Code"));
            Complete(catalog, Enqueue(catalog, id, Path.Combine(source, "notes.txt"), "budget"), 1);
            Require(catalog.Metadata().TotalCodeFiles == 1 && catalog.Counts() == (2, 2), "Metadata does not include all file kinds.");
        }
        using (var catalog = new Catalog(data, 2))
        {
            Require(catalog.Search(Vector(0), 10, 0.6).Single().Kind == "Code", "Reloaded native graph lost code results.");
            Require(catalog.Folders().Single().Id == id, "Folder identity was reset.");
        }
        using (var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(data, "diskann", "current.json"))))
        {
            string graph = Path.Combine(data, "diskann", manifest.RootElement.GetProperty("snapshot").GetString()!, "ann");
            using var stream = new FileStream(graph, FileMode.Open, FileAccess.Write);
            stream.WriteByte(0xFF);
        }
        using var recovered = new Catalog(data, 2);
        Require(recovered.Recovery.Length > 0 && recovered.Search(Vector(1), 10, 0.6).Count == 1,
            "Corrupted graph was not recovered from durable vectors.");
    });
    private static void QueueRecovery() => Temporary(root =>
    {
        string source = Path.Combine(root, "files"); Directory.CreateDirectory(source);
        string data = Path.Combine(root, "index"), path = Path.Combine(source, "pending.py");
        IndexJob previous;
        using (var catalog = new Catalog(data, 2))
        {
            int folder = catalog.AddFolder(source);
            previous = Enqueue(catalog, folder, path, "first", "Code");
            catalog.Prepare(previous, ["one", "two"], null);
            catalog.SaveBatch(previous, [new Passage(0, "one")], [Vector(0)]);
        }
        using var resumed = new Catalog(data, 2);
        var job = resumed.Claim()!;
        Require(job.Id == previous.Id && resumed.Progress(job) == (1, 2), "Committed passage progress did not survive restart.");
        File.WriteAllText(path, "different revision");
        resumed.Enqueue(resumed.Folders()[0].Id, path, FilePaths.Inspect(path), "Code", FilePaths.Hash(path), true);
        Require(!resumed.SaveBatch(job, [new Passage(1, "two")], [Vector(1)]), "A stale revision accepted new vectors.");
        var changed = resumed.Claim()!;
        Complete(resumed, changed);
        Require(resumed.QueueCounts().Queued == 0 && resumed.Counts() == (1, 1), "Publication and dequeue are inconsistent.");
    });
    private static void Ownership() => Temporary(root =>
    {
        string source = Path.Combine(root, "files"), nested = Path.Combine(source, "nested");
        Directory.CreateDirectory(nested);
        using var catalog = new Catalog(Path.Combine(root, "index"), 2);
        int parent = catalog.AddFolder(source), child = catalog.AddFolder(nested);
        string path = Path.Combine(nested, "notes.txt");
        var job = Enqueue(catalog, parent, path, "content");
        catalog.Enqueue(child, path, FilePaths.Inspect(path), "Document", FilePaths.Hash(path), false);
        catalog.RemoveFolders([parent]);
        Complete(catalog, job);
        Require(catalog.Search(Vector(0), 5, 0.5).Count == 1, "Removing one root discarded another root's pending file.");
        catalog.RemoveFolders([child]);
        Require(catalog.Counts() == (0, 0) && File.Exists(path), "Removing scope deleted source data or retained orphan vectors.");
    });
    private static async Task Extraction()
    {
        string root = Path.Combine(Path.GetTempPath(), "ffa-native-text-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var extractor = new TextExtractor(Path.Combine(root, "not-installed.exe"));
            string path = Path.Combine(root, "source.py");
            const string source = "magic = 'Standard ACE DB / %PDF-1.7'\n    # caf\u00e9";
            foreach (Encoding encoding in new Encoding[] { new UTF8Encoding(false), Encoding.Unicode, Encoding.UTF32 })
            {
                await File.WriteAllTextAsync(path, source, encoding);
                Require((await extractor.ExtractAsync(path, default)).Text.Replace("\r\n", "\n") == source, "Source decoding changed content.");
            }
            await File.WriteAllBytesAsync(path, [0x4D, 0x5A, 0]);
            try { await extractor.ExtractAsync(path, default); throw new InvalidOperationException("Binary code was accepted."); }
            catch (ContentException error) { Require(error.Code == "unsupported_content", "Binary code error was not classified."); }
        }
        finally { Directory.Delete(root, true); }
    }
}
