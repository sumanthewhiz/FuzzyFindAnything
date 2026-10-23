using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FuzzyFindAnything.Core;

namespace FuzzyFindAnything.NativeTests;

internal static class ImageModelIntegration
{
    private const string FineTuned = "https://huggingface.co/microsoft/Florence-2-base-ft";
    private const string Text = "https://huggingface.co/sentence-transformers/paraphrase-MiniLM-L3-v2";
    private const string Alternate = "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2";
    private const string Clip = "https://huggingface.co/Xenova/clip-vit-base-patch32";

    public static void Fixture(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, 768, 768));
            context.DrawText(new FormattedText("HELLO SEARCH 123", System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 60, Brushes.Black, 1), new Point(70, 80));
            context.DrawEllipse(Brushes.Red, null, new Point(384, 450), 160, 160);
        }
        var image = new RenderTargetBitmap(768, 768, 96, 96, PixelFormats.Pbgra32); image.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(path); encoder.Save(file);
    }
    internal static void LinkCache(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (Path.GetExtension(file) == ".onnx" && CreateHardLink(target, file, IntPtr.Zero)) continue;
            File.Copy(file, target);
        }
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string destination, string existing, IntPtr security);

    public static async Task RunAsync(string package, string defaultModel, string embeddings, string imageCache, string output, bool runtimeOnly = false)
    {
        package = Path.GetFullPath(package); output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        string fixture = Path.Combine(output, "caption-ocr.png"); Fixture(fixture);
        var imageAssets = new ImageModelAssets(imageCache);
        var nativeResults = new List<object>();
        foreach (var definition in ImageModelRecipes.Registered)
        {
            string url = definition.ModelUrls[0];
            await using var worker = await InferenceClient.StartAsync(
                Path.Combine(package, "hf-inference", "FuzzyFindAnything.HuggingFaceInference.exe"),
                imageAssets.DirectoryFor(definition), 2, (_, stage) => Console.WriteLine(stage), Console.Error.WriteLine,
                CancellationToken.None, imageUnderstanding: true);
            foreach (var (task, prompt, expected) in new[]
            {
                ("caption", "Describe in detail what is shown in the image.", new[] { 0, 47066, 21700, 11, 4617, 99, 16, 2343, 11, 5, 2274, 4, 2 }),
                ("ocr", "What is the text in the image?", new[] { 0, 2264, 16, 5, 2788, 11, 5, 2274, 116, 2 })
            })
            {
                var ids = (await worker.RequestAsync("tokens", new { text = prompt }, CancellationToken.None)).Deserialize<int[]>()!;
                Program.Require(ids.SequenceEqual(expected), "Image task tokens differ from the pinned tokenizer reference.");
                var result = (await worker.RequestAsync("describe", new { path = fixture, task }, CancellationToken.None)).Deserialize<ImageTaskResult>(Protocol.Json)!;
                result.Validate(task);
                Program.Require(result.State == "Completed", "The native task did not terminate with a complete output.");
                if (task == "ocr") Program.Require(result.Text.Contains("HELLO SEARCH 123", StringComparison.OrdinalIgnoreCase),
                    "The native OCR task did not recognize the synthetic reference text.");
                else Program.Require(result.Text.Contains("red", StringComparison.OrdinalIgnoreCase) &&
                    result.Text.Contains("circle", StringComparison.OrdinalIgnoreCase), "The native caption did not describe the reference image.");
                nativeResults.Add(new { url, task, result.Text, result.GeneratedTokens });
                Console.WriteLine("PASS native " + task + ": " + url);
            }
            await Check.ThrowsAsync<ContentException>(async () => await worker.RequestAsync("describe",
                new { path = fixture, task = "execute" }, CancellationToken.None));
        }
        if (runtimeOnly)
        {
            File.WriteAllText(Path.Combine(output, "image-runtime.json"), JsonSerializer.Serialize(new { failures = 0, nativeResults },
                new JsonSerializerOptions { WriteIndented = true }));
            return;
        }
        await Check.TemporaryAsync(async root =>
        {
            string data = Path.Combine(root, "index"), corpus = Path.Combine(root, "files");
            Directory.CreateDirectory(corpus);
            LinkCache(embeddings, Path.Combine(data, "models"));
            LinkCache(imageCache, Path.Combine(data, "models", "image-understanding"));
            File.Copy(fixture, Path.Combine(corpus, "picture.png"));
            File.WriteAllText(Path.Combine(corpus, "notes.txt"), "A travel itinerary for a rail journey through the mountains.");
            var initial = new ModelSelection(Text, Text, FineTuned);
            using (var settings = new ModelSettingsUpdate(data, initial)) { settings.Publish(); settings.Commit(); }
            var engine = new EngineProcess(package, defaultModel, data);
            try
            {
                await engine.ReadyAsync();
                await engine.RequestAsync<JsonElement>("add_folder", new { path = corpus });
                await engine.WaitAsync(state => state.FileCount == 2 && state.QueueCount == 0 && state.ReconcilingFolders == 0, "Initial image fixture did not index.");
                long document = Scalar(data, "SELECT MIN(v.id) FROM vectors v JOIN files f ON f.id=v.file_id WHERE f.kind='Document'");
                long imageId = Scalar(data, "SELECT id FROM files WHERE kind='Image'");
                var selection = initial with { Image = FineTuned, ImagePipeline = new(Ocr: true) };
                var applied = await engine.RequestAsync<ModelApplyResult>("apply_models", selection);
                Program.Require(applied.ReindexedKinds.SequenceEqual(["Image"]), "The image pipeline rebuilt unrelated document/code bindings.");
                await engine.WaitAsync(state => state.Models?.Selection == selection && state.QueueCount == 0 &&
                    state.Models.Reindex is { Completed: true, Failed: 0, Partial: 0 } && state.FileCount == 2,
                    "The native described-image pipeline did not finish.", 360000);
                Program.Require(Scalar(data, "SELECT id FROM files WHERE kind='Image'") == imageId &&
                    Scalar(data, "SELECT MIN(v.id) FROM vectors v JOIN files f ON f.id=v.file_id WHERE f.kind='Document'") == document,
                    "The new pipeline lost stable source identities or unchanged document vectors.");
                Program.Require(Scalar(data, "SELECT COUNT(*) FROM image_representations WHERE state='Completed'") == 2,
                    "Caption/OCR were not committed as independent reusable stages.");
                var results = await engine.RequestAsync<List<ResultFile>>("search", new { query = "HELLO SEARCH 123", threshold = 0, limit = 1 });
                Program.Require(results.Single(value => value.Kind == "Image").Name == "picture.png" &&
                    results.Single(value => value.Kind == "Image").Evidence == "ocr", "OCR matches did not return the original image/evidence.");
                long captionId = Scalar(data, "SELECT id FROM image_representations WHERE task='caption'");
                selection = selection with { ImagePipeline = new(Ocr: true, EmbeddingModel: Alternate) };
                applied = await engine.RequestAsync<ModelApplyResult>("apply_models", selection);
                await engine.WaitAsync(state => state.Models?.Selection == selection && state.QueueCount == 0 &&
                    state.Models.Reindex is { Completed: true, Failed: 0, Partial: 0 }, "Embedding-only image update failed.", 300000);
                Program.Require(applied.ReindexedKinds.SequenceEqual(["Image"]) &&
                    Scalar(data, "SELECT id FROM image_representations WHERE task='caption'") == captionId &&
                    Scalar(data, "SELECT MAX(attempts) FROM image_representations") == 1,
                    "An embedding-only change regenerated completed image text.");
                Console.WriteLine("PASS native image retrieval, stable sources and generation-cache reuse");

                selection = selection with { Text = Alternate };
                long imageVector = Scalar(data, "SELECT MIN(v.id) FROM vectors v JOIN files f ON f.id=v.file_id WHERE f.kind='Image'");
                applied = await engine.RequestAsync<ModelApplyResult>("apply_models", selection);
                await engine.WaitAsync(state => state.Models?.Selection == selection && state.QueueCount == 0 &&
                    state.Models.Reindex is { Completed: true }, "Independent text update did not finish.");
                Program.Require(applied.ReindexedKinds.SequenceEqual(["Document"]) &&
                    Scalar(data, "SELECT MIN(v.id) FROM vectors v JOIN files f ON f.id=v.file_id WHERE f.kind='Image'") == imageVector,
                    "A custom image embedder incorrectly followed the Text setting.");
                selection = selection with { ImagePipeline = new(Ocr: true) };
                applied = await engine.RequestAsync<ModelApplyResult>("apply_models", selection);
                Program.Require(applied.ReindexedKinds.Length == 0, "Linking the same effective text encoder rebuilt image vectors.");
                selection = selection with { Text = Text };
                applied = await engine.RequestAsync<ModelApplyResult>("apply_models", selection);
                await engine.WaitAsync(state => state.Models?.Selection == selection && state.QueueCount == 0 &&
                    state.Models.Reindex is { Completed: true, Failed: 0, Partial: 0 }, "Linked image embeddings did not update.");
                Program.Require(applied.ReindexedKinds.Contains("Document") && applied.ReindexedKinds.Contains("Image") &&
                    Scalar(data, "SELECT MAX(attempts) FROM image_representations") == 1,
                    "The linked Text dependency did not selectively re-embed descriptions.");
                Console.WriteLine("PASS linked/custom embedding dependencies and independent query budgets");
                string preferences = FilePaths.Hash(Path.Combine(data, "settings.xml"));
                var invalid = selection with { ImagePipeline = new(false, false) };
                await Check.ThrowsAsync<IOException>(async () => await engine.RequestAsync<ModelApplyResult>("apply_models", invalid));
                Program.Require(FilePaths.Hash(Path.Combine(data, "settings.xml")) == preferences, "Invalid task choices changed the applied pipeline.");
                await engine.DisposeAsync();
                engine = new EngineProcess(package, defaultModel, data);
                await engine.ReadyAsync();
                await engine.WaitAsync(state => state.QueueCount == 0 && state.FileCount == 2 && state.ReconcilingFolders == 0,
                    "The image pipeline did not restore after restart.");
                var restarted = await engine.RequestAsync<EngineState>("status");
                Program.Require(restarted.Models?.ImagePipeline?.Provider == "" &&
                    restarted.InferenceBatches.CompletedInputs == 0, "Restart unnecessarily loaded generation or re-embedded unchanged files.");
                results = await engine.RequestAsync<List<ResultFile>>("search", new { query = "red circle", threshold = 0, limit = 1 });
                Program.Require(results.Any(value => value.Kind == "Image"), "Existing image descriptions were not searchable without a generation worker.");
                var direct = new ModelSelection(Text, Text, Clip);
                await engine.RequestAsync<ModelApplyResult>("apply_models", direct);
                await engine.WaitAsync(state => state.Models?.Selection == direct && state.FileCount == 2 && state.QueueCount == 0 &&
                    state.Models.Reindex is { Completed: true }, "Restoring direct image embeddings failed.", 300000);
                Program.Require((await engine.RequestAsync<EngineState>("status")).Models?.ImagePipeline is null,
                    "A generation pipeline remained active after restoring a direct model.");
                Console.WriteLine("PASS lazy generation restart, invalid-config preservation and direct-model restoration");
            }
            finally { await engine.DisposeAsync(); }
        });
        File.WriteAllText(Path.Combine(output, "image-pipeline.json"), JsonSerializer.Serialize(new { failures = 0, nativeResults },
            new JsonSerializerOptions { WriteIndented = true }));
    }
    private static long Scalar(string directory, string sql) => Check.Scalar(directory, sql);
}
