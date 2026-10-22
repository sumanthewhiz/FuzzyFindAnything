using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace FuzzyFindAnything.Core;

public sealed record ModelSelection(string Text, string Code, string Image,
    ModelOptions? TextOptions = null, ModelOptions? CodeOptions = null, ModelOptions? ImageOptions = null)
{
    public const string DefaultUrl = "https://huggingface.co/google/embeddinggemma-2";
    public static ModelSelection Default { get; } = new(DefaultUrl, DefaultUrl, DefaultUrl);
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
    public ModelSelection Normalize() => new(Canonical(Text), Canonical(Code), Canonical(Image),
        NormalizeOptions(TextOptions), NormalizeOptions(CodeOptions), NormalizeOptions(ImageOptions));
    private static ModelOptions? NormalizeOptions(ModelOptions? options) =>
        options?.Normalize() is { } value && value != ModelOptions.Default ? value : null;
    public bool RequiresVersion2 => TextOptions is not null || CodeOptions is not null || ImageOptions is not null ||
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
        if ((string?)root.Attribute("version") == "2")
        {
            if (models?.Attributes().Any(attribute => attribute.Name.LocalName is "text" or "code" or "image") == true)
                throw new InvalidDataException("Version-2 model settings require child bindings, not legacy URL attributes.");
            (string Url, ModelOptions? Options) ReadBinding(string name)
            {
                var element = models?.Element(name);
                if ((string?)element?.Attribute("mode") is { } mode && (name != "Image" || mode != "direct"))
                    throw new NotSupportedException("This build supports direct image embeddings only; image-understanding pipelines require a separate adapter.");
                if (element?.Attribute("recipe") is not null)
                    throw new InvalidDataException("Recipe selection is resolved by the native registry, not an editable configuration attribute.");
                return ((string?)element?.Attribute("modelUrl") ?? DefaultUrl,
                    NormalizeOptions(new((string?)element?.Attribute("queryProfile") ?? "",
                        (string?)element?.Attribute("devicePolicy") ?? "Automatic")));
            }
            var text = ReadBinding("Text"); var code = ReadBinding("Code"); var image = ReadBinding("Image");
            return new ModelSelection(text.Url, code.Url, image.Url, text.Options, code.Options, image.Options).Normalize();
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
        if (document.Root?.Name != "FuzzyFindAnythingConfiguration" || (string?)document.Root.Attribute("version") is not ("1" or "2"))
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
            var result = new XElement(name, new XAttribute("modelUrl", ForKind(kind)));
            if (options.QueryProfile.Length > 0) result.Add(new XAttribute("queryProfile", options.QueryProfile));
            if (options.Device != "Automatic") result.Add(new XAttribute("devicePolicy", options.Device));
            return result;
        }
        return new("Models", Binding("Text", "Document"), Binding("Code", "Code"), Binding("Image", "Image"));
    }
}

public sealed record EmbeddingSpace(string ModelUrl, string Revision, string Adapter, int Dimensions,
    int MaximumTokens, bool SupportsImages, long MemoryBytes, EmbeddingRecipe? Recipe = null, string? ArtifactUrl = null)
{
    public static EmbeddingSpace Default { get; } = new(ModelSelection.DefaultUrl, ModelContract.Revision,
        "embeddinggemma2", ModelContract.Dimensions, 8192, true, 3L << 30);
    public string Fingerprint => Recipe is not null
        ? $"{ArtifactUrl ?? ModelUrl}@{Revision}:{Dimensions}:f32:{Recipe.DocumentFingerprint}:native-v2"
        : this == Default ? ModelContract.Fingerprint :
        $"{ModelUrl}@{Revision}:{Dimensions}:f32:{Adapter}:v1";
    public string IndexingFingerprint => Recipe is null ? Fingerprint : $"{Fingerprint}:{MaximumTokens}:{Recipe.IndexingFingerprint}";
    public string CacheKey => Recipe is null ? Key : EmbeddingRecipe.Hash($"{ArtifactUrl ?? ModelUrl}@{Revision}:{Recipe.ContractFingerprint}")[..24];
    public string Key => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Fingerprint))).ToLowerInvariant()[..24];
    public float[] Normalize(ReadOnlySpan<float> values) => ModelContract.Normalize(values, Dimensions);
    public float[] Vector(byte[] values) => ModelContract.Vector(values, Dimensions);

    public void Validate()
    {
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
public sealed record ModelReindexState(string[] Kinds, bool Completed = false, long Succeeded = 0, long Failed = 0)
{
    public string Message => !Completed
        ? "Configuration saved. Affected files are being reindexed in the background."
        : Failed > 0
            ? $"Configuration saved. Reindexing finished with errors: {Succeeded:N0} succeeded; {Failed:N0} failed. See Indexed folders and logs."
            : $"Configuration saved. Reindexing complete. {Succeeded:N0} affected files indexed.";
}

public sealed record ModelConfigurationState(ModelSelection Selection, IReadOnlyList<ModelIndexState> Indexes, string Message = "",
    ModelReindexState? Reindex = null);
public sealed record ModelApplyResult(bool Changed, ModelSelection Selection, string[] ReindexedKinds);

public sealed class ModelConfigurationException(string message, Exception inner) : InvalidOperationException(message, inner);
