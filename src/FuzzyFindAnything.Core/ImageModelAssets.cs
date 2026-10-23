using System.Text.Json;

namespace FuzzyFindAnything.Core;

public sealed record CachedImageModel(ImageModelDefinition Model, ModelAssetFile[] Files)
{
    public const string ManifestName = "ffa-image-model.json";
    public void Verify(string directory, CancellationToken cancellation = default)
    {
        var approved = ImageModelRecipes.Find(Model.ModelUrls.FirstOrDefault() ?? "")
            ?? throw new InvalidDataException("The image-understanding model is not in the reviewed registry.");
        if (Model.Fingerprint != approved.Fingerprint || !Model.Tasks.SequenceEqual(approved.Tasks) ||
            approved.Files.Any(name => Files.Count(file => file.Name == name) != 1) ||
            approved.Checksums.Any(pair => Files.SingleOrDefault(file => file.Name == pair.Key)?.Sha256 != pair.Value))
            throw new InvalidDataException("The image model manifest differs from its reviewed recipe.");
        foreach (var file in Files)
        {
            cancellation.ThrowIfCancellationRequested();
            string path = Path.Combine(directory, CachedModel.LocalName(file.Name));
            if (!File.Exists(path) || new FileInfo(path).Length != file.Bytes || FilePaths.Hash(path) != file.Sha256)
                throw new InvalidDataException("Missing or changed image-understanding asset: " + file.Name);
        }
    }
}

public sealed class ImageModelAssets(string directory)
{
    public string DirectoryFor(ImageModelDefinition model) => Path.Combine(directory, model.Key);
    public static CachedImageModel Load(string path) =>
        JsonSerializer.Deserialize<CachedImageModel>(File.ReadAllText(Path.Combine(path, CachedImageModel.ManifestName)), Protocol.Json)
        ?? throw new InvalidDataException("Invalid image model manifest.");

    public async Task<ImageModelDefinition> AcquireAsync(string url, Action<string> progress, CancellationToken cancellation)
    {
        var model = ImageModelRecipes.Find(ModelSelection.Canonical(url))
            ?? throw new NotSupportedException("This image model requires a reviewed image-understanding recipe.");
        string target = DirectoryFor(model);
        if (File.Exists(Path.Combine(target, CachedImageModel.ManifestName)))
        {
            try { Load(target).Verify(target, cancellation); return model; }
            catch (Exception error) when (error is IOException or InvalidDataException)
            { progress("Repairing the image model cache: " + error.Message); }
        }
        string id = new Uri(model.ArtifactUrl).AbsolutePath.TrimStart('/');
        using var metadata = await HuggingFaceModels.JsonAsync(new Uri($"https://huggingface.co/api/models/{id}/revision/{model.Revision}?blobs=true"), cancellation);
        if (metadata.RootElement.GetProperty("sha").GetString() != model.Revision)
            throw new InvalidDataException("The pinned image model revision changed.");
        var siblings = metadata.RootElement.GetProperty("siblings").EnumerateArray()
            .ToDictionary(item => item.GetProperty("rfilename").GetString()!, item => item.Clone(), StringComparer.Ordinal);
        using var configuration = await HuggingFaceModels.JsonAsync(HuggingFaceModels.AssetUri(model.ArtifactUrl, model.Revision, "config.json"), cancellation);
        if (configuration.RootElement.GetProperty("model_type").GetString() != model.ModelType)
            throw new InvalidDataException("The image artifact does not match its reviewed architecture.");
        string staging = Path.Combine(directory, ".acquiring-" + model.Key);
        Directory.CreateDirectory(staging);
        var files = new List<ModelAssetFile>();
        async Task Download(string name)
        {
            if (files.Any(file => file.Name == name)) return;
            if (!siblings.TryGetValue(name, out var item)) throw new InvalidDataException("Missing image artifact: " + name);
            long size = item.TryGetProperty("size", out var length) ? length.GetInt64() : item.GetProperty("lfs").GetProperty("size").GetInt64();
            if (size < 0 || files.Sum(file => file.Bytes) + size > 12L * 1024 * 1024 * 1024)
                throw new InvalidDataException("The image model exceeds the native acquisition budget.");
            string? hash = model.Checksums.GetValueOrDefault(name) ??
                (item.TryGetProperty("lfs", out var lfs) && lfs.TryGetProperty("sha256", out var sha) ? sha.GetString() : null);
            files.Add(await HuggingFaceModels.DownloadAsync(HuggingFaceModels.AssetUri(model.ArtifactUrl, model.Revision, name),
                staging, name, size, hash, progress, cancellation));
        }
        foreach (string name in model.Files) await Download(name);
        foreach (string graph in model.Recipe.Graphs)
            foreach (var data in OnnxMetadata.Read(Path.Combine(staging, CachedModel.LocalName(graph))).ExternalData)
            {
                string prefix = graph.Contains('/') ? graph[..(graph.LastIndexOf('/') + 1)] : "";
                string name = prefix + data.Location;
                await Download(name);
                long length = files.Single(file => file.Name == name).Bytes;
                if (data.Offset > length || data.Length is { } bytes && bytes > length - data.Offset)
                    throw new InvalidDataException("Image graph external weights exceed their file bounds.");
            }
        var manifest = new CachedImageModel(model, files.ToArray());
        manifest.Verify(staging, cancellation);
        Directory.CreateDirectory(target);
        foreach (var file in files)
        {
            string destination = Path.Combine(target, CachedModel.LocalName(file.Name));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(Path.Combine(staging, CachedModel.LocalName(file.Name)), destination, true);
        }
        string temporary = Path.Combine(target, CachedImageModel.ManifestName + ".tmp");
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(manifest, Protocol.Json), cancellation);
        File.Move(temporary, Path.Combine(target, CachedImageModel.ManifestName), true);
        Directory.Delete(staging, true);
        return model;
    }
}
