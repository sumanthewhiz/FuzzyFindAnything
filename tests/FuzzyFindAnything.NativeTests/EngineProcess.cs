using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using FuzzyFindAnything.Core;

namespace FuzzyFindAnything.NativeTests;

internal sealed class EngineProcess : IAsyncDisposable
{
    private readonly Process process;
    private readonly Task reader, errors;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> pending = new();
    private readonly SemaphoreSlim writes = new(1);
    private Exception? failure;
    private bool stopped;
    private bool disposed;
    public ConcurrentQueue<StartupState> Progress { get; } = [];
    public ConcurrentQueue<string> Diagnostics { get; } = [];
    public EngineState? Status { get; private set; }
    public int Id => process.Id;
    public long PeakMemory => process.PeakWorkingSet64;
    public long Affinity => process.ProcessorAffinity.ToInt64();
    public EngineProcess(string package, string model, string data)
    {
        var start = new ProcessStartInfo(Path.Combine(package, "engine", "FuzzyFindAnything.Engine.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = package,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        foreach (string value in new[] { "--data-dir", data, "--model", model, "--extractor",
            Path.Combine(package, "extractor", "FuzzyFindAnything.Extractor.exe"), "--inference",
            Path.Combine(package, "inference", "FuzzyFindAnything.Inference.exe"), "--app-dir", package })
            start.ArgumentList.Add(value);
        process = Process.Start(start) ?? throw new IOException("The packaged coordinator did not start.");
        reader = Task.Run(async () =>
        {
            try
            {
                while (await process.StandardOutput.ReadLineAsync() is { } line)
                {
                    using var json = JsonDocument.Parse(line);
                    var value = json.RootElement;
                    if (value.TryGetProperty("event", out var kind))
                    {
                        switch (kind.GetString())
                        {
                            case "startup": Progress.Enqueue(value.GetProperty("data").Deserialize<StartupState>(Protocol.Json)!); break;
                            case "status": Status = value.GetProperty("data").Deserialize<EngineState>(Protocol.Json); break;
                            case "fatal": throw new IOException(value.GetProperty("error").GetString());
                        }
                    }
                    else if (value.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String &&
                        pending.TryGetValue(id.GetString()!, out var completion))
                    {
                        if (value.GetProperty("ok").GetBoolean()) completion.TrySetResult(value.GetProperty("result").Clone());
                        else completion.TrySetException(new IOException(value.GetProperty("error").GetString()));
                    }
                }
            }
            catch (Exception error)
            {
                failure = error;
                foreach (var completion in pending.Values) completion.TrySetException(error);
            }
        });
        errors = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is { } line) Diagnostics.Enqueue(line);
        });
    }
    public async Task<T> RequestAsync<T>(string command, object? args = null)
    {
        if (failure is not null) throw new IOException("Coordinator protocol failed.", failure);
        string id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = completion;
        try
        {
            await writes.WaitAsync();
            try
            {
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { id, command, args }, Protocol.Json));
                await process.StandardInput.FlushAsync();
            }
            finally { writes.Release(); }
            return (await completion.Task.WaitAsync(TimeSpan.FromMinutes(command == "apply_models" ? 20 : 4))).Deserialize<T>(Protocol.Json)!;
        }
        finally { pending.TryRemove(id, out _); }
    }
    public async Task ReadyAsync()
    {
        await Check.UntilAsync(() =>
        {
            if (failure is not null) throw new IOException("Coordinator protocol failed.", failure);
            if (process.HasExited) throw new IOException("Coordinator exited: " + string.Join("\n", Diagnostics));
            if (Status?.ModelState == "Error") throw new IOException(Status.Error);
            return Progress.Any(value => value.State == "Ready");
        }, "Native startup never became ready.", 320000);
    }
    public async Task WaitAsync(Func<EngineState, bool> condition, string message, int timeout = 180000)
    {
        await Check.UntilAsync(() =>
        {
            if (failure is not null) throw new IOException("Coordinator protocol failed.", failure);
            if (process.HasExited) throw new IOException("Coordinator exited: " + string.Join("\n", Diagnostics));
            if (Status?.ModelState == "Error") throw new IOException(Status.Error);
            return Status is { } status && condition(status);
        }, message, timeout);
    }
    public async Task CrashAsync()
    {
        stopped = true; process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync(); await Task.WhenAll(reader, errors);
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            if (!stopped && !process.HasExited)
            {
                await RequestAsync<JsonElement>("shutdown");
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
            }
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            await Task.WhenAll(reader, errors);
            process.Dispose(); writes.Dispose();
        }
    }
}
