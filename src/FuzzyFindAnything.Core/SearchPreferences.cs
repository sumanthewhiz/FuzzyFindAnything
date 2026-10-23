using System.Xml;
using System.Xml.Linq;

namespace FuzzyFindAnything.Core;

public sealed record SearchPreferences(bool Documents = true, bool Code = true, bool Images = true,
    double DocumentThreshold = 0.60, double CodeThreshold = 0.60, double ImageThreshold = 0.60)
{
    public string[] Kinds => new[] { Documents ? "Document" : null, Code ? "Code" : null, Images ? "Image" : null }.OfType<string>().ToArray();
    public double ThresholdFor(string kind) => kind switch
    {
        "Document" => DocumentThreshold,
        "Code" => CodeThreshold,
        "Image" => ImageThreshold,
        _ => throw new ArgumentException("Unknown search type.")
    };
    public void Validate()
    {
        if (!Documents && !Code && !Images) throw new ArgumentException("Select at least one search type.");
        if (new[] { DocumentThreshold, CodeThreshold, ImageThreshold }.Any(value => !double.IsFinite(value) || value is < 0 or > 0.9))
            throw new ArgumentException("Each minimum similarity must be a finite number between 0.00 and 0.90.");
    }
    public static SearchPreferences Read(XElement root)
    {
        var search = root.Element("Search");
        string? legacy = (string?)search?.Attribute("minimumSimilarity");
        double initial = legacy is null ? 0.60 : XmlConvert.ToDouble(legacy);
        (bool Enabled, double Threshold) ReadType(string name)
        {
            var item = search?.Element(name);
            return ((string?)item?.Attribute("enabled") is { } enabled ? XmlConvert.ToBoolean(enabled) : true,
                (string?)item?.Attribute("minimumSimilarity") is { } threshold ? XmlConvert.ToDouble(threshold) : initial);
        }
        if (legacy is not null && search?.HasElements == true)
            throw new InvalidDataException("Search preferences cannot mix global and per-type thresholds.");
        var document = ReadType("Documents"); var code = ReadType("Code"); var image = ReadType("Images");
        var result = new SearchPreferences(document.Enabled, code.Enabled, image.Enabled, document.Threshold, code.Threshold, image.Threshold);
        result.Validate();
        return result;
    }
    public XElement ToXml()
    {
        Validate();
        XElement Type(string name, bool enabled, double threshold) => new(name, new XAttribute("enabled", enabled),
            new XAttribute("minimumSimilarity", XmlConvert.ToString(threshold)));
        return new("Search", Type("Documents", Documents, DocumentThreshold), Type("Code", Code, CodeThreshold), Type("Images", Images, ImageThreshold));
    }
}
