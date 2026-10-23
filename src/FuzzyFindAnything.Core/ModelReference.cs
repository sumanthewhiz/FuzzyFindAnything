using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace FuzzyFindAnything.Core;

public abstract record ModelReference
{
    public abstract string Key { get; }
    public static ModelReference Parse(string value)
    {
        if (Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == "provider")
        {
            string[] parts = uri.AbsolutePath.Split('/');
            if (!uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
                parts.Length != 3 || !Regex.IsMatch(uri.Host, "^[a-z][a-z0-9-]{0,63}$") ||
                !Regex.IsMatch(parts[1], @"^\d+\.\d+\.\d+(?:\.\d+)?(?:-[a-zA-Z0-9.-]+)?$") ||
                !Regex.IsMatch(parts[2], "^[a-zA-Z0-9][a-zA-Z0-9._-]{0,99}$"))
                throw new ArgumentException("Choose a provider model and an exact supported drop version.");
            return new ProviderModelReference(uri.Host, parts[1].ToLowerInvariant(), parts[2].ToLowerInvariant());
        }
        return new HuggingFaceModelReference(ModelSelection.Canonical(value!));
    }

    internal static string ReadBinding(XElement? element, bool providersAllowed, bool required = false)
    {
        string? provider = (string?)element?.Attribute("provider");
        if (provider is null or "HuggingFace")
        {
            if (element?.Attribute("modelId") is not null || element?.Attribute("version") is not null)
                throw new InvalidDataException("A Hugging Face binding must use a model-card URL.");
            if (required && element?.Attribute("modelUrl") is null)
                throw new InvalidDataException("A custom embedding model reference is required.");
            return ModelSelection.Canonical((string?)element?.Attribute("modelUrl") ?? ModelSelection.DefaultUrl);
        }
        if (!providersAllowed || element?.Attribute("modelUrl") is not null)
            throw new InvalidDataException("Provider model bindings require configuration version 4 and cannot also contain a model URL.");
        string version = (string?)element?.Attribute("version") ?? throw new InvalidDataException("A provider drop version is required.");
        string model = (string?)element?.Attribute("modelId") ?? throw new InvalidDataException("A provider model identifier is required.");
        return Parse($"provider://{provider}/{version}/{model}").Key;
    }

    internal void WriteBinding(XElement element)
    {
        if (this is ProviderModelReference provider)
        {
            element.Add(new XAttribute("provider", provider.Provider), new XAttribute("version", provider.Version),
                new XAttribute("modelId", provider.Model));
        }
        else element.Add(new XAttribute("modelUrl", Key));
    }
}

public sealed record HuggingFaceModelReference(string ModelUrl) : ModelReference
{
    public override string Key => ModelUrl;
}

public sealed record ProviderModelReference(string Provider, string Version, string Model) : ModelReference
{
    public override string Key => $"provider://{Provider}/{Version}/{Model}";
}
