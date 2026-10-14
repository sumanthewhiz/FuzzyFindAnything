using System.IO;
using System.Xml;
using System.Xml.Linq;

namespace FuzzyFindAnything.App.Services;

public sealed record ApplicationSettings(string Theme, bool LoggingEnabled, string LogDirectory, double MinimumSimilarity = 0.60);

public sealed class SettingsService
{
    public string FilePath { get; }
    public ApplicationSettings Current { get; private set; }
    private readonly string defaultLogDirectory;

    public SettingsService(string dataDirectory)
    {
        FilePath = Path.Combine(dataDirectory, "settings.xml");
        defaultLogDirectory = Path.Combine(dataDirectory, "Logs");
        Current = Load();
        if (!File.Exists(FilePath))
            Save(Current);
    }

    private ApplicationSettings Load()
    {
        if (!File.Exists(FilePath))
            return new ApplicationSettings("Light", true, defaultLogDirectory);
        try
        {
            using var reader = XmlReader.Create(FilePath, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 1_048_576
            });
            var root = XDocument.Load(reader).Root;
            if (root?.Name != "FuzzyFindAnythingConfiguration" || (string?)root.Attribute("version") != "1")
                throw new InvalidDataException("The configuration has an unsupported format or version.");
            string theme = (string?)root.Element("Appearance")?.Attribute("theme") ?? "Light";
            var logging = root.Element("Logging");
            if (!bool.TryParse((string?)logging?.Attribute("enabled") ?? "true", out bool enabled))
                throw new InvalidDataException("The logging enabled setting must be true or false.");
            string directory = (string?)logging?.Attribute("directory") ?? defaultLogDirectory;
            string? similarity = (string?)root.Element("Search")?.Attribute("minimumSimilarity");
            var settings = new ApplicationSettings(theme, enabled, directory,
                similarity is null ? 0.60 : XmlConvert.ToDouble(similarity));
            Validate(settings);
            return settings with { LogDirectory = Path.GetFullPath(directory) };
        }
        catch (Exception exception) when (exception is XmlException or InvalidDataException or ArgumentException or FormatException or OverflowException)
        {
            throw new InvalidDataException($"Cannot read configuration at {FilePath}: {exception.Message}", exception);
        }
    }

    public void Save(ApplicationSettings settings)
    {
        Validate(settings);
        settings = settings with { LogDirectory = Path.GetFullPath(settings.LogDirectory) };
        if (settings.LoggingEnabled && (!File.Exists(FilePath) || !Current.LoggingEnabled ||
            !settings.LogDirectory.Equals(Current.LogDirectory, StringComparison.OrdinalIgnoreCase)))
            ValidateLogDirectory(settings.LogDirectory);
        string directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".settings-{Guid.NewGuid():N}.tmp");
        try
        {
            var document = new XDocument(
                new XElement("FuzzyFindAnythingConfiguration", new XAttribute("version", "1"),
                    new XElement("Appearance", new XAttribute("theme", settings.Theme)),
                    new XElement("Search", new XAttribute("minimumSimilarity", settings.MinimumSimilarity)),
                    new XElement("Logging", new XAttribute("enabled", settings.LoggingEnabled),
                        new XAttribute("directory", settings.LogDirectory))));
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                document.Save(stream);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, FilePath, overwrite: true);
            Current = settings;
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static void Validate(ApplicationSettings settings)
    {
        if (settings.Theme is not ("Light" or "Dark"))
            throw new InvalidDataException($"Unknown appearance theme: {settings.Theme}.");
        if (string.IsNullOrWhiteSpace(settings.LogDirectory) || !Path.IsPathFullyQualified(settings.LogDirectory))
            throw new InvalidDataException("The logging directory must be an absolute path.");
        if (!double.IsFinite(settings.MinimumSimilarity) || settings.MinimumSimilarity is < 0 or > 0.9)
            throw new InvalidDataException("Minimum similarity must be a finite number between 0.00 and 0.90.");
    }

    private static void ValidateLogDirectory(string directory)
    {
        Directory.CreateDirectory(directory);
        string probe = Path.Combine(directory, $".ffa-log-write-test-{Guid.NewGuid():N}.tmp");
        using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            1, FileOptions.DeleteOnClose);
        stream.WriteByte(0);
        stream.Flush(flushToDisk: true);
    }
}
