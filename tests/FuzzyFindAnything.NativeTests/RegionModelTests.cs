using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using FuzzyFindAnything.Core;

namespace FuzzyFindAnything.NativeTests;

internal static class RegionModelTests
{
    public static Task IntegrationAsync(string package, string defaultModel, string embeddings, string imageCache, string image, string output) =>
        Check.TemporaryAsync(async root =>
        {
            string data = Path.Combine(root, "index"), corpus = Path.Combine(root, "images");
            Directory.CreateDirectory(corpus);
            string path = Path.Combine(corpus, "vehicle.jpg"); File.Copy(image, path);
            ImageModelIntegration.LinkCache(embeddings, Path.Combine(data, "models"));
            ImageModelIntegration.LinkCache(imageCache, Path.Combine(data, "models", "image-understanding"));
            var selection = new ModelSelection("https://huggingface.co/sentence-transformers/paraphrase-MiniLM-L3-v2",
                "https://huggingface.co/sentence-transformers/paraphrase-MiniLM-L3-v2",
                "https://huggingface.co/microsoft/Florence-2-base-ft", ImagePipeline: new(false, false, RegionTask: "objects"));
            using (var settings = new ModelSettingsUpdate(data, selection)) { settings.Publish(); settings.Commit(); }
            var engine = new EngineProcess(package, defaultModel, data);
            try
            {
                await engine.ReadyAsync();
                await engine.RequestAsync<JsonElement>("add_folder", new { path = corpus });
                await engine.WaitAsync(state => state.FileCount == 1 && state.QueueCount == 0 && state.ReconcilingFolders == 0,
                    "Region-only indexing did not complete.", 300000);
                var result = await engine.RequestAsync<List<ResultFile>>("search", new { query = "wheel", options = new SearchPreferences(false, false, true, ImageThreshold: 0) });
                Program.Require(result.Count == 1 && result[0].Region?.Text == "wheel" && result[0].Path == path,
                    "Region text did not retrieve the original image and matching box.");
                long artifact = Check.Scalar(data, "SELECT id FROM image_representations WHERE task='objects'");
                selection = selection with { ImagePipeline = new(false, false, "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2", RegionTask: "objects") };
                await engine.RequestAsync<ModelApplyResult>("apply_models", selection);
                await engine.WaitAsync(state => state.Models?.Selection == selection && state.QueueCount == 0 &&
                    state.Models.Reindex is { Completed: true, Failed: 0, Partial: 0 }, "Region re-embedding did not complete.");
                Program.Require(Check.Scalar(data, "SELECT id FROM image_representations WHERE task='objects'") == artifact &&
                    Check.Scalar(data, "SELECT attempts FROM image_representations WHERE task='objects'") == 1,
                    "Changing the final embedder regenerated cached regions.");
                await engine.DisposeAsync(); engine = new(package, defaultModel, data);
                await engine.ReadyAsync();
                await engine.WaitAsync(state => state.QueueCount == 0 && state.ReconcilingFolders == 0, "Region restart did not reconcile.");
                var state = await engine.RequestAsync<EngineState>("status");
                Program.Require(state.Models?.ImagePipeline is { IsLoaded: false }, "Searching retained regions loaded the detector.");
                result = await engine.RequestAsync<List<ResultFile>>("search", new { query = "car", options = new SearchPreferences(false, false, true, ImageThreshold: 0) });
                Program.Require(result.Single().Region is not null, "Restart lost region geometry.");
                Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output, "regions-integration.json"), JsonSerializer.Serialize(new { failures = 0, result }, Protocol.Json));
                Console.WriteLine("PASS native region-only retrieval, cache reuse, geometry and lazy restart");
            }
            finally { await engine.DisposeAsync(); }
        });

    public static async Task RunAsync(string package, string cache, string image, string output)
    {
        Directory.CreateDirectory(output);
        var assets = new ImageModelAssets(cache);
        var results = new List<object>();
        foreach (var model in ImageModelRecipes.Registered)
        {
            await using var worker = await InferenceClient.StartAsync(Path.Combine(package, "hf-inference", "FuzzyFindAnything.HuggingFaceInference.exe"),
                assets.DirectoryFor(model), 2, (_, stage) => Console.WriteLine(stage), Console.Error.WriteLine,
                CancellationToken.None, requiredProvider: "CPUExecutionProvider:CPU", imageUnderstanding: true);
            foreach (string task in new[] { "objects", "region-descriptions" })
            {
                var result = (await worker.RequestAsync("describe", new { path = image, task }, CancellationToken.None)).Deserialize<ImageTaskResult>(Protocol.Json)!;
                result.Validate(task);
                var regions = result.Regions ?? throw new InvalidDataException("Missing typed regions.");
                Program.Require(result.State == "Completed" && regions.Length > 0 &&
                    regions.Any(region => Regex.IsMatch(region.Text, "car|vehicle|Volkswagen|Beetle", RegexOptions.IgnoreCase) &&
                        region.Left < 0.15 && region.Right > 0.8 && region.Top < 0.45 && region.Bottom > 0.7) &&
                    !result.Text.Contains("<loc_", StringComparison.Ordinal), "The region fixture did not produce complete labeled car geometry.");
                foreach (var region in regions) region.Validate();
                var capability = await worker.RequestAsync("capabilities", new { }, CancellationToken.None);
                long peak = capability.GetProperty("peak_working_set_bytes").GetInt64();
                Program.Require(peak <= model.MemoryBytes, "The native image profile exceeds its declared memory budget.");
                results.Add(new { model = model.ModelUrls[0], task, regions = regions.Length, peak });
                Console.WriteLine($"PASS {model.ModelUrls[0]} {task}: {regions.Length} regions; peak {peak / 1073741824d:0.00} GiB");
            }
        }
        File.WriteAllText(Path.Combine(output, "regions-native.json"), JsonSerializer.Serialize(new { failures = 0, results },
            new JsonSerializerOptions { WriteIndented = true }));
    }
}
