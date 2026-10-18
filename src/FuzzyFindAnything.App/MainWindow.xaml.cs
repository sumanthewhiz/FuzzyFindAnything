using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FuzzyFindAnything.App.Services;
using FuzzyFindAnything.App.ViewModels;
using FuzzyFindAnything.Core;
using Microsoft.Win32;

namespace FuzzyFindAnything.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel viewModel = new();
    private readonly EngineClient engine;
    private readonly SettingsService settings;
    private bool quitting;
    private bool updatingAppearance;
    private bool updatingSimilarity;
    private bool engineStarted;
    private CancellationTokenSource thumbnailLifetime = new();
    private CancellationTokenSource? searchLifetime;
    private long searchGeneration;

    public MainWindow(string dataDirectory, SettingsService settings)
    {
        InitializeComponent();
        this.settings = settings;
        viewModel.IsDarkMode = settings.Current.Theme == "Dark";
        viewModel.ApplySettings(settings.Current, settings.FilePath);
        viewModel.ApplyModels(settings.Current.ModelSelection);
        SourceInitialized += (_, _) => ThemeService.ApplyTitleBar(this, viewModel.IsDarkMode);
        DataContext = viewModel;
        engine = new EngineClient(dataDirectory, settings.Current);
        engine.StatusChanged += status => Dispatcher.InvokeAsync(() => viewModel.Apply(status));
        engine.StartupChanged += status => Dispatcher.InvokeAsync(() => viewModel.ApplyStartup(status));
        engine.Warning += message => Dispatcher.InvokeAsync(() => viewModel.Error = message);
        engine.ModelStatusChanged += message => Dispatcher.InvokeAsync(() => viewModel.ModelConfigurationMessage = message);
        engine.Failed += message => Dispatcher.InvokeAsync(() =>
        {
            viewModel.IsConnected = false;
            viewModel.ModelState = "Error";
            viewModel.EngineMessage = "The engine stopped. Restart the app to resume.";
            ShowError(message);
        });
    }

    private void Window_Loaded(object sender, RoutedEventArgs args)
    {
        if (engineStarted)
            return;
        engineStarted = true;
        try
        {
            engine.Start();
            SearchInput.Focus();
        }
        catch (Exception exception)
        {
            viewModel.ModelState = "Error";
            viewModel.EngineMessage = "The local engine could not start.";
            ShowError(exception.Message);
        }
    }

    private async Task SearchAsync()
    {
        if (!viewModel.CanSearch)
            return;
        if (string.IsNullOrWhiteSpace(viewModel.Query))
        {
            ShowError("Enter a phrase that describes the file you want to find.");
            SearchInput.Focus();
            return;
        }
        thumbnailLifetime.Cancel();
        thumbnailLifetime.Dispose();
        thumbnailLifetime = new CancellationTokenSource();
        CancelPendingSearch();
        long generation = searchGeneration;
        using var requestLifetime = new CancellationTokenSource();
        searchLifetime = requestLifetime;
        viewModel.BeginSearch();
        var timer = Stopwatch.StartNew();
        try
        {
            var results = await engine.RequestAsync<List<SearchResult>>(
                "search", new { query = viewModel.Query.Trim(), threshold = viewModel.Threshold, limit = 50 },
                cancellationToken: requestLifetime.Token);
            if (generation != searchGeneration)
                return;
            viewModel.FinishSearch(results, timer.Elapsed);
            _ = LoadThumbnailsAsync(viewModel.ImageResults.ToArray(), thumbnailLifetime.Token);
        }
        catch (OperationCanceledException) when (requestLifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (generation == searchGeneration)
            {
                viewModel.SearchFailed();
                ShowError(exception.Message);
            }
        }
        finally
        {
            if (ReferenceEquals(searchLifetime, requestLifetime))
                searchLifetime = null;
        }
    }

    private void CancelPendingSearch()
    {
        searchGeneration++;
        searchLifetime?.Cancel();
        searchLifetime = null;
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs args)
    {
        CancelPendingSearch();
        thumbnailLifetime.Cancel();
        viewModel.ClearSearch();
        SearchInput.Focus();
    }

    private async void Search_Click(object sender, RoutedEventArgs args) => await SearchAsync();

    private async void ApplyModels_Click(object sender, RoutedEventArgs args)
    {
        if (!viewModel.CanApplyModels) return;
        try
        {
            var selection = new ModelSelection(viewModel.TextModelUrl, viewModel.CodeModelUrl, viewModel.ImageModelUrl).Normalize();
            if (selection == settings.Current.ModelSelection.Normalize()) return;
            viewModel.Error = "";
            viewModel.ConfigurationBusy = true;
            viewModel.ModelConfigurationMessage = "Validating models and available execution devices...";
            var result = await engine.RequestAsync<ModelApplyResult>("apply_models", selection, TimeSpan.FromMinutes(30));
            settings.Reload();
            viewModel.ApplySettings(settings.Current, settings.FilePath);
            viewModel.ApplyModels(result.Selection);
            if (result.Changed)
            {
                CancelPendingSearch(); thumbnailLifetime.Cancel(); viewModel.ClearResults();
                viewModel.ModelConfigurationMessage = "Configuration saved. Affected files are being reindexed in the background.";
            }
        }
        catch (Exception exception)
        {
            viewModel.ModelConfigurationMessage = "Model configuration was not applied: " + exception.Message;
            ShowError(viewModel.ModelConfigurationMessage);
        }
        finally { viewModel.ConfigurationBusy = false; }
    }

    private async void SearchInput_KeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key == Key.Enter)
        {
            args.Handled = true;
            await SearchAsync();
        }
        else if (args.Key == Key.Escape && viewModel.CanClearSearch)
        {
            args.Handled = true;
            ClearSearch_Click(sender, args);
        }
    }

    private void Theme_Changed(object sender, RoutedEventArgs args)
    {
        if (!IsLoaded || updatingAppearance)
            return;
        updatingAppearance = true;
        try
        {
            string theme = ThemeToggle.IsChecked == true ? "Dark" : "Light";
            if (theme == settings.Current.Theme)
                return;
            settings.Save(settings.Current with { Theme = theme });
            ThemeService.Apply(theme, this);
            viewModel.IsDarkMode = theme == "Dark";
        }
        catch (Exception exception)
        {
            ThemeToggle.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, viewModel.IsDarkMode);
            ShowError($"Could not save appearance settings: {exception.Message}");
        }
        finally
        {
            updatingAppearance = false;
        }
    }

    private void Similarity_Changed(object sender, RoutedPropertyChangedEventArgs<double> args)
    {
        if (!IsLoaded || updatingSimilarity || viewModel.ConfigurationBusy ||
            args.NewValue == settings.Current.MinimumSimilarity)
            return;
        updatingSimilarity = true;
        try
        {
            settings.Save(settings.Current with { MinimumSimilarity = args.NewValue });
        }
        catch (Exception exception)
        {
            viewModel.Threshold = settings.Current.MinimumSimilarity;
            SimilaritySlider.SetCurrentValue(System.Windows.Controls.Primitives.RangeBase.ValueProperty, viewModel.Threshold);
            ShowError($"Could not save minimum similarity: {exception.Message}");
        }
        finally
        {
            updatingSimilarity = false;
        }
    }

    private async void Logging_Changed(object sender, RoutedEventArgs args)
    {
        if (!IsLoaded || viewModel.ConfigurationBusy)
            return;
        if ((LoggingToggle.IsChecked == true) == settings.Current.LoggingEnabled)
            return;
        await UpdateLoggingAsync(settings.Current with { LoggingEnabled = LoggingToggle.IsChecked == true });
    }

    private async void ChangeLogDirectory_Click(object sender, RoutedEventArgs args)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose a log folder",
            InitialDirectory = settings.Current.LogDirectory,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true)
            await UpdateLoggingAsync(settings.Current with { LogDirectory = dialog.FolderName });
    }

    private async Task UpdateLoggingAsync(ApplicationSettings updated)
    {
        viewModel.ConfigurationBusy = true;
        ApplicationSettings previous = settings.Current;
        try
        {
            settings.Save(updated);
            try
            {
                viewModel.Apply(await engine.RequestAsync<EngineStatus>("reload_configuration"));
            }
            catch (Exception applyError)
            {
                try
                {
                    settings.Save(previous);
                    viewModel.Apply(await engine.RequestAsync<EngineStatus>("reload_configuration", timeout: TimeSpan.FromSeconds(30)));
                }
                catch (Exception rollbackError)
                {
                    throw new InvalidOperationException(
                        $"Logging configuration could not be applied or fully restored. Restart after checking {settings.FilePath}. " +
                        $"Apply error: {applyError.Message} Restore error: {rollbackError.Message}", rollbackError);
                }
                throw;
            }
            engine.ConfigureLogging(settings.Current);
        }
        catch (Exception exception)
        {
            ShowError($"Could not update logging configuration: {exception.Message}");
        }
        finally
        {
            viewModel.ApplySettings(settings.Current, settings.FilePath);
            LoggingToggle.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, settings.Current.LoggingEnabled);
            viewModel.ConfigurationBusy = false;
        }
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs args)
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder to index", Multiselect = false };
        if (dialog.ShowDialog(this) != true)
            return;
        viewModel.FoldersBusy = true;
        try
        {
            await engine.RequestAsync<JsonElement>("add_folder", new { path = dialog.FolderName });
            viewModel.Apply(await engine.RequestAsync<EngineStatus>("status"));
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
        finally
        {
            viewModel.FoldersBusy = false;
        }
    }

    private async void RemoveFolders_Click(object sender, RoutedEventArgs args)
    {
        int[] identifiers = viewModel.Folders.Where(folder => folder.IsSelected).Select(folder => folder.Id).ToArray();
        if (identifiers.Length == 0)
            return;
        viewModel.FoldersBusy = true;
        CancelPendingSearch();
        thumbnailLifetime.Cancel();
        viewModel.ClearResults();
        try
        {
            viewModel.Apply(await engine.RequestAsync<EngineStatus>("remove_folders", new { ids = identifiers }));
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
        finally
        {
            viewModel.FoldersBusy = false;
        }
    }

    private void Result_Click(object sender, RoutedEventArgs args)
    {
        if (sender is Button { DataContext: SearchResult result })
            OpenResult(result);
    }

    private void Results_KeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key == Key.Enter && sender is ListBox { SelectedItem: SearchResult result })
        {
            args.Handled = true;
            OpenResult(result);
        }
    }

    private void Image_Click(object sender, RoutedEventArgs args)
    {
        if (sender is Button { DataContext: ImageResultViewModel image })
            OpenResult(image.Result);
    }

    private void Images_KeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key == Key.Enter && ImagesList.SelectedItem is ImageResultViewModel image)
        {
            args.Handled = true;
            OpenResult(image.Result);
        }
    }

    private async Task LoadThumbnailsAsync(ImageResultViewModel[] images, CancellationToken cancellationToken)
    {
        await Task.WhenAll(images.Select(async image =>
        {
            try
            {
                image.Thumbnail = await ThumbnailLoader.LoadAsync(image.Result.Path, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                image.ThumbnailMessage = $"Preview unavailable: {exception.Message}";
                engine.Log($"Thumbnail failed for {image.Result.Path}: {exception}");
            }
        }));
    }

    private void OpenResult(SearchResult result)
    {
        try
        {
            if (!File.Exists(result.Path))
                throw new FileNotFoundException("This file was moved or deleted. Search again for up-to-date results.");
            Process.Start(new ProcessStartInfo(result.Path) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            ShowError($"Could not open {result.Name}: {exception.Message}");
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs args)
    {
        if (!((App)Application.Current).AllowExit)
        {
            args.Cancel = true;
            HideToTray();
        }
    }

    protected override void OnStateChanged(EventArgs args)
    {
        base.OnStateChanged(args);
        if (WindowState == WindowState.Minimized && !quitting)
            HideToTray();
    }

    private void HideToTray()
    {
        ShowInTaskbar = false;
        Hide();
    }

    private async void Quit_Click(object sender, RoutedEventArgs args) => await QuitAsync();

    public async Task QuitAsync()
    {
        if (quitting)
            return;
        quitting = true;
        CancelPendingSearch();
        thumbnailLifetime.Cancel();
        IsEnabled = false;
        viewModel.EngineMessage = "Saving the local index and stopping background work...";
        try
        {
            await engine.DisposeAsync();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"Shutdown encountered an error: {exception.Message}\nCommitted indexing work is recoverable on restart.",
                "Fuzzy Find Anything", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            ((App)Application.Current).AllowExit = true;
            Application.Current.Shutdown();
        }
    }

    private void ShowError(string message)
    {
        viewModel.Error = message;
        engine.Log(message);
    }

    private void DismissError_Click(object sender, RoutedEventArgs args) => viewModel.Error = "";

    private void OpenLog_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            string path = settings.Current.LogDirectory;
            if (!Directory.Exists(path))
                throw new DirectoryNotFoundException("The log folder has not been created. Enable logging or choose a log location.");
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            ShowError($"Could not open the diagnostic log: {exception.Message}");
        }
    }

    private void Profile_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            if (sender is not Button { Tag: string address } ||
                address is not ("https://www.linkedin.com/in/ghoshsuman/" or "https://github.com/sumanthewhiz"))
                throw new InvalidOperationException("The profile link is not recognized.");
            Process.Start(new ProcessStartInfo(address) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            ShowError($"Could not open the profile: {exception.Message}");
        }
    }

    public void Restore()
    {
        if (quitting) return;
        ShowInTaskbar = true;
        WindowState = WindowState.Maximized;
        Show();
        Activate();
    }
}
