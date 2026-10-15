using System.Numerics;

namespace FuzzyFindAnything.Core;

public sealed class InferenceBatcher(Func<string, string[], CancellationToken, Task<float[][]>> encode,
    Action<string> log, CancellationToken cancellation) : IAsyncDisposable
{
    private sealed record Request(string Operation, string[] Inputs, int Bucket, TaskCompletionSource<float[][]> Completion);
    private readonly object gate = new();
    private readonly Queue<Request> pending = new();
    private readonly List<Task> workers = [];
    private readonly SemaphoreSlim capacity = new(64, 64);
    private readonly CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
    private TaskCompletionSource changed = NewSignal();
    private readonly TaskCompletionSource drained = NewSignal();
    private long batches, inputs;
    private int lastSize, largest, active, submitters;
    private bool closed;
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private void Wake() { TaskCompletionSource previous = changed; changed = NewSignal(); previous.TrySetResult(); }
    private static int Maximum(string operation) => operation == "documents" ? ModelContract.TextBatchSize : ModelContract.ImageBatchSize;
    public BatchState Statistics { get { lock (gate) return new(batches, inputs, lastSize, largest, active, pending.Count); } }

    public async Task<float[][]> SubmitAsync(string operation, string[] values, CancellationToken cancellation)
    {
        if (operation is not ("documents" or "images") || values.Length < 1 || values.Length > Maximum(operation))
            throw new ArgumentException("Invalid inference batch.");
        CancellationTokenSource linked;
        lock (gate)
        {
            if (closed) throw new OperationCanceledException("Inference batching is stopping.");
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime.Token);
            submitters++;
        }
        bool reserved = false;
        try
        {
            await capacity.WaitAsync(linked.Token);
            reserved = true;
            var completion = new TaskCompletionSource<float[][]>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = linked.Token.Register(() => completion.TrySetCanceled(linked.Token));
            int bucket = operation == "documents" ? BitOperations.Log2((uint)Math.Max(1, values.Max(FilePaths.ScalarLength))) + 1 : 0;
            lock (gate)
            {
                linked.Token.ThrowIfCancellationRequested();
                pending.Enqueue(new(operation, values, bucket, completion));
                reserved = false;
                Wake();
            }
            return await completion.Task;
        }
        finally
        {
            if (reserved) capacity.Release();
            linked.Dispose();
            lock (gate) if (--submitters == 0 && closed) drained.TrySetResult();
        }
    }
    public void AddWorker()
    {
        lock (gate)
        {
            if (closed || lifetime.IsCancellationRequested) return;
            workers.Add(Task.Run(RunAsync));
        }
    }
    private async Task<List<Request>> TakeAsync()
    {
        Request first;
        while (true)
        {
            Task wait;
            lock (gate)
            {
                lifetime.Token.ThrowIfCancellationRequested();
                if (pending.TryDequeue(out first!))
                {
                    capacity.Release();
                    if (first.Completion.Task.IsCanceled) continue;
                    break;
                }
                wait = changed.Task;
            }
            await wait.WaitAsync(lifetime.Token);
        }
        var group = new List<Request> { first };
        int size = first.Inputs.Length;
        long deadline = Environment.TickCount64 + 20;
        while (size < Maximum(first.Operation))
        {
            Task wait;
            lock (gate)
            {
                if (pending.TryPeek(out var next))
                {
                    if (next.Completion.Task.IsCanceled) { pending.Dequeue(); capacity.Release(); continue; }
                    if (next.Operation != first.Operation || next.Bucket != first.Bucket || size + next.Inputs.Length > Maximum(first.Operation)) break;
                    pending.Dequeue(); capacity.Release(); group.Add(next); size += next.Inputs.Length; continue;
                }
                wait = changed.Task;
            }
            int remaining = (int)(deadline - Environment.TickCount64);
            if (remaining <= 0) break;
            await Task.WhenAny(wait, Task.Delay(remaining, lifetime.Token));
            if (lifetime.IsCancellationRequested) break;
        }
        return group;
    }
    private void Record(int count)
    {
        lock (gate) { batches++; inputs += count; lastSize = count; largest = Math.Max(largest, count); }
    }
    private async Task RunAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                List<Request> group = await TakeAsync();
                lock (gate) active++;
                try
                {
                    string[] values = group.SelectMany(item => item.Inputs).ToArray();
                    float[][] result = await encode(group[0].Operation, values, lifetime.Token);
                    if (result.Length != values.Length) throw new InvalidDataException("Incomplete combined inference output.");
                    Record(result.Length);
                    int offset = 0;
                    foreach (var request in group)
                    {
                        request.Completion.TrySetResult(result.AsSpan(offset, request.Inputs.Length).ToArray());
                        offset += request.Inputs.Length;
                    }
                }
                catch (Exception error)
                {
                    if (group.Count > 1 && error is not OperationCanceledException && !lifetime.IsCancellationRequested)
                    {
                        log($"Combined inference failed; isolating {group.Count} requests: {error.Message}");
                        foreach (var request in group)
                        {
                            try
                            {
                                var result = await encode(request.Operation, request.Inputs, lifetime.Token);
                                if (result.Length != request.Inputs.Length) throw new InvalidDataException("Incomplete inference output.");
                                Record(result.Length);
                                request.Completion.TrySetResult(result);
                            }
                            catch (Exception individual) { request.Completion.TrySetException(individual); }
                        }
                    }
                    else foreach (var request in group) request.Completion.TrySetException(error);
                }
                finally { lock (gate) active--; }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync()
    {
        Task[] activeWorkers;
        lock (gate)
        {
            if (closed) return;
            closed = true;
            lifetime.Cancel();
            while (pending.TryDequeue(out var request))
            { capacity.Release(); request.Completion.TrySetCanceled(); }
            Wake();
            activeWorkers = workers.ToArray();
            if (submitters == 0) drained.TrySetResult();
        }
        await Task.WhenAll(activeWorkers);
        await drained.Task;
        lifetime.Dispose();
        capacity.Dispose();
    }
}
