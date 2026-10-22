using System.IO;
using FuzzyFindAnything.Core;

namespace FuzzyFindAnything.NativeTests;

internal static class ModelConfigurationTests
{
    public static void Run()
    {
        Program.Run("Model URL restrictions and canonical no-op values", Urls);
        Program.Run("Independent model indexes and selective invalidation", Shards);
        Program.Run("Durable model reindex completion and targeted image recovery", ReindexStatus);
        Program.Run("Independent search budgets across embedding spaces", () => SearchBudgetsAsync().GetAwaiter().GetResult());
    }
    private static void Urls()
    {
        Program.Require(ModelSelection.Canonical("  https://HUGGINGFACE.CO/google/embeddinggemma-2/ ") == ModelSelection.DefaultUrl,
            "Equivalent model-card URLs were not normalized.");
        foreach (string value in new[] { "", "http://huggingface.co/a/b", "https://example.com/a/b",
            "https://huggingface.co.evil.test/a/b", "https://name@huggingface.co/a/b", "https://huggingface.co/a/b?token=secret",
            "https://huggingface.co/datasets/a/b", "https://huggingface.co/a/b/tree/main", "https://huggingface.co:5000/a/b" })
            Check.Throws<ArgumentException>(() => ModelSelection.Canonical(value));
    }
    private static EmbeddingSpace Space(string name, int dimensions, bool image = false) =>
        new("https://huggingface.co/test/" + name, new string('1', 40), image ? "clip" : "bert-mean", dimensions, 128, image, 1L << 30);
    private static float[] Vector(int dimensions) { float[] value = new float[dimensions]; value[0] = 1; return value; }
    private static void Publish(Catalog catalog, int folder, string path, string kind, float[]? vector = null)
    {
        var job = Program.Enqueue(catalog, folder, path, "content", kind);
        catalog.Prepare(job, ["content"], null);
        catalog.SaveBatch(job, catalog.Remaining(job), [vector ?? Vector(catalog.SpaceFor(kind).Dimensions)]);
        Program.Require(catalog.Complete(job), "Could not publish the model-specific vector.");
    }
    private static void Shards() => Program.Temporary(root =>
    {
        string source = Path.Combine(root, "files"), data = Path.Combine(root, "index");
        Directory.CreateDirectory(source);
        string document = Path.Combine(source, "notes.txt"), code = Path.Combine(source, "source.py"), image = Path.Combine(source, "image.png");
        var bert = Space("bert", 384); var clip = Space("clip", 512, true);
        long codeVector;
        using (var catalog = new Catalog(data, 1))
        {
            int folder = catalog.AddFolder(source);
            Publish(catalog, folder, document, "Document"); Publish(catalog, folder, code, "Code"); Publish(catalog, folder, image, "Image");
            catalog.Checkpoint();
            string initial = FilePaths.Hash(Path.Combine(catalog.IndexDirectory, "current.json"));
            var defaults = new Dictionary<string, EmbeddingSpace>
            { ["Document"] = EmbeddingSpace.Default, ["Code"] = EmbeddingSpace.Default, ["Image"] = EmbeddingSpace.Default };
            Program.Require(catalog.ReconfigureModels(defaults, () => throw new InvalidOperationException("No-op saved XML.")).Length == 0 &&
                FilePaths.Hash(Path.Combine(catalog.IndexDirectory, "current.json")) == initial, "An unchanged configuration mutated its index.");
            var split = new Dictionary<string, EmbeddingSpace>(defaults) { ["Code"] = bert };
            string settingsPath = Path.Combine(data, "settings.xml");
            File.WriteAllText(settingsPath, "<FuzzyFindAnythingConfiguration version=\"1\"><Appearance theme=\"Dark\"/></FuzzyFindAnythingConfiguration>");
            byte[] originalSettings = File.ReadAllBytes(settingsPath);
            using (var update = new ModelSettingsUpdate(data, new(ModelSelection.DefaultUrl, bert.ModelUrl, ModelSelection.DefaultUrl)))
                Check.Throws<IOException>(() => catalog.ReconfigureModels(split, () =>
                {
                    update.Publish();
                    throw new IOException("Simulated failure before the catalog transaction commits.");
                }));
            Program.Require(originalSettings.SequenceEqual(File.ReadAllBytes(settingsPath)) && catalog.Counts() == (3, 3) &&
                catalog.Selection == ModelSelection.Default, "An interrupted configuration publication failed to restore XML/catalog state.");
            long documentVector = Check.Scalar(data, "SELECT v.id FROM vectors v JOIN files f ON f.id=v.file_id WHERE f.kind='Document'");
            int persisted = 0;
            Program.Require(catalog.ReconfigureModels(split, () => persisted++).SequenceEqual(["Code"]) && persisted == 1,
                "Changing the code model invalidated the wrong file types.");
            Program.Require(catalog.Spaces.Count == 2 && catalog.Counts() == (2, 2) && catalog.FindFile(code) is null &&
                Check.Scalar(data, "SELECT v.id FROM vectors v JOIN files f ON f.id=v.file_id WHERE f.kind='Document'") == documentVector,
                "Unchanged document/image data was discarded.");
            Publish(catalog, folder, code, "Code");
            Program.Require(catalog.Search(Vector(384), 10, 0.9, bert).Single().Kind == "Code" &&
                catalog.Search(Vector(768), 10, 0.9).Count == 2, "Search mixed embeddings from incompatible spaces.");
            codeVector = Check.Scalar(data, "SELECT v.id FROM vectors v JOIN files f ON f.id=v.file_id WHERE f.kind='Code'");
            var invalid = new Dictionary<string, EmbeddingSpace>(split) { ["Image"] = bert };
            Check.Throws<InvalidDataException>(() => catalog.ReconfigureModels(invalid, () => { }));
            var three = new Dictionary<string, EmbeddingSpace>(split) { ["Image"] = clip };
            Check.Throws<IOException>(() => catalog.ReconfigureModels(three, () => throw new IOException("XML is locked")));
            Program.Require(catalog.Spaces.Count == 2 && catalog.Counts() == (3, 3), "A failed settings write destroyed an index.");
            catalog.ReconfigureModels(three, () => { });
            Publish(catalog, folder, image, "Image");
            Program.Require(catalog.Spaces.Count == 3 && catalog.Counts() == (3, 3), "Three distinct selections did not create three indexes.");
            three["Document"] = bert;
            catalog.ReconfigureModels(three, () => { });
            Publish(catalog, folder, document, "Document");
            Program.Require(catalog.Spaces.Count == 2 &&
                Check.Scalar(data, "SELECT v.id FROM vectors v JOIN files f ON f.id=v.file_id WHERE f.kind='Code'") == codeVector &&
                catalog.Search(Vector(384), 10, 0.9, bert).Count == 2, "Shared text/code selection replaced unchanged code or failed to share its index.");
        }
        using (var reopened = new Catalog(data, 1))
        {
            Program.Require(reopened.Spaces.Count == 2 && reopened.Search(Vector(384), 10, 0.9, bert).Count == 2 &&
                reopened.Search(Vector(512), 10, 0.9, clip).Count == 1, "Model-specific indexes did not survive restart.");
            var together = new Dictionary<string, EmbeddingSpace> { ["Document"] = clip, ["Code"] = clip, ["Image"] = clip };
            reopened.ReconfigureModels(together, () => { });
            int folder = reopened.Folders().Single().Id;
            Publish(reopened, folder, document, "Document"); Publish(reopened, folder, code, "Code");
            Program.Require(reopened.Spaces.Count == 1 && reopened.Search(Vector(512), 10, 0.9, clip).Count == 3,
                "A common multimodal selection did not consolidate its index.");
        }
        CatalogMaintenance.Reset(data, false);
        using var reset = new Catalog(data, 1);
        Program.Require(reset.Spaces.Count == 1 && reset.Counts() == (0, 0), "Reset retained stale model-specific vectors.");
    });

    private static void ReindexStatus() => Program.Temporary(root =>
    {
        string data = Path.Combine(root, "index"), source = Path.Combine(root, "files");
        Directory.CreateDirectory(source);
        string image = Path.Combine(source, "rounding.jpg"), healthy = Path.Combine(source, "healthy.png");
        string document = Path.Combine(source, "notes.txt"), invalid = Path.Combine(source, "invalid.txt");
        long documentVector;
        using (var catalog = new Catalog(data, 1))
        {
            int folder = catalog.AddFolder(source);
            Publish(catalog, folder, document, "Document");
            documentVector = Check.Scalar(data, "SELECT v.id FROM vectors v JOIN files f ON f.id=v.file_id WHERE f.kind='Document'");
            var clip = Space("clip-progress", 512, true);
            catalog.ReconfigureModels(new Dictionary<string, EmbeddingSpace>
            { ["Document"] = EmbeddingSpace.Default, ["Code"] = EmbeddingSpace.Default, ["Image"] = clip }, () => { });
            Program.Require(catalog.ModelReindex(false) is { Completed: false }, "An undiscovered empty queue was reported complete.");
            Publish(catalog, folder, healthy, "Image");
            var failed = Program.Enqueue(catalog, folder, image, "valid image", "Image");
            Program.Require(catalog.ModelReindex(true) is { Completed: false }, "Active affected work was reported complete.");
            catalog.Fail(failed, "Index was outside the bounds of the array.", "model_inference", false, 1);
            var badDocument = Program.Enqueue(catalog, folder, invalid, "bad document");
            catalog.Fail(badDocument, "Invalid document", "invalid_document", false, 1);
            catalog.SetSetting("scan_error:" + folder, "Folder temporarily unavailable");
            Program.Require(catalog.ModelReindex(true) is { Completed: false }, "Incomplete folder discovery was reported complete.");
            catalog.SetSetting("scan_error:" + folder, "");
        }
        using (var resumed = new Catalog(data, 1))
        {
            int folder = resumed.Folders().Single().Id;
            Program.Require(resumed.ModelReindex(false) is { Completed: false, Succeeded: 1, Failed: 1 },
                "Pending model reindex state was lost across restart.");
            var unrelated = Program.Enqueue(resumed, folder, Path.Combine(source, "unrelated.txt"), "unrelated work");
            Program.Require(resumed.ModelReindex(true) is { Completed: true, Succeeded: 1, Failed: 1 },
                "Completion included unrelated errors or waited for unaffected file types.");
            Program.Complete(resumed, unrelated);
        }
        using (var reopened = new Catalog(data, 1))
        {
            int folder = reopened.Folders().Single().Id;
            Program.Require(reopened.ModelReindex(false) is { Completed: true, Failed: 1 },
                "Completed-with-errors status was not durable.");
            Program.Require(reopened.RetryClipResizeFailures() == 1 && reopened.RetryClipResizeFailures() == 0 &&
                reopened.FindFile(invalid)?.RetryAfter is null && reopened.ModelReindex(false) is { Completed: false },
                "Legacy CLIP recovery retried unrelated failures or failed to reopen progress.");
            Program.Require(reopened.Enqueue(folder, image, FilePaths.Inspect(image), "Image", FilePaths.Hash(image), false),
                "The unchanged failed image was not eligible for incremental recovery.");
            var retry = reopened.Claim() ?? throw new InvalidOperationException("The image retry was not queued.");
            reopened.Prepare(retry, ["content"], null);
            reopened.SaveBatch(retry, reopened.Remaining(retry), [Vector(512)]);
            Program.Require(reopened.Complete(retry), "The recovered image did not publish.");
            Program.Require(reopened.ModelReindex(true) is { Completed: true, Succeeded: 2, Failed: 0 } &&
                Check.Scalar(data, "SELECT v.id FROM vectors v JOIN files f ON f.id=v.file_id WHERE f.path LIKE '%notes.txt'") == documentVector,
                "Targeted image recovery discarded healthy embeddings or retained a stale failure.");
        }
    });

    private static Task SearchBudgetsAsync() => Check.TemporaryAsync(async root =>
    {
        string data = Path.Combine(root, "index"), source = Path.Combine(root, "files");
        Directory.CreateDirectory(source);
        var clip = Space("clip-search", 768, true);
        string image = Path.Combine(source, "matching-image.png");
        using (var catalog = new Catalog(data, 1))
        {
            catalog.ReconfigureModels(new Dictionary<string, EmbeddingSpace>
            { ["Document"] = EmbeddingSpace.Default, ["Code"] = EmbeddingSpace.Default, ["Image"] = clip }, () => { });
            int folder = catalog.AddFolder(source);
            for (int index = 0; index < 3; index++)
            {
                float[] highScore = new float[768];
                highScore[0] = 0.9f; highScore[index + 1] = MathF.Sqrt(1 - 0.9f * 0.9f);
                Publish(catalog, folder, Path.Combine(source, $"document-{index}.txt"), "Document", highScore);
            }
            float[] lowScore = new float[768];
            lowScore[0] = 0.25f; lowScore[1] = MathF.Sqrt(1 - 0.25f * 0.25f);
            Publish(catalog, folder, image, "Image", lowScore);
        }
        var models = new FakeEmbeddings
        { QueryModels = new IEmbeddings[] { new FakeEmbeddings(), new FakeEmbeddings { Space = clip } } };
        await using var engine = new IndexingEngine(data, Path.Combine(root, "models"), Path.Combine(root, "unused.exe"),
            Path.Combine(root, "app"), new(12, 6, 63), models, new EngineLog(data), new(), watch: false);
        engine.Start();
        await Check.UntilAsync(() => engine.Startup.Snapshot.State == "Ready" && engine.Status().ReconcilingFolders == 0,
            "The mixed-model search fixture did not become ready.");
        var results = await engine.SearchAsync("apple", 0, 1);
        Program.Require(results.Count(value => value.Kind == "Document") == 1 && results.Single(value => value.Kind == "Image").Path == image,
            $"Higher document scores suppressed the independent image index: {string.Join(", ", results.GroupBy(value => value.Kind).Select(group => $"{group.Key}={group.Count()}"))}; providers={models.ActiveModels.Count}.");
        Program.Require(results.Count == 2 && (await engine.SearchAsync("apple", 0.3, 1)).Single().Kind == "Document",
            "Per-index budgets or the user's explicit similarity threshold were not respected.");
    });
}
