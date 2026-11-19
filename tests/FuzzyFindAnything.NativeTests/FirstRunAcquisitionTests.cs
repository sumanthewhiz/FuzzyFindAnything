using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Xml.Linq;
using FuzzyFindAnything.Core;
using FuzzyFindAnything.Tools;

namespace FuzzyFindAnything.NativeTests;

internal static class FirstRunAcquisitionTests
{
    public static async Task RunAsync(string archive, string fixtures, string? output)
    {
        archive = Path.GetFullPath(archive); fixtures = Path.GetFullPath(fixtures);
        string runtime, version;
        using (var zip = ZipFile.OpenRead(archive))
        using (var stream = zip.GetEntry("release-manifest.json")!.Open())
        using (var manifest = JsonDocument.Parse(stream))
        {
            runtime = manifest.RootElement.GetProperty("runtime_id").GetString()!;
            version = manifest.RootElement.GetProperty("version").GetString()!;
        }
        if (runtime == "win-arm64" && RuntimeInformation.OSArchitecture != Architecture.Arm64)
            throw new PlatformNotSupportedException("ARM64 cold-start acceptance requires executable ARM64 hardware.");
        ReleasePackage.Validate(archive, runtime, version);
        string root = output is null ? Path.Combine(Path.GetTempPath(), "ffa-first-run-" + Guid.NewGuid().ToString("N")) : Path.GetFullPath(output);
        if (Directory.Exists(root)) throw new ArgumentException("Cold-start acceptance requires a new output directory.");
        Directory.CreateDirectory(root);
        string package = Path.Combine(root, "extracted app"), data = Path.Combine(root, "fresh"), corpus = Path.Combine(root, "corpus");
        ZipFile.ExtractToDirectory(archive, package);
        string missingLegacy = Path.Combine(package, "models", "embeddinggemma-2-onnx");
        var timer = Stopwatch.StartNew();
        Directory.CreateDirectory(corpus);
        File.WriteAllText(Path.Combine(corpus, "orchard.txt"), "Farmers harvest ripe apples from trees in an orchard.");
        File.WriteAllText(Path.Combine(corpus, "journey.txt"), "Passenger trains travel through mountains on a holiday itinerary.");
        File.WriteAllText(Path.Combine(corpus, "average.py"), "def arithmetic_mean(values):\n    return sum(values) / len(values)\n");
        File.WriteAllText(Path.Combine(corpus, "csv.py"), "def parse_csv(line):\n    return line.split(',')\n");
        Fixtures.Image(Path.Combine(corpus, "red.png"), true);
        Fixtures.Image(Path.Combine(corpus, "blue.png"), false);
        await using (var engine = new EngineProcess(package, missingLegacy, data, isolatedRuntime: true))
        {
            var ready = engine.ReadyAsync(1_800_000);
            string last = "";
            while (!ready.IsCompleted)
            {
                string stage = engine.Progress.LastOrDefault()?.Stage ?? "Starting extracted runtime";
                if (stage != last) { Console.WriteLine(stage); last = stage; }
                await Task.WhenAny(ready, Task.Delay(500));
            }
            await ready;
            Program.Require(ModelSelection.Load(data) == ModelSelection.Default && engine.Status?.Folders.Count == 0,
                "Cold start did not persist the public default without adding folders.");
            await engine.RequestAsync<JsonElement>("add_folder", new { path = corpus });
            await IndexedAsync(engine);
            await RetrievalAsync(engine);
        }
        string modelCache = Path.Combine(data, "models");
        var cache = new HuggingFaceModels(modelCache, missingLegacy);
        var space = cache.FindCached(ModelSelection.DefaultUrl);
        string modelDirectory = cache.DirectoryFor(space);
        var cached = HuggingFaceModels.Load(modelDirectory);
        cached.Verify(modelDirectory);
        Program.Require(cached.Space.Adapter == "gemma2-onnx" && cached.Files.Single(file => file.Name == "tokenizer.model").SourceUrl ==
            ModelSelection.LegacyDefaultUrl, "The acquired package lost supplemental-file provenance.");
        long generation = Check.Scalar(data, "SELECT value FROM settings WHERE key='generation'");
        await OfflineRestartAsync(package, missingLegacy, data, generation);
        await OriginalPublicRestartAsync(package, missingLegacy, modelDirectory, corpus, Path.Combine(root, "original-public"));
        Check.DeleteTemporary(Path.Combine(root, "original-public"));
        await RetryAsync(package, missingLegacy, modelCache, corpus, Path.Combine(root, "retry"));
        Check.DeleteTemporary(Path.Combine(root, "retry"));
        await StartupCommitRetryAsync(package, missingLegacy, modelCache, corpus, Path.Combine(root, "commit-retry"));
        Check.DeleteTemporary(Path.Combine(root, "commit-retry"));
        await LegacyRecoveryAsync(package, missingLegacy, modelCache, corpus, Path.Combine(root, "legacy-recovery"));
        var parity = await ParityAsync(package, modelDirectory, space, fixtures);
        File.WriteAllText(Path.Combine(root, "first-run-acquisition.json"), JsonSerializer.Serialize(new
        {
            failures = 0,
            runtime,
            version,
            archive_sha256 = FilePaths.Hash(archive),
            seconds = timer.Elapsed.TotalSeconds,
            fresh_acquisition = true,
            public_default = true,
            synthetic_index_and_retrieval = true,
            offline_restart = true,
            unchanged_apply_recovery = true,
            automatic_legacy_recovery = true,
            preserved_preferences_and_scope = true,
            parity
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS {runtime}: extracted public acquisition, text/code/image retrieval, offline restart, unchanged Apply and automatic legacy recovery");
        if (output is null) Check.DeleteTemporary(root);
    }

    private static Task IndexedAsync(EngineProcess engine) => engine.WaitAsync(state =>
        state.FileCount == 6 && state.QueueCount == 0 && state.ActiveCount == 0 && state.ReconcilingFolders == 0 &&
        state.Folders.All(folder => folder.Errors == 0), "Synthetic files did not finish indexing.", 300000);

    private static async Task RetrievalAsync(EngineProcess engine)
    {
        foreach (var (kind, phrase, name) in new[]
        {
            ("Document", "farmers harvesting apples in an orchard", "orchard.txt"),
            ("Code", "calculate the arithmetic mean of a list of numbers", "average.py"),
            ("Image", "a red circle on a white background", "red.png")
        })
        {
            var result = await engine.RequestAsync<List<ResultFile>>("search", new { query = phrase, threshold = 0, limit = 1 });
            Program.Require(result.Single(value => value.Kind == kind).Name == name, $"The {kind} retrieval winner changed.");
        }
    }

    private static async Task OfflineRestartAsync(string package, string legacy, string data, long generation)
    {
        await using var engine = new EngineProcess(package, legacy, data, offline: true, isolatedRuntime: true);
        await engine.ReadyAsync();
        await IndexedAsync(engine);
        long inputs = engine.Status!.InferenceBatches.CompletedInputs;
        long refreshed = Check.Scalar(data, "SELECT value FROM settings WHERE key='generation'");
        string[] downloads = engine.Progress.Where(value => value.Stage.StartsWith("Downloading", StringComparison.Ordinal))
            .Select(value => value.Stage).ToArray();
        Program.Require(inputs == 0 && refreshed == generation && downloads.Length == 0,
            $"Offline restart changed committed work or reported a download: inputs={inputs}, generation={generation}->{refreshed}, stages={string.Join("; ", downloads)}.");
        await RetrievalAsync(engine);
    }

    private static async Task OriginalPublicRestartAsync(string package, string legacy, string modelDirectory, string corpus, string data)
    {
        using var fixture = PublicModelTests.OriginalPublicDescriptor();
        var space = fixture.RootElement.Deserialize<EmbeddingSpace>(Protocol.Json)!;
        var definition = EmbeddingRecipes.PublicDefault;
        string cache = Path.Combine(data, "models", space.CacheKey);
        CopyCache(modelDirectory, cache);
        File.WriteAllText(Path.Combine(cache, CachedModel.ManifestName), JsonSerializer.Serialize(new
        {
            Space = fixture.RootElement,
            Files = definition.Files.Select(name => new { Name = name, Bytes = definition.Sizes![name], Sha256 = definition.Checksums[name] }),
            SourceUrl = definition.SourceUrl,
            SourceRevision = definition.SourceRevision
        }, Protocol.Json));
        using (var update = new ModelSettingsUpdate(data, ModelSelection.Default)) { update.Publish(); update.Commit(); }
        string routes = PublicModelTests.OriginalPublicRoutes(fixture.RootElement);
        int folder;
        using (var catalog = new Catalog(data, 1))
        {
            folder = catalog.AddFolder(corpus);
            catalog.SetSetting("embedding_routes", routes);
            catalog.SetSetting("model_selection", JsonSerializer.Serialize(ModelSelection.Default, Protocol.Json));
            catalog.SetSetting("schema_version", "6");
            catalog.SetSetting("generation:" + space.Key, catalog.Setting("generation")!);
        }
        string xml = FilePaths.Hash(Path.Combine(data, "settings.xml"));
        await using (var engine = new EngineProcess(package, legacy, data, offline: true, isolatedRuntime: true))
        {
            await engine.ReadyAsync();
            await IndexedAsync(engine);
            await RetrievalAsync(engine);
            Program.Require(engine.Status!.Folders.Single().Id == folder, "Original public scope was replaced.");
        }
        long generation = Check.Scalar(data, "SELECT value FROM settings WHERE key='generation'");
        string partial = Path.Combine(data, "models", ".acquiring-" + space.CacheKey, "tokenizer.model.partial");
        Directory.CreateDirectory(Path.GetDirectoryName(partial)!);
        File.Move(Path.Combine(cache, "tokenizer.model"), partial);
        await OfflineRestartAsync(package, legacy, data, generation);
        using var database = Check.Open(data, readOnly: true);
        using var command = database.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key='embedding_routes'";
        Program.Require((string)command.ExecuteScalar()! == routes && FilePaths.Hash(Path.Combine(data, "settings.xml")) == xml &&
            Directory.Exists(Path.Combine(data, "model-indexes", space.Key)) &&
            File.Exists(Path.Combine(cache, "tokenizer.model")) && !File.Exists(partial),
            "Original public startup/repair changed durable identities, settings or its existing index.");
        Console.WriteLine("PASS original public recipe/cache startup, native retrieval, offline repair and restart without reindexing");
    }

    private static async Task RetryAsync(string package, string legacy, string cache, string corpus, string data)
    {
        Directory.CreateDirectory(data);
        using (var update = new ModelSettingsUpdate(data, ModelSelection.Default)) { update.Publish(); update.Commit(); }
        string xml = FilePaths.Hash(Path.Combine(data, "settings.xml"));
        await using var engine = new EngineProcess(package, legacy, data, offline: true, isolatedRuntime: true);
        await Check.UntilAsync(() => engine.Status?.ModelState == "Error", "Disconnected fresh startup did not fail visibly.", 90000);
        Program.Require(FilePaths.Hash(Path.Combine(data, "settings.xml")) == xml, "A failed download changed settings.");
        await engine.RequestAsync<JsonElement>("add_folder", new { path = corpus });
        CopyCache(cache, Path.Combine(data, "models"));
        var applied = await engine.RequestAsync<ModelApplyResult>("apply_models", ModelSelection.Default);
        Program.Require(applied.Changed, "Unchanged models after failed startup were prematurely treated as a no-op.");
        await engine.ReadyAsync();
        await IndexedAsync(engine);
        await RetrievalAsync(engine);
        Program.Require(Check.Scalar(data, "SELECT COUNT(*) FROM pragma_table_info('job_passages') WHERE name='chunk_json'") == 1,
            "Recovery did not initialize structured content storage.");
        string healthy = FilePaths.Hash(Path.Combine(data, "settings.xml"));
        Program.Require(!(await engine.RequestAsync<ModelApplyResult>("apply_models", ModelSelection.Default)).Changed &&
            FilePaths.Hash(Path.Combine(data, "settings.xml")) == healthy, "Healthy unchanged Apply was not a no-op.");
    }

    private static async Task StartupCommitRetryAsync(string package, string legacy, string cache, string corpus, string data)
    {
        Directory.CreateDirectory(data);
        using (var update = new ModelSettingsUpdate(data, ModelSelection.Default)) { update.Publish(); update.Commit(); }
        CopyCache(cache, Path.Combine(data, "models"));
        string path = Path.Combine(data, "settings.xml"), original = FilePaths.Hash(path);
        var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var engine = new EngineProcess(package, legacy, data, offline: true, isolatedRuntime: true);
        try
        {
            await Check.UntilAsync(() => engine.Status?.ModelState == "Error",
                "Locked startup settings did not fail before publication.", 320000);
            Program.Require(engine.Status!.Models?.Selection == ModelSelection.Default &&
                engine.Status.Cpu.WorkerCount > 0 && FilePaths.Hash(path) == original,
                "The locked-settings fixture did not retain its qualified models and original XML.");
        }
        finally { locked.Dispose(); }
        Program.Require((await engine.RequestAsync<ModelApplyResult>("apply_models", ModelSelection.Default)).Changed,
            "Same-selection recovery skipped a failed startup settings commit.");
        await engine.ReadyAsync();
        await engine.RequestAsync<JsonElement>("add_folder", new { path = corpus });
        await IndexedAsync(engine);
        await RetrievalAsync(engine);
        Console.WriteLine("PASS retry after successful native loading but failed startup settings publication");
    }

    private static async Task LegacyRecoveryAsync(string package, string legacy, string cache, string corpus, string data)
    {
        Directory.CreateDirectory(data);
        string path = Path.Combine(data, "settings.xml");
        var document = new XDocument(new XElement("FuzzyFindAnythingConfiguration", new XAttribute("version", "1"),
            new XElement("Appearance", new XAttribute("theme", "Dark")),
            new XElement("Logging", new XAttribute("enabled", false), new XAttribute("directory", Path.Combine(data, "saved logs"))),
            new SearchPreferences(DocumentThreshold: 0.65, CodeThreshold: 0.45, ImageThreshold: 0.35).ToXml(),
            ModelSelection.LegacyDefault.ToXml()));
        document.Save(path);
        int folder;
        using (var catalog = new Catalog(data, 1))
        {
            folder = catalog.AddFolder(corpus);
            string pending = Path.Combine(corpus, "orchard.txt");
            catalog.Enqueue(folder, pending, FilePaths.Inspect(pending), "Document", FilePaths.Hash(pending), false);
        }
        string original = FilePaths.Hash(path);
        await using (var disconnected = new EngineProcess(package, legacy, data, offline: true, isolatedRuntime: true))
            await Check.UntilAsync(() => disconnected.Status?.ModelState == "Error", "Missing public assets did not fail offline.", 90000);
        Program.Require(FilePaths.Hash(path) == original, "Failed automatic recovery overwrote the old XML.");
        CopyCache(cache, Path.Combine(data, "models"));
        await using (var recovered = new EngineProcess(package, legacy, data, offline: true, isolatedRuntime: true))
        {
            await recovered.ReadyAsync();
            await IndexedAsync(recovered);
            Program.Require(ModelSelection.Load(data) == ModelSelection.Default && recovered.Status!.Folders.Single().Id == folder &&
                recovered.Status.Folders.Single().Path == corpus, "Automatic recovery lost scope or did not persist the public selection.");
            var current = XDocument.Load(path);
            foreach (string section in new[] { "Appearance", "Logging", "Search" })
                Program.Require(XNode.DeepEquals(document.Root!.Element(section), current.Root!.Element(section)),
                    $"Automatic recovery modified the user's {section} preferences.");
        }
        await OfflineRestartAsync(package, legacy, data, Check.Scalar(data, "SELECT value FROM settings WHERE key='generation'"));
    }

    private static void CopyCache(string source, string destination)
    {
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            if (file.EndsWith(".lock", StringComparison.Ordinal)) continue;
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }

    internal static async Task<object> ParityAsync(string package, string directory, EmbeddingSpace space, string fixtures)
    {
        package = Path.GetFullPath(package); directory = Path.GetFullPath(directory); fixtures = Path.GetFullPath(fixtures);
        var memory = new ModelMemoryCoordinator(Console.WriteLine);
        using var admission = await memory.AcquireAsync(null, space.MemoryBytes, default);
        await using var client = await InferenceClient.StartAsync(
            Path.Combine(package, "hf-inference", "FuzzyFindAnything.HuggingFaceInference.exe"), directory,
            CpuBudget.Apply().Processors, (_, stage) => Console.WriteLine(stage), Console.Error.WriteLine, default,
            requiredProvider: "CPUExecutionProvider:CPU");
        using var golden = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixtures, "goldens.json")));
        var root = golden.RootElement;
        Program.Require(root.GetProperty("revision").GetString() == EmbeddingRecipes.PublicDefault.SourceRevision,
            "Independent golden fixtures target a different checkpoint.");
        int tokenCases = 0;
        foreach (var sample in root.GetProperty("tokenizer_stress").EnumerateArray())
        {
            int[] actual = (await client.RequestAsync("tokens", new { text = sample.GetProperty("text").GetString() }, default)).Deserialize<int[]>()!;
            Program.Require(actual.SequenceEqual(sample.GetProperty("ids").Deserialize<int[]>()!), "Native SentencePiece differs from the independent reference.");
            tokenCases++;
        }
        var cases = root.GetProperty("cases").EnumerateArray().ToArray();
        var vectors = new Dictionary<string, float[]>(); var references = new Dictionary<string, float[]>();
        static double Cosine(float[] first, float[] second) => first.Zip(second, (a, b) => (double)a * b).Sum() /
            Math.Sqrt(first.Sum(value => (double)value * value) * second.Sum(value => (double)value * value));
        static double Maximum(float[] first, float[] second) => first.Zip(second, (a, b) => Math.Abs((double)a - b)).Max();
        foreach (var item in cases)
        {
            string id = item.GetProperty("id").GetString()!;
            bool image = item.GetProperty("kind").GetString() == "image";
            object args = image ? new { paths = new[] { Path.Combine(fixtures, item.GetProperty("file").GetString()!) } } :
                new { texts = new[] { item.GetProperty("text").GetString()! } };
            float[] actual = (await client.RequestAsync(image ? "images" : "documents", args, default)).Deserialize<float[][]>()![0];
            float[] expected = item.GetProperty("embedding").Deserialize<float[]>()!;
            double cosine = Cosine(expected, actual), maximum = Maximum(expected, actual);
            Program.Require(actual.Length == space.Dimensions && Math.Abs(Math.Sqrt(actual.Sum(value => (double)value * value)) - 1) <= 0.00001 &&
                cosine >= (image ? 0.999 : 0.99999) && (image || maximum <= 0.0002), $"Public embedding gate failed: {id}, cosine={cosine}, maximum={maximum}");
            vectors[id] = actual; references[id] = expected;
            Console.WriteLine($"PARITY {id}: cosine={cosine:F9}, max={maximum:E3}");
        }
        var textCases = new[] { "extra-long-code", "extra-short", "extra-long-unicode", "extra-query-maximum",
            "extra-overlap", "unicode-cjk", "extra-sql", "extra-long-document" }
            .Select(id => cases.Single(item => item.GetProperty("id").GetString() == id)).ToArray();
        foreach (int count in new[] { 1, 2, 4, 6, 8 })
        {
            var selected = textCases.Take(count).ToArray();
            var batch = (await client.RequestAsync("documents", new { texts = selected.Select(item => item.GetProperty("text").GetString()).ToArray() }, default)).Deserialize<float[][]>()!;
            for (int row = 0; row < count; row++)
            {
                string id = selected[row].GetProperty("id").GetString()!;
                Program.Require(Cosine(references[id], batch[row]) >= 0.99999 && Maximum(references[id], batch[row]) <= 0.0002, "Padding changed a text embedding.");
            }
        }
        var images = cases.Where(item => item.GetProperty("kind").GetString() == "image").ToArray();
        for (int index = 0; index + 1 < images.Length; index += 2)
        {
            var pair = images.Skip(index).Take(2).ToArray();
            var batch = (await client.RequestAsync("images", new { paths = pair.Select(item => Path.Combine(fixtures, item.GetProperty("file").GetString()!)).ToArray() }, default)).Deserialize<float[][]>()!;
            for (int row = 0; row < 2; row++)
                Program.Require(Cosine(vectors[pair[row].GetProperty("id").GetString()!], batch[row]) >= 0.99999, "Image batching changed result ownership.");
        }
        double drift = vectors.Keys.SelectMany(first => vectors.Keys.Select(second => Math.Abs(
            ModelContract.Similarity(references[first], references[second]) - ModelContract.Similarity(vectors[first], vectors[second])))).Max();
        Program.Require(drift <= 0.005, "Pairwise similarity drift exceeded 0.005.");
        var candidates = cases.Where(item => item.GetProperty("kind").GetString() != "query").Select(item => item.GetProperty("id").GetString()!).ToArray();
        foreach (var query in cases.Where(item => item.GetProperty("kind").GetString() == "query"))
        {
            string id = query.GetProperty("id").GetString()!;
            var expected = candidates.OrderByDescending(candidate => ModelContract.Similarity(references[id], references[candidate])).ToArray();
            string actual = candidates.MaxBy(candidate => ModelContract.Similarity(vectors[id], vectors[candidate]))!;
            double gap = ModelContract.Similarity(references[id], references[expected[0]]) - ModelContract.Similarity(references[id], references[expected[1]]);
            Program.Require(gap <= 0.01 || actual == expected[0], "A clearly separated reference retrieval winner changed.");
        }
        return new { tokenizer_cases = tokenCases, embedding_cases = cases.Length, image_pairs = images.Length / 2, pairwise_drift = drift };
    }
}
