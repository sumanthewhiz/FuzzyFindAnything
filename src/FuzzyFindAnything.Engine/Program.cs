using System.Text;
using System.Text.Json;
using FuzzyFindAnything.Core;

namespace FuzzyFindAnything.Engine;

internal static class Program
{
    private static readonly object outputGate = new();
    private static void Send(object value) { lock (outputGate) Console.WriteLine(JsonSerializer.Serialize(value, Protocol.Json)); }
    private static async Task<int> Main(string[] args)
    {
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);
        IndexingEngine? engine = null;
        EngineLease? lease = null;
        EngineLog? log = null;
        using var reporting = new CancellationTokenSource();
        Task? reporter = null;
        Task? applying = null;
        var inspections = new List<Task>();
        try
        {
            var options = new Dictionary<string, string>();
            if (args.Length % 2 != 0) throw new ArgumentException("Engine arguments require name/value pairs.");
            for (int i = 0; i < args.Length; i += 2) options.Add(args[i], args[i + 1]);
            string data = Path.GetFullPath(options["--data-dir"]);
            string model = Path.GetFullPath(options["--model"]);
            string helper = Path.GetFullPath(options["--extractor"]);
            string inference = Path.GetFullPath(options["--inference"]);
            string app = Path.GetFullPath(options["--app-dir"]);
            CpuBudget budget = CpuBudget.Apply();
            Directory.CreateDirectory(data);
            lease = new EngineLease(data);
            log = new EngineLog(data);
            var startup = new StartupProgress(value => Send(new { @event = "startup", data = value }));
            var pool = new ModelManager(data, model, inference,
                Path.Combine(app, "hf-inference", "FuzzyFindAnything.HuggingFaceInference.exe"),
                budget, message => log.Write("inference", message));
            engine = new IndexingEngine(data, model, helper, app, budget, pool, log, startup);
            engine.Start();
            reporter = Task.Run(async () =>
            {
                try
                {
                    while (!reporting.IsCancellationRequested)
                    {
                        Send(new { @event = "status", data = engine.Status() });
                        await Task.Delay(1000, reporting.Token);
                    }
                }
                catch (OperationCanceledException) when (reporting.IsCancellationRequested) { }
                catch (Exception error) { log.Write("status", error.ToString()); Send(new { @event = "fatal", error = error.Message }); }
            });
            string? line;
            while ((line = await Console.In.ReadLineAsync()) is not null)
            {
                string? id = null;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var request = document.RootElement;
                    id = request.GetProperty("id").GetString();
                    string command = request.GetProperty("command").GetString() ?? "";
                    var arguments = request.TryGetProperty("args", out var value) ? value : default;
                    if (command == "provider_catalog")
                    {
                        inspections.RemoveAll(task => task.IsCompleted);
                        if (inspections.Count >= 3) throw new InvalidOperationException("Provider discovery is already in progress. Wait for the current checks.");
                        string provider = arguments.GetProperty("provider").GetString() ?? throw new ArgumentException("A model provider is required.");
                        string? version = arguments.TryGetProperty("version", out var requestedVersion) ? requestedVersion.GetString() : null;
                        bool refresh = arguments.TryGetProperty("refresh", out var requestedRefresh) && requestedRefresh.GetBoolean();
                        string? requestId = id;
                        inspections.Add(Task.Run(async () =>
                        {
                            try { Send(new { id = requestId, ok = true, result = await engine.ProviderCatalogAsync(provider, version, refresh) }); }
                            catch (Exception error)
                            {
                                log.Write("provider", error.ToString());
                                Send(new { id = requestId, ok = false, error = error.Message });
                            }
                        }));
                        continue;
                    }
                    if (command is "apply_models" or "inspect_models")
                    {
                        if (command == "apply_models" && applying is { IsCompleted: false })
                            throw new InvalidOperationException("Another model change is already being validated.");
                        inspections.RemoveAll(task => task.IsCompleted);
                        if (command == "inspect_models" && inspections.Count >= 3)
                            throw new InvalidOperationException("Model requirements are still being resolved. Retry after the current checks finish.");
                        var selection = arguments.Deserialize<ModelSelection>(Protocol.Json)
                            ?? throw new ArgumentException("Model selections are required.");
                        string? requestId = id;
                        async Task RunModelRequest()
                        {
                            try
                            {
                                object modelResult = command == "apply_models"
                                    ? await engine.ApplyModelsAsync(selection, message => Send(new { @event = "model_status", message }))
                                    : await engine.InspectModelsAsync(selection);
                                Send(new { id = requestId, ok = true, result = modelResult });
                            }
                            catch (Exception error)
                            {
                                log.Write("request", error.ToString());
                                Send(new
                                {
                                    id = requestId,
                                    ok = false,
                                    error = error.Message,
                                    configuration_applied = error is ModelConfigurationException
                                });
                            }
                        }
                        var operation = Task.Run(RunModelRequest);
                        if (command == "apply_models") applying = operation;
                        else inspections.Add(operation);
                        continue;
                    }
                    object? result;
                    switch (command)
                    {
                        case "status": result = engine.Status(); break;
                        case "add_folder": result = new { id = engine.AddFolder(arguments.GetProperty("path").GetString()!) }; break;
                        case "remove_folders":
                            engine.RemoveFolders(arguments.GetProperty("ids").Deserialize<int[]>()!); result = engine.Status(); break;
                        case "search":
                            var searchOptions = arguments.TryGetProperty("options", out var optionsJson)
                                ? optionsJson.Deserialize<SearchPreferences>(Protocol.Json) ?? throw new ArgumentException("Search options are required.")
                                : new SearchPreferences(DocumentThreshold: arguments.TryGetProperty("threshold", out var threshold) ? threshold.GetDouble() : 0.6,
                                    CodeThreshold: arguments.TryGetProperty("threshold", out threshold) ? threshold.GetDouble() : 0.6,
                                    ImageThreshold: arguments.TryGetProperty("threshold", out threshold) ? threshold.GetDouble() : 0.6);
                            result = await engine.SearchAsync(arguments.GetProperty("query").GetString()!, searchOptions,
                                arguments.TryGetProperty("limit", out var limit) ? limit.GetInt32() : 50); break;
                        case "rescan": engine.Rescan(); result = engine.Status(); break;
                        case "reload_configuration": log.Reload(); result = engine.Status(); break;
                        case "cancel_model_change": result = new { cancelled = engine.CancelModelChange() }; break;
                        case "retry_image_tasks": result = new { files = engine.RetryImageTasks() }; break;
                        case "shutdown": Send(new { id, ok = true, result = (object?)null }); return 0;
                        default: throw new ArgumentException("Unknown engine command: " + command);
                    }
                    Send(new { id, ok = true, result });
                }
                catch (Exception error)
                {
                    log.Write("request", error.ToString());
                    Send(new { id, ok = false, error = error.Message });
                }
            }
            return 0;
        }
        catch (Exception error)
        {
            log?.Write("fatal", error.ToString());
            Send(new { @event = "fatal", error = error.Message });
            return 1;
        }
        finally
        {
            reporting.Cancel();
            if (reporter is not null) await reporter;
            try
            {
                if (engine is not null) await engine.DisposeAsync();
                if (applying is not null) await applying;
                await Task.WhenAll(inspections);
            }
            finally { lease?.Dispose(); }
        }
    }
}
