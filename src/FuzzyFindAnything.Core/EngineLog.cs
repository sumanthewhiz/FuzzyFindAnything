using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace FuzzyFindAnything.Core;

public sealed class EngineLog
{
    private readonly object gate = new();
    private readonly string dataDirectory;
    private bool enabled;
    private string directory;
    private string error = "";

    public EngineLog(string dataDirectory)
    {
        this.dataDirectory = dataDirectory;
        directory = Path.Combine(dataDirectory, "Logs");
        var settings = ReadConfiguration();
        enabled = settings.Enabled;
        directory = settings.Directory;
        try { ValidateDestination(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { error = "Logging is unavailable: " + exception.Message; }
    }
    public LogState State { get { lock (gate) return new(enabled, directory, error); } }
    private LogState ReadConfiguration()
    {
        string path = Path.Combine(dataDirectory, "settings.xml");
        if (!File.Exists(path)) return new(true, Path.Combine(dataDirectory, "Logs"), "");
        using var reader = XmlReader.Create(path, new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1_048_576 });
        var root = XDocument.Load(reader).Root;
        if (root?.Name != "FuzzyFindAnythingConfiguration" || (string?)root.Attribute("version") != "1")
            throw new InvalidDataException("Unsupported application configuration.");
        var logging = root.Element("Logging");
        if (!bool.TryParse((string?)logging?.Attribute("enabled") ?? "true", out bool isEnabled))
            throw new InvalidDataException("Logging must be true or false.");
        string location = (string?)logging?.Attribute("directory") ?? Path.Combine(dataDirectory, "Logs");
        if (!Path.IsPathFullyQualified(location)) throw new InvalidDataException("The log location must be absolute.");
        return new(isEnabled, Path.GetFullPath(location), "");
    }
    private void ValidateDestination()
    {
        if (!enabled) return;
        Directory.CreateDirectory(directory);
        foreach (string file in new[] { "engine.log", "indexer.jsonl" })
        {
            using var stream = new FileStream(Path.Combine(directory, file), FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        }
    }
    public void Reload()
    {
        LogState next = ReadConfiguration();
        lock (gate)
        {
            var previous = new LogState(enabled, directory, error);
            enabled = next.Enabled; directory = next.Directory; error = "";
            try { ValidateDestination(); }
            catch { enabled = previous.Enabled; directory = previous.Directory; error = previous.Error; throw; }
        }
    }
    public bool Owns(string path)
    {
        lock (gate)
        {
            if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), directory, StringComparison.OrdinalIgnoreCase)) return false;
            string name = Path.GetFileName(path);
            return new[] { "app.log", "engine.log", "native-engine.log", "indexer.jsonl" }
                .Any(prefix => name.Equals(prefix, StringComparison.OrdinalIgnoreCase) || name.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase));
        }
    }
    public void Write(string component, string message) =>
        Append("engine.log", $"{DateTimeOffset.UtcNow:O} {component}: {message}", 4_000_000);
    public void RecordFile(IndexJob job, string outcome, double elapsedMs, int passages = 0, Exception? failure = null,
        string? code = null, string? warning = null) =>
        Append("indexer.jsonl", JsonSerializer.Serialize(new
        {
            timestamp = DateTimeOffset.UtcNow.ToString("O"),
            path = job.Path,
            kind = job.Kind,
            outcome,
            duration_ms = Math.Round(elapsedMs, 1),
            attempt = job.Attempts + 1,
            revision = job.Revision,
            passages,
            warning,
            error = failure is null ? null : new { type = failure.GetType().Name, code, message = failure.Message, details = failure.ToString() }
        }, Protocol.Json), 8_000_000);

    private void Append(string name, string value, long maximumBytes)
    {
        lock (gate)
        {
            if (!enabled || error.Length != 0) return;
            try
            {
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, name);
                if (System.IO.File.Exists(path) && new FileInfo(path).Length >= maximumBytes)
                    for (int index = 3; index >= 1; index--)
                    {
                        string source = index == 1 ? path : path + "." + (index - 1);
                        if (System.IO.File.Exists(source)) System.IO.File.Move(source, path + "." + index, overwrite: true);
                    }
                System.IO.File.AppendAllText(path, value + "\n", new UTF8Encoding(false));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { error = $"Logging stopped at {directory}: {exception.Message}. Choose a writable location or re-enable logging."; }
        }
    }
}
