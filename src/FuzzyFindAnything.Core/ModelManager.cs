namespace FuzzyFindAnything.Core;

public sealed class ModelManager : IEmbeddings
{
    private readonly object gate = new();
    private readonly HuggingFaceModels cache;
    private readonly ImageModelAssets imageAssets;
    private readonly ExternalModels external;
    private readonly string legacyHost, compatibleHost, dataDirectory;
    private readonly CpuBudget budget;
    private readonly Action<string> log;
    private Dictionary<string, ModelPool> pools = [];
    private Dictionary<string, EmbeddingSpace> routes = [];
    private Dictionary<string, ModelPool> kindPools = [];
    private ModelSelection applied = ModelSelection.Default;
    private ModelSelection embeddingSelection = ModelSelection.Default;
    private ImageUnderstandingPool? imageUnderstanding;
    private Catalog? catalog;
    private CancellationTokenSource? lifetime;
    private bool disposed;
    private string warning = "";

    public ModelManager(string dataDirectory, string modelDirectory, string legacyHost, string compatibleHost,
        CpuBudget budget, Action<string> log, string? providerDirectory = null)
    {
        this.dataDirectory = dataDirectory; this.legacyHost = legacyHost; this.compatibleHost = compatibleHost;
        this.budget = budget; this.log = log;
        cache = new(Path.Combine(dataDirectory, "models"), modelDirectory);
        imageAssets = new(Path.Combine(dataDirectory, "models", "image-understanding"));
        external = new(Path.Combine(dataDirectory, "models", "external"), log, providerDirectory);
    }
    public void Bind(Catalog store) => catalog = store;
    public ModelSelection Selection { get { lock (gate) return applied; } }
    public IReadOnlyDictionary<string, EmbeddingSpace> Routes { get { lock (gate) return new Dictionary<string, EmbeddingSpace>(routes); } }
    public IReadOnlyList<IEmbeddings> ActiveModels { get { lock (gate) return pools.Values.Cast<IEmbeddings>().ToArray(); } }
    public IEmbeddings ForKind(string kind) { lock (gate) return kindPools[kind]; }
    public IImageUnderstanding? ImageUnderstanding { get { lock (gate) return imageUnderstanding; } }
    public bool SupportsStructuredChunking => ActiveModels.Count > 0 && ActiveModels.All(model => model.SupportsStructuredChunking);
    public IReadOnlyList<EmbeddingSearchBinding> SearchBindings
    {
        get
        {
            lock (gate)
                return routes.Keys.GroupBy(kind => (Pool: kindPools[kind], Profile: QueryFor(kind, routes[kind], embeddingSelection.OptionsFor(kind)),
                    Channel: kind == "Image" && imageUnderstanding is not null ? "Image" : "Shared"))
                    .Select(group => new EmbeddingSearchBinding(group.Key.Pool, group.Key.Profile, group.ToArray())).ToArray();
        }
    }
    private static string QueryFor(string kind, EmbeddingSpace space, ModelOptions options) =>
        options.QueryProfile.Length > 0 ? options.QueryProfile : space.Recipe?.DefaultQuery(kind) ?? "default";
    private static string SessionKey(EmbeddingSpace space, ModelOptions options) => space.CacheKey + ":" + options.Device;
    private static string LookupKey(ModelSelection selection, string kind) => selection.ReferenceFor(kind) is ProviderModelReference
        ? selection.ForKind(kind) + "\n" + selection.OptionsFor(kind).Device : selection.ForKind(kind);
    public int ReadyWorkers => ActiveModels.Sum(model => model.ReadyWorkers) + (ImageUnderstanding?.IsLoaded == true ? 1 : 0);
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
        var encoded = wanted.EmbeddingSelection();
        var spaces = new Dictionary<string, EmbeddingSpace>();
        foreach (string kind in new[] { "Document", "Code", "Image" })
        {
            string url = encoded.ForKind(kind), key = LookupKey(encoded, kind);
            if (spaces.ContainsKey(key)) continue;
            spaces[key] = ModelReference.Parse(url) is ProviderModelReference reference
                ? external.FindCached(reference, encoded.OptionsFor(kind))
                : catalog?.Spaces.FirstOrDefault(space => space.ModelUrl == url) ?? cache.FindCached(url);
        }
        encoded = NormalizeSelection(encoded, spaces);
        wanted = wanted.WithEmbeddingOptions(encoded);
        var next = BuildRoutes(encoded, spaces, wanted.UsesImageUnderstanding);
        var started = new Dictionary<string, ModelPool>();
        var byKind = new Dictionary<string, ModelPool>();
        try
        {
            int completed = 0, total = next.Select(pair => SessionKey(pair.Value, encoded.OptionsFor(pair.Key))).Distinct().Count();
            foreach (var (kind, space) in next)
            {
                string key = SessionKey(space, encoded.OptionsFor(kind));
                if (!started.TryGetValue(key, out var pool))
                {
                    pool = CreatePool(space, total, encoded.OptionsFor(kind));
                    started.Add(key, pool);
                    await pool.StartAsync((step, stage) => progress((completed * 6 + step) / total, $"{stage}"), lifetime.Token);
                    completed++;
                }
                byKind[kind] = pool;
            }
            ImageUnderstandingPool? images = null;
            if (wanted.UsesImageUnderstanding)
                images = CreateImagePool(Pipeline(wanted, next["Image"]), lifetime.Token);
            lock (gate) { pools = started; routes = next; kindPools = byKind; applied = wanted; embeddingSelection = encoded; imageUnderstanding = images; }
        }
        catch
        {
            foreach (var pool in started.Values) await pool.DisposeAsync();
            throw;
        }
    }
    private ModelPool CreatePool(EmbeddingSpace space, int count, ModelOptions options) => new(
        space.External is not null ? external.ExecutableFor(space) : space == EmbeddingSpace.Default ? legacyHost : compatibleHost,
        space.External is not null ? external.DirectoryFor((ProviderModelReference)ModelReference.Parse(space.ModelUrl), options.Device) : cache.DirectoryFor(space),
        budget, log,
        space: space, workerLimit: count > 1 || space != EmbeddingSpace.Default ? 1 : null,
        threadLimit: count > 1 ? Math.Max(1, budget.Processors / count) : null,
        requiredProvider: space == EmbeddingSpace.Default ? null : options.ProviderSelector);

    internal static ModelSelection NormalizeSelection(ModelSelection selection, IReadOnlyDictionary<string, EmbeddingSpace> spaces)
    {
        ModelOptions? Options(string kind)
        {
            var space = spaces[LookupKey(selection, kind)];
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
        {
            if (kind == "Image" && ImageModelRecipes.Find(selection.Image) is { } image)
            {
                var options = selection.EffectiveImagePipeline;
                foreach (string task in options.Tasks) _ = image.Task(task);
                if (options.RegionTask.Length > 0 && image.Task(options.RegionTask).OutputKind != "regions")
                    throw new ArgumentException("Choose a region-output task for regional image search.");
                string embedder = options.EmbeddingModel ?? selection.Text;
                var target = await InspectBindingAsync("Document", embedder, cancellation);
                requirements.Add(new("Image", selection.Image, target.State,
                    $"Image descriptions ({string.Join(" + ", options.Tasks)}) -> {embedder}",
                    $"Native image-understanding recipe {image.Recipe.Id}; {image.ArtifactUrl}@{image.Revision}. " +
                    "Task prompts, tokenizer and stages are automatic. Generated text is searched with the selected text embedder.",
                    target.QueryProfiles, target.DefaultQuery, false, target.Error, image, embedder));
            }
            else requirements.Add(await InspectBindingAsync(kind, selection.ForKind(kind), cancellation));
        }
        return new(selection, requirements.ToArray());
    }
    private Task<ModelRequirement> InspectBindingAsync(string kind, string reference, CancellationToken cancellation) =>
        ModelReference.Parse(reference) is ProviderModelReference provider
            ? external.InspectAsync(kind, provider, cancellation) : cache.InspectAsync(kind, reference, cancellation);

    public Task<ProviderCatalog> ProviderCatalogAsync(string provider, string? version, bool refresh, CancellationToken cancellation) =>
        external.CatalogAsync(provider, version, refresh, cancellation);

    private static Dictionary<string, EmbeddingSpace> BuildRoutes(ModelSelection selection, IReadOnlyDictionary<string, EmbeddingSpace> spaces,
        bool describedImages = false)
    {
        var result = new Dictionary<string, EmbeddingSpace>
        {
            ["Document"] = spaces[LookupKey(selection, "Document")],
            ["Code"] = spaces[LookupKey(selection, "Code")],
            ["Image"] = spaces[LookupKey(selection, "Image")]
        };
        if (!describedImages && !result["Image"].SupportsImages)
            throw new NotSupportedException($"The image selection is a text-only model: {selection.Image}. The current indexes were not changed.");
        foreach (var (kind, space) in result)
            if (space.External is { } provider && !provider.Kinds.Contains(kind == "Image" && describedImages ? "Document" : kind))
                throw new NotSupportedException($"The selected provider model is not qualified for {kind}. The current indexes were not changed.");
        return result;
    }
    private static ImagePipelineDescriptor Pipeline(ModelSelection selection, EmbeddingSpace embedder)
    {
        var model = ImageModelRecipes.Find(selection.Image) ?? throw new NotSupportedException("The image-understanding adapter is unavailable.");
        var options = selection.EffectiveImagePipeline;
        foreach (string task in options.Tasks) _ = model.Task(task);
        if (options.RegionTask.Length > 0 && model.Task(options.RegionTask).OutputKind != "regions")
            throw new ArgumentException("Choose a region-output task for regional image search.");
        return new(selection.Image, model, options.Tasks, embedder, selection.EmbeddingSelection().OptionsFor("Image"), selection.OptionsFor("Image").Device);
    }
    private ImageUnderstandingPool CreateImagePool(ImagePipelineDescriptor descriptor, CancellationToken cancellation) =>
        new(descriptor, compatibleHost, imageAssets.DirectoryFor(descriptor.Model), Math.Max(1, Math.Min(2, budget.Processors)),
            log, cancellation);
    public async Task<PreparedModels?> PrepareAsync(ModelSelection selection, Action<string> progress, CancellationToken cancellation)
    {
        selection = selection.Normalize();
        if (selection == Selection) return null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime?.Token ?? default);
        if (!selection.UsesImageUnderstanding && ImageUnderstanding is ImageUnderstandingPool previousImages)
            await previousImages.ReleaseIfIdleAsync();
        var encoded = selection.EmbeddingSelection();
        var spaces = new Dictionary<string, EmbeddingSpace>();
        foreach (string kind in new[] { "Document", "Code", "Image" })
        {
            string url = encoded.ForKind(kind), key = LookupKey(encoded, kind);
            if (spaces.ContainsKey(key)) continue;
            EmbeddingSpace? existing;
            lock (gate) existing = routes.Where(pair => pair.Value.ModelUrl == url &&
                (pair.Value.External is null || embeddingSelection.OptionsFor(pair.Key).Device == encoded.OptionsFor(kind).Device))
                .Select(pair => pair.Value).FirstOrDefault();
            if (ModelReference.Parse(url) is ProviderModelReference reference)
            {
                spaces[key] = existing ?? await external.ResolveAsync(reference, encoded.OptionsFor(kind), progress, linked.Token);
            }
            else spaces[key] = existing ?? cache.FindVerified(url, progress) ?? await cache.ResolveAsync(url, progress, linked.Token);
        }
        encoded = NormalizeSelection(encoded, spaces);
        selection = selection.WithEmbeddingOptions(encoded);
        if (selection == Selection) return null;
        var nextRoutes = BuildRoutes(encoded, spaces, selection.UsesImageUnderstanding);
        var next = new Dictionary<string, ModelPool>();
        var byKind = new Dictionary<string, ModelPool>();
        var created = new List<ModelPool>();
        ImageUnderstandingPool? generator = null;
        ImagePipelineDescriptor? pipeline = null;
        bool ownsGenerator = false;
        try
        {
            int count = nextRoutes.Select(pair => SessionKey(pair.Value, encoded.OptionsFor(pair.Key))).Distinct().Count();
            foreach (var (kind, space) in nextRoutes)
            {
                string key = SessionKey(space, encoded.OptionsFor(kind));
                if (next.TryGetValue(key, out var shared)) { byKind[kind] = shared; continue; }
                ModelPool? existing;
                lock (gate) existing = pools.GetValueOrDefault(key);
                if (existing is not null && existing.ReadyWorkers > 0) { next[key] = existing; byKind[kind] = existing; continue; }
                progress($"Checking local hardware and inference for {space.ModelUrl}");
                var pool = CreatePool(space, count, encoded.OptionsFor(kind));
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
                _ = await byKind[kind].QueryAsync("local model compatibility check", QueryFor(kind, nextRoutes[kind], encoded.OptionsFor(kind)), linked.Token);
            if (selection.UsesImageUnderstanding)
            {
                pipeline = Pipeline(selection, nextRoutes["Image"]);
                lock (gate) generator = imageUnderstanding;
                if (generator?.Descriptor.Model.Fingerprint != pipeline.Model.Fingerprint || generator.Descriptor.GenerationDevice != pipeline.GenerationDevice)
                {
                    if (generator is not null) await generator.ReleaseIfIdleAsync();
                    _ = await imageAssets.AcquireAsync(selection.Image, progress, linked.Token);
                    generator = CreateImagePool(pipeline, lifetime?.Token ?? cancellation); ownsGenerator = true;
                }
                await generator.ValidateAsync(progress, linked.Token);
            }
            return new(selection, encoded, nextRoutes, next, byKind, created, generator, pipeline, ownsGenerator);
        }
        catch
        {
            foreach (var pool in created) await pool.DisposeAsync();
            if (ownsGenerator && generator is not null) await generator.DisposeAsync();
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
        ImageUnderstandingPool? previousGenerator;
        lock (gate)
        {
            retired = pools.Values.Except(prepared.Pools.Values).ToArray();
            previousGenerator = imageUnderstanding;
            pools = prepared.Pools; routes = prepared.Routes; kindPools = prepared.KindPools; applied = prepared.Selection;
            embeddingSelection = prepared.EmbeddingSelection; imageUnderstanding = prepared.Generator;
            if (imageUnderstanding is not null && prepared.Pipeline is not null) imageUnderstanding.Rebind(prepared.Pipeline);
            prepared.Committed = true;
        }
        if (previousGenerator is not null && !ReferenceEquals(previousGenerator, imageUnderstanding)) await previousGenerator.DisposeAsync();
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
        }).ToArray(), catalog?.Setting("model_apply_message") ?? "", ImagePipeline: imageUnderstanding is { } generator
            ? new(generator.Descriptor.ModelUrl, generator.Descriptor.Embedder.ModelUrl, generator.Descriptor.Tasks,
                generator.Provider, generator.Device, IsLoaded: generator.IsLoaded) : null);
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
        if (imageUnderstanding is not null) await imageUnderstanding.DisposeAsync();
        lifetime?.Dispose();
    }

    public sealed class PreparedModels(ModelSelection selection, ModelSelection embeddingSelection, Dictionary<string, EmbeddingSpace> routes,
        Dictionary<string, ModelPool> pools, Dictionary<string, ModelPool> kindPools, List<ModelPool> created,
        ImageUnderstandingPool? generator, ImagePipelineDescriptor? pipeline, bool ownsGenerator) : IAsyncDisposable
    {
        public ModelSelection Selection { get; } = selection;
        public ModelSelection EmbeddingSelection { get; } = embeddingSelection;
        public Dictionary<string, EmbeddingSpace> Routes { get; } = routes;
        internal Dictionary<string, ModelPool> Pools { get; } = pools;
        internal Dictionary<string, ModelPool> KindPools { get; } = kindPools;
        internal ImageUnderstandingPool? Generator { get; } = generator;
        public ImagePipelineDescriptor? Pipeline { get; } = pipeline;
        internal bool Committed { get; set; }
        public async ValueTask DisposeAsync()
        {
            if (!Committed) foreach (var pool in created) await pool.DisposeAsync();
            if (!Committed && ownsGenerator && Generator is not null) await Generator.DisposeAsync();
        }
    }
}
