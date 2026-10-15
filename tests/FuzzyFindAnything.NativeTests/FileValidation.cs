using System.Diagnostics;
using System.IO;
using System.Text.Json;
using FuzzyFindAnything.Core;

namespace FuzzyFindAnything.NativeTests;

internal static class FileValidation
{
    public static void Lookup(string directory, string name)
    {
        SQLitePCL.Batteries_V2.Init();
        using var database = Check.Open(directory, true);
        using var command = database.CreateCommand(); command.CommandText = "SELECT path FROM files";
        using var reader = command.ExecuteReader();
        var paths = new List<string>();
        while (reader.Read())
            if (Path.GetFileName(reader.GetString(0)).Equals(name, StringComparison.OrdinalIgnoreCase)) paths.Add(reader.GetString(0));
        Console.WriteLine(JsonSerializer.Serialize(paths));
    }
    public static async Task RunAsync(string package, string models, string output, string[] files)
    {
        package = Path.GetFullPath(package); models = Path.GetFullPath(models);
        Directory.CreateDirectory(output);
        if (files.Length == 0) throw new ArgumentException("Supply the specific source files to validate.");
        var results = new List<object>();
        foreach (string source in files)
        {
            string path = Path.GetFullPath(source);
            string kind = FilePaths.Kind(path) ?? throw new ArgumentException("Unsupported validation file.");
            if (FilePaths.IsOffline(path)) throw new IOException("Make the validation file available locally first.");
            string originalHash = FilePaths.Hash(path);
            await Check.TemporaryAsync(async root =>
            {
                string corpus = Path.Combine(root, "files"); Directory.CreateDirectory(corpus);
                string copied = Path.Combine(corpus, Path.GetFileName(path)); File.Copy(path, copied);
                var timer = Stopwatch.StartNew();
                string? text = kind == "Image" ? null :
                    (await new TextExtractor(Path.Combine(package, "extractor", "FuzzyFindAnything.Extractor.exe")).ExtractAsync(copied, default)).Text;
                double extraction = timer.Elapsed.TotalSeconds;
                int expected = 1;
                if (text is not null)
                {
                    CpuBudget budget = CpuBudget.Apply();
                    await using var inference = await InferenceClient.StartAsync(Path.Combine(package, "inference", "FuzzyFindAnything.Inference.exe"),
                        models, Math.Min(3, budget.Processors), null, Console.Error.WriteLine, default);
                    expected = (await inference.RequestAsync("split", new { text }, default)).GetArrayLength();
                }
                Console.WriteLine($"VALIDATING {Path.GetFileName(path)}: {text?.Length ?? 0} characters, {expected} distinct passages, extraction {extraction:F3}s");
                string data = Path.Combine(root, "index");
                await using (var engine = new EngineProcess(package, models, data))
                {
                    await engine.ReadyAsync();
                    await engine.RequestAsync<JsonElement>("add_folder", new { path = corpus });
                    await engine.WaitAsync(state =>
                    {
                        if (state.Folders.Any(folder => folder.Failed > 0)) throw new IOException("The operator-supplied validation file failed indexing.");
                        return state.FileCount == 1 && state.QueueCount == 0;
                    }, "The complete file did not finish indexing.", 1800000);
                    Program.Require(engine.Status!.VectorCount == expected, "The engine omitted or repeated passages.");
                    var search = await engine.RequestAsync<List<ResultFile>>("search",
                        new { query = "three dimensional geometry model coordinates", threshold = 0 });
                    Program.Require(search.Single().Name == Path.GetFileName(path), "The complete file was not searchable.");
                }
                using (var catalog = new Catalog(data, 1))
                    Program.Require(catalog.Counts() == (1, expected), "File vectors did not survive native checkpoint/reload.");
                Program.Require(FilePaths.Hash(path) == originalHash, "Validation modified the original source.");
                results.Add(new
                {
                    file = Path.GetFileName(path),
                    kind,
                    characters = text?.Length,
                    passages = expected,
                    extraction_seconds = extraction,
                    total_seconds = timer.Elapsed.TotalSeconds,
                    source_unchanged = true
                });
                Console.WriteLine($"PASS all {expected} passages indexed and durable: {Path.GetFileName(path)}");
            });
        }
        File.WriteAllText(Path.Combine(output, "files.json"), JsonSerializer.Serialize(new { failures = 0, files = results },
            new JsonSerializerOptions { WriteIndented = true }));
    }
}
