using System.IO;
using FuzzyFindAnything.Core;

namespace FuzzyFindAnything.NativeTests;

internal static class ModelConfigurationTests
{
    public static void Run()
    {
        Program.Run("Model URL restrictions and canonical no-op values", Urls);
        Program.Run("Independent model indexes and selective invalidation", Shards);
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
    private static void Publish(Catalog catalog, int folder, string path, string kind)
    {
        var job = Program.Enqueue(catalog, folder, path, "content", kind);
        catalog.Prepare(job, ["content"], null);
        catalog.SaveBatch(job, catalog.Remaining(job), [Vector(catalog.SpaceFor(kind).Dimensions)]);
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
}
