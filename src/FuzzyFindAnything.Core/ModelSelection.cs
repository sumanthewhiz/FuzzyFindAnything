using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace FuzzyFindAnything.Core;

public sealed record ModelSelection(string Text, string Code, string Image,
    ModelOptions? TextOptions = null, ModelOptions? CodeOptions = null, ModelOptions? ImageOptions = null,
    ImagePipelineOptions? ImagePipeline = null)
{
    public const string DefaultUrl = "https://huggingface.co/google/embeddinggemma-2";
    public static ModelSelection Default { get; } = new(DefaultUrl, DefaultUrl, DefaultUrl);
    public ModelReference ReferenceFor(string kind) => ModelReference.Parse(ForKind(kind));
    public bool UsesExternalModels => EmbeddingSelection().DistinctUrls.Any(url => ModelReference.Parse(url) is ProviderModelReference);
    public string ForKind(string kind) => kind switch
    {
        "Document" => Text,
        "Code" => Code,
        "Image" => Image,
        _ => throw new ArgumentException("Unknown indexed file kind.", nameof(kind))
    };
    public ModelOptions OptionsFor(string kind) => kind switch
    {
        "Document" => TextOptions ?? ModelOptions.Default,
        "Code" => CodeOptions ?? ModelOptions.Default,
        "Image" => ImageOptions ?? ModelOptions.Default,
        _ => throw new ArgumentException("Unknown indexed file kind.", nameof(kind))
    };
    public bool UsesImageUnderstanding => ImageModelRecipes.Find(Image) is not null;
    public ImagePipelineOptions EffectiveImagePipeline => ImagePipeline ??
        (ImageModelRecipes.Find(Image) is { } definition && !definition.Tasks.Any(task => task.Id == "caption")
            ? new(Caption: false, Ocr: true) : new());
    public ModelSelection Normalize()
    {
        string image = ModelReference.Parse(Image).Key;
        var pipeline = ImageModelRecipes.Find(image) is not null ? ImagePipeline?.Normalize() : null;
        var defaultPipeline = ImageModelRecipes.Find(image) is { } definition && !definition.Tasks.Any(task => task.Id == "caption")
            ? new ImagePipelineOptions(Caption: false, Ocr: true) : new();
        if (pipeline == defaultPipeline) pipeline = null;
        return new(ModelReference.Parse(Text).Key, ModelReference.Parse(Code).Key, image, NormalizeOptions(TextOptions),
            NormalizeOptions(CodeOptions), NormalizeOptions(ImageOptions), pipeline);
    }
    public ModelSelection EmbeddingSelection()
    {
        if (!UsesImageUnderstanding) return this;
        var pipeline = EffectiveImagePipeline;
        var options = pipeline.EmbeddingOptions ?? ModelOptions.Default;
        if (pipeline.EmbeddingModel is null) options = options with { Device = OptionsFor("Document").Device };
        return new(Text, Code, pipeline.EmbeddingModel ?? Text, TextOptions, CodeOptions, NormalizeOptions(options));
    }
    public ModelSelection WithEmbeddingOptions(ModelSelection normalized)
    {
        if (!UsesImageUnderstanding) return normalized;
        var pipeline = EffectiveImagePipeline;
        var embedded = normalized.OptionsFor("Image");
        if (pipeline.EmbeddingModel is null) embedded = embedded with { Device = "Automatic" };
        return (this with
        {
            TextOptions = normalized.TextOptions,
            CodeOptions = normalized.CodeOptions,
            ImageOptions = NormalizeOptions(OptionsFor("Image") with { QueryProfile = "" }),
            ImagePipeline = pipeline with { EmbeddingOptions = NormalizeOptions(embedded) }
        }).Normalize();
    }
    private static ModelOptions? NormalizeOptions(ModelOptions? options) =>
        options?.Normalize() is { } value && value != ModelOptions.Default ? value : null;
    public bool RequiresVersion2 => UsesExternalModels || UsesImageUnderstanding || TextOptions is not null || CodeOptions is not null || ImageOptions is not null ||
        DistinctUrls.Any(url => EmbeddingRecipes.Find(url) is not null);
    public string[] DistinctUrls => new[] { Text, Code, Image }.Distinct(StringComparer.Ordinal).ToArray();

    public static string Canonical(string value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Equals("huggingface.co", StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("Enter an HTTPS Hugging Face model-card URL such as https://huggingface.co/owner/model.");
        string path = uri.AbsolutePath.TrimEnd('/');
        if (!Regex.IsMatch(path, @"^/[A-Za-z0-9][A-Za-z0-9_.-]*/[A-Za-z0-9][A-Za-z0-9_.-]*$") ||
            path.Split('/').Any(part => part is "." or ".."))
            throw new ArgumentException("Use a model-card URL with exactly an owner and model name, not a dataset, file or revision URL.");
        return "https://huggingface.co" + path;
    }

    public static ModelSelection Read(XElement root)
    {
        var models = root.Element("Models");
        bool providersAllowed = (string?)root.Attribute("version") == "4";
        if ((string?)root.Attribute("version") is "2" or "3" or "4")
        {
            if (models?.Attributes().Any(attribute => attribute.Name.LocalName is "text" or "code" or "image") == true)
                throw new InvalidDataException("Version-2 model settings require child bindings, not legacy URL attributes.");
            (string Url, ModelOptions? Options) ReadBinding(string name)
            {
                var element = models?.Element(name);
                if ((string?)element?.Attribute("mode") is { } mode && (name != "Image" || mode is not ("direct" or "described")))
                    throw new NotSupportedException("Unsupported image processing mode.");
                if (element?.Attribute("recipe") is not null)
                    throw new InvalidDataException("Recipe selection is resolved by the native registry, not an editable configuration attribute.");
                return (ModelReference.ReadBinding(element, providersAllowed),
                    NormalizeOptions(new((string?)element?.Attribute("queryProfile") ?? "",
                        (string?)element?.Attribute("devicePolicy") ?? "Automatic")));
            }
            var text = ReadBinding("Text"); var code = ReadBinding("Code"); var image = ReadBinding("Image");
            var imageElement = models?.Element("Image");
            ImagePipelineOptions? pipeline = null;
            if ((string?)imageElement?.Attribute("mode") == "described" || imageElement?.Element("Tasks") is not null)
            {
                if (ImageModelRecipes.Find(image.Url) is null) throw new NotSupportedException("The image model has no reviewed image-understanding recipe.");
                var tasks = imageElement?.Element("Tasks");
                bool Flag(string name, bool fallback) => tasks?.Attribute(name) is { } value ? XmlConvert.ToBoolean(value.Value) : fallback;
                var embedding = imageElement?.Element("Embedding");
                string use = (string?)embedding?.Attribute("use") ?? "Text";
                if (use is not ("Text" or "Custom")) throw new InvalidDataException("Choose Text or Custom for the image text embedder.");
                string? url = use == "Custom" ? embedding is not null ? ModelReference.ReadBinding(embedding, providersAllowed, required: true) :
                    throw new InvalidDataException("A custom image text embedder is required.") : null;
                pipeline = new(Flag("caption", true), Flag("ocr", false), url,
                    NormalizeOptions(new((string?)embedding?.Attribute("queryProfile") ?? "", (string?)embedding?.Attribute("devicePolicy") ?? "Automatic")),
                    (string?)tasks?.Attribute("regions") ?? "");
            }
            return new ModelSelection(text.Url, code.Url, image.Url, text.Options, code.Options, image.Options, pipeline).Normalize();
        }
        if (models?.HasElements == true)
            throw new InvalidDataException("Extended model bindings require configuration version 2.");
        return new ModelSelection((string?)models?.Attribute("text") ?? DefaultUrl,
            (string?)models?.Attribute("code") ?? DefaultUrl, (string?)models?.Attribute("image") ?? DefaultUrl).Normalize();
    }

    public static ModelSelection Load(string dataDirectory)
    {
        string path = Path.Combine(dataDirectory, "settings.xml");
        return File.Exists(path) ? Read(ReadDocument(path).Root!) : Default;
    }

    internal static XDocument ReadDocument(string path)
    {
        using var reader = XmlReader.Create(path, new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1_048_576 });
        var document = XDocument.Load(reader);
        if (document.Root?.Name != "FuzzyFindAnythingConfiguration" || (string?)document.Root.Attribute("version") is not ("1" or "2" or "3" or "4"))
            throw new InvalidDataException("Unsupported application configuration.");
        return document;
    }

    public XElement ToXml(bool version2 = false)
    {
        if (!version2 && !RequiresVersion2)
            return new("Models", new XAttribute("text", Text), new XAttribute("code", Code), new XAttribute("image", Image));
        XElement Binding(string name, string kind)
        {
            var options = OptionsFor(kind);
            var result = new XElement(name);
            ReferenceFor(kind).WriteBinding(result);
            if (options.QueryProfile.Length > 0) result.Add(new XAttribute("queryProfile", options.QueryProfile));
            if (options.Device != "Automatic") result.Add(new XAttribute("devicePolicy", options.Device));
            if (kind == "Image" && UsesImageUnderstanding)
            {
                var pipeline = EffectiveImagePipeline;
                result.Add(new XAttribute("mode", "described"));
                var tasks = new XElement("Tasks", new XAttribute("caption", pipeline.Caption), new XAttribute("ocr", pipeline.Ocr));
                if (pipeline.RegionTask.Length > 0) tasks.Add(new XAttribute("regions", pipeline.RegionTask));
                result.Add(tasks);
                var embedding = new XElement("Embedding", new XAttribute("use", pipeline.EmbeddingModel is null ? "Text" : "Custom"));
                if (pipeline.EmbeddingModel is not null) ModelReference.Parse(pipeline.EmbeddingModel).WriteBinding(embedding);
                if (pipeline.EmbeddingOptions?.QueryProfile is { Length: > 0 } profile) embedding.Add(new XAttribute("queryProfile", profile));
                if (pipeline.EmbeddingOptions?.Device is { } device && device != "Automatic") embedding.Add(new XAttribute("devicePolicy", device));
                result.Add(embedding);
            }
            return result;
        }
        return new("Models", Binding("Text", "Document"), Binding("Code", "Code"), Binding("Image", "Image"));
    }
}

public sealed record EmbeddingSpace(string ModelUrl, string Revision, string Adapter, int Dimensions,
    int MaximumTokens, bool SupportsImages, long MemoryBytes, EmbeddingRecipe? Recipe = null, string? ArtifactUrl = null,
    ExternalEmbeddingContract? External = null)
{
    public static EmbeddingSpace Default { get; } = new(ModelSelection.DefaultUrl, ModelContract.Revision,
        "embeddinggemma2", ModelContract.Dimensions, 8192, true, 3L << 30);
    public string Fingerprint => External is { } external
        ? $"{ModelUrl}:{external.VectorIdentity}:{external.Device}:{external.LogicalDimensions}:{Dimensions}:zero-pad8:f32:cosine:external-v1"
        : Recipe is not null
        ? $"{ArtifactUrl ?? ModelUrl}@{Revision}:{Dimensions}:f32:{Recipe.DocumentFingerprint}:native-v2"
        : this == Default ? ModelContract.Fingerprint :
        $"{ModelUrl}@{Revision}:{Dimensions}:f32:{Adapter}:v1";
    public string IndexingFingerprint => External is { } external
        ? $"{Fingerprint}:{external.TokenizerIdentity}:{external.ChunkTokens}:provider-chunks-v1"
        : Recipe is null ? Fingerprint : $"{Fingerprint}:{MaximumTokens}:{Recipe.IndexingFingerprint}";
    public string CacheKey => Recipe is null ? Key : EmbeddingRecipe.Hash($"{ArtifactUrl ?? ModelUrl}@{Revision}:{Recipe.ContractFingerprint}")[..24];
    public string Key => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Fingerprint))).ToLowerInvariant()[..24];
    public float[] Normalize(ReadOnlySpan<float> values)
    {
        var result = ModelContract.Normalize(values, Dimensions);
        ValidatePadding(result); return result;
    }
    public float[] Vector(byte[] values)
    {
        var result = ModelContract.Vector(values, Dimensions);
        ValidatePadding(result); return result;
    }
    private void ValidatePadding(ReadOnlySpan<float> values)
    {
        if (External is not { } external) return;
        foreach (float value in values[external.LogicalDimensions..])
            if (value != 0) throw new InvalidDataException("The provider returned nonzero values in the declared zero-padding coordinates.");
    }

    public void Validate()
    {
        if (External is { } external)
        {
            external.Validate(this);
            if (MaximumTokens is < 16 or > 8192 || MemoryBytes <= 0)
                throw new InvalidDataException("The provider model limits are invalid.");
            return;
        }
        _ = ModelSelection.Canonical(ModelUrl);
        if (ArtifactUrl is not null) _ = ModelSelection.Canonical(ArtifactUrl);
        Recipe?.Validate(MaximumTokens);
        if (!Regex.IsMatch(Revision, "^[0-9a-f]{40}$") || Dimensions is < 8 or > 4096 || Dimensions % 8 != 0 ||
            MaximumTokens is < 16 or > 8192 || MemoryBytes <= 0 ||
            Adapter is not ("embeddinggemma2" or "bert-mean" or "clip" or "native-text") ||
            (Adapter == "native-text") != (Recipe is not null) || Recipe is not null && SupportsImages)
            throw new InvalidDataException("The native embedding model descriptor is not supported.");
    }
}

public sealed record ModelIndexState(string ModelUrl, string[] Kinds, int Dimensions, long Files, long Vectors,
    string Directory, string Provider, string Device)
{
    public string KindLabel => string.Join(", ", Kinds);
    public string Details => $"{Dimensions} dimensions / {Files:N0} files / {Vectors:N0} passages / {Provider} / {Device}";
}
public sealed record ModelReindexState(string[] Kinds, bool Completed = false, long Succeeded = 0, long Failed = 0,
    string? OperationId = null, bool DiscoveryComplete = false, long Partial = 0, long Skipped = 0, long Removed = 0)
{
    public string Message => !Completed
        ? "Configuration saved. Affected files are being reindexed in the background."
        : Failed > 0 || Partial > 0
            ? $"Configuration saved. Reindexing finished with errors: {Succeeded:N0} succeeded; {Partial:N0} partial; {Failed:N0} failed; {Skipped:N0} skipped. See Indexed folders and logs."
            : Skipped > 0 || Removed > 0
                ? $"Configuration saved. Reindexing complete. {Succeeded:N0} indexed; {Skipped:N0} skipped (no searchable text); {Removed:N0} removed."
            : $"Configuration saved. Reindexing complete. {Succeeded:N0} affected files indexed.";
}

public sealed record ModelConfigurationState(ModelSelection Selection, IReadOnlyList<ModelIndexState> Indexes, string Message = "",
    ModelReindexState? Reindex = null, ImagePipelineStatus? ImagePipeline = null);
public sealed record ModelApplyResult(bool Changed, ModelSelection Selection, string[] ReindexedKinds);

public sealed class ModelConfigurationException(string message, Exception inner) : InvalidOperationException(message, inner);
