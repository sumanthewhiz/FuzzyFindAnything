using System.Text;
using System.Text.Json;
using FuzzyFindAnything.Core;

namespace FuzzyFindAnything.Tools;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, value) => { value.Cancel = true; cancellation.Cancel(); };
        try
        {
            if (args.Length == 0) throw new ArgumentException("Commands: acquire-model, verify-model, prepare-hf-model, reset-index, native-smoke, verify-release.");
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int index = 1; index < args.Length; index++)
            {
                string key = args[index];
                if (key is "--clear-folders" or "--smoke") options.Add(key, "true");
                else if (key.StartsWith("--", StringComparison.Ordinal) && index + 1 < args.Length)
                    options.Add(key, args[++index]);
                else throw new ArgumentException("Invalid argument: " + key);
            }
            string Required(string key) => options.GetValueOrDefault(key) ?? throw new ArgumentException("Required argument: " + key);
            switch (args[0])
            {
                case "acquire-model":
                    await ModelAssets.AcquireAsync(Path.GetFullPath(Required("--destination")), options.GetValueOrDefault("--source-directory"),
                        options.TryGetValue("--base-uri", out string? source) ? new Uri(source, UriKind.Absolute) : null,
                        Console.WriteLine, cancellation.Token);
                    break;
                case "verify-model":
                    ModelAssets.Verify(Path.GetFullPath(Required("--destination")),
                        (completed, total, file) => Console.WriteLine($"Verified {completed}/{total}: {file}"), cancellation.Token);
                    break;
                case "prepare-hf-model":
                    var models = new HuggingFaceModels(Path.GetFullPath(Required("--cache")), Path.GetFullPath(Required("--default-model")));
                    var space = await models.ResolveAsync(Required("--url"), Console.Error.WriteLine, cancellation.Token);
                    Console.WriteLine(JsonSerializer.Serialize(new { space, directory = models.DirectoryFor(space) }, Protocol.Json));
                    break;
                case "reset-index":
                    Console.WriteLine(JsonSerializer.Serialize(CatalogMaintenance.Reset(Required("--data-dir"),
                        options.ContainsKey("--clear-folders")), Protocol.Json));
                    break;
                case "native-smoke":
                    CpuBudget.Apply();
                    string temporary = Path.Combine(Path.GetTempPath(), "ffa-smoke-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(temporary);
                    try
                    {
                        using (var catalog = new Catalog(temporary, 1))
                            if (catalog.Counts() != (0, 0)) throw new InvalidDataException("Unexpected native catalog state.");
                        Console.WriteLine($"Native DiskANN {NativeIndex.Version}, SQLite and .NET are available.");
                    }
                    finally { Directory.Delete(temporary, true); }
                    break;
                case "verify-release":
                    ReleasePackage.Validate(Required("--archive"), Required("--runtime-id"), Required("--version"));
                    if (options.ContainsKey("--smoke"))
                        await ReleasePackage.SmokeAsync(Required("--archive"), Required("--runtime-id"), cancellation.Token);
                    break;
                default: throw new ArgumentException("Unknown command: " + args[0]);
            }
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error is OperationCanceledException ? "The operation was cancelled." : error.Message);
            return 1;
        }
    }
}
