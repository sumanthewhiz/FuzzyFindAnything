using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FuzzyFindAnything.Core;

public sealed record CachedModel(EmbeddingSpace Space, ModelAssetFile[] Files)
{
    public const string ManifestName = "ffa-model.json";
    public void Verify(string directory, CancellationToken cancellation = default)
    {
        Space.Validate();
        foreach (var file in Files)
        {
            cancellation.ThrowIfCancellationRequested();
            string path = Path.Combine(directory, LocalName(file.Name));
            if (!File.Exists(path) || new FileInfo(path).Length != file.Bytes || FilePaths.Hash(path) != file.Sha256)
                throw new InvalidDataException($"Model asset is missing or changed: {file.Name}. Apply the model configuration again to repair the cache.");
        }
    }
    internal static string LocalName(string name)
    {
        if (name.Length == 0 || name.Contains('\\') || name.Contains(':') ||
            name.Split('/').Any(part => part.Length == 0 || part is "." or ".."))
            throw new InvalidDataException("Invalid model asset path.");
        return name.Replace('/', '\\');
    }
}

public sealed class HuggingFaceModels(string cacheDirectory, string defaultDirectory)
{
    private const long MaximumAssetBytes = 4L * 1024 * 1024 * 1024;
    private static readonly HttpClient client = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        SslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13
    })
    { Timeout = TimeSpan.FromMinutes(20) };

    public string DirectoryFor(EmbeddingSpace space) => space == EmbeddingSpace.Default ? defaultDirectory : Path.Combine(cacheDirectory, space.Key);

    public async Task<EmbeddingSpace> ResolveAsync(string modelUrl, Action<string> report, CancellationToken cancellation)
    {
        modelUrl = ModelSelection.Canonical(modelUrl);
        if (modelUrl == ModelSelection.DefaultUrl) return EmbeddingSpace.Default;
        string id = new Uri(modelUrl).AbsolutePath.TrimStart('/');
        report($"Inspecting {id}");
        using var metadata = await JsonAsync(new Uri($"https://huggingface.co/api/models/{id}?blobs=true"), cancellation);
        string revision = metadata.RootElement.GetProperty("sha").GetString()!;
        if (!Regex.IsMatch(revision, "^[0-9a-f]{40}$")) throw new InvalidDataException("Hugging Face returned an invalid model revision.");
        using var config = await JsonAsync(AssetUri(modelUrl, revision, "config.json"), cancellation);
        string type = config.RootElement.GetProperty("model_type").GetString() ?? "";
        EmbeddingSpace space;
        var names = new List<string> { "config.json", "tokenizer.json", "tokenizer_config.json" };
        var root = config.RootElement;
        if (type == "bert")
        {
            using var pooling = await JsonAsync(AssetUri(modelUrl, revision, "1_Pooling/config.json"), cancellation);
            if (!pooling.RootElement.TryGetProperty("pooling_mode_mean_tokens", out var mean) || !mean.GetBoolean() ||
                pooling.RootElement.EnumerateObject().Any(property => property.Name.StartsWith("pooling_mode_", StringComparison.Ordinal) &&
                    property.Name != "pooling_mode_mean_tokens" && property.Value.ValueKind == JsonValueKind.True))
                throw new NotSupportedException("This BERT model requires a pooling strategy not supported by the native embedding adapter.");
            int dimensions = root.GetProperty("hidden_size").GetInt32();
            int maximum = Math.Min(512, root.GetProperty("max_position_embeddings").GetInt32());
            using var sentence = await OptionalJsonAsync(AssetUri(modelUrl, revision, "sentence_bert_config.json"), cancellation);
            if (sentence is not null && sentence.RootElement.TryGetProperty("max_seq_length", out var sequence))
            {
                maximum = Math.Min(maximum, sequence.GetInt32());
                names.Add("sentence_bert_config.json");
            }
            space = new(modelUrl, revision, "bert-mean", dimensions, maximum, false, 768L << 20);
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
            throw new NotSupportedException($"Model type '{type}' has no compatible native embedding adapter. Supported: EmbeddingGemma2, mean-pooled BERT ONNX and CLIP ONNX. No index was changed.");
        space.Validate();
        var siblings = metadata.RootElement.GetProperty("siblings").EnumerateArray()
            .ToDictionary(item => item.GetProperty("rfilename").GetString()!, item => item.Clone(), StringComparer.Ordinal);
        foreach (string graph in names.Where(name => name.EndsWith(".onnx", StringComparison.Ordinal)).ToArray())
        {
            if (!siblings.ContainsKey(graph))
                throw new NotSupportedException($"The Hugging Face repository does not provide {graph}. Use a compatible ONNX export repository; remote model code is never executed.");
            if (siblings.ContainsKey(graph + "_data")) names.Add(graph + "_data");
            if (siblings.ContainsKey(graph + ".data")) names.Add(graph + ".data");
        }
        foreach (string optional in new[] { "README.md", "LICENSE", "LICENSE.txt", "LICENSE.md" })
            if (siblings.ContainsKey(optional)) names.Add(optional);
        string directory = DirectoryFor(space);
        Directory.CreateDirectory(directory);
        var files = new List<ModelAssetFile>();
        foreach (string name in names)
        {
            if (!siblings.TryGetValue(name, out var item)) throw new InvalidDataException($"Required model file is missing: {name}.");
            long size = item.TryGetProperty("size", out var length) ? length.GetInt64() :
                item.TryGetProperty("lfs", out var lfsSize) ? lfsSize.GetProperty("size").GetInt64() : -1;
            string? hash = item.TryGetProperty("lfs", out var lfs) && lfs.TryGetProperty("sha256", out var sha)
                ? sha.GetString() : null;
            files.Add(await DownloadAsync(AssetUri(modelUrl, revision, name), directory, name, size, hash, report, cancellation));
        }
        var manifest = new CachedModel(space, files.ToArray());
        manifest.Verify(directory, cancellation);
        string target = Path.Combine(directory, CachedModel.ManifestName);
        string temporary = target + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(manifest, Protocol.Json), cancellation);
        File.Move(temporary, target, true);
        return space;
    }

    public EmbeddingSpace FindCached(string url)
    {
        url = ModelSelection.Canonical(url);
        if (url == ModelSelection.DefaultUrl) return EmbeddingSpace.Default;
        if (Directory.Exists(cacheDirectory))
            foreach (string directory in Directory.EnumerateDirectories(cacheDirectory))
            {
                string path = Path.Combine(directory, CachedModel.ManifestName);
                if (!File.Exists(path)) continue;
                var model = JsonSerializer.Deserialize<CachedModel>(File.ReadAllText(path), Protocol.Json)
                    ?? throw new InvalidDataException("Invalid cached model manifest.");
                model.Space.Validate();
                if (model.Space.ModelUrl == url && Path.GetFileName(directory) == model.Space.Key) return model.Space;
            }
        throw new FileNotFoundException($"The selected model is not cached: {url}. Apply the model configuration while connected to Hugging Face.");
    }

    public EmbeddingSpace? FindVerified(string url, Action<string> report)
    {
        try
        {
            var space = FindCached(url);
            if (space != EmbeddingSpace.Default) Load(DirectoryFor(space)).Verify(DirectoryFor(space));
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
        var model = JsonSerializer.Deserialize<CachedModel>(File.ReadAllText(Path.Combine(directory, CachedModel.ManifestName)), Protocol.Json)
            ?? throw new InvalidDataException("Invalid model manifest.");
        model.Space.Validate();
        return model;
    }

    private static Uri AssetUri(string url, string revision, string file) =>
        new($"{url}/resolve/{revision}/{string.Join("/", file.Split('/').Select(Uri.EscapeDataString))}");

    private static async Task<JsonDocument> JsonAsync(Uri uri, CancellationToken cancellation) =>
        await OptionalJsonAsync(uri, cancellation) ?? throw new NotSupportedException($"Required native model metadata was not found: {uri.AbsolutePath}.");

    private static async Task<JsonDocument?> OptionalJsonAsync(Uri uri, CancellationToken cancellation)
    {
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellation);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(cancellation);
        using var buffer = new MemoryStream();
        await CopyAsync(input, buffer, 8 * 1024 * 1024, cancellation);
        return JsonDocument.Parse(buffer.ToArray());
    }

    private static async Task<ModelAssetFile> DownloadAsync(Uri uri, string directory, string name, long expected,
        string? expectedHash, Action<string> report, CancellationToken cancellation)
    {
        if (expected > MaximumAssetBytes) throw new InvalidDataException("The model asset exceeds the native download safety limit.");
        string target = Path.Combine(directory, CachedModel.LocalName(name));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(target) && expected > 0 && new FileInfo(target).Length == expected &&
            expectedHash is not null && FilePaths.Hash(target) == expectedHash)
            return new(name, expected, expectedHash);
        report($"Downloading {name}");
        string partial = target + ".partial";
        long offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (expected > 0 && offset >= expected) { File.Delete(partial); offset = 0; }
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
        response.EnsureSuccessStatusCode();
        bool append = response.StatusCode == HttpStatusCode.PartialContent && offset > 0;
        if (append && response.Content.Headers.ContentRange?.From != offset)
            throw new InvalidDataException("The model host returned an invalid resume range.");
        long maximum = expected > 0 ? expected : MaximumAssetBytes;
        await using (var input = await response.Content.ReadAsStreamAsync(cancellation))
        await using (var output = new FileStream(partial, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await CopyAsync(input, output, maximum - (append ? offset : 0), cancellation);
            output.Flush(true);
        }
        long actual = new FileInfo(partial).Length;
        string digest = FilePaths.Hash(partial);
        if (expected > 0 && actual != expected || expectedHash is not null && digest != expectedHash)
        {
            File.Delete(partial);
            throw new InvalidDataException($"The downloaded asset failed integrity verification: {name}.");
        }
        File.Move(partial, target, true);
        return new(name, actual, digest);
    }

    private static async Task CopyAsync(Stream input, Stream output, long maximum, CancellationToken cancellation)
    {
        byte[] buffer = new byte[1024 * 1024];
        long count = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellation)) != 0)
        {
            count += read;
            if (count > maximum) throw new InvalidDataException("The response exceeds its model-asset size limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellation);
        }
    }
}
