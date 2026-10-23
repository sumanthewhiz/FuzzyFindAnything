using System.Runtime.InteropServices;
using System.Text.Json;

namespace FuzzyFindAnything.Core;

public interface IEmbeddings : IAsyncDisposable
{
    EmbeddingSpace Space => EmbeddingSpace.Default;
    string Provider => "CPUExecutionProvider";
    string Device => RuntimeInformation.ProcessArchitecture + " CPU";
    IEmbeddings ForKind(string kind) => this;
    IReadOnlyList<IEmbeddings> ActiveModels => new[] { this };
    IReadOnlyList<EmbeddingSearchBinding> SearchBindings => ActiveModels.Select(model => new EmbeddingSearchBinding(model, "default", null)).ToArray();
    IImageUnderstanding? ImageUnderstanding => null;
    bool SupportsStructuredChunking => false;
    int ReadyWorkers { get; }
    int Threads { get; }
    int BatchConcurrency { get; }
    string Warning { get; }
    BatchState Statistics { get; }
    Task StartAsync(Action<int, string> progress, CancellationToken cancellation);
    Task<string[]> SplitAsync(string text, CancellationToken cancellation);
    Task<ChunkingResult> SplitContentAsync(string text, ChunkingContext context, CancellationToken cancellation) =>
        throw new NotSupportedException("This worker does not support structured content preparation.");
    Task<float[][]> DocumentsAsync(string title, string[] passages, CancellationToken cancellation);
    Task<float[]> ImageAsync(string path, CancellationToken cancellation);
    Task<float[]> QueryAsync(string text, CancellationToken cancellation);
    Task<float[]> QueryAsync(string text, string profile, CancellationToken cancellation) => profile == "default"
        ? QueryAsync(text, cancellation) : throw new NotSupportedException("This model does not support role-aware query profiles.");
}

public sealed record EmbeddingSearchBinding(IEmbeddings Model, string Profile, string[]? Kinds);

public sealed class ModelPool(string executable, string modelDirectory, CpuBudget budget, Action<string> log,
    Func<long>? memoryAvailable = null, EmbeddingSpace? space = null, int? workerLimit = null, int? threadLimit = null,
    string? requiredProvider = null) : IEmbeddings
{
    public EmbeddingSpace Space { get; } = space ?? EmbeddingSpace.Default;
    public string Provider { get; private set; } = "CPUExecutionProvider";
    public string Device { get; private set; } = RuntimeInformation.ProcessArchitecture + " CPU";
    public bool SupportsStructuredChunking { get; private set; }
    private readonly object gate = new();
    private readonly List<InferenceClient> clients = [];
    private readonly Queue<InferenceClient> available = new();
    private TaskCompletionSource changed = NewSignal();
    private CancellationTokenSource? lifetime;
    private InferenceBatcher? batches;
    private Task? warmup;
    private bool closed;
    private int queryWaiters, desired;
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private void Wake() { TaskCompletionSource previous = changed; changed = NewSignal(); previous.TrySetResult(); }
    public int ReadyWorkers { get { lock (gate) return closed ? 0 : clients.Count(client => client.Alive); } }
    public int Threads { get; private set; }
    public int BatchConcurrency => Math.Min(budget.Processors, 2 * Math.Max(1, desired));
    public string Warning { get; private set; } = "";
    public BatchState Statistics => batches?.Statistics ?? new(0, 0, 0, 0, 0, 0);

    public async Task StartAsync(Action<int, string> progress, CancellationToken cancellation)
    {
        lock (gate)
        {
            if (lifetime is not null || closed) throw new InvalidOperationException("The native model pool cannot be started twice.");
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        }
        long memory = (memoryAvailable ?? AvailableMemory)();
        if (Space == EmbeddingSpace.Default) (desired, Threads) = Plan(budget.Processors, memory);
        else
        {
            if (memory < Space.MemoryBytes + (2L << 30)) throw new IOException("Insufficient available memory for this model and the system reserve.");
            desired = 1; Threads = budget.Processors;
        }
        if (workerLimit is { } maximum) desired = Math.Min(desired, maximum);
        if (threadLimit is { } compute) Threads = Math.Clamp(compute, 1, budget.Processors);
        else Threads = Math.Max(1, budget.Processors / desired);
        log($"Native inference plan: {desired} model processes, {Threads} threads each, {memory / (double)(1L << 30):0.0} GiB available.");
        var client = await StartClientAsync(progress, lifetime.Token);
        lock (gate) { clients.Add(client); available.Enqueue(client); Wake(); }
        batches = new InferenceBatcher(EncodeAsync, log, lifetime.Token);
        batches.AddWorker();
        if (desired > 1) warmup = Task.Run(WarmAsync);
    }
    private async Task<InferenceClient> StartClientAsync(Action<int, string>? progress, CancellationToken cancellation)
    {
        var client = await InferenceClient.StartAsync(executable, modelDirectory, Threads, progress, log, cancellation,
            requiredProvider: requiredProvider);
        try
        {
            var capabilities = await client.RequestAsync("capabilities", new { }, cancellation);
            var actual = capabilities.GetProperty("space").Deserialize<EmbeddingSpace>(Protocol.Json);
            if (actual?.Fingerprint != Space.Fingerprint) throw new InvalidDataException("The model worker loaded a different embedding space.");
            if (Space.Recipe is not null && (!capabilities.TryGetProperty("protocol_version", out var version) || version.GetInt32() != 2 ||
                actual.Recipe?.ContractFingerprint != Space.Recipe.ContractFingerprint))
                throw new InvalidDataException("The native worker does not implement the required embedding recipe/protocol.");
            Provider = capabilities.GetProperty("provider").GetString()!;
            Device = capabilities.GetProperty("device").GetString()!;
            SupportsStructuredChunking = capabilities.TryGetProperty("structured_chunking", out var chunking) && chunking.GetInt32() == 1;
            return client;
        }
        catch { await client.DisposeAsync(); throw; }
    }
    internal static (int Workers, int Threads) Plan(int processors, long memory)
    {
        if (processors < 1) throw new ArgumentOutOfRangeException(nameof(processors));
        const long gib = 1L << 30;
        int permitted = (int)Math.Max(0, (memory - 2 * gib) / (3 * gib));
        if (permitted == 0) throw new IOException($"At least 5 GiB of available RAM is required for native inference; {memory / (double)gib:0.0} GiB is available.");
        int workers = Math.Min(processors >= 4 ? 2 : 1, permitted);
        return (workers, Math.Max(1, processors / workers));
    }
    private async Task WarmAsync()
    {
        try
        {
            if ((memoryAvailable ?? AvailableMemory)() < 5L << 30) throw new IOException("Available memory became too low for an additional model.");
            var client = await StartClientAsync(null, lifetime!.Token);
            lock (gate)
            {
                if (!closed) { clients.Add(client); available.Enqueue(client); Wake(); }
            }
            if (closed) await client.DisposeAsync();
            else { batches!.AddWorker(); log("Additional native model is ready."); }
        }
        catch (OperationCanceledException) when (lifetime!.IsCancellationRequested) { }
        catch (Exception error)
        {
            Warning = "Search remains available with one model. Additional startup failed: " + error.Message;
            log(Warning);
        }
    }
    private async Task<T> WithClientAsync<T>(bool query, Func<InferenceClient, Task<T>> action, CancellationToken cancellation)
    {
        InferenceClient? client = null;
        bool counted = query;
        if (query) lock (gate) { queryWaiters++; Wake(); }
        try
        {
            while (client is null)
            {
                Task wait;
                lock (gate)
                {
                    if (closed) throw new OperationCanceledException("Inference is stopping.");
                    if (available.Count > 0 && (query || queryWaiters == 0))
                    {
                        client = available.Dequeue();
                        if (counted) { queryWaiters--; counted = false; }
                        break;
                    }
                    wait = changed.Task;
                }
                await wait.WaitAsync(cancellation);
            }
            if (!client.Alive)
            {
                await client.DisposeAsync();
                var replacement = await StartClientAsync(null, cancellation);
                lock (gate) clients[clients.IndexOf(client)] = replacement;
                client = replacement;
            }
            return await action(client);
        }
        finally
        {
            lock (gate)
            {
                if (counted) queryWaiters--;
                if (client is not null && !closed) available.Enqueue(client);
                Wake();
            }
        }
    }
    private async Task<float[][]> EncodeAsync(string operation, string[] values, CancellationToken cancellation) =>
        await WithClientAsync(false, async client =>
        {
            object arguments = operation == "documents" ? new { texts = values } : new { paths = values };
            var result = await client.RequestAsync(operation, arguments, cancellation);
            var vectors = result.Deserialize<float[][]>() ?? throw new InvalidDataException("Empty inference output.");
            if (vectors.Length != values.Length) throw new InvalidDataException("Incomplete inference batch.");
            return vectors.Select(vector => Space.Normalize(vector)).ToArray();
        }, cancellation);
    public Task<string[]> SplitAsync(string text, CancellationToken cancellation) => WithClientAsync(false, async client =>
        (await client.RequestAsync("split", new { text }, cancellation)).Deserialize<string[]>()
            ?? throw new InvalidDataException("Empty passage preparation output."), cancellation);
    public Task<ChunkingResult> SplitContentAsync(string text, ChunkingContext context, CancellationToken cancellation) =>
        WithClientAsync(false, async client =>
            (await client.RequestAsync("split_content", new { text, context }, cancellation)).Deserialize<ChunkingResult>(Protocol.Json)
                ?? throw new InvalidDataException("Empty structured passage response."), cancellation);
    public Task<float[][]> DocumentsAsync(string title, string[] passages, CancellationToken cancellation) =>
        batches!.SubmitAsync("documents", passages.Select(passage => Space == EmbeddingSpace.Default
            ? $"title: {FilePaths.ScalarPrefix(title, 160)} | text: {passage}" : passage).ToArray(), cancellation);
    public async Task<float[]> ImageAsync(string path, CancellationToken cancellation) =>
        (await batches!.SubmitAsync("images", [path], cancellation))[0];
    public Task<float[]> QueryAsync(string text, CancellationToken cancellation) => QueryAsync(text, Space.Recipe?.TextQuery ?? "default", cancellation);
    public Task<float[]> QueryAsync(string text, string profile, CancellationToken cancellation) => WithClientAsync(true, async client =>
        Space.Normalize((await client.RequestAsync("query", Space.Recipe is null ? new { text } : (object)new { text, profile }, cancellation)).Deserialize<float[]>()
            ?? throw new InvalidDataException("Empty query embedding.")), cancellation);

    public async ValueTask DisposeAsync()
    {
        lock (gate) { if (closed) return; closed = true; Wake(); }
        lifetime?.Cancel();
        if (warmup is not null) await warmup;
        if (batches is not null) await batches.DisposeAsync();
        InferenceClient[] snapshot;
        lock (gate) snapshot = clients.ToArray();
        foreach (var client in snapshot) await client.DisposeAsync();
        lifetime?.Dispose();
    }
    public static long AvailableMemory()
    {
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref memory)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return checked((long)memory.Available);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong Total, Available, PageFile, AvailablePageFile, Virtual, AvailableVirtual, Extended;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
