using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Globalization;
using System.Windows.Media.Imaging;
using FuzzyFindAnything.App.Services;
using FuzzyFindAnything.Core;

namespace FuzzyFindAnything.App.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        Changed(name);
    }

    protected void Changed([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class FolderViewModel : ObservableObject
{
    private bool selected;
    private FolderStatus data;

    public FolderViewModel(FolderStatus data) => this.data = data;
    public int Id => data.Id;
    public string Path => data.Path;
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\')) is { Length: > 0 } name ? name : Path;
    public string Status => data.Status;
    public string Detail => $"{data.Succeeded:N0} succeeded  |  {data.Failed:N0} failed" +
        (data.Partial > 0 ? $"  |  {data.Partial:N0} partial" : "") + (data.Skipped > 0 ? $"  |  {data.Skipped:N0} skipped" : "");
    public bool IsIndexed => Status == "Indexed";
    public bool IsIndeterminate => !IsIndexed && data.Total == 0 && data.Errors == 0;
    public double Progress => data.Total == 0 ? (IsIndexed ? 100 : 0) : 100d * data.Completed / data.Total;
    public string ProgressLabel => data.Total == 0 ? (IsIndexed ? "No supported files" : "Discovering files") : $"{data.Completed:N0} / {data.Total:N0} files";
    public bool IsSelected { get => selected; set => Set(ref selected, value); }

    public void Update(FolderStatus status)
    {
        data = status;
        Changed(null);
    }
}

public sealed class MainViewModel : ObservableObject
{
    public MainViewModel()
    {
        foreach (var requirement in new[] { TextRequirements, CodeRequirements, ImageRequirements })
            requirement.PropertyChanged += (_, _) => Changed(nameof(CanApplyModels));
        foreach (var source in ModelSources)
        {
            source.PropertyChanged += (_, _) => Changed(nameof(CanApplyModels));
            source.SelectionChanged += reload =>
            {
                var requirements = source.Kind == "Document" ? TextRequirements : source.Kind == "Code" ? CodeRequirements : ImageRequirements;
                requirements.Reset(false);
                if (source.Kind == "Image") ImageProcessing.Reset(source.IsMicrosoft ? "" : ImageModelUrl);
                ModelSourceChanged?.Invoke(source, reload);
            };
        }
    }
    private string query = "";
    private SearchPreferences searchOptions = new();
    public event Action? SearchOptionsChanged;
    private bool searching;
    private bool connected;
    private bool ready;
    private bool darkMode;
    private bool loggingEnabled = true;
    private bool configurationBusy;
    private bool foldersBusy;
    private string logDirectory = "";
    private string settingsPath = "";
    private string error = "";
    private string engineMessage = "Starting the local indexing engine...";
    private string modelState = "Starting";
    private int startupSequence = -1;
    private int startupPercent;
    private string startupStage = "Starting local engine";
    private string monitor = "Watching begins when you add a folder.";
    private string counts = "0 files / 0 passages";
    private string resultSummary = "Enter a search phrase to begin.";
    private string emptyTitle = "Search your index";
    private string emptyHint = "Add folders to index, then search their documents, code and images.";
    private string cpuSummary = "CPU limit";
    private string cpuDetail = "The application is restricted to at most half of the physical CPU cores.";
    private IndexMetadata? indexInfo;
    private string textModelUrl = ModelSelection.DefaultUrl;
    private string codeModelUrl = ModelSelection.DefaultUrl;
    private string imageModelUrl = ModelSelection.DefaultUrl;
    private string modelConfigurationMessage = "Models are checked for native embedding and device compatibility before indexes are changed.";
    private bool showModelReindexStatus = true;
    private ModelSelection? expectedReindexSelection;
    public ModelRequirementViewModel TextRequirements { get; } = new();
    public ModelRequirementViewModel CodeRequirements { get; } = new();
    public ModelRequirementViewModel ImageRequirements { get; } = new();
    public ModelSourceViewModel TextSource { get; } = new("Document");
    public ModelSourceViewModel CodeSource { get; } = new("Code");
    public ModelSourceViewModel ImageSource { get; } = new("Image");
    public IEnumerable<ModelSourceViewModel> ModelSources => [TextSource, CodeSource, ImageSource];
    public event Action<ModelSourceViewModel, bool>? ModelSourceChanged;
    public ImagePipelineViewModel ImageProcessing { get; } = new();
    private ImagePipelineStatus? imagePipelineStatus;
    public bool HasImagePipeline => imagePipelineStatus is not null;
    public string ImagePipelineSummary => imagePipelineStatus is { } image
        ? $"{image.ModelUrl} -> {image.EmbeddingModel}\n{string.Join(" + ", image.Tasks)}; " +
          $"{image.Generated:N0} complete, {image.Partial:N0} partial, {image.Failed:N0} failed, {image.Empty:N0} skipped. " +
          (image.Provider.Length > 0 ? $"{image.Provider} / {image.Device}" + (image.IsLoaded ? "" : " (idle; reloads on demand)") :
              "Generation loads on demand; existing image search is available.")
        : "";
    public bool CanRetryImages => CanConfigure && imagePipelineStatus is { } image && image.Partial + image.Failed > 0;
    public string TextModelUrl
    {
        get => textModelUrl;
        set { if (textModelUrl != value) TextRequirements.Reset(value.Trim().TrimEnd('/') == ModelSelection.DefaultUrl); Set(ref textModelUrl, value); }
    }
    public string CodeModelUrl
    {
        get => codeModelUrl;
        set { if (codeModelUrl != value) CodeRequirements.Reset(value.Trim().TrimEnd('/') == ModelSelection.DefaultUrl); Set(ref codeModelUrl, value); }
    }
    public string ImageModelUrl
    {
        get => imageModelUrl;
        set
        {
            if (imageModelUrl != value) { ImageRequirements.Reset(value.Trim().TrimEnd('/') == ModelSelection.DefaultUrl); ImageProcessing.Reset(value); }
            Set(ref imageModelUrl, value);
        }
    }
    public ModelSelection DraftModels => new ModelSelection(TextSource.Reference(TextModelUrl), CodeSource.Reference(CodeModelUrl), ImageSource.Reference(ImageModelUrl),
        TextSource.Options(TextRequirements.Options), CodeSource.Options(CodeRequirements.Options), ImageProcessing.IsVisible
            ? ImageRequirements.Options is { } options ? options with { QueryProfile = "" } : null : ImageSource.Options(ImageRequirements.Options),
        ImageProcessing.Options(ImageRequirements.SelectedQueryProfile)).Normalize();
    public string ModelConfigurationMessage { get => modelConfigurationMessage; set => Set(ref modelConfigurationMessage, value); }
    public ObservableCollection<ModelIndexState> ModelIndexes { get; } = [];
    public bool CanApplyModels => CanConfigure && !IsEngineLoading && !FoldersBusy && ModelSources.All(source => source.IsReady) &&
        new[] { TextRequirements, CodeRequirements, ImageRequirements }.All(requirement =>
            !requirement.RequiresQueryChoice || requirement.SelectedQueryProfile.Length > 0);

    public ObservableCollection<FolderViewModel> Folders { get; } = [];
    public ObservableCollection<SearchResult> Results { get; } = [];
    public ObservableCollection<SearchResult> DocumentResults { get; } = [];
    public ObservableCollection<SearchResult> CodeResults { get; } = [];
    public ObservableCollection<ImageResultViewModel> ImageResults { get; } = [];
    public string DocumentHeading => $"Documents ({DocumentResults.Count})";
    public string CodeHeading => $"Code ({CodeResults.Count})";
    public string ImageHeading => $"Images ({ImageResults.Count})";
    public bool NoDocuments => DocumentResults.Count == 0;
    public bool NoCode => CodeResults.Count == 0;
    public bool NoImages => ImageResults.Count == 0;
    public bool ShowResults => Results.Count > 0 && !IsSearching;
    public string IndexType => indexInfo?.TypeVersion ?? "Connecting...";
    public string IndexLocation => indexInfo?.DatabaseLocation ?? "Connecting...";
    public string TotalFiles => (indexInfo?.TotalFiles ?? 0).ToString("N0");
    public string TotalDocuments => (indexInfo?.TotalDocuments ?? 0).ToString("N0");
    public string TotalCodeFiles => (indexInfo?.TotalCodeFiles ?? 0).ToString("N0");
    public string TotalImages => (indexInfo?.TotalImages ?? 0).ToString("N0");
    public string DatabaseSize => indexInfo is null ? "Connecting..." : FormatBytes(indexInfo.DatabaseSizeBytes);
    public string LastRefreshed => DateTimeOffset.TryParse(indexInfo?.LastRefreshed, CultureInfo.InvariantCulture,
        DateTimeStyles.None, out var timestamp) ? timestamp.ToLocalTime().ToString("G") : "Not refreshed yet";
    public string RefreshTimestamp => indexInfo?.LastRefreshed ?? "";
    public string Query { get => query; set { Set(ref query, value); Changed(nameof(CanClearSearch)); } }
    public bool CanClearSearch => Query.Length > 0 || Results.Count > 0 || IsSearching;
    public bool IsDarkMode { get => darkMode; set { Set(ref darkMode, value); Changed(nameof(ThemeName)); } }
    public string ThemeName => IsDarkMode ? "Dark" : "Light";
    public bool LoggingEnabled { get => loggingEnabled; private set { Set(ref loggingEnabled, value); Changed(nameof(LoggingLabel)); } }
    public string LoggingLabel => LoggingEnabled ? "Enabled" : "Disabled";
    public string LogDirectory { get => logDirectory; private set => Set(ref logDirectory, value); }
    public string SettingsPath { get => settingsPath; private set => Set(ref settingsPath, value); }
    public bool ConfigurationBusy
    {
        get => configurationBusy;
        set { Set(ref configurationBusy, value); Changed(nameof(CanConfigure)); Changed(nameof(IsConfigurationIdle)); Changed(nameof(CanApplyModels)); Changed(nameof(CanSearch)); Changed(nameof(CanManageFolders)); Changed(nameof(CanRemove)); Changed(nameof(CanRetryImages)); NotifySearchOptions(); }
    }
    public bool IsConfigurationIdle => !ConfigurationBusy;
    public bool CanConfigure => IsConnected && !ConfigurationBusy;
    public string CpuSummary { get => cpuSummary; private set => Set(ref cpuSummary, value); }
    public string CpuDetail { get => cpuDetail; private set => Set(ref cpuDetail, value); }
    public SearchPreferences SearchOptions => searchOptions;
    public bool SearchDocuments { get => searchOptions.Documents; set => ChangeSearch(searchOptions with { Documents = value }); }
    public bool SearchCode { get => searchOptions.Code; set => ChangeSearch(searchOptions with { Code = value }); }
    public bool SearchImages { get => searchOptions.Images; set => ChangeSearch(searchOptions with { Images = value }); }
    public double DocumentThreshold { get => searchOptions.DocumentThreshold; set => ChangeSearch(searchOptions with { DocumentThreshold = value }); }
    public double CodeThreshold { get => searchOptions.CodeThreshold; set => ChangeSearch(searchOptions with { CodeThreshold = value }); }
    public double ImageThreshold { get => searchOptions.ImageThreshold; set => ChangeSearch(searchOptions with { ImageThreshold = value }); }
    public bool CanToggleDocuments => !ConfigurationBusy && (!SearchDocuments || SearchCode || SearchImages);
    public bool CanToggleCode => !ConfigurationBusy && (!SearchCode || SearchDocuments || SearchImages);
    public bool CanToggleImages => !ConfigurationBusy && (!SearchImages || SearchDocuments || SearchCode);
    public string DocumentWidth => SearchDocuments ? "*" : "0";
    public string CodeWidth => SearchCode ? "*" : "0";
    public string ImageWidth => SearchImages ? "*" : "0";
    public string DocumentGap => SearchDocuments && (SearchCode || SearchImages) ? "18" : "0";
    public string CodeGap => SearchCode && SearchImages ? "18" : "0";
    private void ChangeSearch(SearchPreferences value)
    {
        try { value.Validate(); }
        catch (ArgumentException error) { Error = error.Message; NotifySearchOptions(); return; }
        if (searchOptions == value) return;
        searchOptions = value; NotifySearchOptions(); SearchOptionsChanged?.Invoke();
    }
    public void RestoreSearchOptions(SearchPreferences value)
    {
        value.Validate(); searchOptions = value; NotifySearchOptions();
    }
    private void NotifySearchOptions()
    {
        foreach (string property in new[] { nameof(SearchOptions), nameof(SearchDocuments), nameof(SearchCode), nameof(SearchImages),
            nameof(DocumentThreshold), nameof(CodeThreshold), nameof(ImageThreshold), nameof(CanToggleDocuments), nameof(CanToggleCode),
            nameof(CanToggleImages), nameof(DocumentWidth), nameof(CodeWidth), nameof(ImageWidth), nameof(DocumentGap), nameof(CodeGap) })
            Changed(property);
    }
    public bool IsSearching
    {
        get => searching;
        set { Set(ref searching, value); Changed(nameof(CanSearch)); Changed(nameof(CanClearSearch)); Changed(nameof(SearchButtonLabel)); Changed(nameof(ShowEmpty)); Changed(nameof(ShowResults)); }
    }
    public bool IsConnected
    {
        get => connected;
        set { Set(ref connected, value); Changed(nameof(CanSearch)); Changed(nameof(CanRemove)); Changed(nameof(CanConfigure)); Changed(nameof(CanManageFolders)); Changed(nameof(CanApplyModels)); Changed(nameof(CanRetryImages)); }
    }
    public bool FoldersBusy
    {
        get => foldersBusy;
        set { Set(ref foldersBusy, value); Changed(nameof(CanSearch)); Changed(nameof(CanRemove)); Changed(nameof(CanManageFolders)); Changed(nameof(CanApplyModels)); }
    }
    public bool CanManageFolders => IsConnected && !FoldersBusy && !ConfigurationBusy;
    public bool CanSearch => IsConnected && ready && !IsSearching && !FoldersBusy;
    public bool CanRemove => CanManageFolders && Folders.Any(folder => folder.IsSelected);
    public bool HasFolders => Folders.Count > 0;
    public bool NoFolders => !HasFolders;
    public bool ShowEmpty => Results.Count == 0 && !IsSearching;
    public bool HasError => !string.IsNullOrEmpty(Error);
    public string SearchButtonLabel => IsSearching ? "Searching..." : "Search";
    public string Error { get => error; set { Set(ref error, value); Changed(nameof(HasError)); } }
    public string EngineMessage { get => engineMessage; set => Set(ref engineMessage, value); }
    public string ModelState
    {
        get => modelState;
        set { Set(ref modelState, value); Changed(nameof(IsEngineLoading)); Changed(nameof(ShowEngineState)); Changed(nameof(CanApplyModels)); }
    }
    public bool IsEngineLoading => ModelState is "Starting" or "Loading";
    public bool ShowEngineState => !IsEngineLoading;
    public int StartupPercent { get => startupPercent; private set { Set(ref startupPercent, value); Changed(nameof(StartupLabel)); } }
    public string StartupLabel => $"{StartupPercent:0}% loaded";
    public string StartupStage { get => startupStage; private set => Set(ref startupStage, value); }
    public string Monitor { get => monitor; set => Set(ref monitor, value); }
    public string Counts { get => counts; set => Set(ref counts, value); }
    public string ResultSummary { get => resultSummary; set => Set(ref resultSummary, value); }
    public string EmptyTitle { get => emptyTitle; set => Set(ref emptyTitle, value); }
    public string EmptyHint { get => emptyHint; set => Set(ref emptyHint, value); }
    public string FolderSummary => $"{Folders.Count} configured folder{(Folders.Count == 1 ? "" : "s")}";
    public string RemoveLabel => Folders.Count(folder => folder.IsSelected) is var count && count > 0
        ? $"Remove selected ({count})" : "Remove selected";

    public void Apply(EngineStatus status)
    {
        IsConnected = true;
        if (status.Startup is not null)
            ApplyStartup(status.Startup);
        else if (startupSequence < 0)
        {
            ready = status.ModelState == "Ready";
            ModelState = status.ModelState;
        }
        if (status.ModelState == "Error")
        {
            ready = false;
            ModelState = "Error";
        }
        EngineMessage = IsEngineLoading ? StartupStage : status.Activities.Count > 0
            ? string.Join("  |  ", status.Activities.Select(item =>
                $"{item.Stage}: {Path.GetFileName(item.Path)}" +
                (item.TotalPassages > 0 ? $" ({item.CompletedPassages:N0}/{item.TotalPassages:N0} passages)" : "")))
            : status.QueueCount > 0 && ready ? $"{status.QueueCount:N0} files queued"
            : status.ReconcilingFolders > 0 && ready ? "Checking indexed folders for changes..."
            : ready ? "Search engine ready" : status.Message;
        Monitor = status.Monitor;
        if (status.Models is { } models)
        {
            imagePipelineStatus = models.ImagePipeline;
            Changed(nameof(HasImagePipeline)); Changed(nameof(ImagePipelineSummary)); Changed(nameof(CanRetryImages));
            ModelIndexes.Clear();
            foreach (var index in models.Indexes) ModelIndexes.Add(index);
            if (showModelReindexStatus && !ConfigurationBusy && status.ModelState == "Ready" &&
                (expectedReindexSelection is null || expectedReindexSelection == models.Selection))
            {
                if (models.Reindex is { } reindex) ModelConfigurationMessage = reindex.Message;
                else if (models.Message.Length > 0) ModelConfigurationMessage = models.Message;
            }
        }
        Counts = $"{status.FileCount:N0} files | {status.VectorCount:N0} passages";
        CpuSummary = $"CPU limit: {status.Cpu.CoreLimit}/{status.Cpu.TotalCores}";
        CpuDetail = $"Maximum {status.Cpu.CoreLimit} of {status.Cpu.TotalCores} physical cores across the application and all workers. " +
            $"{status.Cpu.FileWorkerCount} concurrent file workers; {status.Cpu.WorkerCount} ready model processes, " +
            $"up to {status.Cpu.ThreadsPerWorker} compute threads per process. Last inference batch: {status.Cpu.LastBatchSize} inputs.";
        indexInfo = status.IndexInfo;
        foreach (string property in new[] { nameof(IndexType), nameof(IndexLocation), nameof(TotalFiles),
                     nameof(TotalDocuments), nameof(TotalCodeFiles), nameof(TotalImages), nameof(DatabaseSize), nameof(LastRefreshed), nameof(RefreshTimestamp) })
            Changed(property);
        if (!string.IsNullOrWhiteSpace(status.Error))
            Error = status.Error;
        else if (!string.IsNullOrWhiteSpace(status.Logging.Error))
            Error = status.Logging.Error;
        foreach (var obsolete in Folders.Where(folder => status.Folders.All(item => item.Id != folder.Id)).ToArray())
        {
            obsolete.PropertyChanged -= FolderChanged;
            Folders.Remove(obsolete);
        }

        foreach (var item in status.Folders)
        {
            var existing = Folders.FirstOrDefault(folder => folder.Id == item.Id);
            if (existing is not null)
                existing.Update(item);
            else
            {
                var folder = new FolderViewModel(item);
                folder.PropertyChanged += FolderChanged;
                Folders.Add(folder);
            }
        }
        Changed(nameof(CanSearch));
        Changed(nameof(CanRemove));
        Changed(nameof(HasFolders));
        Changed(nameof(NoFolders));
        Changed(nameof(FolderSummary));
        Changed(nameof(RemoveLabel));
    }

    public void ApplySettings(ApplicationSettings settings, string path)
    {
        RestoreSearchOptions(settings.SearchSettings);
        LoggingEnabled = settings.LoggingEnabled;
        LogDirectory = settings.LogDirectory;
        SettingsPath = path;
    }

    public void ApplyModels(ModelSelection selection)
    {
        foreach (var source in ModelSources) source.Restore(selection.ReferenceFor(source.Kind), selection.OptionsFor(source.Kind));
        if (TextSource.IsHuggingFace) TextModelUrl = selection.Text;
        if (CodeSource.IsHuggingFace) CodeModelUrl = selection.Code;
        if (ImageSource.IsHuggingFace) ImageModelUrl = selection.Image;
        TextRequirements.Restore(selection.OptionsFor("Document"));
        CodeRequirements.Restore(selection.OptionsFor("Code"));
        ImageRequirements.Restore(selection.OptionsFor("Image"));
        ImageProcessing.Restore(selection);
        if (selection.UsesImageUnderstanding)
            ImageRequirements.Restore(new(selection.EffectiveImagePipeline.EmbeddingOptions?.QueryProfile ?? "", selection.OptionsFor("Image").Device));
    }
    public void ApplyModelInspection(ModelInspection inspection)
    {
        ModelSelection current;
        try { current = DraftModels; }
        catch (ArgumentException) { return; }
        foreach (var requirement in inspection.Requirements)
        {
            if (current.ForKind(requirement.Kind) != requirement.ModelUrl) continue;
            var target = requirement.Kind switch
            {
                "Document" => TextRequirements,
                "Code" => CodeRequirements,
                "Image" => ImageRequirements,
                _ => throw new InvalidDataException("Unknown model requirement kind.")
            };
            target.Apply(requirement);
            if (requirement.Kind == "Image") ImageProcessing.ApplyDefinition(requirement.ImageModel);
        }
    }
    public void BeginModelConfiguration()
    {
        showModelReindexStatus = false;
        expectedReindexSelection = null;
        ModelConfigurationMessage = "Validating models and available execution devices...";
    }
    public void ShowImageRetry(int count)
    {
        showModelReindexStatus = true; expectedReindexSelection = null;
        ModelConfigurationMessage = $"Retrying incomplete image tasks for {count:N0} files in the background.";
    }
    public void FinishModelConfiguration(ModelApplyResult result)
    {
        ApplyModels(result.Selection);
        showModelReindexStatus = true;
        expectedReindexSelection = result.Selection;
        ModelConfigurationMessage = !result.Changed ? "Model configuration is already applied."
            : result.ReindexedKinds.Length > 0 ? new ModelReindexState(result.ReindexedKinds).Message
            : "Configuration saved. Query/device settings updated; existing embeddings were retained.";
    }

    public void ApplyStartup(StartupStatus status)
    {
        if (status.Sequence < startupSequence)
            return;
        startupSequence = status.Sequence;
        StartupPercent = status.Percent;
        StartupStage = status.Stage;
        ModelState = status.State;
        ready = status.State == "Ready";
        IsConnected = true;
        EngineMessage = status.Stage;
        if (!string.IsNullOrEmpty(status.Error))
            Error = status.Error;
        Changed(nameof(CanSearch));
    }

    private void FolderChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(FolderViewModel.IsSelected))
        {
            Changed(nameof(CanRemove));
            Changed(nameof(RemoveLabel));
        }
    }

    public void BeginSearch()
    {
        Error = "";
        Results.Clear();
        DocumentResults.Clear();
        CodeResults.Clear();
        ImageResults.Clear();
        IsSearching = true;
        ResultSummary = $"Finding files that match \"{Query.Trim()}\"";
    }

    public void FinishSearch(IReadOnlyList<SearchResult> results, TimeSpan elapsed)
    {
        Results.Clear();
        DocumentResults.Clear();
        CodeResults.Clear();
        ImageResults.Clear();
        foreach (var result in results)
        {
            Results.Add(result);
            if (result.Kind == "Image")
                ImageResults.Add(new ImageResultViewModel(result));
            else if (result.Kind == "Code")
                CodeResults.Add(result);
            else
                DocumentResults.Add(result);
        }
        ResultSummary = $"{results.Count} matching file{(results.Count == 1 ? "" : "s")} / {elapsed.TotalSeconds:0.0}s";
        EmptyTitle = "No matching files found.";
        EmptyHint = HasFolders
            ? "Try a different description, lower the minimum similarity, or wait for indexing to finish."
            : "Add a folder in Indexed folders to make your files searchable.";
        IsSearching = false;
        Changed(nameof(ShowEmpty));
        ResultsChanged();
    }

    public void SearchFailed()
    {
        IsSearching = false;
        ResultSummary = "Search could not be completed.";
        EmptyTitle = "Search is temporarily unavailable";
        EmptyHint = "See the message above. Your folders and committed index have been preserved.";
        Changed(nameof(ShowEmpty));
    }

    public void ClearResults()
    {
        Results.Clear();
        DocumentResults.Clear();
        CodeResults.Clear();
        ImageResults.Clear();
        IsSearching = false;
        ResultSummary = "The folder list changed. Search again for current results.";
        EmptyTitle = "Folder configuration updated";
        EmptyHint = "Search again to refresh the results.";
        Changed(nameof(ShowEmpty));
        ResultsChanged();
    }

    public void ClearSearch()
    {
        Query = "";
        Results.Clear();
        DocumentResults.Clear();
        CodeResults.Clear();
        ImageResults.Clear();
        IsSearching = false;
        ResultSummary = "Enter a search phrase to begin.";
        EmptyTitle = "Search your index";
        EmptyHint = "Search the documents, code and images in your configured folders.";
        Changed(nameof(ShowEmpty));
        ResultsChanged();
    }

    private void ResultsChanged()
    {
        foreach (string property in new[] { nameof(DocumentHeading), nameof(CodeHeading), nameof(ImageHeading), nameof(NoDocuments), nameof(NoCode), nameof(NoImages), nameof(ShowResults), nameof(CanClearSearch) })
            Changed(property);
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824d:N2} GB",
        >= 1_048_576 => $"{bytes / 1_048_576d:N2} MB",
        >= 1024 => $"{bytes / 1024d:N1} KB",
        _ => $"{bytes:N0} bytes"
    };
}

public sealed class ImageResultViewModel(SearchResult result) : ObservableObject
{
    private BitmapSource? thumbnail;
    private string thumbnailMessage = "Loading preview...";
    public SearchResult Result { get; } = result;
    public BitmapSource? Thumbnail
    {
        get => thumbnail;
        set
        {
            Set(ref thumbnail, value); Changed(nameof(HasNoThumbnail)); Changed(nameof(ThumbnailStatus));
            foreach (string property in new[] { nameof(HasRegion), nameof(RegionLeft), nameof(RegionTop), nameof(RegionWidth), nameof(RegionHeight), nameof(RegionStroke) })
                Changed(property);
        }
    }
    public bool HasRegion => Thumbnail is not null && Result.Region is not null;
    public double RegionLeft => (Result.Region?.Left ?? 0) * (Thumbnail?.PixelWidth ?? 1);
    public double RegionTop => (Result.Region?.Top ?? 0) * (Thumbnail?.PixelHeight ?? 1);
    public double RegionWidth => ((Result.Region?.Right ?? 0) - (Result.Region?.Left ?? 0)) * (Thumbnail?.PixelWidth ?? 1);
    public double RegionHeight => ((Result.Region?.Bottom ?? 0) - (Result.Region?.Top ?? 0)) * (Thumbnail?.PixelHeight ?? 1);
    public double RegionStroke => Math.Max((Thumbnail?.PixelWidth ?? 1) / 152d, (Thumbnail?.PixelHeight ?? 1) / 116d) * 2;
    public bool HasNoThumbnail => Thumbnail is null;
    public string ThumbnailStatus => HasNoThumbnail ? ThumbnailMessage : "Preview loaded";
    public string ThumbnailMessage
    {
        get => thumbnailMessage;
        set { Set(ref thumbnailMessage, value); Changed(nameof(ThumbnailStatus)); }
    }
}
