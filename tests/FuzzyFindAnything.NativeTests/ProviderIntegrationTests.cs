using System.IO;
using System.Text.Json;
using FuzzyFindAnything.Core;

namespace FuzzyFindAnything.NativeTests;

internal static class ProviderIntegrationTests
{
    public static async Task RunAsync(string package, string defaultModel, string cachedModels, string configurations, string output,
        string? imageModels = null)
    {
        var cases = JsonSerializer.Deserialize<ModelSelection[]>(File.ReadAllText(configurations), Protocol.Json)
            ?? throw new InvalidDataException("Supply approved local-provider model configurations.");
        if (cases.Length == 0 || cases.Any(value => !value.UsesExternalModels))
            throw new ArgumentException("Each provider integration case must select an actual installed local provider.");
        var records = new List<object>();
        await Check.TemporaryAsync(async root =>
        {
            string data = Path.Combine(root, "index"), corpus = Path.Combine(root, "files");
            Directory.CreateDirectory(corpus);
            ImageModelIntegration.LinkCache(cachedModels, Path.Combine(data, "models"));
            if (imageModels is not null) ImageModelIntegration.LinkCache(imageModels, Path.Combine(data, "models", "image-understanding"));
            File.WriteAllText(Path.Combine(corpus, "journey.txt"), "A holiday itinerary for a passenger train journey through the mountains.");
            File.WriteAllText(Path.Combine(corpus, "average.py"),
                "def arithmetic_mean(values):\n    \"\"\"Compute the arithmetic average of a list of numbers.\"\"\"\n    return sum(values) / len(values)\n");
            File.WriteAllText(Path.Combine(corpus, "network.py"),
                "def fetch_web_page(url):\n    \"\"\"Download a web page using an HTTP request.\"\"\"\n    import urllib.request\n    return urllib.request.urlopen(url).read()\n");
            Fixtures.Image(Path.Combine(corpus, "red.png"), true);
            const string small = "https://huggingface.co/sentence-transformers/paraphrase-MiniLM-L3-v2";
            var baseline = new ModelSelection(small, small, "https://huggingface.co/Xenova/clip-vit-base-patch32");
            using (var settings = new ModelSettingsUpdate(data, baseline)) { settings.Publish(); settings.Commit(); }
            var engine = new EngineProcess(package, defaultModel, data);
            try
            {
                await engine.ReadyAsync();
                await engine.RequestAsync<JsonElement>("add_folder", new { path = corpus });
                await engine.WaitAsync(state => state.FileCount == 4 && state.QueueCount == 0 && state.ReconcilingFolders == 0,
                    "The isolated HF baseline did not index.");
                var catalogRequests = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
                    engine.RequestAsync<ProviderCatalog>("provider_catalog", new { provider = "microsoft", version = "", refresh = false })));
                Program.Require(catalogRequests.All(value => value.InternalAccessVerified && value.Models.Length > 0),
                    "Concurrent source discovery was rejected as busy or lost verified catalog access.");
                Console.WriteLine("PASS concurrent provider discovery queue without persistent busy errors");
                var previous = baseline;
                foreach (var selection in cases.Select(value => value.Normalize()))
                {
                    var stable = new[] { "Document", "Code", "Image" }.ToDictionary(kind => kind, kind => VectorId(data, kind));
                    var applied = await engine.RequestAsync<ModelApplyResult>("apply_models", selection);
                    Program.Require(applied.Changed, "The provider selection did not apply.");
                    await engine.WaitAsync(state => state.FileCount == 4 && state.QueueCount == 0 && state.ReconcilingFolders == 0 &&
                        state.Models?.Selection == selection && state.Models.Reindex is { Completed: true, Failed: 0, Partial: 0 },
                        "The mixed provider configuration did not finish indexing.", 600000);
                    foreach (string kind in stable.Keys)
                        if (previous.ForKind(kind) == selection.ForKind(kind) && previous.OptionsFor(kind) == selection.OptionsFor(kind))
                            Program.Require(VectorId(data, kind) == stable[kind], "An unchanged model role was rebuilt.");
                    foreach (var (kind, query, expected) in new[]
                    {
                        ("Document", "A passenger train holiday itinerary in the mountains", "journey.txt"),
                        ("Code", "Compute the arithmetic average of a list of numbers", "average.py"),
                        ("Image", "a red circle on a white background", "red.png")
                    })
                    {
                        var options = new SearchPreferences(kind == "Document", kind == "Code", kind == "Image", 0, 0, 0);
                        var matches = await engine.RequestAsync<List<ResultFile>>("search", new { query, options });
                        Program.Require(matches.FirstOrDefault()?.Name == expected, "Provider retrieval failed for " + kind);
                        if (kind == "Code") Program.Require(matches[0].MatchedChunk.Contains("arithmetic_mean", StringComparison.Ordinal),
                            "The native provider lost code chunk provenance.");
                    }
                    string xml = FilePaths.Hash(Path.Combine(data, "settings.xml"));
                    long generation = Check.Scalar(data, "SELECT CAST(value AS INTEGER) FROM settings WHERE key='generation'");
                    var noop = await engine.RequestAsync<ModelApplyResult>("apply_models", selection);
                    Program.Require(!noop.Changed && FilePaths.Hash(Path.Combine(data, "settings.xml")) == xml &&
                        Check.Scalar(data, "SELECT CAST(value AS INTEGER) FROM settings WHERE key='generation'") == generation,
                        "An unchanged provider Apply mutated settings or vectors.");
                    string providerKind = new[] { "Document", "Code" }.First(kind => selection.ReferenceFor(kind) is ProviderModelReference);
                    var invalid = providerKind == "Document"
                        ? selection with { TextOptions = new(Device: "MissingExecutionProvider:NPU") }
                        : selection with { CodeOptions = new(Device: "MissingExecutionProvider:NPU") };
                    await Check.ThrowsAsync<IOException>(async () => await engine.RequestAsync<ModelApplyResult>("apply_models", invalid));
                    Program.Require(FilePaths.Hash(Path.Combine(data, "settings.xml")) == xml &&
                        Check.Scalar(data, "SELECT CAST(value AS INTEGER) FROM settings WHERE key='generation'") == generation,
                        "A missing requested device changed the applied model configuration.");
                    await engine.DisposeAsync(); engine = new(package, defaultModel, data);
                    await engine.ReadyAsync();
                    await engine.WaitAsync(state => state.QueueCount == 0 && state.ReconcilingFolders == 0 && state.FileCount == 4,
                        "The mixed provider configuration did not restore.");
                    var status = await engine.RequestAsync<EngineState>("status");
                    Program.Require(status.Models?.Selection == selection && status.InferenceBatches.CompletedInputs == 0 &&
                        ModelSelection.Load(data) == selection, "Restart lost provider selections or re-embedded unchanged files.");
                    records.Add(new { selection, indexes = status.Models!.Indexes });
                    Console.WriteLine("PASS mixed provider retrieval, selective indexing, device rejection, no-op and restart");
                    previous = selection;
                }
                await engine.RequestAsync<ModelApplyResult>("apply_models", baseline);
                await engine.WaitAsync(state => state.FileCount == 4 && state.QueueCount == 0 &&
                    state.Models?.Selection == baseline && state.Models.Reindex is { Completed: true, Failed: 0 },
                    "Restoring HF models failed.");
                Program.Require(Check.Scalar(data, "SELECT CAST(value AS INTEGER) FROM settings WHERE key='schema_version'") == 10,
                    "Restoring HF models downgraded the provider-aware catalog.");
            }
            finally { await engine.DisposeAsync(); }
        });
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "provider-integration.json"), JsonSerializer.Serialize(new { failures = 0, cases = records }, Protocol.Json));
    }
    private static long VectorId(string directory, string kind) => Check.Scalar(directory,
        $"SELECT MIN(v.id) FROM vectors v JOIN files f ON f.id=v.file_id WHERE f.kind='{kind}'");
}
