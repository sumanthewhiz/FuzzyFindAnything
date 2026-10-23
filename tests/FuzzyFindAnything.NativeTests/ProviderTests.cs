using System.IO;
using System.Xml.Linq;
using FuzzyFindAnything.Core;

namespace FuzzyFindAnything.NativeTests;

internal static class ProviderTests
{
    private static readonly ProviderModelReference Reference = new("test-provider", "1.2.3.4", "text");
    private static EmbeddingSpace Space(string device = "CPU") => new(Reference.Key, new string('a', 64), "external-v1",
        104, 512, false, 1L << 30, External: new("test-provider", new string('a', 64), new string('b', 64),
            100, 512, 512, ["Document", "Code"], device));
    public static void Run()
    {
        Program.Run("Typed provider references, mixed XML and execution identities", References);
        Program.Run("Provider padding, native retrieval and schema preservation", Storage);
        ProviderBootstrapTests.Run();
    }
    private static void References()
    {
        var selected = new ModelSelection(Reference.Key, ModelSelection.DefaultUrl, ModelSelection.DefaultUrl,
            TextOptions: new(Device: "CPU"));
        var root = new XElement("FuzzyFindAnythingConfiguration", new XAttribute("version", "4"), selected.ToXml());
        Program.Require(ModelSelection.Read(root) == selected && root.Element("Models")!.Element("Text")!.Attribute("modelUrl") is null &&
            (string?)root.Element("Models")!.Element("Text")!.Attribute("provider") == "test-provider",
            "A native provider reference was treated as a Hugging Face URL.");
        root.SetAttributeValue("version", "3");
        Check.Throws<InvalidDataException>(() => ModelSelection.Read(root));
        var missingCustom = XElement.Parse("""
            <FuzzyFindAnythingConfiguration version="4"><Models>
              <Image modelUrl="https://huggingface.co/microsoft/Florence-2-base-ft" mode="described">
                <Tasks caption="true"/><Embedding use="Custom"/>
              </Image>
            </Models></FuzzyFindAnythingConfiguration>
            """);
        Check.Throws<InvalidDataException>(() => ModelSelection.Read(missingCustom));
        foreach (string reference in new[]
        {
            "provider://user:secret@test-provider/1.2.3.4/text", "provider://test-provider/latest/text",
            "provider://test-provider/1.2.3.4/%2e%2e", "provider://test-provider/1.2.3.4/text?token=secret",
            "provider://test-provider/1.2.3.4/text/other"
        }) Check.Throws<ArgumentException>(() => ModelReference.Parse(reference));
        Check.Throws<ArgumentException>(() => ModelSelection.Canonical(Reference.Key));
        var cpu = Space(); var npu = Space("QNNExecutionProvider:NPU");
        cpu.Validate(); npu.Validate();
        Program.Require(cpu.Key != npu.Key && cpu.IndexingFingerprint != npu.IndexingFingerprint &&
            EmbeddingSpace.Default.Fingerprint == ModelContract.Fingerprint, "Execution profiles or legacy embedding identities were merged.");
        var mixed = new ModelSelection(Reference.Key, Reference.Key, ModelSelection.DefaultUrl,
            TextOptions: new(Device: "CPU"), CodeOptions: new(Device: "QNNExecutionProvider:NPU"));
        var spaces = new Dictionary<string, EmbeddingSpace>
        {
            [Reference.Key + "\nCPU"] = cpu,
            [Reference.Key + "\nQNNExecutionProvider:NPU"] = npu,
            [ModelSelection.DefaultUrl] = EmbeddingSpace.Default
        };
        Program.Require(ModelManager.NormalizeSelection(mixed, spaces) == mixed, "Independent native execution bindings were lost.");
        var imagePipeline = mixed with { Image = "https://huggingface.co/microsoft/Florence-2-base-ft" };
        Program.Require(imagePipeline.EmbeddingSelection().Image == Reference.Key &&
            imagePipeline.EmbeddingSelection().OptionsFor("Image").Device == "CPU", "A linked HF image pipeline lost its provider text embedder.");
    }
    private static void Storage() => Program.Temporary(root =>
    {
        var space = Space();
        float[] logical = ModelContract.Normalize(Enumerable.Range(1, 100).Select(value => (float)value).ToArray(), 100);
        float[] padded = new float[104]; logical.CopyTo(padded, 0);
        Program.Require(Math.Abs(ModelContract.Similarity(logical, logical) - ModelContract.Similarity(padded, padded)) < 1e-7,
            "Padding changed the retrieval metric.");
        float[] invalid = (float[])padded.Clone(); invalid[100] = 0.1f;
        Check.Throws<InvalidDataException>(() => space.Normalize(invalid));
        Check.Throws<InvalidDataException>(() => space.Vector(ModelContract.Bytes(invalid)));
        string corpus = Path.Combine(root, "files"), data = Path.Combine(root, "index");
        Directory.CreateDirectory(corpus);
        var routes = new Dictionary<string, EmbeddingSpace>
        { ["Document"] = space, ["Code"] = EmbeddingSpace.Default, ["Image"] = EmbeddingSpace.Default };
        var selection = new ModelSelection(Reference.Key, ModelSelection.DefaultUrl, ModelSelection.DefaultUrl);
        long imageId;
        using (var catalog = new Catalog(data, 1))
        {
            int folder = catalog.AddFolder(corpus);
            var image = Program.Enqueue(catalog, folder, Path.Combine(corpus, "unchanged.png"), "fixture", "Image");
            Program.Complete(catalog, image); imageId = catalog.FindFile(image.Path)!.Id;
            catalog.ReconfigureModels(routes, () => { }, selection: selection);
            var job = Program.Enqueue(catalog, folder, Path.Combine(corpus, "provider.txt"), "Native provider content");
            catalog.Prepare(job, ["Native provider content"], null);
            catalog.SaveBatch(job, catalog.Remaining(job), [padded]);
            catalog.Complete(job);
            Program.Require(catalog.Setting("schema_version") == "10" && catalog.FindFile(image.Path)!.Id == imageId &&
                catalog.Search(padded, 1, 0.5, space, ["Document"]).Single().Name == "provider.txt",
                "Native provider publication changed unrelated image ownership or lost padded-vector retrieval.");
        }
        using (var catalog = new Catalog(data, 1))
        {
            Program.Require(catalog.Spaces.Any(value => value.External is { LogicalDimensions: 100 } && value.Dimensions == 104) &&
                catalog.Search(padded, 1, 0.5, space, ["Document"]).Count == 1, "The external vector contract did not survive restart.");
            var restored = routes.ToDictionary(pair => pair.Key, _ => EmbeddingSpace.Default);
            catalog.ReconfigureModels(restored, () => { }, selection: ModelSelection.Default);
            catalog.ConfigureChunkingProfiles(new Dictionary<string, string>
            { ["Document"] = ChunkingProfiles.Documents, ["Code"] = ChunkingProfiles.Code });
            Program.Require(catalog.Setting("schema_version") == "10" &&
                catalog.FindFile(Path.Combine(corpus, "unchanged.png"))!.Id == imageId, "Restoring HF models downgraded the provider schema or removed unrelated sources.");
        }
    });
}
