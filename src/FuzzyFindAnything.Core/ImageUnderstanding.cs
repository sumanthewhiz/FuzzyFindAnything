using System.Text.Json;
using System.Text.RegularExpressions;

namespace FuzzyFindAnything.Core;

public sealed record ImageTaskProfile(string Id, string Label, string Prompt, int MaximumNewTokens)
{
    public string Fingerprint => EmbeddingRecipe.Hash($"{Id}\n{Prompt}\n{MaximumNewTokens}");
}

public sealed record ImageGenerationRecipe(string Id, string VisionGraph, string TokenGraph, string EncoderGraph,
    string DecoderGraph, int Width, int Height, int HiddenSize, int Heads, int Layers, int HeadSize,
    float[] Mean, float[] Deviation, int BeginningToken = 0, int EndToken = 2, int PaddingToken = 1,
    int DecoderStartToken = 2, int NoRepeatNgram = 3)
{
    public string Fingerprint => EmbeddingRecipe.Hash(JsonSerializer.Serialize(new
    {
        Id,
        VisionGraph,
        TokenGraph,
        EncoderGraph,
        DecoderGraph,
        Width,
        Height,
        HiddenSize,
        Heads,
        Layers,
        HeadSize,
        Mean,
        Deviation,
        BeginningToken,
        EndToken,
        PaddingToken,
        DecoderStartToken,
        NoRepeatNgram,
        Version = 1
    }, Protocol.Json));
    public string[] Graphs => [VisionGraph, TokenGraph, EncoderGraph, DecoderGraph];
    public void Validate()
    {
        foreach (string graph in Graphs) _ = CachedModel.LocalName(graph);
        if (Id != "vision-text-seq2seq-bpe-v1" || Width is < 16 or > 1024 || Height is < 16 or > 1024 ||
            HiddenSize is < 8 or > 2048 || Heads is < 1 or > 64 || Layers is < 1 or > 48 ||
            HeadSize is < 1 or > 256 || Heads * HeadSize != HiddenSize ||
            Mean.Length != 3 || Deviation.Length != 3 || Mean.Any(value => !float.IsFinite(value)) ||
            Deviation.Any(value => !float.IsFinite(value) || value <= 0) ||
            new[] { BeginningToken, EndToken, PaddingToken, DecoderStartToken }.Any(value => value < 0) ||
            NoRepeatNgram is < 0 or > 8)
            throw new NotSupportedException("This image-understanding recipe needs another reviewed native adapter.");
    }
}

public sealed record ImageModelDefinition(string[] ModelUrls, string ArtifactUrl, string Revision, string SourceRevision,
    string ModelType, long MemoryBytes, ImageGenerationRecipe Recipe, ImageTaskProfile[] Tasks,
    string[] Files, Dictionary<string, string> Checksums)
{
    public string Fingerprint => EmbeddingRecipe.Hash($"{ArtifactUrl}@{Revision}:{Recipe.Fingerprint}");
    public string Key => Fingerprint[..24];
    public ImageTaskProfile Task(string id) => Tasks.SingleOrDefault(task => task.Id == id)
        ?? throw new NotSupportedException($"This image-understanding model does not support task '{id}'.");
    public string TaskKey(string id) => EmbeddingRecipe.Hash($"{Fingerprint}:{Task(id).Fingerprint}");
}

public static class ImageModelRecipes
{
    private static readonly Lazy<ImageModelDefinition[]> registry = new(() =>
    {
        using var stream = typeof(ImageModelRecipes).Assembly.GetManifestResourceStream("FuzzyFindAnything.ImageRecipes.json")
            ?? throw new InvalidDataException("The image-understanding registry is missing.");
        var entries = JsonSerializer.Deserialize<ImageModelDefinition[]>(stream, Protocol.Json)
            ?? throw new InvalidDataException("The image-understanding registry is empty.");
        var urls = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            _ = ModelSelection.Canonical(entry.ArtifactUrl);
            if (!Regex.IsMatch(entry.Revision, "^[0-9a-f]{40}$") || !Regex.IsMatch(entry.SourceRevision, "^[0-9a-f]{40}$") ||
                entry.MemoryBytes <= 0 || entry.ModelUrls.Length == 0 || entry.Tasks.Length is < 1 or > 2 ||
                entry.Tasks.Select(value => value.Id).Distinct().Count() != entry.Tasks.Length)
                throw new InvalidDataException("Invalid image-understanding registry entry.");
            entry.Recipe.Validate();
            foreach (string url in entry.ModelUrls)
                if (ModelSelection.Canonical(url) != url || !urls.Add(url))
                    throw new InvalidDataException("Duplicate or invalid image model URL.");
            foreach (var task in entry.Tasks)
                if (task.Id is not ("caption" or "ocr") || task.Prompt.Length is < 1 or > 512 ||
                    task.MaximumNewTokens is < 8 or > 768 || task.Label.Length is < 1 or > 80)
                    throw new InvalidDataException("Unsupported image task profile.");
            foreach (string file in entry.Files) _ = CachedModel.LocalName(file);
            foreach (var pair in entry.Checksums)
                if (!entry.Files.Contains(pair.Key) || !Regex.IsMatch(pair.Value, "^[0-9a-f]{64}$"))
                    throw new InvalidDataException("Invalid image asset checksum.");
        }
        return entries;
    });
    public static IReadOnlyList<ImageModelDefinition> Registered => registry.Value;
    public static ImageModelDefinition? Find(string url) =>
        Registered.SingleOrDefault(entry => entry.ModelUrls.Contains(url, StringComparer.Ordinal));
}

public sealed record ImagePipelineOptions(bool Caption = true, bool Ocr = false, string? EmbeddingModel = null,
    ModelOptions? EmbeddingOptions = null)
{
    public string[] Tasks => new[] { Caption ? "caption" : null, Ocr ? "ocr" : null }.OfType<string>().ToArray();
    public ImagePipelineOptions Normalize()
    {
        if (!Caption && !Ocr) throw new ArgumentException("Select at least one supported image description task.");
        string? url = string.IsNullOrWhiteSpace(EmbeddingModel) ? null : ModelSelection.Canonical(EmbeddingModel);
        var options = EmbeddingOptions?.Normalize();
        if (options == ModelOptions.Default) options = null;
        return new(Caption, Ocr, url, options);
    }
}

public sealed record ImagePipelineDescriptor(string ModelUrl, ImageModelDefinition Model, string[] Tasks,
    EmbeddingSpace Embedder, ModelOptions EmbeddingOptions, string GenerationDevice)
{
    public string Fingerprint => EmbeddingRecipe.Hash(string.Join("\n",
        Tasks.Order(StringComparer.Ordinal).Select(Model.TaskKey).Prepend(Embedder.IndexingFingerprint)));
    public string QueryProfile => EmbeddingOptions.QueryProfile.Length > 0 ? EmbeddingOptions.QueryProfile :
        Embedder.Recipe?.TextQuery ?? "default";
    public string Summary => $"Image descriptions ({string.Join(" + ", Tasks)}) -> {Embedder.ModelUrl}";
}

public sealed record ImageTaskResult(string Task, string Text, string State, int GeneratedTokens = 0,
    string? Warning = null, string? Error = null, string? ErrorCode = null, bool Retryable = false)
{
    public void Validate(string task)
    {
        if (Task != task || State is not ("Completed" or "Partial" or "Empty" or "Failed") ||
            GeneratedTokens < 0 || Text.Length > 1_048_576 ||
            State is "Completed" or "Partial" && string.IsNullOrWhiteSpace(Text) ||
            State == "Empty" && Text.Length != 0 || State == "Failed" && string.IsNullOrWhiteSpace(Error))
            throw new InvalidDataException("The image worker returned an invalid task outcome.");
    }
}

public interface IImageUnderstanding : IAsyncDisposable
{
    ImagePipelineDescriptor Descriptor { get; }
    string Provider { get; }
    string Device { get; }
    bool IsLoaded => true;
    Task<ImageTaskResult> GenerateAsync(string path, string task, CancellationToken cancellation);
}

public sealed record ImagePipelineStatus(string ModelUrl, string EmbeddingModel, string[] Tasks,
    string Provider, string Device, long Generated = 0, long Partial = 0, long Failed = 0, long Empty = 0, bool IsLoaded = false);
