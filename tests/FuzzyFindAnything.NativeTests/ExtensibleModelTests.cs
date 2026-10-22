using System.Diagnostics;
using System.IO;
using System.Text.Json;
using FuzzyFindAnything.Core;

namespace FuzzyFindAnything.NativeTests;

internal static class ExtensibleModelTests
{
    private const string Qwen = "https://huggingface.co/Qwen/Qwen3-Embedding-0.6B";
    private const string PrefixBase = "https://huggingface.co/intfloat/e5-base-v2";
    private const string PrefixSmall = "https://huggingface.co/vectoriseai/e5-small-v2";

    public static async Task RunAsync(string package, string defaultModel, string cacheDirectory, string output, bool runtimeOnly = false)
    {
        package = Path.GetFullPath(package); output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        var cache = new HuggingFaceModels(cacheDirectory, defaultModel);
        var runtimeResults = new List<object>();
        foreach (string url in new[] { Qwen, PrefixBase, PrefixSmall })
        {
            var space = cache.FindCached(url);
            var timer = Stopwatch.StartNew();
            await using var client = await InferenceClient.StartAsync(
                Path.Combine(package, "hf-inference", "FuzzyFindAnything.HuggingFaceInference.exe"),
                cache.DirectoryFor(space), 2, (_, stage) => Console.WriteLine(stage), Console.Error.WriteLine, CancellationToken.None,
                requiredProvider: "CPUExecutionProvider:CPU");
            var capabilities = await client.RequestAsync("capabilities", new { }, CancellationToken.None);
            Program.Require(capabilities.GetProperty("protocol_version").GetInt32() == 2 &&
                capabilities.GetProperty("space").Deserialize<EmbeddingSpace>(Protocol.Json)?.Fingerprint == space.Fingerprint,
                "The native host did not negotiate the reviewed recipe contract.");
            string[] documents = ["The capital of China is Beijing.",
                "Gravity is a force that attracts two bodies towards each other. It gives weight to physical objects and is responsible for the movement of planets around the sun."];
            float[][] batch = (await client.RequestAsync("documents", new { texts = documents }, CancellationToken.None)).Deserialize<float[][]>()!;
            for (int row = 0; row < batch.Length; row++)
            {
                float[] single = (await client.RequestAsync("documents", new { texts = new[] { documents[row] } }, CancellationToken.None)).Deserialize<float[][]>()![0];
                Program.Require(batch[row].Length == space.Dimensions && ModelContract.Similarity(single, batch[row]) > 0.99999,
                    "Padding/mixed-length batching changed the embedding.");
            }
            if (url == Qwen)
            {
                using var fixture = JsonDocument.Parse(typeof(ExtensibleModelTests).Assembly
                    .GetManifestResourceStream("FuzzyFindAnything.Tests.Embedding.qwen-reference.json")!);
                foreach (var sample in fixture.RootElement.GetProperty("samples").EnumerateArray())
                {
                    int[] actual = (await client.RequestAsync("tokens", new { text = sample.GetProperty("text").GetString() }, CancellationToken.None)).Deserialize<int[]>()!;
                    Program.Require(actual.SequenceEqual(sample.GetProperty("ids").Deserialize<int[]>()!),
                        "Native byte-level tokenizer differs from the pinned reference.");
                }
                var queries = fixture.RootElement.GetProperty("queries").Deserialize<string[]>()!;
                var expected = fixture.RootElement.GetProperty("scores").Deserialize<double[][]>()!;
                for (int row = 0; row < queries.Length; row++)
                {
                    float[] query = (await client.RequestAsync("query", new { text = queries[row], profile = "document-search-v1" }, CancellationToken.None)).Deserialize<float[]>()!;
                    for (int column = 0; column < documents.Length; column++)
                        Program.Require(Math.Abs(ModelContract.Similarity(query, batch[column]) - expected[row][column]) < 0.0001,
                            "The Qwen query/document score differs from the official reference matrix.");
                }
                await Check.ThrowsAsync<ContentException>(async () => await client.RequestAsync("query",
                    new { text = string.Concat(Enumerable.Repeat(" test", 2100)), profile = "code-search-v1" }, CancellationToken.None));
                await Check.ThrowsAsync<ContentException>(async () => await client.RequestAsync("query",
                    new { text = "test", profile = "unapproved" }, CancellationToken.None));
            }
            var input = new[] { "def add(a, b):\n    return a + b", "def parse_csv(row):\n    return row.split(',')" };
            float[][] code = (await client.RequestAsync("documents", new { texts = input }, CancellationToken.None)).Deserialize<float[][]>()!;
            float[] codeQuery = (await client.RequestAsync("query",
                new { text = "function that adds two numbers", profile = space.Recipe!.CodeQuery }, CancellationToken.None)).Deserialize<float[]>()!;
            Program.Require(ModelContract.Similarity(codeQuery, code[0]) > ModelContract.Similarity(codeQuery, code[1]),
                "The code retrieval positive did not outrank the distractor.");
            string longSource = string.Join("\n", Enumerable.Range(0, 180).Select(index => $"def function_{index}(value):\n    return value + {index}  # caf\u00e9 \U0001F600"));
            string[] passages = (await client.RequestAsync("split", new { text = longSource }, CancellationToken.None)).Deserialize<string[]>()!;
            int covered = 0;
            foreach (string passage in passages)
            {
                int start = longSource.IndexOf(passage, Math.Max(0, covered - 256), StringComparison.Ordinal);
                Program.Require(start >= 0 && start <= covered && passage.Contains('\n'), "Native chunking lost source coverage or code line breaks.");
                covered = Math.Max(covered, start + passage.Length);
                var tokens = (await client.RequestAsync("tokens", new { text = space.Recipe.DocumentPrefix + passage }, CancellationToken.None)).Deserialize<int[]>()!;
                Program.Require(tokens.Length <= space.Recipe.ChunkTokens, "The document prefix overflowed the prepared token budget.");
            }
            Program.Require(covered == longSource.Length, "Chunking omitted the end of a source file.");
            runtimeResults.Add(new { url, space.Revision, space.Dimensions, seconds = timer.Elapsed.TotalSeconds });
            Console.WriteLine("PASS native tokenizer/pooling/profile retrieval and bounded workload: " + url);
        }
        if (runtimeOnly)
        {
            File.WriteAllText(Path.Combine(output, "embedding-runtime.json"),
                JsonSerializer.Serialize(new { failures = 0, runtimeResults }, new JsonSerializerOptions { WriteIndented = true }));
            return;
        }
        await Check.TemporaryAsync(async root =>
        {
            string data = Path.Combine(root, "index"), corpus = Path.Combine(root, "files");
            Directory.CreateDirectory(corpus);
            ModelPermutationTests.Copy(cacheDirectory, Path.Combine(data, "models"));
            using (var initialSettings = new ModelSettingsUpdate(data, ModelSelection.Default))
            { initialSettings.Publish(); initialSettings.Commit(); }
            File.WriteAllText(Path.Combine(corpus, "orchard.txt"), "Farmers harvest ripe apples from trees in an orchard.");
            File.WriteAllText(Path.Combine(corpus, "travel.txt"), "Trains carry passengers through mountains and valleys.");
            File.WriteAllText(Path.Combine(corpus, "add.py"), "def add_numbers(a, b):\n    return a + b\n");
            File.WriteAllText(Path.Combine(corpus, "parse.py"), "def parse_csv(row):\n    return row.split(',')\n");
            Fixtures.Image(Path.Combine(corpus, "red.png"), true);
            var engine = new EngineProcess(package, defaultModel, data);
            try
            {
                await engine.ReadyAsync();
                await engine.RequestAsync<JsonElement>("add_folder", new { path = corpus });
                await engine.WaitAsync(state => state.FileCount == 5 && state.QueueCount == 0 && state.ReconcilingFolders == 0, "Initial corpus did not index.");
                var invalid = ModelSelection.Default with { Image = Qwen };
                string xml = FilePaths.Hash(Path.Combine(data, "settings.xml"));
                await Check.ThrowsAsync<IOException>(async () => await engine.RequestAsync<ModelApplyResult>("apply_models", invalid));
                Program.Require(FilePaths.Hash(Path.Combine(data, "settings.xml")) == xml, "An invalid image assignment changed XML.");

                var qwenCode = ModelSelection.Default with { Code = Qwen };
                var cancellable = engine.RequestAsync<ModelApplyResult>("apply_models", qwenCode);
                await Task.Delay(300);
                var cancelled = await engine.RequestAsync<JsonElement>("cancel_model_change");
                Program.Require(cancelled.GetProperty("cancelled").GetBoolean(), "Model preparation did not expose cancellation.");
                await Check.ThrowsAsync<IOException>(async () => await cancellable);
                Program.Require(FilePaths.Hash(Path.Combine(data, "settings.xml")) == xml, "Cancelling preflight changed XML.");
                Console.WriteLine("PASS incompatible image selection and cancelled preflight preserve the active index");

                foreach (var selection in new[]
                {
                    qwenCode,
                    new ModelSelection(Qwen, Qwen, ModelSelection.DefaultUrl),
                    new ModelSelection(Qwen, Qwen, ModelSelection.DefaultUrl, CodeOptions: new("document-search-v1")),
                    new ModelSelection(Qwen, PrefixBase, ModelSelection.DefaultUrl),
                    new ModelSelection(Qwen, PrefixSmall, ModelSelection.DefaultUrl)
                })
                {
                    bool queryOnly = ModelSelection.Load(data).Text == selection.Text && ModelSelection.Load(data).Code == selection.Code;
                    long generation = Check.Scalar(data, "SELECT value FROM settings WHERE key='generation'");
                    long document = VectorId(data, "Document"), image = VectorId(data, "Image");
                    var result = await engine.RequestAsync<ModelApplyResult>("apply_models", selection);
                    Program.Require(result.Changed && (queryOnly ? result.ReindexedKinds.Length == 0 : result.ReindexedKinds.Contains("Code") || result.ReindexedKinds.Contains("Document")),
                        "An embedding/query change was incorrectly classified.");
                    await engine.WaitAsync(state => state.Models?.Selection == result.Selection && state.FileCount == 5 &&
                        state.QueueCount == 0 && state.ReconcilingFolders == 0 &&
                        (queryOnly || state.Models.Reindex is { Completed: true, Failed: 0 }), "The new embedding recipe did not finish indexing.", 300000);
                    Program.Require(VectorId(data, "Image") == image, "Unchanged image vectors were rebuilt.");
                    if (!result.ReindexedKinds.Contains("Document"))
                        Program.Require(VectorId(data, "Document") == document, "Unchanged document vectors were rebuilt.");
                    if (queryOnly) Program.Require(Check.Scalar(data, "SELECT value FROM settings WHERE key='generation'") == generation,
                        "A query-only change advanced the vector generation.");
                    var codeResults = await engine.RequestAsync<List<ResultFile>>("search", new { query = "function that adds two numbers", threshold = 0, limit = 1 });
                    Program.Require(codeResults.Single(value => value.Kind == "Code").Name == "add.py", "Role-aware code search did not retrieve the expected source.");
                    var documents = await engine.RequestAsync<List<ResultFile>>("search", new { query = "farmers harvesting apples", threshold = 0, limit = 1 });
                    Program.Require(documents.Single(value => value.Kind == "Document").Name == "orchard.txt", "Shared-space document search used the wrong query role.");
                    if (selection.Text == selection.Code)
                    {
                        var state = await engine.RequestAsync<EngineState>("status");
                        Program.Require(state.Models!.Indexes.Count == 2, "Two query roles duplicated their shared document-vector index.");
                    }
                    xml = FilePaths.Hash(Path.Combine(data, "settings.xml"));
                    var noop = await engine.RequestAsync<ModelApplyResult>("apply_models", result.Selection);
                    Program.Require(!noop.Changed && FilePaths.Hash(Path.Combine(data, "settings.xml")) == xml, "No-op Apply mutated extended settings.");
                    Console.WriteLine("PASS app routing, selective changes and no-op: " + selection.Code + (queryOnly ? " (query only)" : ""));
                }
                xml = FilePaths.Hash(Path.Combine(data, "settings.xml"));
                var strictDevice = ModelSelection.Load(data) with { CodeOptions = new(Device: "NonexistentExecutionProvider:GPU") };
                await Check.ThrowsAsync<IOException>(async () => await engine.RequestAsync<ModelApplyResult>("apply_models", strictDevice));
                Program.Require(FilePaths.Hash(Path.Combine(data, "settings.xml")) == xml, "An unavailable strict device changed the applied configuration.");
                Console.WriteLine("PASS unavailable strict accelerator rejection without configuration mutation");
                await engine.DisposeAsync();
                engine = new EngineProcess(package, defaultModel, data);
                await engine.ReadyAsync();
                await engine.WaitAsync(state => state.FileCount == 5 && state.QueueCount == 0 && state.ReconcilingFolders == 0,
                    "Extended model bindings did not resume.");
                Program.Require((await engine.RequestAsync<EngineState>("status")).InferenceBatches.CompletedInputs == 0,
                    "Restart reembedded unchanged files.");
                var restored = await engine.RequestAsync<ModelApplyResult>("apply_models", ModelSelection.Default);
                await engine.WaitAsync(state => state.Models?.Selection == ModelSelection.Default && state.FileCount == 5 &&
                    state.QueueCount == 0 && state.ReconcilingFolders == 0, "Restoring the default recipes did not complete.", 300000);
                Program.Require(restored.Changed, "Default model restoration was lost.");
                Console.WriteLine("PASS extended XML/catalog restart and default restoration without production-data changes");
            }
            finally { await engine.DisposeAsync(); }
        });
        File.WriteAllText(Path.Combine(output, "extensible-models.json"),
            JsonSerializer.Serialize(new { failures = 0, runtimeResults }, new JsonSerializerOptions { WriteIndented = true }));
    }
    public static async Task OutputsAsync(string package, string cacheDirectory, string output)
    {
        var cache = new HuggingFaceModels(cacheDirectory, "");
        var results = new List<object>();
        foreach (string url in new[] { "https://huggingface.co/BAAI/bge-small-en-v1.5",
            "https://huggingface.co/mixedbread-ai/mxbai-embed-xsmall-v1" })
        {
            var space = cache.FindCached(url);
            await using var client = await InferenceClient.StartAsync(
                Path.Combine(package, "hf-inference", "FuzzyFindAnything.HuggingFaceInference.exe"),
                cache.DirectoryFor(space), 2, null, Console.Error.WriteLine, CancellationToken.None);
            var vectors = (await client.RequestAsync("documents", new
            {
                texts = new[] { "Farmers grow apples in an orchard.", "A rocket travels to distant planets in outer space." }
            }, CancellationToken.None)).Deserialize<float[][]>()!;
            var query = (await client.RequestAsync("query", new { text = "farmers harvesting apples", profile = "default" },
                CancellationToken.None)).Deserialize<float[]>()!;
            Program.Require(vectors.Length == 2 && vectors.All(value => value.Length == 384 &&
                Math.Abs(value.Sum(coordinate => (double)coordinate * coordinate) - 1) < 0.0001) &&
                ModelContract.Similarity(query, vectors[0]) > ModelContract.Similarity(query, vectors[1]),
                "The generalized CLS/pre-pooled output path failed native embedding/retrieval.");
            results.Add(new { url, space.Revision, space.Recipe!.Pooling, space.Recipe.Output });
            Console.WriteLine("PASS generalized " + space.Recipe.Pooling + " native export: " + url);
        }
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "embedding-outputs.json"),
            JsonSerializer.Serialize(new { failures = 0, results }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static long VectorId(string data, string kind) => Check.Scalar(data,
        $"SELECT MIN(v.id) FROM vectors v JOIN files f ON f.id=v.file_id WHERE f.kind='{kind}'");
}
