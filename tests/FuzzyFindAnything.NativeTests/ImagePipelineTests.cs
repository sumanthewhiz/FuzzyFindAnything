using System.IO;
using System.Xml.Linq;
using FuzzyFindAnything.Core;

namespace FuzzyFindAnything.NativeTests;

internal static class ImagePipelineTests
{
    private const string ImageModel = "https://huggingface.co/microsoft/Florence-2-base-ft";
    public static void Run()
    {
        Program.Run("Generic image selection, linked embedder and simple defaults", Selection);
        Program.Run("Durable image representations, selective re-embedding and ownership", Persistence);
        Program.Run("Finite image task retries and explicit empty outcomes", Outcomes);
        Program.Run("Image-stage restart reuses completed generation", () => RestartAsync().GetAwaiter().GetResult());
    }
    private static ImagePipelineDescriptor Pipeline(EmbeddingSpace space, params string[] tasks) =>
        new(ImageModel, ImageModelRecipes.Find(ImageModel)!, tasks, space, ModelOptions.Default, "Automatic");
    private static Dictionary<string, EmbeddingSpace> Routes(EmbeddingSpace space) => new()
    { ["Document"] = space, ["Code"] = EmbeddingSpace.Default, ["Image"] = space };
    private static void Selection()
    {
        var selected = ModelSelection.Default with
        {
            Image = ImageModel,
            TextOptions = new("document-search-v1", "CPU"),
            ImagePipeline = new(Caption: true, Ocr: true)
        };
        var normalized = selected.Normalize();
        Program.Require(normalized.UsesImageUnderstanding && normalized.EmbeddingSelection().Image == selected.Text &&
            normalized.EmbeddingSelection().OptionsFor("Image").Device == "CPU" &&
            normalized.EmbeddingSelection().OptionsFor("Image").QueryProfile == "",
            "Image query/embedding dependencies were copied incorrectly.");
        var xml = new XElement("FuzzyFindAnythingConfiguration", new XAttribute("version", "2"), normalized.ToXml(true));
        Program.Require(ModelSelection.Read(xml) == normalized, "Image task configuration did not round-trip.");
        var direct = selected with { Image = ModelSelection.DefaultUrl };
        Program.Require(direct.Normalize().ImagePipeline is null, "Inactive image pipeline options leaked into direct embeddings.");
        Check.Throws<ArgumentException>(() => new ImagePipelineOptions(false, false).Normalize());
        Check.Throws<NotSupportedException>(() => ImageModelRecipes.Find(ImageModel)!.Task("execute"));
        Program.Require(ImageModelRecipes.Registered.Count >= 2 &&
            ImageModelRecipes.Registered.Select(model => model.Recipe.Id).Distinct().Count() == 1,
            "The second compatible checkpoint requires a model-specific pipeline implementation.");
    }
    private static void Publish(Catalog catalog, IndexJob job, StoredImageTask task, int dimensions, string? warning = null)
    {
        catalog.PrepareImage(job, [new(0, task.Result.Text, task.Result.Task, task.Id)], warning);
        float[] vector = new float[dimensions]; vector[0] = 1;
        catalog.SaveBatch(job, catalog.Remaining(job), [vector]);
        Program.Require(catalog.Complete(job), "Image text vectors did not publish.");
    }
    private static void Persistence() => Program.Temporary(root =>
    {
        string source = Path.Combine(root, "files"), nested = Path.Combine(source, "images"), data = Path.Combine(root, "index");
        Directory.CreateDirectory(nested);
        string path = Path.Combine(nested, "picture.png");
        var text = new EmbeddingSpace("https://huggingface.co/test/text-encoder", new string('a', 40), "bert-mean", 384, 128, false, 1L << 30);
        var pipeline = Pipeline(text, "caption", "ocr");
        var selection = new ModelSelection(text.ModelUrl, ModelSelection.DefaultUrl, ImageModel, ImagePipeline: new(Ocr: true));
        long imageId, captionId;
        int parent, child;
        using (var catalog = new Catalog(data, 1))
        {
            parent = catalog.AddFolder(source); child = catalog.AddFolder(nested);
            catalog.ReconfigureModels(Routes(text), () => { }, selection: selection, pipeline: pipeline);
            var job = Program.Enqueue(catalog, parent, path, "synthetic image bytes", "Image");
            catalog.Enqueue(child, path, FilePaths.Inspect(path), "Image", FilePaths.Hash(path), false);
            var caption = catalog.SaveImageTask(job, pipeline.Model.TaskKey("caption"), new("caption", "A red circle.", "Completed", 8));
            catalog.SaveImageTask(job, pipeline.Model.TaskKey("ocr"), new("ocr", "", "Failed", Error: "Unreadable text", ErrorCode: "ocr_failure"));
            Publish(catalog, job, caption, 384, "OCR failed; caption remains searchable.");
            imageId = catalog.FindFile(path)!.Id; captionId = caption.Id;
            float[] query = new float[384]; query[0] = 1;
            var result = catalog.Search(query, 1, 0, text, ["Image"]).Single();
            Program.Require(result.Kind == "Image" && result.Evidence == "caption" && result.Path == path,
                "Generated text lost its image identity/provenance.");
            string operation = catalog.ReindexOperation()!;
            catalog.ReindexScanCompleted(parent, operation, true); catalog.ReindexScanCompleted(child, operation, true);
            Program.Require(catalog.ModelReindex(false) is { DiscoveryComplete: true }, "Initial image discovery did not seal its operation.");
            _ = Program.Enqueue(catalog, parent, Path.Combine(source, "later.txt"), "later unrelated work");
            Program.Require(catalog.ModelReindex(true, false) is { Completed: true, Partial: 1 },
                "Later unrelated indexing prevented the completed image operation from reporting its outcome.");
        }
        using (var catalog = new Catalog(data, 1))
        {
            var next = Pipeline(EmbeddingSpace.Default, "caption", "ocr");
            var changed = catalog.ReconfigureModels(Routes(EmbeddingSpace.Default), () => { },
                selection: ModelSelection.Default with { Image = ImageModel, ImagePipeline = new(Ocr: true) }, pipeline: next);
            Program.Require(changed.Contains("Image") && catalog.FindFile(path)!.Id == imageId, "Changing the text embedder deleted stable image metadata.");
            Program.Require(catalog.Enqueue(parent, path, FilePaths.Inspect(path), "Image", "", false), "Image re-embedding was not queued.");
            var job = catalog.Claim()!;
            var cached = catalog.ReadImageTask(job, next.Model.TaskKey("caption"));
            Program.Require(cached?.Id == captionId && cached.Result.Text == "A red circle.", "Embedding-only changes discarded completed caption text.");
            Publish(catalog, job, cached!, 768, "OCR failed; caption retained.");
            Program.Require(catalog.RetryImageTasks() > 0 &&
                Check.Scalar(data, "SELECT COUNT(*) FROM image_representations WHERE task='caption' AND state='Completed'") == 1 &&
                Check.Scalar(data, "SELECT COUNT(*) FROM image_representations WHERE task='ocr'") == 0,
                "Retry invalidated a successful sibling task or retained failed work.");
            catalog.RemoveFolders([parent]);
            Program.Require(catalog.FindFile(path) is not null &&
                Check.Scalar(data, "SELECT COUNT(*) FROM image_representations") == 1, "Removing an overlapping parent discarded image artifacts.");
            catalog.RemoveFolders([child]);
            Program.Require(Check.Scalar(data, "SELECT COUNT(*) FROM image_representations") == 0, "Out-of-scope generated text was retained.");
        }
    });
    private static Task RestartAsync() => Check.TemporaryAsync(async root =>
    {
        string source = Path.Combine(root, "files"), data = Path.Combine(root, "index");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "image.png"), "synthetic image input");
        var descriptor = Pipeline(EmbeddingSpace.Default, "caption", "ocr");
        var first = new FixtureProvider(descriptor, failOcr: true);
        await using (var engine = new IndexingEngine(data, Path.Combine(root, "models"), Path.Combine(root, "unused.exe"),
            Path.Combine(root, "app"), new(12, 6, 63), first, new EngineLog(data), new(), watch: false))
        {
            engine.Store.AddFolder(source);
            engine.Store.ReconfigureModels(Routes(EmbeddingSpace.Default), () => { },
                selection: ModelSelection.Default with { Image = ImageModel, ImagePipeline = new(Ocr: true) }, pipeline: descriptor);
            engine.Start();
            await Check.UntilAsync(() => engine.Store.Counts().Files == 1 && engine.Store.QueueCounts().Queued == 1 &&
                engine.Store.FindFile(Path.Combine(source, "image.png"))?.Warning is not null, "Partial image output was not published.");
            Program.Require(first.CaptionCalls == 1 && first.OcrCalls == 1, "The first image stage did not execute exactly once.");
        }
        Check.Execute(data, "UPDATE index_jobs SET retry_at=0; UPDATE image_representations SET retry_at=0 WHERE retryable=1");
        var resumed = new FixtureProvider(descriptor, failOcr: false);
        await using (var engine = new IndexingEngine(data, Path.Combine(root, "models"), Path.Combine(root, "unused.exe"),
            Path.Combine(root, "app"), new(12, 6, 63), resumed, new EngineLog(data), new(), watch: false))
        {
            engine.Start();
            await Check.UntilAsync(() => engine.Store.Counts().Vectors == 2 && engine.Store.QueueCounts().Queued == 0,
                "The retained image stages did not resume.");
            Program.Require(resumed.CaptionCalls == 0 && resumed.OcrCalls == 1 &&
                engine.Store.FindFile(Path.Combine(source, "image.png"))?.Warning is null,
                "Restart regenerated a completed caption or retained a stale partial warning.");
            var result = await engine.SearchAsync("apple", 0);
            Program.Require(result.Single().Kind == "Image" && result.Single().Evidence == "caption",
                "The generic image provider did not merge its text embeddings back into image results.");
        }
    });

    private sealed class FixtureProvider(ImagePipelineDescriptor descriptor, bool failOcr) : IEmbeddings, IImageUnderstanding
    {
        private readonly FakeEmbeddings embeddings = new();
        public int CaptionCalls { get; private set; }
        public int OcrCalls { get; private set; }
        public ImagePipelineDescriptor Descriptor => descriptor;
        public IImageUnderstanding ImageUnderstanding => this;
        public string Provider => "FixtureExecutionProvider";
        public string Device => "Deterministic test provider";
        public int ReadyWorkers => embeddings.ReadyWorkers;
        public int Threads => 1;
        public int BatchConcurrency => 1;
        public string Warning => "";
        public BatchState Statistics => embeddings.Statistics;
        public Task StartAsync(Action<int, string> progress, CancellationToken cancellation) => embeddings.StartAsync(progress, cancellation);
        public Task<string[]> SplitAsync(string text, CancellationToken cancellation) => embeddings.SplitAsync(text, cancellation);
        public Task<float[][]> DocumentsAsync(string title, string[] passages, CancellationToken cancellation) => embeddings.DocumentsAsync(title, passages, cancellation);
        public Task<float[]> ImageAsync(string path, CancellationToken cancellation) => throw new InvalidOperationException("Image text must use the downstream text encoder.");
        public Task<float[]> QueryAsync(string text, CancellationToken cancellation) => embeddings.QueryAsync(text, cancellation);
        public Task<ImageTaskResult> GenerateAsync(string path, string task, CancellationToken cancellation)
        {
            if (task == "caption") { CaptionCalls++; return Task.FromResult(new ImageTaskResult(task, "apple orchard", "Completed")); }
            OcrCalls++;
            if (failOcr) throw new ContentException("Temporary OCR failure", "test_ocr", true);
            return Task.FromResult(new ImageTaskResult(task, "WELCOME", "Completed"));
        }
        public ValueTask DisposeAsync() => embeddings.DisposeAsync();
    }

    private static void Outcomes() => Program.Temporary(root =>
    {
        string data = Path.Combine(root, "index"), source = Path.Combine(root, "files");
        Directory.CreateDirectory(source);
        using var catalog = new Catalog(data, 1);
        int folder = catalog.AddFolder(source);
        var pipeline = Pipeline(EmbeddingSpace.Default, "ocr");
        catalog.ReconfigureModels(Routes(EmbeddingSpace.Default), () => { },
            selection: ModelSelection.Default with { Image = ImageModel, ImagePipeline = new(false, true) }, pipeline: pipeline);
        var job = Program.Enqueue(catalog, folder, Path.Combine(source, "empty.png"), "empty OCR fixture", "Image");
        StoredImageTask? failed = null;
        for (int attempt = 0; attempt < 3; attempt++)
            failed = catalog.SaveImageTask(job, pipeline.Model.TaskKey("ocr"),
                new("ocr", "", "Failed", Error: "Temporary execution problem", ErrorCode: "temporary", Retryable: true));
        Program.Require(failed is { Attempts: 3, RetryAt: null, Result.Retryable: false }, "Image tasks can retry without a terminal bound.");
        catalog.SaveImageTask(job, pipeline.Model.TaskKey("ocr"), new("ocr", "", "Empty"));
        Program.Require(catalog.CompleteEmptyImage(job) && catalog.Counts() == (0, 0) && catalog.QueueCounts().Queued == 0,
            "An empty OCR result was counted as a searchable image or left pending.");
        var counts = catalog.FolderCounts(catalog.Folders().Single(), false);
        Program.Require(counts.Skipped == 1 && counts.Failed == 0, "An empty successful OCR task was misreported as a failure.");
    });
}
