using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using FuzzyFindAnything.App.Services;
using FuzzyFindAnything.Core;

namespace FuzzyFindAnything.App;

public partial class App : Application
{
    private Mutex? instanceMutex;
    private readonly CancellationTokenSource activationLifetime = new();
    private bool ownsMutex;
    public bool AllowExit { get; set; }

    protected override async void OnStartup(StartupEventArgs args)
    {
        base.OnStartup(args);
        try
        {
            CpuBudget.Apply();
            string dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FuzzyFindAnything");
            if (args.Args.Length > 0)
            {
                if (args.Args.Length != 2 || args.Args[0] != "--data-dir")
                    throw new ArgumentException("Usage: FuzzyFindAnything.exe [--data-dir <local index directory>]");
                dataDirectory = Path.GetFullPath(args.Args[1]);
            }
            string identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dataDirectory.ToUpperInvariant())))[..20];
            string name = "FuzzyFindAnything-" + identity;
            instanceMutex = new Mutex(true, "Local\\" + name, out ownsMutex);
            if (!ownsMutex)
            {
                using var client = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await client.ConnectAsync(5000);
                await using var writer = new StreamWriter(client) { AutoFlush = true };
                await writer.WriteLineAsync("show");
                Shutdown();
                return;
            }
            var settings = new SettingsService(dataDirectory);
            ThemeService.Apply(settings.Current.Theme);
            var window = new MainWindow(dataDirectory, settings);
            MainWindow = window;
            window.Show();
            _ = ListenForActivationAsync(name, window);
        }
        catch (Exception exception)
        {
            MessageBox.Show($"Fuzzy Find Anything could not start:\n\n{exception.Message}", "Fuzzy Find Anything",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private async Task ListenForActivationAsync(string name, MainWindow window)
    {
        try
        {
            while (!activationLifetime.IsCancellationRequested)
            {
                await using var server = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(activationLifetime.Token);
                using var reader = new StreamReader(server);
                if (await reader.ReadLineAsync(activationLifetime.Token) == "show")
                    await Dispatcher.InvokeAsync(window.Restore);
            }
        }
        catch (OperationCanceledException) when (activationLifetime.IsCancellationRequested)
        {
        }
        catch (IOException exception)
        {
            await Dispatcher.InvokeAsync(() => MessageBox.Show(window,
                $"The app is running, but single-instance activation is unavailable: {exception.Message}",
                "Fuzzy Find Anything", MessageBoxButton.OK, MessageBoxImage.Warning));
        }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs args)
    {
        AllowExit = true;
        base.OnSessionEnding(args);
    }

    protected override void OnExit(ExitEventArgs args)
    {
        activationLifetime.Cancel();
        if (ownsMutex)
            instanceMutex?.ReleaseMutex();
        instanceMutex?.Dispose();
        activationLifetime.Dispose();
        base.OnExit(args);
    }
}
