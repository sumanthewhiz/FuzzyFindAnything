namespace FuzzyFindAnything.Core;

public sealed class ModelMemoryException(long modelBytes, long availableBytes) : IOException(
    $"The model needs {modelBytes / (double)(1L << 30):0.0} GiB plus a 2.0 GiB system reserve; " +
    $"{availableBytes / (double)(1L << 30):0.0} GiB is currently available after releasing idle model workers.")
{
    public long ModelBytes { get; } = modelBytes;
    public long AvailableBytes { get; } = availableBytes;
}

internal interface IModelResidency
{
    bool HasResidentWorker { get; }
    bool HasBusyWorker { get; }
    long LastUsed { get; }
    Task<bool> ReleaseWorkerAsync(bool wait, CancellationToken cancellation);
}

public sealed class ModelMemoryCoordinator(Action<string> log, Func<long>? availableMemory = null)
{
    public const long SystemReserveBytes = 2L << 30;
    private readonly object gate = new();
    private readonly HashSet<IModelResidency> residents = [];
    private readonly SemaphoreSlim loading = new(1, 1);
    public long AvailableBytes => (availableMemory ?? ModelPool.AvailableMemory)();

    internal IDisposable Register(IModelResidency resident)
    {
        lock (gate) residents.Add(resident);
        return new Registration(this, resident);
    }

    internal async Task<IDisposable> AcquireAsync(IModelResidency? owner, long modelBytes,
        CancellationToken cancellation, bool reclaim = true)
    {
        if (modelBytes <= 0) throw new ArgumentOutOfRangeException(nameof(modelBytes));
        long required = checked(modelBytes + SystemReserveBytes);
        await loading.WaitAsync(cancellation);
        try
        {
            bool waitingLogged = false;
            while (AvailableBytes < required)
            {
                if (!reclaim) throw new ModelMemoryException(modelBytes, AvailableBytes);
                IModelResidency[] candidates;
                lock (gate) candidates = residents.ToArray();
                candidates = candidates.Where(value => value.HasResidentWorker)
                    .OrderBy(value => ReferenceEquals(value, owner)).ThenBy(value => value.LastUsed).ToArray();
                bool released = false;
                foreach (var candidate in candidates)
                {
                    if (!await candidate.ReleaseWorkerAsync(false, cancellation)) continue;
                    released = true;
                    log("Released an idle native model worker to make room for the requested model; its index remains available.");
                    if (AvailableBytes >= required) break;
                }
                if (AvailableBytes >= required) break;
                if (released) { await Task.Delay(50, cancellation); continue; }
                var busy = candidates.FirstOrDefault(value => value.HasBusyWorker);
                if (busy is null)
                    throw new ModelMemoryException(modelBytes, AvailableBytes);
                if (!waitingLogged)
                {
                    log("Waiting for an active model operation before reclaiming memory.");
                    waitingLogged = true;
                }
                if (!await busy.ReleaseWorkerAsync(true, cancellation)) await Task.Delay(50, cancellation);
            }
            return new LoadingLease(loading);
        }
        catch { loading.Release(); throw; }
    }

    private sealed class LoadingLease(SemaphoreSlim semaphore) : IDisposable
    {
        private bool disposed;
        public void Dispose() { if (!disposed) { disposed = true; semaphore.Release(); } }
    }
    private sealed class Registration(ModelMemoryCoordinator owner, IModelResidency resident) : IDisposable
    {
        public void Dispose() { lock (owner.gate) owner.residents.Remove(resident); }
    }
}
