namespace FuzzyFindAnything.Core;

public sealed class ModelManager : IEmbeddings
{
    private readonly object gate = new();
    private readonly HuggingFaceModels cache;
    private readonly string legacyHost, compatibleHost, dataDirectory;
    private readonly CpuBudget budget;
    private readonly Action<string> log;
    private Dictionary<string, ModelPool> pools = [];
    private Dictionary<string, EmbeddingSpace> routes = [];
    private Dictionary<string, ModelPool> kindPools = [];
    private ModelSelection applied = ModelSelection.Default;
    private Catalog? catalog;
    private CancellationTokenSource? lifetime;
    private bool disposed;
    private string warning = "";

    public ModelManager(string dataDirectory, string modelDirectory, string legacyHost, string compatibleHost,
        CpuBudget budget, Action<string> log)
    {
        this.dataDirectory = dataDirectory; this.legacyHost = legacyHost; this.compatibleHost = compatibleHost;
        this.budget = budget; this.log = log;
        cache = new(Path.Combine(dataDirectory, "models"), modelDirectory);
    }
    public void Bind(Catalog store) => catalog = store;
    public ModelSelection Selection { get { lock (gate) return applied; } }
    public IReadOnlyDictionary<string, EmbeddingSpace> Routes { get { lock (gate) return new Dictionary<string, EmbeddingSpace>(routes); } }
    public IReadOnlyList<IEmbeddings> ActiveModels { get { lock (gate) return pools.Values.Cast<IEmbeddings>().ToArray(); } }
    public IEmbeddings ForKind(string kind) { lock (gate) return kindPools[kind]; }
    public IReadOnlyList<EmbeddingSearchBinding> SearchBindings
    {
        get
        {
            lock (gate)
                return routes.Keys.GroupBy(kind => (Pool: kindPools[kind], Profile: QueryFor(kind, routes[kind], applied.OptionsFor(kind))))
                    .Select(group => new EmbeddingSearchBinding(group.Key.Pool, group.Key.Profile, group.ToArray())).ToArray();
        }
    }
    private static string QueryFor(string kind, EmbeddingSpace space, ModelOptions options) =>
        options.QueryProfile.Length > 0 ? options.QueryProfile : space.Recipe?.DefaultQuery(kind) ?? "default";
    private static string SessionKey(EmbeddingSpace space, ModelOptions options) => space.CacheKey + ":" + options.Device;
    public int ReadyWorkers => ActiveModels.Sum(model => model.ReadyWorkers);
    public int Threads => ActiveModels.Select(model => model.Threads).DefaultIfEmpty(1).Max();
    public int BatchConcurrency => Math.Max(1, Math.Min(budget.Processors, ReadyWorkers * 2));
    public string Warning => string.Join(" ", ActiveModels.Select(model => model.Warning).Append(warning).Where(value => value.Length > 0));
    public BatchState Statistics
    {
        get
        {
            var values = ActiveModels.Select(model => model.Statistics).ToArray();
            return new(values.Sum(value => value.CompletedBatches), values.Sum(value => value.CompletedInputs),
                values.Select(value => value.LastBatchSize).DefaultIfEmpty().Max(), values.Select(value => value.LargestBatch).DefaultIfEmpty().Max(),
                values.Sum(value => value.ActiveBatches), values.Sum(value => value.PendingRequests));
        }
    }
    public async Task StartAsync(Action<int, string> progress, CancellationToken cancellation)
    {
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var wanted = ModelSelection.Load(dataDirectory);
        var spaces = new Dictionary<string, EmbeddingSpace>();
        foreach (string url in wanted.DistinctUrls)
            spaces[url] = catalog?.Spaces.FirstOrDefault(space => space.ModelUrl == url) ?? cache.FindCached(url);
        wanted = NormalizeSelection(wanted, spaces);
        var next = BuildRoutes(wanted, spaces);
        var started = new Dictionary<string, ModelPool>();
        var byKind = new Dictionary<string, ModelPool>();
        try
        {
            int completed = 0, total = next.Select(pair => SessionKey(pair.Value, wanted.OptionsFor(pair.Key))).Distinct().Count();
            foreach (var (kind, space) in next)
            {
                string key = SessionKey(space, wanted.OptionsFor(kind));
                if (!started.TryGetValue(key, out var pool))
                {
                    pool = CreatePool(space, total, wanted.OptionsFor(kind));
                    started.Add(key, pool);
                    await pool.StartAsync((step, stage) => progress((completed * 6 + step) / total, $"{stage}"), lifetime.Token);
                    completed++;
                }
                byKind[kind] = pool;
            }
            lock (gate) { pools = started; routes = next; kindPools = byKind; applied = wanted; }
        }
        catch
        {
            foreach (var pool in started.Values) await pool.DisposeAsync();
            throw;
        }
    }
    private ModelPool CreatePool(EmbeddingSpace space, int count, ModelOptions options) => new(
        space == EmbeddingSpace.Default ? legacyHost : compatibleHost, cache.DirectoryFor(space), budget, log,
        space: space, workerLimit: count > 1 || space != EmbeddingSpace.Default ? 1 : null,
        threadLimit: count > 1 ? Math.Max(1, budget.Processors / count) : null,
        requiredProvider: space == EmbeddingSpace.Default ? null : options.ProviderSelector);

    internal static ModelSelection NormalizeSelection(ModelSelection selection, IReadOnlyDictionary<string, EmbeddingSpace> spaces)
    {
        ModelOptions? Options(string kind)
        {
            var space = spaces[selection.ForKind(kind)];
            var value = selection.OptionsFor(kind);
            string profile = QueryFor(kind, space, value);
            if (profile == "choose") throw new ArgumentException($"Choose an approved query purpose for {kind} before applying this model.");
            if (space.Recipe is { } recipe) _ = recipe.Query(profile);
            else if (profile != "default") throw new NotSupportedException("This legacy model does not expose configurable query profiles.");
            if (space == EmbeddingSpace.Default && value.Device is not ("Automatic" or "CPU"))
                throw new NotSupportedException("The bundled EmbeddingGemma2 artifact is CPU-only. Choose a separately qualified accelerator artifact rather than forcing this export onto another device.");
            if (space.Recipe is null && profile == "default") value = value with { QueryProfile = "" };
            return value == ModelOptions.Default ? null : value;
        }
        return selection with { TextOptions = Options("Document"), CodeOptions = Options("Code"), ImageOptions = Options("Image") };
    }

    public async Task<ModelInspection> InspectAsync(ModelSelection selection, CancellationToken cancellation)
    {
        selection = selection.Normalize();
        var requirements = new List<ModelRequirement>();
        foreach (string kind in new[] { "Document", "Code", "Image" })
            requirements.Add(await cache.InspectAsync(kind, selection.ForKind(kind), cancellation));
        return new(selection, requirements.ToArray());
    }

    private static Dictionary<string, EmbeddingSpace> BuildRoutes(ModelSelection selection, IReadOnlyDictionary<string, EmbeddingSpace> spaces)
    {
        var result = new Dictionary<string, EmbeddingSpace>
        { ["Document"] = spaces[selection.Text], ["Code"] = spaces[selection.Code], ["Image"] = spaces[selection.Image] };
        if (!result["Image"].SupportsImages)
            throw new NotSupportedException($"The image selection is a text-only model: {selection.Image}. The current indexes were not changed.");
        return result;
    }
    public async Task<PreparedModels?> PrepareAsync(ModelSelection selection, Action<string> progress, CancellationToken cancellation)
    {
        selection = selection.Normalize();
        if (selection == Selection) return null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime?.Token ?? default);
        var spaces = new Dictionary<string, EmbeddingSpace>();
        foreach (string url in selection.DistinctUrls)
        {
            EmbeddingSpace? existing;
            lock (gate) existing = routes.Values.FirstOrDefault(space => space.ModelUrl == url);
            spaces[url] = existing ?? cache.FindVerified(url, progress) ?? await cache.ResolveAsync(url, progress, linked.Token);
        }
        selection = NormalizeSelection(selection, spaces);
        if (selection == Selection) return null;
        var nextRoutes = BuildRoutes(selection, spaces);
        var next = new Dictionary<string, ModelPool>();
        var byKind = new Dictionary<string, ModelPool>();
        var created = new List<ModelPool>();
        try
        {
            int count = nextRoutes.Select(pair => SessionKey(pair.Value, selection.OptionsFor(pair.Key))).Distinct().Count();
            foreach (var (kind, space) in nextRoutes)
            {
                string key = SessionKey(space, selection.OptionsFor(kind));
                if (next.TryGetValue(key, out var shared)) { byKind[kind] = shared; continue; }
                ModelPool? existing;
                lock (gate) existing = pools.GetValueOrDefault(key);
                if (existing is not null && existing.ReadyWorkers > 0) { next[key] = existing; byKind[kind] = existing; continue; }
                progress($"Checking local hardware and inference for {space.ModelUrl}");
                var pool = CreatePool(space, count, selection.OptionsFor(kind));
                created.Add(pool);
                await pool.StartAsync((_, stage) => progress(stage), linked.Token);
                _ = await pool.QueryAsync("local model compatibility check", linked.Token);
                if (space.SupportsImages && space == EmbeddingSpace.Default)
                {
                    string probe = Path.Combine(dataDirectory, ".model-image-probe-" + Guid.NewGuid().ToString("N") + ".bmp");
                    try
                    {
                        WriteProbeBitmap(probe);
                        _ = await pool.ImageAsync(probe, linked.Token);
                    }
                    finally { File.Delete(probe); }
                }
                next[key] = pool; byKind[kind] = pool;
            }
            foreach (var kind in nextRoutes.Keys)
                _ = await byKind[kind].QueryAsync("local model compatibility check", QueryFor(kind, nextRoutes[kind], selection.OptionsFor(kind)), linked.Token);
            return new(selection, nextRoutes, next, byKind, created);
        }
        catch
        {
            foreach (var pool in created) await pool.DisposeAsync();
            throw;
        }
    }
    private static void WriteProbeBitmap(string path)
    {
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write((ushort)0x4d42); writer.Write(58); writer.Write(0); writer.Write(54);
        writer.Write(40); writer.Write(1); writer.Write(1); writer.Write((ushort)1); writer.Write((ushort)24);
        writer.Write(0); writer.Write(4); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
        writer.Write(new byte[] { 0, 0, 255, 0 });
    }
    public async Task CommitAsync(PreparedModels prepared)
    {
        ModelPool[] retired;
        lock (gate)
        {
            retired = pools.Values.Except(prepared.Pools.Values).ToArray();
            pools = prepared.Pools; routes = prepared.Routes; kindPools = prepared.KindPools; applied = prepared.Selection; prepared.Committed = true;
        }
        foreach (var pool in retired)
        {
            try { await pool.DisposeAsync(); }
            catch (Exception error) when (error is IOException or InvalidOperationException)
            { warning = "A retired model worker could not close cleanly: " + error.Message; log(warning); }
        }
    }
    public ModelConfigurationState State()
    {
        var indexes = catalog?.ModelIndexes() ?? [];
        Dictionary<string, ModelPool> active;
        lock (gate) active = new(kindPools);
        return new(Selection, indexes.Select(index =>
        {
            var model = index.Kinds.Length > 0 ? active.GetValueOrDefault(index.Kinds[0]) : null;
            return index with { Provider = model?.Provider ?? "", Device = model?.Device ?? "" };
        }).ToArray(), catalog?.Setting("model_apply_message") ?? "");
    }
    public Task<string[]> SplitAsync(string text, CancellationToken cancellation) => ForKind("Document").SplitAsync(text, cancellation);
    public Task<float[][]> DocumentsAsync(string title, string[] passages, CancellationToken cancellation) => ForKind("Document").DocumentsAsync(title, passages, cancellation);
    public Task<float[]> ImageAsync(string path, CancellationToken cancellation) => ForKind("Image").ImageAsync(path, cancellation);
    public Task<float[]> QueryAsync(string text, CancellationToken cancellation) => ForKind("Document").QueryAsync(text, cancellation);
    public async ValueTask DisposeAsync()
    {
        lock (gate) { if (disposed) return; disposed = true; }
        lifetime?.Cancel();
        foreach (var model in ActiveModels) await model.DisposeAsync();
        lifetime?.Dispose();
    }

    public sealed class PreparedModels(ModelSelection selection, Dictionary<string, EmbeddingSpace> routes,
        Dictionary<string, ModelPool> pools, Dictionary<string, ModelPool> kindPools, List<ModelPool> created) : IAsyncDisposable
    {
        public ModelSelection Selection { get; } = selection;
        public Dictionary<string, EmbeddingSpace> Routes { get; } = routes;
        internal Dictionary<string, ModelPool> Pools { get; } = pools;
        internal Dictionary<string, ModelPool> KindPools { get; } = kindPools;
        internal bool Committed { get; set; }
        public async ValueTask DisposeAsync()
        {
            if (!Committed) foreach (var pool in created) await pool.DisposeAsync();
        }
    }
}
