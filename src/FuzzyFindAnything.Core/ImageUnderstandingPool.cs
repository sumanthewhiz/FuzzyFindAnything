using System.Text.Json;

namespace FuzzyFindAnything.Core;

public sealed class ImageUnderstandingPool : IImageUnderstanding
{
    private readonly string executable, directory;
    private readonly int threads;
    private readonly Action<string> log;
    private readonly SemaphoreSlim work = new(1, 1);
    private readonly CancellationTokenSource lifetime;
    private readonly Task idle;
    private InferenceClient? client;
    private long lastUsed = Environment.TickCount64;
    private bool disposed;
    public ImagePipelineDescriptor Descriptor { get; private set; }
    public string Provider { get; private set; } = "";
    public string Device { get; private set; } = "";
    public bool IsLoaded => client is { Alive: true };

    public ImageUnderstandingPool(ImagePipelineDescriptor descriptor, string executable, string directory,
        int threads, Action<string> log, CancellationToken cancellation)
    {
        Descriptor = descriptor; this.executable = executable; this.directory = directory;
        this.threads = threads; this.log = log;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        idle = Task.Run(ReleaseIdleAsync);
    }
    private async Task ReadyAsync(Action<int, string>? progress, CancellationToken cancellation)
    {
        if (client is { Alive: true }) return;
        if (client is not null) { await client.DisposeAsync(); client = null; }
        long available = ModelPool.AvailableMemory(), required = Descriptor.Model.MemoryBytes + (2L << 30);
        if (available < required)
            throw new ContentException($"Image understanding needs {required / (double)(1L << 30):0.0} GiB available including the system reserve; " +
                $"{available / (double)(1L << 30):0.0} GiB is available. Close unused applications and retry.", "image_memory", true);
        string? provider = new ModelOptions(Device: Descriptor.GenerationDevice).Normalize().ProviderSelector;
        var started = await InferenceClient.StartAsync(executable, directory, threads, progress, log, cancellation,
            requiredProvider: provider, imageUnderstanding: true);
        try
        {
            var capabilities = await started.RequestAsync("capabilities", new { }, cancellation);
            if (capabilities.GetProperty("protocol_version").GetInt32() != 1 ||
                capabilities.GetProperty("image_model").GetString() != Descriptor.Model.Fingerprint)
                throw new InvalidDataException("The image worker loaded a different task recipe.");
            Provider = capabilities.GetProperty("provider").GetString()!;
            Device = capabilities.GetProperty("device").GetString()!;
            client = started;
        }
        catch { await started.DisposeAsync(); throw; }
    }
    public async Task ValidateAsync(Action<string> progress, CancellationToken cancellation)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellation);
        await work.WaitAsync(linked.Token);
        try { await ReadyAsync((_, stage) => progress(stage), linked.Token); lastUsed = Environment.TickCount64; }
        finally { work.Release(); }
    }
    public void Rebind(ImagePipelineDescriptor descriptor)
    {
        if (descriptor.Model.Fingerprint != Descriptor.Model.Fingerprint || descriptor.GenerationDevice != Descriptor.GenerationDevice)
            throw new InvalidOperationException("An image worker cannot be rebound to a different native artifact/device.");
        Descriptor = descriptor;
    }
    public async Task ReleaseIfIdleAsync()
    {
        if (!await work.WaitAsync(0)) return;
        try
        {
            if (client is not null)
            {
                await client.DisposeAsync(); client = null;
                log("Released an idle image worker before qualifying another model.");
            }
        }
        finally { work.Release(); }
    }
    public async Task<ImageTaskResult> GenerateAsync(string path, string task, CancellationToken cancellation)
    {
        var profile = Descriptor.Model.Task(task);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellation);
        await work.WaitAsync(linked.Token);
        try
        {
            await ReadyAsync(null, linked.Token);
            var value = await client!.RequestAsync("describe", new { path, task }, linked.Token);
            var result = value.Deserialize<ImageTaskResult>(Protocol.Json)
                ?? throw new InvalidDataException("The image worker returned no task result.");
            result.Validate(profile);
            return result;
        }
        finally { lastUsed = Environment.TickCount64; work.Release(); }
    }
    private async Task ReleaseIdleAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                await Task.Delay(15000, lifetime.Token);
                if (Environment.TickCount64 - Interlocked.Read(ref lastUsed) < 60000 || !await work.WaitAsync(0, lifetime.Token)) continue;
                try
                {
                    if (client is not null)
                    {
                        await client.DisposeAsync(); client = null;
                        log("Released the idle image-understanding worker; existing image search remains available.");
                    }
                }
                finally { work.Release(); }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) { log("Image worker idle cleanup failed: " + error.Message); }
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true; lifetime.Cancel();
        await idle;
        await work.WaitAsync();
        try { if (client is not null) await client.DisposeAsync(); }
        finally { work.Dispose(); lifetime.Dispose(); }
    }
}
