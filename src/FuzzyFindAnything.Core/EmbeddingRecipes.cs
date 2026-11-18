using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace FuzzyFindAnything.Core;

public sealed record QueryProfile(string Id, string Label, string Prefix);

public sealed record MultimodalEmbeddingRecipe(
    string VisionGraph = "onnx/vision_encoder.onnx", string VisionOutput = "image_features",
    string ImageFeatures = "image_features", string VideoFeatures = "video_features", string AudioFeatures = "audio_features",
    string PixelValues = "pixel_values", string PixelPositions = "pixel_position_ids",
    string Processor = "processor_config.json", string DocumentFormat = "title-text-v1",
    string Preprocessing = "rgb-bicubic-rescale255-patch16-pool3-soft280-positions-v1",
    int FeatureDimensions = 512, int ImageToken = 258880, string Precision = "f32");

public sealed record EmbeddingRecipe(
    string Id, string Tokenizer, string Pooling, string Output, int ChunkTokens,
    string DocumentPrefix = "", string Padding = "right", int? BeginningToken = null, int? EndToken = null,
    int PaddingToken = 0, int MaximumBatchTokens = 4096, string Graph = "onnx/model.onnx",
    string InputIds = "input_ids", string AttentionMask = "attention_mask", string TokenTypeIds = "token_type_ids",
    string PositionIds = "position_ids", string TextQuery = "default", string CodeQuery = "default",
    QueryProfile[]? Queries = null, MultimodalEmbeddingRecipe? Multimodal = null,
    string? DocumentTemplate = null, OriginalImageRecipe? Image = null)
{
    [JsonIgnore]
    public bool IsOriginalPublicFormat => Image is not null;
    [JsonIgnore]
    public MultimodalEmbeddingRecipe? NativeMultimodal => Multimodal ??
        (IsOriginalPublicFormat ? OriginalGemma2Contract.NativeInterface : null);
    public IReadOnlyList<QueryProfile> QueryProfiles => Queries ?? [new("default", "Model default", "")];
    private string OriginalDocumentFingerprint => Hash(JsonSerializer.Serialize(new
    {
        Tokenizer,
        Pooling,
        Output,
        DocumentPrefix,
        Padding,
        BeginningToken,
        EndToken,
        PaddingToken,
        Graph,
        InputIds,
        AttentionMask,
        TokenTypeIds,
        PositionIds,
        Version = 1
    }, Protocol.Json));
    public string DocumentFingerprint => IsOriginalPublicFormat ? OriginalGemma2Contract.DocumentFingerprint(this) :
        Multimodal is null ? OriginalDocumentFingerprint :
        Hash(OriginalDocumentFingerprint + ":" + JsonSerializer.Serialize(Multimodal, Protocol.Json));
    public string IndexingFingerprint => Hash($"{DocumentFingerprint}:{ChunkTokens}:overlap-128-chars-v1");
    public string ContractFingerprint => Hash(JsonSerializer.Serialize(new
    { Id, DocumentFingerprint, IndexingFingerprint, MaximumBatchTokens, TextQuery, CodeQuery, QueryProfiles }, Protocol.Json));
    public string DefaultQuery(string kind) => kind == "Code" ? CodeQuery : TextQuery;
    public QueryProfile Query(string profile) => QueryProfiles.SingleOrDefault(value => value.Id == profile)
        ?? throw new NotSupportedException($"The embedding recipe does not support query profile '{profile}'.");
    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public void Validate(int maximumTokens)
    {
        _ = CachedModel.LocalName(Graph);
        if (!Regex.IsMatch(Id, "^[a-z0-9][a-z0-9.-]{0,99}$") ||
            Tokenizer is not ("bert-wordpiece" or "byte-bpe-nfc" or "gemma-sentencepiece") ||
            Pooling is not ("mean" or "cls" or "last-token" or "pooled") ||
            Padding is not ("left" or "right") || PaddingToken < 0 || BeginningToken < 0 || EndToken < 0 ||
            ChunkTokens < 16 || ChunkTokens > maximumTokens || MaximumBatchTokens < maximumTokens ||
            MaximumBatchTokens > 65536 || DocumentPrefix.Length > 1024 ||
            new[] { Output, InputIds, AttentionMask, TokenTypeIds, PositionIds }.Any(value =>
                value.Length is < 1 or > 128 || !Regex.IsMatch(value, "^[a-zA-Z_][a-zA-Z0-9_.]*$")) ||
            QueryProfiles.Count is < 1 or > 8 || QueryProfiles.Select(value => value.Id).Distinct().Count() != QueryProfiles.Count ||
            QueryProfiles.Any(value => !Regex.IsMatch(value.Id, "^[a-z0-9][a-z0-9.-]{0,63}$") ||
                value.Prefix.Length > 1024 || value.Label.Length is < 1 or > 100) ||
            (TextQuery == "choose" || CodeQuery == "choose") && QueryProfiles.Count < 2)
            throw new InvalidDataException("Unsupported native embedding recipe.");
        if (TextQuery != "choose") _ = Query(TextQuery);
        if (CodeQuery != "choose") _ = Query(CodeQuery);
        if (IsOriginalPublicFormat) OriginalGemma2Contract.ValidateRecipe(this);
        else if (!string.IsNullOrEmpty(DocumentTemplate))
            throw new NotSupportedException("A saved document template requires its reviewed original image recipe.");
        if (NativeMultimodal is not null && (NativeMultimodal != new MultimodalEmbeddingRecipe() ||
            Tokenizer != "gemma-sentencepiece" || Pooling != "pooled" || Output != "sentence_embedding" ||
            Padding != "right" || BeginningToken != 2 || EndToken != 1 || PaddingToken != 0 ||
            maximumTokens != 2048 || ChunkTokens != 512 || MaximumBatchTokens != 4096 ||
            Graph != "onnx/model.onnx" || InputIds != "input_ids" || AttentionMask != "attention_mask"))
            throw new NotSupportedException("The multimodal export differs from the qualified FP32 Gemma2 contract.");
        if (Tokenizer == "gemma-sentencepiece" && NativeMultimodal is null)
            throw new NotSupportedException("Gemma tokenization requires its reviewed multimodal contract.");
    }
}

public sealed record RegisteredEmbedding(string[] ModelUrls, string ArtifactUrl, string Revision,
    string ModelType, int Dimensions, int MaximumTokens, long MemoryBytes, string[] Files,
    Dictionary<string, string> Checksums, EmbeddingRecipe Recipe, string? SourceRevision = null,
    string Adapter = "native-text", bool SupportsImages = false, string? SourceUrl = null,
    Dictionary<string, long>? Sizes = null, SupplementalModelAsset[]? Supplemental = null);

public sealed record SupplementalModelAsset(string Name, string SourceUrl, string Revision, long Bytes, string Sha256);

public static class EmbeddingRecipes
{
    private static readonly Lazy<RegisteredEmbedding[]> registry = new(() =>
    {
        using var stream = typeof(EmbeddingRecipes).Assembly.GetManifestResourceStream("FuzzyFindAnything.EmbeddingRecipes.json")
            ?? throw new InvalidDataException("The native embedding registry is missing.");
        var existing = JsonSerializer.Deserialize<RegisteredEmbedding[]>(stream, Protocol.Json)
            ?? throw new InvalidDataException("The native embedding registry is empty.");
        using var publicStream = typeof(EmbeddingRecipes).Assembly.GetManifestResourceStream("FuzzyFindAnything.PublicModel.json")
            ?? throw new InvalidDataException("The public model contract is missing.");
        var publicModel = JsonSerializer.Deserialize<RegisteredEmbedding>(publicStream, Protocol.Json)
            ?? throw new InvalidDataException("The public model contract is invalid.");
        RegisteredEmbedding[] entries = [.. existing, publicModel];
        var urls = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            _ = ModelSelection.Canonical(entry.ArtifactUrl);
            if (!Regex.IsMatch(entry.Revision, "^[0-9a-f]{40}$") || entry.ModelUrls.Length == 0 ||
                entry.Files.Length == 0 || entry.MemoryBytes <= 0 ||
                entry.SourceRevision is not null && !Regex.IsMatch(entry.SourceRevision, "^[0-9a-f]{40}$"))
                throw new InvalidDataException("Invalid registered embedding artifact.");
            foreach (string url in entry.ModelUrls)
                if (ModelSelection.Canonical(url) != url || !urls.Add(url))
                    throw new InvalidDataException("Duplicate or invalid registered model URL.");
            foreach (string file in entry.Files) _ = CachedModel.LocalName(file);
            foreach (var pair in entry.Checksums)
                if (!entry.Files.Contains(pair.Key) || !Regex.IsMatch(pair.Value, "^[0-9a-f]{64}$"))
                    throw new InvalidDataException("Invalid registered asset checksum.");
            foreach (var asset in entry.Supplemental ?? [])
                if (!entry.Files.Contains(asset.Name) || ModelSelection.Canonical(asset.SourceUrl) != asset.SourceUrl ||
                    !Regex.IsMatch(asset.Revision, "^[0-9a-f]{40}$") || entry.Checksums.GetValueOrDefault(asset.Name) != asset.Sha256 ||
                    entry.Sizes?.GetValueOrDefault(asset.Name) != asset.Bytes)
                    throw new InvalidDataException("Invalid supplemental asset provenance.");
            entry.Recipe.Validate(entry.MaximumTokens);
        }
        return entries;
    });
    public static IReadOnlyList<RegisteredEmbedding> Registered => registry.Value;
    public static RegisteredEmbedding PublicDefault => Find(ModelSelection.DefaultUrl)!;
    public static RegisteredEmbedding? Find(string modelUrl) =>
        Registered.SingleOrDefault(entry => entry.ModelUrls.Contains(modelUrl, StringComparer.Ordinal));
    public static EmbeddingRecipe StandardBert(string pooling, string output, int maximumTokens)
    {
        if (pooling == "cls" ? output != "last_hidden_state" :
            pooling != "pooled" || output is not ("sentence_embedding" or "sentence_embeddings"))
            throw new NotSupportedException("This BERT output/pooling combination needs a reviewed recipe.");
        return new(pooling == "cls" ? "bert-cls-v1" : "bert-pooled-v1", "bert-wordpiece", pooling, output, maximumTokens,
            BeginningToken: 101, EndToken: 102, PaddingToken: 0);
    }
}

public sealed record ModelOptions(string QueryProfile = "", string Device = "Automatic")
{
    public static ModelOptions Default { get; } = new();
    public ModelOptions Normalize()
    {
        string query = QueryProfile.Trim();
        string device = Device.Trim();
        if (query == "automatic") query = "";
        if (device is "" or "automatic") device = "Automatic";
        if (device == "CPUExecutionProvider:CPU") device = "CPU";
        if (query.Length > 0 && !Regex.IsMatch(query, "^[a-z0-9][a-z0-9.-]{0,63}$"))
            throw new ArgumentException("Choose an approved query profile.");
        if (device is not ("Automatic" or "CPU") &&
            !Regex.IsMatch(device, "^[A-Za-z0-9]+ExecutionProvider:(CPU|GPU|NPU)$"))
            throw new ArgumentException("Choose Automatic, CPU or an available local execution provider/device.");
        return new(query, device);
    }
    public string? ProviderSelector => Device switch
    {
        "Automatic" => null,
        "CPU" => "CPUExecutionProvider:CPU",
        _ => Device
    };
}

public sealed record ModelRequirement(string Kind, string ModelUrl, string State, string Summary, string Details,
    QueryProfile[] QueryProfiles, string DefaultQuery, bool IsSimple, string? Error = null,
    ImageModelDefinition? ImageModel = null, string? EmbeddingModel = null, ProviderDevice[]? Devices = null);

public sealed record ModelInspection(ModelSelection Selection, ModelRequirement[] Requirements);
