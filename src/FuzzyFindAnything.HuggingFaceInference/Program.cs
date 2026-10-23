using System.IO;
using System.Text;
using System.Text.Json;
using FuzzyFindAnything.Core;
using Microsoft.ML.OnnxRuntime;

namespace FuzzyFindAnything.HuggingFaceInference;

internal static class Program
{
    private static TextWriter protocol = Console.Out;
    [STAThread]
    private static int Main(string[] args)
    {
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);
        using var output = ProtocolOutput.Open();
        protocol = output;
        try
        {
            CpuBudget budget = CpuBudget.Apply();
            if (args.SequenceEqual(["--devices"]))
            {
                Send(DeviceExecution.Discover());
                return 0;
            }
            if (args.Length is not (4 or 6) || args[0] is not ("--model" or "--image-model") || args[2] != "--threads" ||
                args.Length == 6 && args[4] != "--provider")
                throw new ArgumentException("Usage: --model <native HF model directory> --threads <count> [--provider <name[:CPU|GPU|NPU]>]");
            int threads = int.Parse(args[3]);
            if (threads < 1 || threads > budget.Processors) throw new ArgumentException("Inference threads exceed the shared CPU limit.");
            if (args[0] == "--image-model") return ImageHost(args, threads);
            Send(new { @event = "startup", completed = 1, total = 6, stage = "Verifying Hugging Face model assets" });
            var metadata = HuggingFaceModels.Load(args[1]);
            metadata.Verify(args[1]);
            Send(new { @event = "startup", completed = 2, total = 6, stage = "Preparing native model tokenizer" });
            using var model = CompatibleModel.Create(args[1], metadata.Space, threads,
                stage => Send(new { @event = "startup", completed = 4, total = 6, stage }), args.Length == 6 ? args[5] : null);
            Send(new { @event = "startup", completed = 6, total = 6, stage = $"Ready on {model.Device.Type}: {model.Device.Name}" });
            Send(new { ok = true, result = "ready" });
            while (Console.ReadLine() is { } line)
            {
                bool stop = false;
                try
                {
                    using var request = JsonDocument.Parse(line);
                    string operation = request.RootElement.GetProperty("operation").GetString()!;
                    var value = request.RootElement.GetProperty("args");
                    object? result = operation switch
                    {
                        "query" => model.Query(value.GetProperty("text").GetString()!,
                            value.TryGetProperty("profile", out var profile) ? profile.GetString() : null),
                        "tokens" => model.TokenIds(value.GetProperty("text").GetString()!),
                        "split" => model.Split(value.GetProperty("text").GetString()!),
                        "split_content" => model.SplitContent(value.GetProperty("text").GetString()!,
                            value.GetProperty("context").Deserialize<ChunkingContext>(Protocol.Json)!),
                        "documents" => model.Texts(value.GetProperty("texts").Deserialize<string[]>()!),
                        "images" => model.Images(value.GetProperty("paths").Deserialize<string[]>()!),
                        "capabilities" => new
                        {
                            space = metadata.Space,
                            provider = model.Device.Provider,
                            device = model.Device.Name,
                            device_type = model.Device.Type,
                            protocol_version = 2,
                            structured_chunking = 1,
                            recipe = metadata.Space.Recipe?.ContractFingerprint
                        },
                        "shutdown" => null,
                        _ => throw new ArgumentException("Unknown inference operation.")
                    };
                    stop = operation == "shutdown";
                    Send(new { ok = true, result });
                }
                catch (Exception error)
                {
                    Console.Error.WriteLine(error);
                    Send(new
                    {
                        ok = false,
                        error = error.Message,
                        code = error is ContentException content ? content.Code : error is NotSupportedException ? "model_contract" : "model_inference",
                        retryable = false
                    });
                }
                if (stop) break;
            }
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            Send(new { ok = false, error = error.Message, code = "model_compatibility", retryable = false });
            return 1;
        }
    }
    private static int ImageHost(string[] args, int threads)
    {
        Send(new { @event = "startup", completed = 1, total = 6, stage = "Verifying image-understanding assets" });
        var metadata = ImageModelAssets.Load(args[1]);
        metadata.Verify(args[1]);
        using var model = ImageSequenceModel.Create(args[1], metadata.Model, threads,
            stage => Send(new { @event = "startup", completed = 4, total = 6, stage }), args.Length == 6 ? args[5] : null);
        Send(new { @event = "startup", completed = 6, total = 6, stage = $"Image understanding ready on {model.Device.Type}" });
        Send(new { ok = true, result = "ready" });
        while (Console.ReadLine() is { } line)
        {
            bool stop = false;
            try
            {
                using var request = JsonDocument.Parse(line);
                string operation = request.RootElement.GetProperty("operation").GetString()!;
                var value = request.RootElement.GetProperty("args");
                object? result = operation switch
                {
                    "describe" => model.Generate(value.GetProperty("path").GetString()!, value.GetProperty("task").GetString()!),
                    "tokens" => model.TokenIds(value.GetProperty("text").GetString()!),
                    "capabilities" => new
                    {
                        protocol_version = 1,
                        image_model = metadata.Model.Fingerprint,
                        tasks = metadata.Model.Tasks.Select(task => task.Id),
                        provider = model.Device.Provider,
                        device = model.Device.Name
                    },
                    "shutdown" => null,
                    _ => throw new NotSupportedException("This worker provides image-understanding tasks, not direct embeddings.")
                };
                Send(new { ok = true, result }); stop = operation == "shutdown";
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                Send(new
                {
                    ok = false,
                    error = error.Message,
                    code = error is ContentException content ? content.Code : "image_generation",
                    retryable = error is ContentException { Retryable: true } or OnnxRuntimeException
                });
            }
            if (stop) break;
        }
        return 0;
    }
    private static void Send(object value) => protocol.WriteLine(JsonSerializer.Serialize(value, Protocol.Json));
}
