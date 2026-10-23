using System.IO;
using FuzzyFindAnything.Core;

namespace FuzzyFindAnything.NativeTests;

internal static class SearchPreferenceTests
{
    public static void Run() => Program.Run("Selected search types and per-kind thresholds share vectors safely", () => VerifyAsync().GetAwaiter().GetResult());
    private static Task VerifyAsync() => Check.TemporaryAsync(async root =>
    {
        string data = Path.Combine(root, "index"), source = Path.Combine(root, "files");
        Directory.CreateDirectory(source);
        using (var catalog = new Catalog(data, 1))
        {
            int folder = catalog.AddFolder(source);
            foreach (var (kind, name, score) in new[] { ("Document", "doc.txt", 0.8f), ("Code", "code.cs", 0.5f), ("Image", "image.png", 0.2f) })
            {
                var job = Program.Enqueue(catalog, folder, Path.Combine(source, name), "fixture", kind);
                float[] vector = Program.Vector(0);
                vector[0] = score; vector[1] = MathF.Sqrt(1 - score * score);
                catalog.Prepare(job, ["fixture"], null); catalog.SaveBatch(job, catalog.Remaining(job), [vector]); catalog.Complete(job);
            }
        }
        var documents = new FakeEmbeddings(); var code = new FakeEmbeddings(); var images = new FakeEmbeddings();
        var model = new FakeEmbeddings
        {
            QueryBindings = [
            new(documents, "default", ["Document"]), new(code, "default", ["Code"]), new(images, "default", ["Image"])]
        };
        await using var engine = new IndexingEngine(data, Path.Combine(root, "models"), Path.Combine(root, "unused.exe"), Path.Combine(root, "app"),
            new(12, 6, 63), model, new EngineLog(data), new(), watch: false);
        engine.Start();
        await Check.UntilAsync(() => engine.Status() is { ModelState: "Ready", QueueCount: 0, ReconcilingFolders: 0 }, "Search fixture was not ready.");
        var onlyDocuments = await engine.SearchAsync("apple first", new SearchPreferences(true, false, false, 0.7), 1);
        Program.Require(onlyDocuments.Single().Kind == "Document" && documents.Queries.Count == 1 && code.Queries.Count == 0 && images.Queries.Count == 0,
            "Disabled search types were queried or returned.");
        var mixed = await engine.SearchAsync("apple second", new SearchPreferences(true, true, true, 0.9, 0.4, 0.1), 1);
        Program.Require(mixed.Select(value => value.Kind).Order().SequenceEqual(new[] { "Code", "Image" }),
            "Shared-index results did not honor independent thresholds and per-kind limits.");
        await Check.ThrowsAsync<ArgumentException>(() => engine.SearchAsync("apple", new SearchPreferences(false, false, false)));
        await Check.ThrowsAsync<ArgumentException>(() => engine.SearchAsync("apple", new SearchPreferences(ImageThreshold: double.NaN)));
    });
}
