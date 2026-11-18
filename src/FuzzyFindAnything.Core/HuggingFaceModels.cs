using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FuzzyFindAnything.Core;

public sealed record CachedModel(EmbeddingSpace Space, ModelAssetFile[] Files, string? SourceUrl = null, string? SourceRevision = null)
{
    public const string ManifestName = "ffa-model.json";
    public void Validate()
    {
        Space.Validate();
        if (SourceUrl is not null) _ = ModelSelection.Canonical(SourceUrl);
        if (SourceRevision is not null && !Regex.IsMatch(SourceRevision, "^[0-9a-f]{40}$"))
            throw new InvalidDataException("Invalid source-model revision in the artifact manifest.");
        if (Space.Recipe is not null)
        {
            var registered = EmbeddingRecipes.Find(Space.ModelUrl);
            if (registered is not null && (Space.Revision != registered.Revision || Space.ArtifactUrl != registered.ArtifactUrl ||
                !Space.IsOriginalPublicFormat && Space.Recipe.ContractFingerprint != registered.Recipe.ContractFingerprint ||
                Space.Dimensions != registered.Dimensions ||
                Space.MaximumTokens != registered.MaximumTokens || Space.MemoryBytes < registered.MemoryBytes))
                throw new InvalidDataException("The cached model recipe differs from the reviewed registry. Reacquire this model before applying it.");
            if (registered is not null && (registered.Files.Any(name => Files.Count(file => file.Name == name) != 1) ||
                registered.Checksums.Any(pair => Files.SingleOrDefault(file => file.Name == pair.Key)?.Sha256 != pair.Value)))
                throw new InvalidDataException("The cached artifact manifest differs from its reviewed files/checksums.");
            if (registered is null && Space.Adapter != "gemma2-onnx" && Space.Recipe.ContractFingerprint !=
                EmbeddingRecipes.StandardBert(Space.Recipe.Pooling, Space.Recipe.Output, Space.MaximumTokens).ContractFingerprint)
                throw new NotSupportedException("The cached model recipe is not registered or a supported standard BERT contract.");
        }
        if (Files.Length is < 1 or > 100 || Files.Select(file => file.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Files.Length ||
            Files.Sum(file => file.Bytes) > 12L << 30)
            throw new InvalidDataException("The cache manifest has invalid asset counts or sizes.");
        foreach (var file in Files)
        {
            _ = LocalName(file.Name);
            if (file.Bytes is <= 0 or > 4L * 1024 * 1024 * 1024 || !Regex.IsMatch(file.Sha256, "^[0-9a-f]{64}$") ||
                file.Revision is not null && !Regex.IsMatch(file.Revision, "^[0-9a-f]{40}$"))
                throw new InvalidDataException("The cache manifest has an invalid asset identity.");
            if (file.SourceUrl is not null) _ = ModelSelection.Canonical(file.SourceUrl);
        }
        if (Space.IsOriginalPublicFormat) OriginalGemma2Contract.ValidateManifest(this);
        else if (Space.Adapter == "gemma2-onnx")
        {
            var approved = EmbeddingRecipes.PublicDefault;
            if (Space.Dimensions != 768 || Space.MemoryBytes < 3L << 30 ||
                Space.Recipe?.ContractFingerprint != approved.Recipe.ContractFingerprint ||
                Space.AssetIdentity != Gemma2ModelContract.AssetIdentity(Files) ||
                approved.Files.Any(name => Files.Count(file => file.Name == name) != 1))
                throw new InvalidDataException("The Gemma2 asset/processing identity is incomplete.");
            var supplemental = approved.Supplemental!.Single();
            var tokenizer = Files.Single(file => file.Name == supplemental.Name);
            if (tokenizer.Sha256 != supplemental.Sha256 || tokenizer.Bytes != supplemental.Bytes ||
                tokenizer.SourceUrl != supplemental.SourceUrl || tokenizer.Revision != supplemental.Revision ||
                Files.Single(file => file.Name == "tokenizer.json").Sha256 != approved.Checksums["tokenizer.json"])
                throw new NotSupportedException("This Gemma2 export does not use the qualified pinned SentencePiece tokenizer.");
        }
    }
    internal ModelAssetFile ResolveSource(ModelAssetFile file) => Space.IsOriginalPublicFormat
        ? OriginalGemma2Contract.ResolveSource(Space, file) : file;
    public void Verify(string directory, CancellationToken cancellation = default)
    {
        Validate();
        foreach (var file in Files)
        {
            cancellation.ThrowIfCancellationRequested();
            string path = Path.Combine(directory, LocalName(file.Name));
            if (!File.Exists(path) || new FileInfo(path).Length != file.Bytes || ModelAssets.Hash(path, cancellation) != file.Sha256)
                throw new InvalidDataException($"Model asset is missing or changed: {file.Name}. Apply the model configuration again to repair the cache.");
        }
        if (Space.Adapter == "gemma2-onnx")
        {
            Gemma2ModelContract.ValidateMetadata(directory);
            Gemma2ModelContract.ValidateGraphs(directory, Space.Recipe!);
        }
    }
    internal static string LocalName(string name)
    {
        if (name.Length == 0 || name.Contains('\\') || name.Any(char.IsControl) ||
            name.IndexOfAny([':', '*', '?', '"', '<', '>', '|']) >= 0 ||
            name.Split('/').Any(part => part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')))
            throw new InvalidDataException("Invalid model asset path.");
        return name.Replace('/', '\\');
    }
}

public sealed class HuggingFaceModels(string cacheDirectory, string defaultDirectory)
{
    private const long MaximumAssetBytes = 4L * 1024 * 1024 * 1024;
    private static readonly HttpClient client = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.None,
        SslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13
    })
    { Timeout = TimeSpan.FromMinutes(20) };
    static HuggingFaceModels() => client.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));

    private sealed record Candidate(EmbeddingSpace Space, string ArtifactUrl, JsonElement[] Assets,
        List<string> Names, RegisteredEmbedding? Registered);

    public string DirectoryFor(EmbeddingSpace space) => space.UsesLegacyHost ? defaultDirectory : Path.Combine(cacheDirectory, space.CacheKey);

    public async Task<ModelRequirement> InspectAsync(string kind, string url, CancellationToken cancellation,
        EmbeddingSpace? saved = null)
    {
        url = ModelSelection.Canonical(url);
        try
        {
            saved?.Validate();
            if (saved is not null && saved.ModelUrl != url)
                throw new InvalidDataException("The saved descriptor does not match the inspected model.");
            EmbeddingSpace? cached = saved;
            if (cached is null)
            {
                try { cached = FindCached(url); }
                catch (FileNotFoundException) { }
            }
            var space = cached ?? (await DiscoverAsync(url, _ => { }, cancellation)).Space;
            if (kind == "Image" && !space.SupportsImages)
                throw new NotSupportedException("This model provides text embeddings, not direct image embeddings. The image-understanding pipeline requires a separate adapter.");
            var recipe = space.Recipe;
            var entry = space.UsesLegacyHost ? null : EmbeddingRecipes.Find(url);
            string details = recipe is null ? $"{space.Dimensions} dimensions. Existing native tokenizer and preprocessing are automatic." :
                $"{recipe.Id}; {space.Dimensions} dimensions; {space.MaximumTokens} input tokens; {recipe.Pooling} pooling. " +
                $"Native export: {space.ArtifactUrl ?? space.ModelUrl}@{space.Revision}. Tokenizer and query formatting are automatic. " +
                "Local device qualification runs before Apply is accepted.";
            if (entry?.SourceRevision is { } sourceRevision)
                details += $" Source checkpoint: {entry.SourceUrl ?? entry.ModelUrls[0]}@{sourceRevision}.";
            return new(kind, url, "Available", recipe?.DefaultQuery(kind) == "choose"
                    ? "Choose a query purpose required by this model." : "No additional model setup is required.", details,
                recipe?.QueryProfiles.ToArray() ?? [], recipe?.DefaultQuery(kind) ?? "default", recipe is null);
        }
        catch (Exception error) when (error is NotSupportedException or InvalidDataException or HttpRequestException or IOException)
        {
            return new(kind, url, error is HttpRequestException or IOException ? "Unavailable" : "Unsupported",
                "This model is not ready to apply.", "", [], "", true, error.Message);
        }
    }

    public async Task<EmbeddingSpace> ResolveAsync(string modelUrl, Action<string> report, CancellationToken cancellation,
        EmbeddingSpace? saved = null)
    {
        modelUrl = ModelSelection.Canonical(modelUrl);
        if (saved?.UsesLegacyHost == true)
        {
            ModelAssets.Verify(defaultDirectory, (_, _, name) => report("Verifying " + name), cancellation);
            return saved;
        }
        Directory.CreateDirectory(cacheDirectory);
        using var lease = await AcquireLeaseAsync(report, cancellation);
        EmbeddingSpace? cached = saved;
        try { cached ??= FindCached(modelUrl); }
        catch (FileNotFoundException) { }
        if (cached is not null)
        {
            string directory = DirectoryFor(cached);
            string manifestPath = Path.Combine(directory, CachedModel.ManifestName);
            if (File.Exists(manifestPath))
            {
                var existing = Load(directory);
                existing.Validate();
                if (saved is not null && existing.Space.Fingerprint != saved.Fingerprint)
                    throw new InvalidDataException("The saved route does not match the cached model identity.");
                try
                {
                    report("Verifying cached model assets");
                    existing.Verify(directory, cancellation);
                    return existing.Space with { ModelUrl = modelUrl };
                }
                catch (Exception error) when (error is FileNotFoundException or InvalidDataException)
                {
                    report("Repairing assets at their saved immutable revision: " + error.Message);
                    return await RepairAsync(existing, modelUrl, report, cancellation);
                }
            }
        }
        var candidate = await DiscoverAsync(modelUrl, report, cancellation, cached?.Revision);
        var space = candidate.Space;
        var siblings = candidate.Assets.ToDictionary(item => item.GetProperty("rfilename").GetString()!, StringComparer.Ordinal);
        var names = candidate.Names;
        long total = names.Sum(name => siblings.TryGetValue(name, out var item) && item.TryGetProperty("size", out var size) ? size.GetInt64() : 0);
        if (total > 12L * 1024 * 1024 * 1024) throw new InvalidDataException("The model exceeds the total native artifact size limit.");
        string staging = Path.Combine(cacheDirectory, ".acquiring-" + space.CacheKey);
        Directory.CreateDirectory(staging);
        var files = new List<ModelAssetFile>();
        async Task Download(string name)
        {
            if (files.Any(file => file.Name == name)) return;
            var supplemental = (candidate.Registered?.Supplemental ??
                (space.Adapter == "gemma2-onnx" ? EmbeddingRecipes.PublicDefault.Supplemental : null))?.SingleOrDefault(asset => asset.Name == name);
            if (supplemental is not null)
            {
                var acquired = await DownloadAsync(AssetUri(supplemental.SourceUrl, supplemental.Revision, name), staging, name,
                    supplemental.Bytes, supplemental.Sha256, report, cancellation);
                files.Add(acquired with { SourceUrl = supplemental.SourceUrl, Revision = supplemental.Revision });
                return;
            }
            if (!siblings.TryGetValue(name, out var item)) throw new InvalidDataException($"Required model file is missing: {name}.");
            long size = item.TryGetProperty("size", out var length) ? length.GetInt64() :
                item.TryGetProperty("lfs", out var lfsSize) ? lfsSize.GetProperty("size").GetInt64() : -1;
            string? hash = candidate.Registered?.Checksums.GetValueOrDefault(name) ??
                (item.TryGetProperty("lfs", out var lfs) && lfs.TryGetProperty("sha256", out var sha) ? sha.GetString() : null);
            if (candidate.Registered?.Sizes?.TryGetValue(name, out long pinnedSize) == true && size != pinnedSize ||
                hash is not null && item.TryGetProperty("lfs", out var sourceHash) && sourceHash.GetProperty("sha256").GetString() != hash)
                throw new InvalidDataException("The source metadata differs from the pinned asset identity: " + name);
            if (files.Sum(file => file.Bytes) + Math.Max(0, size) > 12L * 1024 * 1024 * 1024)
                throw new InvalidDataException("The model exceeds the total native artifact size limit.");
            files.Add((await DownloadAsync(AssetUri(candidate.ArtifactUrl, space.Revision, name), staging, name, size, hash, report, cancellation))
                with
            { SourceUrl = candidate.ArtifactUrl, Revision = space.Revision });
        }
        foreach (string name in names) await Download(name);
        foreach (string graph in names.Where(name => name.EndsWith(".onnx", StringComparison.Ordinal)).ToArray())
        {
            var metadata = OnnxMetadata.Read(Path.Combine(staging, CachedModel.LocalName(graph)));
            foreach (var data in metadata.ExternalData)
            {
                string prefix = graph.Contains('/') ? graph[..(graph.LastIndexOf('/') + 1)] : "";
                string asset = prefix + data.Location;
                _ = CachedModel.LocalName(asset);
                await Download(asset);
                long size = files.Single(file => file.Name == asset).Bytes;
                if (data.Offset > size || data.Length is { } bytes && bytes > size - data.Offset)
                    throw new InvalidDataException("ONNX external weights exceed their declared artifact bounds.");
            }
            if (space.Adapter == "bert-mean" && !metadata.Outputs.Any(value => value.Name == "last_hidden_state"))
            {
                var pooled = metadata.Outputs.SingleOrDefault(value =>
                    value.Name is "sentence_embedding" or "sentence_embeddings" && value.Dimensions.Length == 2 &&
                    value.Dimensions[^1] == space.Dimensions && value.ElementType == 1);
                if (pooled is null) throw new NotSupportedException("The BERT export has no supported token or pooled embedding output. A reviewed adapter is required.");
                space = space with
                {
                    Adapter = "native-text",
                    ArtifactUrl = candidate.ArtifactUrl,
                    Recipe = EmbeddingRecipes.StandardBert("pooled", pooled.Name, space.MaximumTokens)
                };
            }
        }
        long payload = files.Sum(file => file.Bytes);
        space = space with { MemoryBytes = Math.Max(space.MemoryBytes, payload + (512L << 20)) };
        if (saved?.IsOriginalPublicFormat == true)
        {
            saved.Validate();
            space = saved with { MemoryBytes = Math.Max(saved.MemoryBytes, space.MemoryBytes) };
        }
        else if (space.Adapter == "gemma2-onnx") space = space with { AssetIdentity = Gemma2ModelContract.AssetIdentity(files) };
        space.Validate();
        if (saved is not null && space.Fingerprint != saved.Fingerprint)
            throw new InvalidDataException("The repaired assets differ from the saved embedding space; no route was changed.");
        var manifest = new CachedModel(space, files.ToArray(), candidate.Registered?.SourceUrl ?? candidate.Registered?.ModelUrls[0] ?? modelUrl,
            candidate.Registered?.SourceRevision ?? space.Revision);
        manifest.Verify(staging, cancellation);
        await PublishAsync(manifest, staging, cancellation);
        return space;
    }

    private async Task<FileStream> AcquireLeaseAsync(Action<string> report, CancellationToken cancellation)
    {
        bool waiting = false;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            try { return new FileStream(Path.Combine(cacheDirectory, ".acquisition.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
            {
                if (!waiting) { report("Waiting for another model acquisition to finish"); waiting = true; }
                await Task.Delay(200, cancellation);
            }
        }
    }

    private async Task<EmbeddingSpace> RepairAsync(CachedModel manifest, string url, Action<string> report, CancellationToken cancellation)
    {
        string staging = Path.Combine(cacheDirectory, ".acquiring-" + manifest.Space.CacheKey);
        Directory.CreateDirectory(staging);
        foreach (var file in manifest.Files)
        {
            cancellation.ThrowIfCancellationRequested();
            string existing = Path.Combine(DirectoryFor(manifest.Space), CachedModel.LocalName(file.Name));
            string target = Path.Combine(staging, CachedModel.LocalName(file.Name));
            if (File.Exists(existing) && new FileInfo(existing).Length == file.Bytes && ModelAssets.Hash(existing, cancellation) == file.Sha256)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(existing, target, true);
            }
            else
            {
                var source = manifest.ResolveSource(file);
                await DownloadAsync(AssetUri(source.SourceUrl ?? manifest.Space.ArtifactUrl ?? manifest.Space.ModelUrl,
                    source.Revision ?? manifest.Space.Revision, file.Name), staging, file.Name, file.Bytes, file.Sha256, report, cancellation);
            }
        }
        manifest.Verify(staging, cancellation);
        await PublishAsync(manifest, staging, cancellation);
        return manifest.Space with { ModelUrl = url };
    }

    private async Task PublishAsync(CachedModel manifest, string staging, CancellationToken cancellation)
    {
        var space = manifest.Space;
        string directory = DirectoryFor(space);
        Directory.CreateDirectory(directory);
        foreach (var file in manifest.Files)
        {
            string destination = Path.Combine(directory, CachedModel.LocalName(file.Name));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(Path.Combine(staging, CachedModel.LocalName(file.Name)), destination, true);
        }
        string target = Path.Combine(directory, CachedModel.ManifestName), temporary = target + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(manifest, Protocol.Json), cancellation);
        File.Move(temporary, target, true);
        Directory.Delete(staging, true);
    }

    private static async Task<Candidate> DiscoverAsync(string modelUrl, Action<string> report, CancellationToken cancellation, string? savedRevision = null)
    {
        var registered = EmbeddingRecipes.Find(modelUrl);
        string artifactUrl = registered?.ArtifactUrl ?? modelUrl;
        string id = new Uri(artifactUrl).AbsolutePath.TrimStart('/');
        report($"Inspecting {id}");
        string? pinned = registered?.Revision ?? savedRevision;
        string suffix = pinned is null ? "?blobs=true" : $"/revision/{pinned}?blobs=true";
        using var metadata = await JsonAsync(new Uri($"https://huggingface.co/api/models/{id}{suffix}"), cancellation);
        string revision = metadata.RootElement.GetProperty("sha").GetString()!;
        if (!Regex.IsMatch(revision, "^[0-9a-f]{40}$")) throw new InvalidDataException("Hugging Face returned an invalid model revision.");
        if (registered is not null && revision != registered.Revision) throw new InvalidDataException("The pinned native artifact revision changed.");
        using var config = await JsonAsync(AssetUri(artifactUrl, revision, "config.json"), cancellation);
        string type = config.RootElement.GetProperty("model_type").GetString() ?? "";
        EmbeddingSpace space;
        var names = new List<string> { "config.json", "tokenizer.json", "tokenizer_config.json" };
        var root = config.RootElement;
        if (registered is not null)
        {
            if (type != registered.ModelType || (registered.Adapter == "gemma2-onnx"
                ? root.GetProperty("text_config").GetProperty("embedding_dim").GetInt32()
                : root.GetProperty("hidden_size").GetInt32()) != registered.Dimensions)
                throw new InvalidDataException("The native artifact does not match its reviewed embedding recipe.");
            space = new(modelUrl, revision, registered.Adapter, registered.Dimensions, registered.MaximumTokens, registered.SupportsImages,
                registered.MemoryBytes, registered.Recipe, artifactUrl);
            names = registered.Files.ToList();
        }
        else if (type == "embedding_gemma2")
        {
            var contract = EmbeddingRecipes.PublicDefault;
            space = new(modelUrl, revision, contract.Adapter, contract.Dimensions, contract.MaximumTokens, true,
                contract.MemoryBytes, contract.Recipe, artifactUrl);
            names = contract.Files.ToList();
        }
        else if (type == "bert")
        {
            using var pooling = await JsonAsync(AssetUri(modelUrl, revision, "1_Pooling/config.json"), cancellation);
            string[] enabled = pooling.RootElement.EnumerateObject().Where(property =>
                property.Name.StartsWith("pooling_mode_", StringComparison.Ordinal) && property.Value.ValueKind == JsonValueKind.True)
                .Select(property => property.Name).ToArray();
            if (enabled.Length != 1 || enabled[0] is not ("pooling_mode_mean_tokens" or "pooling_mode_cls_token"))
                throw new NotSupportedException("This BERT pooling pipeline requires a reviewed native adapter.");
            using var modules = await OptionalJsonAsync(AssetUri(modelUrl, revision, "modules.json"), cancellation);
            if (modules is not null && modules.RootElement.EnumerateArray().Any(item =>
                item.GetProperty("type").GetString()?.Split('.').Last() is not ("Transformer" or "Pooling" or "Normalize")))
                throw new NotSupportedException("This model has additional transformation/projection modules. A reviewed embedding recipe is required.");
            using var sentenceConfig = await OptionalJsonAsync(AssetUri(modelUrl, revision, "config_sentence_transformers.json"), cancellation);
            if (sentenceConfig is not null && sentenceConfig.RootElement.TryGetProperty("prompts", out var prompts) &&
                prompts.ValueKind == JsonValueKind.Object && prompts.EnumerateObject().Any(value => !string.IsNullOrEmpty(value.Value.GetString())))
                throw new NotSupportedException("This model declares retrieval prompts. A reviewed role-aware embedding recipe is required.");
            if (sentenceConfig is not null && sentenceConfig.RootElement.TryGetProperty("similarity_fn_name", out var metric) &&
                metric.ValueKind == JsonValueKind.String && metric.GetString() is not ("cosine" or null) &&
                !(metric.GetString() == "dot" && modules is not null && modules.RootElement.EnumerateArray().Any(item =>
                    item.GetProperty("type").GetString()?.EndsWith(".Normalize", StringComparison.Ordinal) == true)))
                throw new NotSupportedException("This model requires a non-cosine scoring contract. A reviewed metric-aware adapter is required.");
            int dimensions = root.GetProperty("hidden_size").GetInt32();
            int maximum = Math.Min(512, root.GetProperty("max_position_embeddings").GetInt32());
            using var sentence = await OptionalJsonAsync(AssetUri(modelUrl, revision, "sentence_bert_config.json"), cancellation);
            if (sentence is not null && sentence.RootElement.TryGetProperty("max_seq_length", out var sequence))
            {
                maximum = Math.Min(maximum, sequence.GetInt32());
                names.Add("sentence_bert_config.json");
            }
            space = new(modelUrl, revision, "bert-mean", dimensions, maximum, false, 768L << 20);
            if (enabled[0] == "pooling_mode_cls_token")
                space = space with
                {
                    Adapter = "native-text",
                    ArtifactUrl = artifactUrl,
                    Recipe = EmbeddingRecipes.StandardBert("cls", "last_hidden_state", maximum)
                };
            names.AddRange(["vocab.txt", "1_Pooling/config.json", "onnx/model.onnx"]);
        }
        else if (type == "clip")
        {
            using var processor = await JsonAsync(AssetUri(modelUrl, revision, "preprocessor_config.json"), cancellation);
            int dimensions = root.GetProperty("projection_dim").GetInt32();
            int maximum = root.GetProperty("text_config").GetProperty("max_position_embeddings").GetInt32();
            var preprocessing = processor.RootElement;
            if (!preprocessing.GetProperty("do_resize").GetBoolean() || !preprocessing.GetProperty("do_center_crop").GetBoolean() ||
                !preprocessing.GetProperty("do_normalize").GetBoolean() || preprocessing.GetProperty("resample").GetInt32() != 3)
                throw new NotSupportedException("This CLIP image processor is not compatible with the native resize/crop adapter.");
            space = new(modelUrl, revision, "clip", dimensions, maximum, true, 2L << 30);
            names.AddRange(["vocab.json", "merges.txt", "preprocessor_config.json", "onnx/text_model.onnx", "onnx/vision_model.onnx"]);
        }
        else
            throw new NotSupportedException($"Model type '{type}' needs a reviewed native embedding recipe/adapter. Generation models are not embedding models. No configuration or index was changed.");
        space.Validate();
        var assets = metadata.RootElement.GetProperty("siblings").EnumerateArray().Select(item => item.Clone()).ToArray();
        var siblings = assets.Select(item => item.GetProperty("rfilename").GetString()!).ToHashSet(StringComparer.Ordinal);
        foreach (string name in names)
            if (!siblings.Contains(name) && !(space.Adapter == "gemma2-onnx" &&
                EmbeddingRecipes.PublicDefault.Supplemental!.Any(asset => asset.Name == name)))
                throw new NotSupportedException($"The Hugging Face repository does not provide {name}. A compatible native export is required; remote model code is never executed.");
        foreach (string optional in new[] { "README.md", "LICENSE", "LICENSE.txt", "LICENSE.md" })
            if (siblings.Contains(optional) && !names.Contains(optional)) names.Add(optional);
        return new(space, artifactUrl, assets, names, registered);
    }

    public EmbeddingSpace FindCached(string url)
    {
        url = ModelSelection.Canonical(url);
        if (Directory.Exists(cacheDirectory))
            foreach (string directory in Directory.EnumerateDirectories(cacheDirectory))
            {
                string path = Path.Combine(directory, CachedModel.ManifestName);
                if (!File.Exists(path)) continue;
                var model = Load(directory);
                if (Path.GetFileName(directory) != model.Space.CacheKey) continue;
                var registered = EmbeddingRecipes.Find(url);
                if (model.Space.ModelUrl == url || registered is not null &&
                    model.Space.ArtifactUrl == registered.ArtifactUrl && model.Space.Revision == registered.Revision &&
                    (model.Space.IsOriginalPublicFormat || model.Space.Recipe?.ContractFingerprint == registered.Recipe.ContractFingerprint))
                    return model.Space with { ModelUrl = url };
            }
        throw new FileNotFoundException($"The selected model is not cached: {url}. Apply the model configuration while connected to Hugging Face.");
    }

    public EmbeddingSpace? FindVerified(string url, Action<string> report)
    {
        try
        {
            var space = FindCached(url);
            if (space.UsesLegacyHost) ModelAssets.Verify(DirectoryFor(space));
            else Load(DirectoryFor(space)).Verify(DirectoryFor(space));
            return space;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException or InvalidDataException)
        {
            report("Acquiring model assets: " + error.Message);
            return null;
        }
    }

    public static CachedModel Load(string directory)
    {
        string path = Path.Combine(directory, CachedModel.ManifestName);
        if (new FileInfo(path).Length > 4_194_304) throw new InvalidDataException("The model cache manifest exceeds its size limit.");
        var model = JsonSerializer.Deserialize<CachedModel>(File.ReadAllText(path), Protocol.Json)
            ?? throw new InvalidDataException("Invalid model manifest.");
        model.Space.Validate();
        return model;
    }

    internal static Uri AssetUri(string url, string revision, string file) =>
        new($"{url}/resolve/{revision}/{string.Join("/", file.Split('/').Select(Uri.EscapeDataString))}");

    internal static async Task<JsonDocument> JsonAsync(Uri uri, CancellationToken cancellation) =>
        await OptionalJsonAsync(uri, cancellation) ?? throw new NotSupportedException($"Required native model metadata was not found: {uri.AbsolutePath}.");

    private static async Task<JsonDocument?> OptionalJsonAsync(Uri uri, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        cancellation = deadline.Token;
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellation);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(cancellation);
        using var buffer = new MemoryStream();
        await CopyAsync(input, buffer, 8 * 1024 * 1024, cancellation);
        return JsonDocument.Parse(buffer.ToArray());
    }

    internal static async Task<ModelAssetFile> DownloadAsync(Uri uri, string directory, string name, long expected,
        string? expectedHash, Action<string> report, CancellationToken cancellation)
    {
        if (!uri.IsAbsoluteUri || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 ||
            uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback))
            throw new ArgumentException("Model assets require an HTTPS URL without credentials or fragments.");
        if (expected is 0 or < -1 || expected > MaximumAssetBytes ||
            expectedHash is not null && !Regex.IsMatch(expectedHash, "^[0-9a-f]{64}$"))
            throw new InvalidDataException("The model asset has an invalid size or checksum.");
        cancellation.ThrowIfCancellationRequested();
        string target = Path.Combine(directory, CachedModel.LocalName(name));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(target) && expected > 0 && new FileInfo(target).Length == expected &&
            expectedHash is not null && ModelAssets.Hash(target, cancellation) == expectedHash)
            return new(name, expected, expectedHash);
        string partial = target + ".partial";
        long offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (expected > 0 && offset == expected && expectedHash is not null && ModelAssets.Hash(partial, cancellation) == expectedHash)
        {
            File.Move(partial, target, true);
            report($"Verified {name}");
            return new(name, expected, expectedHash);
        }
        if (expected > 0 && offset >= expected) { File.Delete(partial); offset = 0; }
        if (offset > MaximumAssetBytes) { File.Delete(partial); offset = 0; }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromMinutes(30));
        cancellation = deadline.Token;
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
        report($"Downloading {name}");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
        response.EnsureSuccessStatusCode();
        bool ranged = response.StatusCode == HttpStatusCode.PartialContent;
        if (ranged && (response.Content.Headers.ContentRange is not { } range ||
            range.Unit != "bytes" || range.From != offset || range.Length is not { } full || full > MaximumAssetBytes ||
            expected > 0 && full != expected || range.To != full - 1))
            throw new InvalidDataException("The model host returned an invalid resume range.");
        if (!ranged && response.StatusCode != HttpStatusCode.OK)
            throw new InvalidDataException("The model host returned no asset content.");
        bool append = ranged && offset > 0;
        long start = append ? offset : 0;
        if (response.Content.Headers.ContentLength is { } contentLength &&
            (contentLength > MaximumAssetBytes - start || expected > 0 && contentLength != expected - start))
            throw new InvalidDataException("The model host returned an unexpected asset length.");
        long maximum = expected > 0 ? expected : MaximumAssetBytes;
        try
        {
            await using var input = await response.Content.ReadAsStreamAsync(cancellation);
            await using var output = new FileStream(partial, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None);
            long lastReport = 0;
            await CopyAsync(input, output, maximum - start, cancellation, bytes =>
            {
                if (Environment.TickCount64 - lastReport < 1000) return;
                lastReport = Environment.TickCount64;
                long completed = bytes + start;
                report(expected > 0 ? $"Downloading {name}: {completed * 100 / expected}% ({completed / (1024 * 1024):N0} MiB)"
                    : $"Downloading {name}: {completed / (1024 * 1024):N0} MiB");
            });
            output.Flush(true);
        }
        catch (InvalidDataException) { File.Delete(partial); throw; }
        long actual = new FileInfo(partial).Length;
        if (expected > 0 && actual < expected)
            throw new IOException("The model transfer was interrupted; retry to resume the retained partial download.");
        report($"Verifying {name}");
        string digest = ModelAssets.Hash(partial, cancellation);
        if (expectedHash is not null && digest != expectedHash)
        {
            File.Delete(partial);
            throw new InvalidDataException($"The downloaded asset failed integrity verification: {name}.");
        }
        File.Move(partial, target, true);
        return new(name, actual, digest);
    }

    private static async Task CopyAsync(Stream input, Stream output, long maximum, CancellationToken cancellation, Action<long>? progress = null)
    {
        byte[] buffer = new byte[1024 * 1024];
        long count = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellation)) != 0)
        {
            count += read;
            if (count > maximum) throw new InvalidDataException("The response exceeds its model-asset size limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellation);
            progress?.Invoke(count);
        }
    }
}
