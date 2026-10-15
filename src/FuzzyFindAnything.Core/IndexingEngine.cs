using System.ComponentModel;
using System.Diagnostics;

namespace FuzzyFindAnything.Core;

public sealed class IndexingEngine : IAsyncDisposable
{
    private sealed class Dirty
    {
        public long Due;
        public bool Full, Force;
        public HashSet<string> Paths { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
    private sealed class SourceChanged : Exception;
    private readonly object gate = new();
    private readonly Dictionary<int, Dirty> pending = [];
    private readonly HashSet<int> scanning = [];
    private readonly Dictionary<int, long> lastScans = [];
    private readonly Dictionary<int, Dictionary<UInt128, string>> identities = [];
    private readonly Dictionary<long, ActivityState> activity = [];
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim wake = new(0, 1);
    private readonly List<Task> workers = [];
    private readonly TextExtractor extractor;
    private readonly string[] excluded;
    private readonly CpuBudget budget;
    private readonly EngineLog log;
    private readonly IEmbeddings model;
    private readonly ChangeMonitor? monitor;
    private Task? discovery, loading;
    private string modelState = "Loading", error = "";
    private int fileWorkerCount;
    public Catalog Store { get; }
    public StartupProgress Startup { get; }
    public int FileWorkerCount
    {
        get => fileWorkerCount;
        internal set
        {
            if (discovery is not null || value < 1 || value > Math.Min(8, budget.Processors))
                throw new ArgumentOutOfRangeException(nameof(value), "File concurrency must fit the CPU budget and be selected before startup.");
            fileWorkerCount = value;
        }
    }

    public IndexingEngine(string data, string models, string extractorPath, string appDirectory, CpuBudget budget,
        IEmbeddings model, EngineLog log, StartupProgress startup, bool watch = true)
    {
        this.budget = budget; this.model = model; this.log = log; Startup = startup;
        Startup.Update(0, "Opening the saved index");
        Store = new Catalog(data, Math.Min(2, budget.Processors), message => log.Write("catalog", message));
        Startup.Update(1, "Preparing change monitoring");
        extractor = new TextExtractor(extractorPath);
        excluded = new[] { data, models, appDirectory }.Select(FilePaths.Canonical).ToArray();
        FileWorkerCount = Math.Min(8, budget.Processors);
        if (watch) monitor = new ChangeMonitor(Store, (id, path, force) => Schedule(id, path, force), message => log.Write("monitor", message));
    }
    public void Start()
    {
        if (discovery is not null) throw new InvalidOperationException("The engine is already started.");
        var folders = Store.Folders();
        monitor?.Configure(folders); monitor?.Start();
        foreach (var folder in folders) Schedule(folder.Id, immediate: true);
        Startup.Update(2, "Starting the native search model");
        discovery = Task.Run(DiscoverLoopAsync);
        loading = Task.Run(LoadAsync);
    }
    private bool Excluded(string path) => excluded.Any(root => FilePaths.Within(path, root)) || log.Owns(path);
    private void Signal() { if (wake.CurrentCount == 0) { try { wake.Release(); } catch (SemaphoreFullException) { } } }
    public void Schedule(int folder, string? path = null, bool force = false, bool immediate = false)
    {
        if (path is not null && Excluded(path)) return;
        lock (gate)
        {
            if (lifetime.IsCancellationRequested) return;
            if (!pending.TryGetValue(folder, out var work)) pending[folder] = work = new() { Due = Environment.TickCount64 + 600 };
            if (path is null) work.Full = true;
            else work.Paths.Add(Path.GetFullPath(path));
            work.Force |= force;
            if (immediate) work.Due = 0;
        }
        Signal();
    }
    public int AddFolder(string path)
    {
        path = FilePaths.Canonical(path);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("Choose an existing folder to index.");
        if (Excluded(path)) throw new ArgumentException("Application-owned folders cannot be indexed.");
        int id = Store.AddFolder(path);
        monitor?.Configure(Store.Folders());
        Schedule(id, immediate: true);
        return id;
    }
    public void RemoveFolders(int[] ids)
    {
        if (ids.Any(id => id <= 0)) throw new ArgumentException("Folder identifiers must be positive.");
        Store.RemoveFolders(ids);
        lock (gate)
            foreach (int id in ids) { pending.Remove(id); lastScans.Remove(id); identities.Remove(id); }
        monitor?.Configure(Store.Folders());
    }
    public void Rescan() { foreach (var folder in Store.Folders()) Schedule(folder.Id, force: true, immediate: true); }
    public EngineState Status()
    {
        var count = Store.Counts();
        var folders = Store.Folders();
        HashSet<int> busy;
        List<ActivityState> activities;
        lock (gate) { busy = new(scanning.Concat(pending.Keys)); activities = activity.Values.ToList(); }
        folders = folders.Select(folder => Store.FolderCounts(folder, busy.Contains(folder.Id))).ToList();
        var queue = Store.QueueCounts();
        var batches = model.Statistics;
        return new(modelState, modelState == "Ready" ? "Search engine ready" : Startup.Snapshot.Stage,
            error.Length > 0 ? error : model.Warning, folders, count.Files, count.Vectors, monitor?.Description ?? "Monitoring disabled",
            Store.Recovery, Store.Metadata(), new(budget.TotalCores, budget.Processors, model.ReadyWorkers, model.Threads,
                FileWorkerCount, batches.LastBatchSize), queue.Queued, queue.Active, activities, log.State, Startup.Snapshot, batches);
    }
    public async Task<List<ResultFile>> SearchAsync(string query, double threshold = 0.6, int limit = 50)
    {
        if (string.IsNullOrWhiteSpace(query) || FilePaths.ScalarLength(query) > 2048)
            throw new ArgumentException("Enter a search phrase of at most 2,048 characters.");
        if (modelState != "Ready") throw new InvalidOperationException(error.Length > 0 ? error : Startup.Snapshot.Stage);
        return Store.Search(await model.QueryAsync(query.Trim(), lifetime.Token), limit, threshold);
    }
    private async Task LoadAsync()
    {
        try
        {
            await model.StartAsync(Startup.ModelProgress, lifetime.Token);
            lock (gate)
            {
                lifetime.Token.ThrowIfCancellationRequested();
                modelState = "Ready";
                for (int i = 0; i < FileWorkerCount; i++) workers.Add(Task.Run(WorkLoopAsync));
                log.Write("startup", $"Primary model ready; {FileWorkerCount} file pipelines, {model.ReadyWorkers} model processes, {budget.Processors} permitted cores.");
                Startup.Ready();
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            modelState = "Error"; error = exception.Message;
            log.Write("startup", exception.ToString()); Startup.Fail(exception);
        }
    }
    private async Task DiscoverLoopAsync()
    {
        long checkpointAt = Environment.TickCount64;
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                (int Id, Dirty Work)? selected = null;
                var folders = Store.Folders();
                lock (gate)
                {
                    long now = Environment.TickCount64;
                    foreach (var folder in folders)
                        if (lastScans.TryGetValue(folder.Id, out long previous) && now - previous >= 300_000 && !pending.ContainsKey(folder.Id))
                            pending[folder.Id] = new() { Due = 0, Full = true };
                    foreach (var pair in pending)
                        if (pair.Value.Due <= now) { selected = (pair.Key, pair.Value); break; }
                    if (selected is not null) { pending.Remove(selected.Value.Id); scanning.Add(selected.Value.Id); }
                }
                if (selected is not null)
                {
                    var (id, work) = selected.Value;
                    try { Discover(id, work); }
                    catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { break; }
                    catch (Exception exception)
                    {
                        Store.SetSetting("scan_error:" + id, exception.Message);
                        log.Write("discovery", exception.ToString());
                    }
                    finally
                    {
                        lock (gate)
                        {
                            scanning.Remove(id); lastScans[id] = Environment.TickCount64;
                            if (Store.HasFolder(id) && !string.IsNullOrEmpty(Store.Setting("scan_error:" + id)))
                                pending[id] = new() { Due = Environment.TickCount64 + 30_000, Full = true };
                            if (pending.Count == 0) monitor?.Checkpoint();
                        }
                    }
                }
                else await wake.WaitAsync(500, lifetime.Token);
                if (Environment.TickCount64 - checkpointAt >= 120_000 || Store.QueueCounts().Queued == 0)
                {
                    try
                    {
                        Store.Checkpoint(); checkpointAt = Environment.TickCount64;
                        if (error.StartsWith("Index checkpoint failed:", StringComparison.Ordinal)) error = "";
                    }
                    catch (Exception exception) { error = "Index checkpoint failed: " + exception.Message; log.Write("checkpoint", exception.ToString()); }
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception exception) { StopAfterStorageFailure(exception); }
    }
    private void Enqueue(int folder, string path, bool force)
    {
        if (lifetime.IsCancellationRequested || !Store.HasFolder(folder)) return;
        string? kind = FilePaths.Kind(path);
        if (kind is null || Excluded(path) || FilePaths.IsNameSurrogate(path)) return;
        path = FilePaths.Canonical(path);
        string? root = Store.Folders().FirstOrDefault(value => value.Id == folder)?.Path;
        if (root is null || !FilePaths.Within(path, root) || Excluded(path)) return;
        var signature = FilePaths.Inspect(path);
        var old = Store.FindFile(path);
        string hash = "";
        if (!FilePaths.IsOffline(path) && (force || old?.Fingerprint != signature))
        {
            try { hash = FilePaths.Hash(path); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { log.Write("fingerprint", $"Cannot hash {path}: {exception.Message}; extraction will report its access error."); }
        }
        Store.Enqueue(folder, path, signature, kind, hash, force);
        lock (gate)
        {
            if (!identities.TryGetValue(folder, out var known)) identities[folder] = known = [];
            known[UInt128.Parse(signature.Identity)] = path;
        }
    }
    private void Discover(int id, Dirty request)
    {
        var folder = Store.Folders().FirstOrDefault(value => value.Id == id);
        if (folder is null) return;
        var failures = new List<string>();
        if (request.Paths.Count > 0 && !request.Full && !request.Force)
        {
            foreach (string input in request.Paths)
            {
                lifetime.Token.ThrowIfCancellationRequested();
                string path = FilePaths.Canonical(input);
                if (!FilePaths.Within(path, folder.Path) || Excluded(path)) continue;
                try
                {
                    FileAttributes attributes = File.GetAttributes(path);
                    if (attributes.HasFlag(FileAttributes.Directory)) DiscoverTree(id, path, false, failures);
                    else Enqueue(id, path, true);
                }
                catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { Store.RemovePath(path, recursive: true); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception) { failures.Add($"{path}: {exception.Message}"); }
            }
        }
        else DiscoverTree(id, folder.Path, request.Force, failures);
        Dictionary<UInt128, string>? snapshot;
        lock (gate) snapshot = identities.TryGetValue(id, out var known) ? new(known) : null;
        if (snapshot is not null) monitor?.UpdateIdentities(id, snapshot, failures.Count == 0);
        Store.SetSetting("scan_error:" + id, string.Join("; ", failures));
        if (failures.Count > 0) log.Write("discovery", string.Join("; ", failures));
    }
    private void DiscoverTree(int id, string root, bool force, List<string> failures)
    {
        int previousErrors = failures.Count;
        var directories = new Stack<string>();
        var entries = new List<(string Path, long Length)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Dictionary<UInt128, string> known;
        lock (gate)
            if (!identities.TryGetValue(id, out known!)) identities[id] = known = [];
        directories.Push(root);
        while (directories.TryPop(out string? directory))
        {
            lifetime.Token.ThrowIfCancellationRequested();
            if (!Store.HasFolder(id)) return;
            try
            {
                if (FilePaths.IsNameSurrogate(directory)) { Store.RemovePath(directory, recursive: true); continue; }
                UInt128 identity = UInt128.Parse(FilePaths.Inspect(directory).Identity);
                lock (gate) known[identity] = directory;
                foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    if (Excluded(entry)) continue;
                    try
                    {
                        if (FilePaths.IsNameSurrogate(entry)) { Store.RemovePath(entry, recursive: true); continue; }
                        if (File.GetAttributes(entry).HasFlag(FileAttributes.Directory)) directories.Push(entry);
                        else if (FilePaths.Kind(entry) is not null)
                        { seen.Add(FilePaths.Key(entry)); entries.Add((entry, new FileInfo(entry).Length)); }
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
                    { failures.Add($"{entry}: {exception.Message}"); }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
            { failures.Add($"{directory}: {exception.Message}"); }
        }
        foreach (var entry in entries.OrderBy(value => value.Length).ThenBy(value => value.Path, StringComparer.OrdinalIgnoreCase))
        {
            lifetime.Token.ThrowIfCancellationRequested();
            try { Enqueue(id, entry.Path, force); }
            catch (FileNotFoundException) { seen.Remove(FilePaths.Key(entry.Path)); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
            { failures.Add($"{entry.Path}: {exception.Message}"); }
        }
        if (previousErrors == failures.Count) Store.Prune(id, root, seen);
    }
    private async Task WorkLoopAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var job = Store.Claim();
                if (job is null) { await Task.Delay(300, lifetime.Token); continue; }
                await ProcessAsync(job);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            StopAfterStorageFailure(exception);
        }
    }
    private void Activity(IndexJob job, string stage)
    {
        var progress = Store.Progress(job);
        lock (gate) activity[job.Id] = new(job.Path, stage, progress.Completed, progress.Total);
    }
    private void CheckCurrent(IndexJob job)
    {
        lifetime.Token.ThrowIfCancellationRequested();
        if (!Store.IsCurrent(job)) throw new OperationCanceledException("The source revision or indexing scope changed.");
    }
    private void CheckSourceScope(IndexJob job)
    {
        string canonical = FilePaths.Canonical(job.Path);
        var owners = Store.Owners(job).ToHashSet();
        if (FilePaths.IsNameSurrogate(job.Path) || Excluded(canonical) ||
            !Store.Folders().Any(folder => owners.Contains(folder.Id) && FilePaths.Within(canonical, folder.Path)))
        {
            Store.RemovePath(job.Path);
            throw new OperationCanceledException("The source moved outside the configured scope.");
        }
    }
    private async Task ProcessAsync(IndexJob job)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            CheckCurrent(job);
            CheckSourceScope(job);
            if (FilePaths.Inspect(job.Path) != job.Fingerprint) throw new SourceChanged();
            if (FilePaths.IsOffline(job.Path)) throw new ContentException("The file is online-only. Make it available on this device.", "cloud_unavailable", true);
            if (!job.Prepared)
            {
                if (job.Kind == "Image") Store.Prepare(job, ["Visual content"], null);
                else
                {
                    Activity(job, "Extracting");
                    var extracted = await extractor.ExtractAsync(job.Path, lifetime.Token);
                    Activity(job, "Preparing");
                    string[] passages = await model.SplitAsync(extracted.Text, lifetime.Token);
                    CheckCurrent(job);
                    if (FilePaths.Inspect(job.Path) != job.Fingerprint) throw new SourceChanged();
                    Store.Prepare(job, passages, extracted.Warnings.Length == 0 ? null : string.Join(" ", extracted.Warnings));
                }
            }
            CheckCurrent(job);
            var remaining = Store.Remaining(job);
            Activity(job, "Embedding");
            if (job.Kind == "Image" && remaining.Count > 0)
            {
                float[] vector = await model.ImageAsync(job.Path, lifetime.Token);
                if (!Store.SaveBatch(job, remaining, [vector])) throw new OperationCanceledException();
            }
            else if (remaining.Count > 0)
            {
                using var concurrency = new SemaphoreSlim(model.BatchConcurrency);
                async Task Embed(Passage[] batch)
                {
                    await concurrency.WaitAsync(lifetime.Token);
                    try
                    {
                        CheckCurrent(job);
                        float[][] values = await model.DocumentsAsync(Path.GetFileName(job.Path), batch.Select(value => value.Content).ToArray(), lifetime.Token);
                        if (!Store.SaveBatch(job, batch, values)) throw new OperationCanceledException();
                        Activity(job, "Embedding");
                    }
                    finally { concurrency.Release(); }
                }
                await Task.WhenAll(remaining.Chunk(4).Select(Embed));
            }
            CheckCurrent(job);
            var progress = Store.Progress(job);
            if (progress.Completed == progress.Total && progress.Total > 0)
            {
                CheckSourceScope(job);
                if (FilePaths.Inspect(job.Path) != job.Fingerprint || job.ContentHash.Length > 0 && FilePaths.Hash(job.Path) != job.ContentHash)
                    throw new SourceChanged();
                Activity(job, "Saving");
                if (Store.Complete(job))
                    log.RecordFile(job, "Succeeded", job.ElapsedMs + timer.Elapsed.TotalMilliseconds, progress.Total,
                        warning: Store.FindFile(job.Path)?.Warning);
            }
            else Store.Release(job, timer.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested || !Store.IsCurrent(job))
        { Store.Release(job, timer.Elapsed.TotalMilliseconds); }
        catch (FileNotFoundException exception)
        {
            if (!File.Exists(job.Path)) Store.RemovePath(job.Path);
            else Failure(job, exception, timer.Elapsed.TotalMilliseconds, "missing_dependency");
        }
        catch (SourceChanged)
        {
            foreach (int owner in Store.Owners(job))
            {
                try { Enqueue(owner, job.Path, true); }
                catch (FileNotFoundException) { Store.RemovePath(job.Path); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
                { Failure(job, exception, timer.Elapsed.TotalMilliseconds); }
            }
            Store.Release(job, timer.Elapsed.TotalMilliseconds);
        }
        catch (Exception exception) when (exception is NativeIndexException or Microsoft.Data.Sqlite.SqliteException)
        { StopAfterStorageFailure(exception); }
        catch (Exception exception) { Failure(job, exception, timer.Elapsed.TotalMilliseconds); }
        finally { lock (gate) activity.Remove(job.Id); }
    }
    private void StopAfterStorageFailure(Exception exception)
    {
        lock (gate)
        {
            error = "Indexing stopped after a storage failure. Restart to recover committed work: " + exception.Message;
            modelState = "Error";
            lifetime.Cancel();
        }
        Startup.Fail(exception);
        log.Write("fatal", exception.ToString());
    }
    private void Failure(IndexJob job, Exception exception, double elapsedMs, string? code = null)
    {
        code ??= exception is ContentException content ? content.Code : exception is OperationCanceledException ? "inference_timeout" : "indexing_failure";
        bool retryable = exception is not ContentException classified || classified.Retryable;
        if (Store.Fail(job, exception.Message, code, retryable, job.ElapsedMs + elapsedMs))
        {
            log.Write("indexer", $"{job.Path}: {exception}");
            log.RecordFile(job, "Failed", job.ElapsedMs + elapsedMs, failure: exception, code: code);
        }
    }
    public async ValueTask DisposeAsync()
    {
        lock (gate) lifetime.Cancel();
        Signal();
        try
        {
            if (monitor is not null) await monitor.DisposeAsync();
            var tasks = new List<Task>();
            if (discovery is not null) tasks.Add(discovery);
            if (loading is not null) tasks.Add(loading);
            await Task.WhenAll(tasks);
            await Task.WhenAll(workers);
        }
        finally
        {
            try { await model.DisposeAsync(); }
            finally
            {
                try { Store.Dispose(); }
                finally { wake.Dispose(); lifetime.Dispose(); }
            }
        }
    }
}
