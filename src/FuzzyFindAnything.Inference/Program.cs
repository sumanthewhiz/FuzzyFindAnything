using System.Text;
using System.Text.Json;
using FuzzyFindAnything.Core;
using Microsoft.ML.OnnxRuntime;

namespace FuzzyFindAnything.Inference;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);
        try
        {
            CpuBudget budget = CpuBudget.Apply();
            if (args.SequenceEqual(["--self-test"]))
            {
                OrtEnv.Instance().DisableTelemetryEvents();
                string[] providers = OrtEnv.Instance().GetAvailableProviders();
                if (!providers.Contains("CPUExecutionProvider")) throw new InvalidOperationException("The native CPU provider is unavailable.");
                Send(new
                {
                    ok = true,
                    architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                    runtime_version = typeof(InferenceSession).Assembly.GetName().Version?.ToString(),
                    providers
                });
                return 0;
            }
            if (args.Length != 4 || args[0] != "--model" || args[2] != "--threads")
                throw new ArgumentException("Usage: FuzzyFindAnything.Inference.exe --model <directory> --threads <count>");
            int threads = int.Parse(args[3]);
            if (threads < 1 || threads > budget.Processors) throw new ArgumentException("Inference threads exceed the shared CPU budget.");
            using var model = new NativeModel(args[1], threads, (completed, stage) =>
                Send(new { @event = "startup", completed, total = 6, stage }));
            Send(new { ok = true, result = "ready" });
            string? line;
            while ((line = Console.ReadLine()) is not null)
            {
                bool shutdown = false;
                try
                {
                    using var request = JsonDocument.Parse(line);
                    string operation = request.RootElement.GetProperty("operation").GetString()!;
                    JsonElement data = request.RootElement.GetProperty("args");
                    object? result = operation switch
                    {
                        "query" => model.Query(data.GetProperty("text").GetString()!),
                        "split" => model.Split(data.GetProperty("text").GetString()!),
                        "documents" => model.EncodeTexts(data.GetProperty("texts").Deserialize<string[]>()!),
                        "images" => model.EncodeImages(data.GetProperty("paths").Deserialize<string[]>()!),
                        "capabilities" => new
                        {
                            space = EmbeddingSpace.Default,
                            provider = "CPUExecutionProvider",
                            device = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture + " CPU",
                            device_type = "CPU"
                        },
                        "shutdown" => null,
                        _ => throw new ArgumentException("Unknown inference operation: " + operation)
                    };
                    shutdown = operation == "shutdown";
                    Send(new { ok = true, result });
                }
                catch (Exception error)
                {
                    Send(new
                    {
                        ok = false,
                        error = error.Message,
                        code = error is ContentException content ? content.Code : "inference_failure",
                        retryable = error is not ContentException classified || classified.Retryable
                    });
                    Console.Error.WriteLine(error);
                }
                if (shutdown) break;
            }
            return 0;
        }
        catch (Exception error)
        {
            Send(new { ok = false, error = error.Message, code = "initialization_failure", retryable = true });
            Console.Error.WriteLine(error);
            return 1;
        }
    }
    private static void Send(object message) => Console.WriteLine(JsonSerializer.Serialize(message, Protocol.Json));
}
