using System.IO;
using System.Text.Json;
using System.Xml.Linq;
using FuzzyFindAnything.Core;

namespace FuzzyFindAnything.NativeTests;

internal static class ModelPermutationTests
{
    public static async Task RunAsync(string package, string defaultModel, string cachedModels, string output)
    {
        package = Path.GetFullPath(package); defaultModel = Path.GetFullPath(defaultModel);
        cachedModels = Path.GetFullPath(cachedModels); output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        const string bert = "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2";
        const string small = "https://huggingface.co/sentence-transformers/paraphrase-MiniLM-L3-v2";
        const string clip = "https://huggingface.co/Xenova/clip-vit-base-patch32";
        string gemma = ModelSelection.DefaultUrl;
        var cases = new[]
        {
            ("three different models", new ModelSelection(bert, small, clip), 3),
            ("shared text and code", new ModelSelection(bert, bert, clip), 2),
            ("one multimodal model", new ModelSelection(clip, clip, clip), 1),
            ("shared text and images", new ModelSelection(clip, small, clip), 2),
            ("shared code and images", new ModelSelection(bert, clip, clip), 2),
            ("default text and images", new ModelSelection(gemma, small, gemma), 2),
            ("restore all defaults", ModelSelection.Default, 1)
        };
        var results = new List<object>();
        await Check.TemporaryAsync(async root =>
        {
            string data = Path.Combine(root, "index"), corpus = Path.Combine(root, "files");
            Directory.CreateDirectory(data); Directory.CreateDirectory(corpus);
            Copy(cachedModels, Path.Combine(data, "models"));
            File.WriteAllText(Path.Combine(corpus, "orchard.txt"), "Farmers harvest ripe apples in the orchard and irrigate fruit trees.");
            File.WriteAllText(Path.Combine(corpus, "parser.py"), "def parse_csv(rows):\n    return [row.split(',') for row in rows]\n");
            Fixtures.Image(Path.Combine(corpus, "red.png"), true);
            var engine = new EngineProcess(package, defaultModel, data);
            try
            {
                await engine.ReadyAsync();
                await engine.RequestAsync<JsonElement>("add_folder", new { path = corpus });
                await engine.WaitAsync(value => value.FileCount == 3 && value.QueueCount == 0 && value.ReconcilingFolders == 0,
                    "Default test corpus did not index.");
                var previous = ModelSelection.Default;
                foreach (var (label, selection, count) in cases)
                {
                    long stableDocument = VectorId(data, "Document"), stableCode = VectorId(data, "Code"), stableImage = VectorId(data, "Image");
                    var applied = await engine.RequestAsync<ModelApplyResult>("apply_models", selection);
                    Program.Require(applied.Changed, "A changed model configuration was treated as a no-op.");
                    await engine.WaitAsync(value => value.FileCount == 3 && value.QueueCount == 0 && value.ReconcilingFolders == 0 &&
                        value.Models?.Selection == selection, "Model permutation did not finish indexing: " + label, 300000);
                    var state = await engine.RequestAsync<EngineState>("status");
                    Program.Require(state.Models!.Indexes.Count == count && state.Models.Indexes.Sum(index => index.Files) == 3,
                        "Incorrect index partition: " + label);
                    foreach (var index in state.Models.Indexes)
                        Program.Require(index.Provider.Length > 0 && index.Device.Length > 0 && index.Kinds.All(kind => selection.ForKind(kind) == index.ModelUrl),
                            "Missing device validation or incorrect file/model ownership.");
                    if (previous.Text == selection.Text) Program.Require(VectorId(data, "Document") == stableDocument, "Unchanged text index was rebuilt.");
                    if (previous.Code == selection.Code) Program.Require(VectorId(data, "Code") == stableCode, "Unchanged code index was rebuilt.");
                    if (previous.Image == selection.Image) Program.Require(VectorId(data, "Image") == stableImage, "Unchanged image index was rebuilt.");
                    Program.Require(ModelSelection.Load(data) == selection, "Applied model URLs were not persisted to XML.");
                    foreach (var (kind, phrase, file) in new[]
                    {
                        ("Document", "Farmers harvest ripe apples in an orchard", "orchard.txt"),
                        ("Code", "def parse_csv(rows): return rows split by commas", "parser.py"),
                        ("Image", "a red circle on a white background", "red.png")
                    })
                    {
                        var matches = await engine.RequestAsync<List<ResultFile>>("search", new { query = phrase, threshold = 0 });
                        Program.Require(matches.Where(value => value.Kind == kind).FirstOrDefault()?.Name == file,
                            $"The {kind} model did not retrieve its matching file in {label}.");
                    }
                    long generation = Check.Scalar(data, "SELECT CAST(value AS INTEGER) FROM settings WHERE key='generation'");
                    string xml = FilePaths.Hash(Path.Combine(data, "settings.xml"));
                    var noop = await engine.RequestAsync<ModelApplyResult>("apply_models", selection);
                    Program.Require(!noop.Changed && Check.Scalar(data, "SELECT CAST(value AS INTEGER) FROM settings WHERE key='generation'") == generation &&
                        FilePaths.Hash(Path.Combine(data, "settings.xml")) == xml, "An unchanged Apply mutated XML or indexes.");
                    var invalid = selection with { Image = bert };
                    await Check.ThrowsAsync<IOException>(async () => await engine.RequestAsync<ModelApplyResult>("apply_models", invalid));
                    Program.Require(ModelSelection.Load(data) == selection &&
                        Check.Scalar(data, "SELECT CAST(value AS INTEGER) FROM settings WHERE key='generation'") == generation,
                        "An incompatible image model destroyed the active configuration.");
                    previous = selection;
                    results.Add(new { label, selection, indexes = state.Models.Indexes });
                    Console.WriteLine("PASS " + label + $": {count} indexes; no-op and invalid configuration preserved data.");
                    if (label == "shared text and code")
                    {
                        await engine.DisposeAsync();
                        engine = new EngineProcess(package, defaultModel, data);
                        await engine.ReadyAsync();
                        await engine.WaitAsync(value => value.FileCount == 3 && value.QueueCount == 0 && value.ReconcilingFolders == 0,
                            "Non-default model configuration did not resume.");
                        var restored = await engine.RequestAsync<EngineState>("status");
                        Program.Require(restored.Models?.Selection == selection && restored.Models.Indexes.Count == count &&
                            restored.InferenceBatches.CompletedInputs == 0, "Non-default model restart rebuilt or lost its indexes.");
                        Console.WriteLine("PASS non-default XML/index restart without re-embedding");
                    }
                }
                await Check.ThrowsAsync<IOException>(async () => await engine.RequestAsync<ModelApplyResult>("apply_models",
                    ModelSelection.Default with { Code = "https://example.com/not-a-model" }));
            }
            finally { await engine.DisposeAsync(); }
            var interruptedSelection = new ModelSelection(bert, bert, clip);
            string settingsPath = Path.Combine(data, "settings.xml");
            var interrupted = XDocument.Load(settingsPath);
            interrupted.Root!.Element("Models")!.ReplaceWith(interruptedSelection.ToXml());
            interrupted.Save(settingsPath);
            await using (var recovered = new EngineProcess(package, defaultModel, data))
            {
                await recovered.ReadyAsync();
                await recovered.WaitAsync(value => value.FileCount == 3 && value.QueueCount == 0 && value.ReconcilingFolders == 0 &&
                    value.Models?.Selection == interruptedSelection, "Interrupted model-change intent did not recover and reindex.", 180000);
                var restored = await recovered.RequestAsync<ModelApplyResult>("apply_models", ModelSelection.Default);
                Program.Require(restored.Changed, "Could not restore defaults after intent recovery.");
                await recovered.WaitAsync(value => value.FileCount == 3 && value.QueueCount == 0 && value.ReconcilingFolders == 0 &&
                    value.Models?.Selection == ModelSelection.Default,
                    "Default reindex did not finish after recovery.");
            }
            Console.WriteLine("PASS interrupted XML/catalog model-change recovery");
            await using (var restarted = new EngineProcess(package, defaultModel, data))
            {
                await restarted.ReadyAsync();
                await restarted.WaitAsync(value => value.FileCount == 3 && value.QueueCount == 0 && value.ReconcilingFolders == 0,
                    "Model configuration did not restore after restart.");
                var status = await restarted.RequestAsync<EngineState>("status");
                Program.Require(status.Models?.Selection == ModelSelection.Default && status.Models.Indexes.Count == 1 &&
                    status.InferenceBatches.CompletedInputs == 0, "Restoring defaults reembedded unchanged files or lost model configuration.");
            }
            Program.Require(ModelSelection.Load(data) == ModelSelection.Default, "The permutation test did not restore EmbeddingGemma2 defaults.");
        });
        File.WriteAllText(Path.Combine(output, "model-permutations.json"), JsonSerializer.Serialize(new { failures = 0, cases = results },
            new JsonSerializerOptions { WriteIndented = true }));
    }
    private static long VectorId(string directory, string kind) => Check.Scalar(directory,
        $"SELECT MIN(v.id) FROM vectors v JOIN files f ON f.id=v.file_id WHERE f.kind='{kind}'");
    private static void Copy(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target);
        }
    }
}
