using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
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
    private CancellationTokenSource? modelInspection;
    private long inspectionGeneration;
    private bool modelInspectionStarted;
    private readonly SemaphoreSlim providerGate = new(1, 1);
    private CancellationTokenSource? providerDiscovery;

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
        engine.StatusChanged += status => Dispatcher.InvokeAsync(() =>
        {
            viewModel.Apply(status);
            if (!modelInspectionStarted) { modelInspectionStarted = true; _ = InitializeModelSourcesAsync(); }
        });
        engine.StartupChanged += status => Dispatcher.InvokeAsync(() => viewModel.ApplyStartup(status));
        engine.Warning += message => Dispatcher.InvokeAsync(() => viewModel.Error = message);
        engine.ModelStatusChanged += message => Dispatcher.InvokeAsync(() => viewModel.ModelConfigurationMessage = message);
        viewModel.SearchOptionsChanged += SaveSearchOptions;
        viewModel.ImageProcessing.OptionsChanged += () =>
        {
            if (engineStarted && !viewModel.ConfigurationBusy) _ = InspectModelsAsync();
        };
        viewModel.ModelSourceChanged += (source, reload) =>
        {
            if (!engineStarted || viewModel.ConfigurationBusy) return;
            if (reload && source.IsMicrosoft) _ = RefreshProviderSourcesAsync(false);
            else _ = InspectModelsAsync();
        };
        NetworkChange.NetworkAddressChanged += ProviderNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += ProviderNetworkChanged;
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
                "search", new { query = viewModel.Query.Trim(), options = viewModel.SearchOptions, limit = 50 },
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
            var selection = viewModel.DraftModels;
            if (selection == settings.Current.ModelSelection.Normalize()) return;
            viewModel.Error = "";
            viewModel.ConfigurationBusy = true;
            viewModel.BeginModelConfiguration();
            var result = await engine.RequestAsync<ModelApplyResult>("apply_models", selection, TimeSpan.FromMinutes(30));
            settings.Reload();
            viewModel.ApplySettings(settings.Current, settings.FilePath);
            viewModel.FinishModelConfiguration(result);
            if (result.Changed)
            {
                CancelPendingSearch(); thumbnailLifetime.Cancel(); viewModel.ClearResults();
            }
        }
        catch (Exception exception)
        {
            viewModel.ModelConfigurationMessage = exception is EngineRequestException { ConfigurationApplied: true }
                ? "Configuration applied, but indexing did not finish: " + exception.Message
                : "Model configuration was not applied: " + exception.Message;
            ShowError(viewModel.ModelConfigurationMessage);
        }
        finally { viewModel.ConfigurationBusy = false; _ = InspectModelsAsync(); }
    }

    private void ModelUrl_Changed(object sender, TextChangedEventArgs args)
    {
        if (engineStarted && !viewModel.ConfigurationBusy) _ = InspectModelsAsync();
    }
    private Task InitializeModelSourcesAsync() => RefreshProviderSourcesAsync(false);
    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!ReferenceEquals(args.OriginalSource, MainTabs) || !engineStarted || !viewModel.IsConnected ||
            viewModel.ConfigurationBusy || ModelsPage?.IsSelected != true) return;
        if (providerDiscovery is null) _ = RefreshProviderSourcesAsync(false);
    }
    private void ProviderNetworkChanged(object? sender, EventArgs args)
    {
        if (Dispatcher.HasShutdownStarted) return;
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (quitting) return;
            providerDiscovery?.Cancel();
            foreach (var source in viewModel.ModelSources)
                source.SetAvailability(false, "Model availability must be verified for the current connection.");
            if (viewModel.IsConnected && !viewModel.ConfigurationBusy) _ = RefreshProviderSourcesAsync(false);
        });
    }
    protected override void OnClosed(EventArgs args)
    {
        NetworkChange.NetworkAddressChanged -= ProviderNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= ProviderNetworkChanged;
        providerDiscovery?.Cancel();
        base.OnClosed(args);
    }
    private async void RefreshProviderVersions_Click(object sender, RoutedEventArgs args) => await RefreshProviderSourcesAsync(true);
    private async Task RefreshProviderSourcesAsync(bool refresh)
    {
        providerDiscovery?.Cancel();
        using var request = new CancellationTokenSource();
        providerDiscovery = request;
        foreach (var source in viewModel.ModelSources)
        {
            source.IsLoading = true;
            source.SetAvailability(false, "Checking the current connection and available model sources...");
        }
        viewModel.SourceAvailabilityMessage = "Checking available local model sources...";
        bool entered = false, registryVerified = false;
        try
        {
            await providerGate.WaitAsync(request.Token);
            entered = true;
            // Keep the slot until the remote call finishes, even if a newer UI request supersedes this one.
            var catalog = await engine.RequestAsync<ProviderCatalog>("provider_catalog",
                new { provider = "microsoft", version = "", refresh }, TimeSpan.FromMinutes(3));
            request.Token.ThrowIfCancellationRequested();
            registryVerified = catalog.InternalAccessVerified;
            foreach (var source in viewModel.ModelSources) source.Discover(catalog);
            if (registryVerified)
            {
                foreach (var source in viewModel.ModelSources.Where(source => !source.IsMicrosoft ||
                    source.Version.Length == 0 || source.Version == catalog.Version))
                    source.Apply(catalog, complete: false);
                foreach (var group in viewModel.ModelSources.Where(source => source.IsMicrosoft && source.Version != catalog.Version)
                    .GroupBy(source => source.Version).ToArray())
                {
                    var selected = await engine.RequestAsync<ProviderCatalog>("provider_catalog",
                        new { provider = "microsoft", version = group.Key, refresh }, TimeSpan.FromMinutes(3));
                    request.Token.ThrowIfCancellationRequested();
                    if (!selected.InternalAccessVerified)
                    {
                        foreach (var source in viewModel.ModelSources) source.Discover(selected);
                        registryVerified = false;
                        break;
                    }
                    foreach (var source in group) source.Apply(selected, complete: false);
                }
            }
            viewModel.SourceAvailabilityMessage = registryVerified
                ? "Available model sources and device choices are up to date."
                : "Hugging Face is available. Optional local providers are unavailable for this connection.";
            viewModel.ProviderDiscoveryCompleted();
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or TimeoutException or InvalidOperationException)
        {
            if (!request.IsCancellationRequested)
            {
                foreach (var source in viewModel.ModelSources)
                {
                    if (!registryVerified) source.SetAvailability(false, error.Message);
                    else if (source.IsLoading) source.Invalidate(error.Message);
                }
                viewModel.SourceAvailabilityMessage = "Model source discovery did not finish. Refresh to retry.";
                viewModel.ProviderDiscoveryFailed(error.Message);
            }
        }
        finally
        {
            if (entered) providerGate.Release();
            if (ReferenceEquals(providerDiscovery, request))
            {
                providerDiscovery = null;
                foreach (var source in viewModel.ModelSources) source.IsLoading = false;
                if (!quitting) _ = InspectModelsAsync();
            }
        }
    }
    private async Task InspectModelsAsync()
    {
        modelInspection?.Cancel();
        var request = new CancellationTokenSource();
        modelInspection = request;
        long generation = ++inspectionGeneration;
        try
        {
            await Task.Delay(450, request.Token);
            if (!viewModel.IsConnected || viewModel.ConfigurationBusy || providerDiscovery is not null) return;
            ModelSelection selection;
            try { selection = viewModel.DraftModels; }
            catch (ArgumentException error)
            {
                if (viewModel.ImageProcessing.IsVisible)
                    viewModel.ImageRequirements.Unavailable("Image", viewModel.ImageModelUrl, error.Message);
                foreach (var (kind, url, target) in new[]
                {
                    ("Document", viewModel.TextModelUrl, viewModel.TextRequirements),
                    ("Code", viewModel.CodeModelUrl, viewModel.CodeRequirements),
                    ("Image", viewModel.ImageModelUrl, viewModel.ImageRequirements)
                })
                {
                    var source = viewModel.ModelSources.Single(source => source.Kind == kind);
                    try { _ = source.Reference(url); }
                    catch (ArgumentException) { target.Unavailable(kind, url, error.Message); }
                }
                return;
            }
            var result = await engine.RequestAsync<ModelInspection>("inspect_models", selection,
                TimeSpan.FromSeconds(45), request.Token);
            if (generation == inspectionGeneration) viewModel.ApplyModelInspection(result);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception error) when (error is InvalidOperationException or TimeoutException or IOException)
        {
            if (generation == inspectionGeneration)
            {
                viewModel.TextRequirements.Unavailable("Document", viewModel.TextModelUrl, error.Message);
                viewModel.CodeRequirements.Unavailable("Code", viewModel.CodeModelUrl, error.Message);
                viewModel.ImageRequirements.Unavailable("Image", viewModel.ImageModelUrl, error.Message);
            }
        }
        finally
        {
            if (ReferenceEquals(modelInspection, request)) modelInspection = null;
            request.Dispose();
        }
    }
    private async void CancelModelChange_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            var result = await engine.RequestAsync<JsonElement>("cancel_model_change");
            viewModel.ModelConfigurationMessage = result.GetProperty("cancelled").GetBoolean()
                ? "Cancelling model validation. The applied configuration will be retained."
                : "The configuration is already applied. Background indexing will resume after restart if you exit.";
        }
        catch (Exception error) { ShowError("Could not cancel the model change: " + error.Message); }
    }

    private async void RetryImageTasks_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            var result = await engine.RequestAsync<JsonElement>("retry_image_tasks");
            viewModel.ShowImageRetry(result.GetProperty("files").GetInt32());
        }
        catch (Exception error) { ShowError("Could not retry image tasks: " + error.Message); }
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

    private void SaveSearchOptions()
    {
        if (!IsLoaded || updatingSimilarity || viewModel.ConfigurationBusy ||
            viewModel.SearchOptions == settings.Current.SearchSettings)
            return;
        updatingSimilarity = true;
        try
        {
            settings.Save(settings.Current with { Search = viewModel.SearchOptions });
            CancelPendingSearch(); thumbnailLifetime.Cancel(); viewModel.ClearResults();
            viewModel.EmptyTitle = "Search options updated";
            viewModel.EmptyHint = "Run Search to use the selected file types and their minimum similarities.";
        }
        catch (Exception exception)
        {
            viewModel.RestoreSearchOptions(settings.Current.SearchSettings);
            ShowError($"Could not save search preferences: {exception.Message}");
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
                image.Thumbnail = await ThumbnailLoader.LoadAsync(image.Result, cancellationToken);
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
        modelInspection?.Cancel();
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
                "FuzzyFindAnything 2.0", MessageBoxButton.OK, MessageBoxImage.Warning);
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
