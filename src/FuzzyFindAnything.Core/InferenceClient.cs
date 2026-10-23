using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace FuzzyFindAnything.Core;

public sealed class InferenceClient : IAsyncDisposable
{
    private readonly Process process;
    private readonly Channel<JsonElement> messages = Channel.CreateUnbounded<JsonElement>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly SemaphoreSlim requests = new(1, 1);
    private readonly Task outputReader, errorReader;
    private readonly object state = new();
    private bool disposed, aborted;
    private string lastStage = "Starting native inference";
    public bool Alive { get { lock (state) return !disposed && !aborted && !process.HasExited; } }

    private InferenceClient(Process process, Action<string> log)
    {
        this.process = process;
        outputReader = Task.Run(async () =>
        {
            try
            {
                while (await process.StandardOutput.ReadLineAsync() is { } line)
                {
                    using var document = JsonDocument.Parse(line);
                    await messages.Writer.WriteAsync(document.RootElement.Clone());
                }
                messages.Writer.TryComplete(new IOException("The native inference worker exited."));
            }
            catch (Exception error) { messages.Writer.TryComplete(error); }
        });
        errorReader = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is { } line) log(line);
        });
    }

    public static async Task<InferenceClient> StartAsync(string executable, string model, int threads,
        Action<int, string>? progress, Action<string> log, CancellationToken cancellation, TimeSpan? startupTimeout = null,
        string? requiredProvider = null, bool imageUnderstanding = false)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        foreach (string argument in new[] { imageUnderstanding ? "--image-model" : "--model", model, "--threads", threads.ToString() }) start.ArgumentList.Add(argument);
        if (requiredProvider is not null) { start.ArgumentList.Add("--provider"); start.ArgumentList.Add(requiredProvider); }
        var process = Process.Start(start) ?? throw new IOException("Could not start native inference.");
        var client = new InferenceClient(process, log);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(startupTimeout ?? TimeSpan.FromMinutes(5));
        try
        {
            JsonElement result = await client.ReceiveAsync(progress, deadline.Token);
            if (result.GetString() != "ready") throw new InvalidDataException("Inference did not report readiness.");
            return client;
        }
        catch (Exception error)
        {
            client.Abort();
            await client.DisposeAsync();
            if (error is OperationCanceledException && !cancellation.IsCancellationRequested)
                throw new TimeoutException($"Native inference startup timed out during '{client.lastStage}'. Verify the model assets and available memory.", error);
            throw;
        }
    }

    private async Task<JsonElement> ReceiveAsync(Action<int, string>? progress, CancellationToken cancellation)
    {
        while (true)
        {
            JsonElement value = await messages.Reader.ReadAsync(cancellation);
            if (value.TryGetProperty("event", out var kind) && kind.GetString() == "startup")
            {
                int completed = value.GetProperty("completed").GetInt32(), total = value.GetProperty("total").GetInt32();
                string stage = value.GetProperty("stage").GetString() ?? "";
                if (total != 6 || completed is < 0 or > 6 || stage.Length == 0)
                    throw new InvalidDataException("Invalid inference startup progress.");
                lastStage = stage;
                progress?.Invoke(completed, stage);
                continue;
            }
            if (!value.GetProperty("ok").GetBoolean())
                throw new ContentException(value.GetProperty("error").GetString() ?? "Inference failed.",
                    value.TryGetProperty("code", out var code) ? code.GetString() ?? "inference_failure" : "inference_failure",
                    !value.TryGetProperty("retryable", out var retryable) || retryable.GetBoolean());
            return value.GetProperty("result").Clone();
        }
    }
    public async Task<JsonElement> RequestAsync(string operation, object arguments, CancellationToken cancellation)
    {
        await requests.WaitAsync(cancellation);
        try
        {
            if (!Alive) throw new IOException("The native inference worker is unavailable.");
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { operation, args = arguments }, Protocol.Json));
            await process.StandardInput.FlushAsync();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromMinutes(3));
            try { return await ReceiveAsync(null, deadline.Token); }
            catch (OperationCanceledException error)
            {
                Abort();
                if (!cancellation.IsCancellationRequested) throw new TimeoutException("The native inference operation exceeded three minutes.", error);
                throw;
            }
        }
        catch (ContentException) { throw; }
        catch (Exception error) when (error is IOException or JsonException or InvalidDataException or
            ChannelClosedException or KeyNotFoundException or InvalidOperationException)
        { Abort(); throw; }
        finally { requests.Release(); }
    }
    private void Abort()
    {
        lock (state)
        {
            if (aborted || disposed) return;
            aborted = true;
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        try
        {
            if (Alive)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await RequestAsync("shutdown", new { }, deadline.Token);
                await process.WaitForExitAsync(deadline.Token);
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or InvalidOperationException or ChannelClosedException)
        { Abort(); }
        finally
        {
            if (!process.HasExited) { Abort(); await process.WaitForExitAsync(); }
            lock (state) disposed = true;
            await Task.WhenAll(outputReader, errorReader);
            process.Dispose();
            requests.Dispose();
        }
    }
}
