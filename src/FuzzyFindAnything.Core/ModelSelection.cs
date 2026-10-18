using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace FuzzyFindAnything.Core;

public sealed record ModelSelection(string Text, string Code, string Image)
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
    public ModelSelection Normalize() => new(Canonical(Text), Canonical(Code), Canonical(Image));
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
        if (document.Root?.Name != "FuzzyFindAnythingConfiguration" || (string?)document.Root.Attribute("version") != "1")
            throw new InvalidDataException("Unsupported application configuration.");
        return document;
    }

    public XElement ToXml() => new("Models", new XAttribute("text", Text), new XAttribute("code", Code), new XAttribute("image", Image));
}

public sealed record EmbeddingSpace(string ModelUrl, string Revision, string Adapter, int Dimensions,
    int MaximumTokens, bool SupportsImages, long MemoryBytes)
{
    public static EmbeddingSpace Default { get; } = new(ModelSelection.DefaultUrl, ModelContract.Revision,
        "embeddinggemma2", ModelContract.Dimensions, 8192, true, 3L << 30);
    public string Fingerprint => this == Default ? ModelContract.Fingerprint :
        $"{ModelUrl}@{Revision}:{Dimensions}:f32:{Adapter}:v1";
    public string Key => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Fingerprint))).ToLowerInvariant()[..24];
    public float[] Normalize(ReadOnlySpan<float> values) => ModelContract.Normalize(values, Dimensions);
    public float[] Vector(byte[] values) => ModelContract.Vector(values, Dimensions);

    public void Validate()
    {
        _ = ModelSelection.Canonical(ModelUrl);
        if (!Regex.IsMatch(Revision, "^[0-9a-f]{40}$") || Dimensions is < 8 or > 4096 || Dimensions % 8 != 0 ||
            MaximumTokens is < 16 or > 8192 || MemoryBytes <= 0 ||
            Adapter is not ("embeddinggemma2" or "bert-mean" or "clip"))
            throw new InvalidDataException("The native embedding model descriptor is not supported.");
    }
}

public sealed record ModelIndexState(string ModelUrl, string[] Kinds, int Dimensions, long Files, long Vectors,
    string Directory, string Provider, string Device)
{
    public string KindLabel => string.Join(", ", Kinds);
    public string Details => $"{Dimensions} dimensions / {Files:N0} files / {Vectors:N0} passages / {Provider} / {Device}";
}
public sealed record ModelConfigurationState(ModelSelection Selection, IReadOnlyList<ModelIndexState> Indexes, string Message = "");
public sealed record ModelApplyResult(bool Changed, ModelSelection Selection, string[] ReindexedKinds);
