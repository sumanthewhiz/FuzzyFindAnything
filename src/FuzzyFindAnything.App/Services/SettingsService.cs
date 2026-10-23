using System.IO;
using System.Xml;
using System.Xml.Linq;
using FuzzyFindAnything.Core;

namespace FuzzyFindAnything.App.Services;

public sealed record ApplicationSettings(string Theme, bool LoggingEnabled, string LogDirectory,
    ModelSelection? Models = null, SearchPreferences? Search = null)
{
    public ModelSelection ModelSelection => Models ?? Core.ModelSelection.Default;
    public SearchPreferences SearchSettings => Search ?? new();
}

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
            if (root?.Name != "FuzzyFindAnythingConfiguration" || (string?)root.Attribute("version") is not ("1" or "2" or "3"))
                throw new InvalidDataException("The configuration has an unsupported format or version.");
            string theme = (string?)root.Element("Appearance")?.Attribute("theme") ?? "Light";
            var logging = root.Element("Logging");
            if (!bool.TryParse((string?)logging?.Attribute("enabled") ?? "true", out bool enabled))
                throw new InvalidDataException("The logging enabled setting must be true or false.");
            string directory = (string?)logging?.Attribute("directory") ?? defaultLogDirectory;
            var settings = new ApplicationSettings(theme, enabled, directory, ModelSelection.Read(root), SearchPreferences.Read(root));
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
        settings = settings with { LogDirectory = Path.GetFullPath(settings.LogDirectory), Models = settings.ModelSelection.Normalize() };
        if (settings.LoggingEnabled && (!File.Exists(FilePath) || !Current.LoggingEnabled ||
            !settings.LogDirectory.Equals(Current.LogDirectory, StringComparison.OrdinalIgnoreCase)))
            ValidateLogDirectory(settings.LogDirectory);
        string directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".settings-{Guid.NewGuid():N}.tmp");
        try
        {
            XDocument document;
            if (File.Exists(FilePath))
            {
                using var reader = XmlReader.Create(FilePath, new XmlReaderSettings
                { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1_048_576 });
                document = XDocument.Load(reader);
            }
            else document = new XDocument(new XElement("FuzzyFindAnythingConfiguration", new XAttribute("version", "1")));
            var root = document.Root!;
            root.SetAttributeValue("version", "3");
            XElement Section(string name)
            {
                if (root.Element(name) is { } existing) return existing;
                var created = new XElement(name); root.Add(created); return created;
            }
            Section("Appearance").SetAttributeValue("theme", settings.Theme);
            root.Element("Search")?.Remove();
            root.Add(settings.SearchSettings.ToXml());
            root.Element("Models")?.Remove();
            root.Add(settings.ModelSelection.Normalize().ToXml(version2: true));
            Section("Logging").SetAttributeValue("enabled", settings.LoggingEnabled);
            Section("Logging").SetAttributeValue("directory", settings.LogDirectory);
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
        _ = settings.ModelSelection.Normalize();
        if (settings.Theme is not ("Light" or "Dark"))
            throw new InvalidDataException($"Unknown appearance theme: {settings.Theme}.");
        if (string.IsNullOrWhiteSpace(settings.LogDirectory) || !Path.IsPathFullyQualified(settings.LogDirectory))
            throw new InvalidDataException("The logging directory must be an absolute path.");
        try { settings.SearchSettings.Validate(); }
        catch (ArgumentException error) { throw new InvalidDataException(error.Message, error); }
    }

    public void Reload() => Current = Load();

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
