using System.Diagnostics;
using System.IO;
using System.Text;

namespace FuzzyFindAnything.App.Services;

public sealed class DiagnosticLog(ApplicationSettings settings)
{
    private readonly object sync = new();
    private ApplicationSettings settings = settings;
    private bool faulted;
    public event Action<string>? Failed;

    public void Configure(ApplicationSettings value)
    {
        lock (sync)
        {
            settings = value;
            faulted = false;
        }
    }

    public void App(string message) => Write("app.log", $"{DateTimeOffset.Now:O} {message}");
    public void Native(string message) => Write("native-engine.log", message);

    private void Write(string name, string message)
    {
        string? failure = null;
        lock (sync)
        {
            if (!settings.LoggingEnabled || faulted)
                return;
            try
            {
                Directory.CreateDirectory(settings.LogDirectory);
                string path = Path.Combine(settings.LogDirectory, name);
                if (File.Exists(path) && new FileInfo(path).Length >= 4_000_000)
                {
                    for (int index = 3; index >= 1; index--)
                    {
                        string source = index == 1 ? path : path + "." + (index - 1);
                        if (File.Exists(source))
                            File.Move(source, path + "." + index, overwrite: true);
                    }
                }
                File.AppendAllText(path, message + Environment.NewLine, new UTF8Encoding(false));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                faulted = true;
                failure = $"Diagnostic logging stopped at {settings.LogDirectory}: {exception.Message}. Choose a writable log location or re-enable logging.";
                Trace.TraceError(failure);
            }
        }
        if (failure is not null)
            Failed?.Invoke(failure);
    }
}
