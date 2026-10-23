using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using FuzzyFindAnything.App.Services;
using FuzzyFindAnything.App.ViewModels;
using FuzzyFindAnything.Core;

namespace FuzzyFindAnything.HeadlessTests;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "render") { VisualChecks.Render(args[1], args[2]); return 0; }
        if (args.Length != 1)
            throw new ArgumentException("Pass the repository root. This test executable never starts the application.");
        Run("XML search defaults and persistence", SettingsRoundTrip);
        Run("Existing preferences and index remain intact", ExistingSettings);
        Run("Invalid similarity is rejected without overwriting preferences", InvalidSimilarity);
        Run("Three result groups and clearing", ResultGroups);
        Run("Region thumbnails preserve geometry and reject changed sources", RegionThumbnails);
        Run("Loading indicator state", LoadingState);
        Run("Startup reconciliation does not disable search", ReconciliationState);
        Run("Model defaults and XML round-trip", ModelsRoundTrip);
        Run("Model reindex completion, errors and validation feedback", ModelReindexFeedback);
        Run("Adaptive model requirements and extended XML bindings", AdaptiveModels);
        Run("WPF query-purpose selection survives Apply refresh", QueryPurposeBinding);
        Run("Image pipeline requirements stay hidden for direct models", ImageRequirements);
        Run("Mixed provider choices, version persistence and unavailable-model guard", ProviderChoices);
        Run("Search type guardrail and independent thresholds", SearchOptions);
        Run("Footer alignment and results layout", () => LayoutContracts(args[0]));
        Console.WriteLine("All headless checks passed. No application window or engine was started.");
        return 0;
    }

    private static void Run(string name, Action test)
    {
        test();
        Console.WriteLine($"PASS {name}");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void InTemporaryDirectory(Action<string> action)
    {
        string path = Path.Combine(Path.GetTempPath(), $"ffa-headless-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        try { action(path); }
        finally { Directory.Delete(path, recursive: true); }
    }

    private static void SettingsRoundTrip() => InTemporaryDirectory(directory =>
    {
        var settings = new SettingsService(directory);
        Require(settings.Current.SearchSettings == new SearchPreferences(), "First launch must select all types at the default thresholds.");
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            settings.Save(settings.Current with
            {
                Search = new(Documents: true, Code: false, Images: true,
                DocumentThreshold: 0.75, CodeThreshold: 0.30, ImageThreshold: 0.15)
            });
            settings.Save(settings.Current with { Theme = "Dark", LoggingEnabled = false });
            var restored = new SettingsService(directory);
            Require(restored.Current.SearchSettings == new SearchPreferences(true, false, true, 0.75, 0.30, 0.15), "Search choices were not retained across reload.");
            Require(restored.Current.Theme == "Dark" && !restored.Current.LoggingEnabled, "Other preferences were lost.");
            Require((string?)XDocument.Load(settings.FilePath).Root?.Element("Search")?.Element("Documents")?.Attribute("minimumSimilarity") == "0.75",
                "Similarity must use an invariant XML numeric representation.");
            var model = new MainViewModel();
            model.ApplySettings(restored.Current, restored.FilePath);
            Require(model.DocumentThreshold == 0.75 && model.CodeThreshold == 0.30 && model.ImageThreshold == 0.15 &&
                !model.SearchCode, "The view model did not restore search preferences.");
        }
        finally { CultureInfo.CurrentCulture = culture; }
    });

    private static void ExistingSettings() => InTemporaryDirectory(directory =>
    {
        string catalog = Path.Combine(directory, "catalog.sqlite3");
        File.WriteAllText(catalog, "existing index sentinel");
        string settingsPath = Path.Combine(directory, "settings.xml");
        new XDocument(new XElement("FuzzyFindAnythingConfiguration", new XAttribute("version", "1"),
            new XElement("Appearance", new XAttribute("theme", "Dark")),
            new XElement("Logging", new XAttribute("enabled", "true"), new XAttribute("directory", Path.Combine(directory, "Logs")))))
            .Save(settingsPath);
        byte[] previous = File.ReadAllBytes(settingsPath);
        var settings = new SettingsService(directory);
        Require(settings.Current.SearchSettings == new SearchPreferences(), "Older XML must receive the search defaults.");
        Require(previous.SequenceEqual(File.ReadAllBytes(settingsPath)), "Reading existing preferences rewrote them.");
        File.WriteAllText(settings.Current.LogDirectory, "unavailable log destination");
        settings.Save(settings.Current with { Search = new(DocumentThreshold: 0.65) });
        Require(new SettingsService(directory).Current.SearchSettings.DocumentThreshold == 0.65, "An unchanged log path blocked search preferences.");
        Require(File.ReadAllText(catalog) == "existing index sentinel", "Saving preferences modified the existing index.");
    });

    private static void InvalidSimilarity() => InTemporaryDirectory(directory =>
    {
        var settings = new SettingsService(directory);
        byte[] previous = File.ReadAllBytes(settings.FilePath);
        foreach (double value in new[] { -0.01, 0.95, double.NaN, double.PositiveInfinity })
        {
            try
            {
                settings.Save(settings.Current with { Search = new(DocumentThreshold: value) });
                throw new InvalidOperationException($"Invalid threshold {value} was accepted.");
            }
            catch (InvalidDataException) { }
            Require(previous.SequenceEqual(File.ReadAllBytes(settings.FilePath)), "An invalid preference overwrote valid XML.");
        }
        foreach (string value in new[] { "NaN", "INF", "0,60", "1.2", "invalid" })
        {
            var document = XDocument.Load(settings.FilePath);
            document.Root!.Element("Search")!.Element("Documents")!.SetAttributeValue("minimumSimilarity", value);
            document.Save(settings.FilePath);
            try
            {
                _ = new SettingsService(directory);
                throw new InvalidOperationException($"Invalid XML threshold {value} was accepted.");
            }
            catch (InvalidDataException) { }
        }
    });

    private static void ResultGroups()
    {
        var model = new MainViewModel { Query = "parsing files", DocumentThreshold = 0.65 };
        model.Apply(new EngineStatus("Ready", "Ready", "", [], 4, 4, "Monitoring", "",
            new IndexMetadata("DiskANN", @"C:\fixtures\index", 4, 1, 1, 1024, null, 2),
            new CpuStatus(12, 6, 1, 6, 6, 8), 0, 0, [], new LoggingStatus(false, @"C:\fixtures\logs", "")));
        Require(model.TotalFiles == "4" && model.TotalDocuments == "1" && model.TotalCodeFiles == "2" && model.TotalImages == "1",
            "Live metadata does not account for all three file categories.");
        Require(model.CpuDetail.Contains("6 concurrent file workers") && model.CpuDetail.Contains("1 ready model processes"),
            "CPU diagnostics must distinguish file concurrency from model-copy count.");
        SearchResult[] results =
        [
            new(@"C:\fixtures\guide.txt", "guide.txt", "Document", 120, 0, "guide", 0.9),
            new(@"C:\fixtures\parser.py", "parser.py", "Code", 150, 0, "def parse():\n    pass", 0.85),
            new(@"C:\fixtures\main.cs", "main.cs", "Code", 150, 0, "class Program {}", 0.8),
            new(@"C:\fixtures\diagram.png", "diagram.png", "Image", 200, 0, "diagram", 0.75)
        ];
        model.FinishSearch(results, TimeSpan.FromMilliseconds(25));
        Require(model.DocumentResults.Count == 1 && model.CodeResults.Count == 2 && model.ImageResults.Count == 1,
            "Search results were not partitioned into documents, code and images.");
        Require(model.CodeResults[0].Path == results[1].Path && !model.NoCode, "Code results lost their associated source path.");
        model.BeginSearch();
        Require(model.CodeResults.Count == 0 && model.Results.Count == 0, "Starting a search retained stale code results.");
        model.FinishSearch(results, TimeSpan.Zero);
        model.ClearSearch();
        Require(model.Query == "" && model.Results.Count == 0 && model.CodeResults.Count == 0 &&
            model.DocumentResults.Count == 0 && model.ImageResults.Count == 0 && model.DocumentThreshold == 0.65,
            "Clear must remove all result categories without resetting the threshold.");
        model.FinishSearch(results, TimeSpan.Zero);
        model.ClearResults();
        Require(model.CodeResults.Count == 0 && model.NoCode, "Folder changes retained code results.");
    }

    private static void RegionThumbnails() => InTemporaryDirectory(directory =>
    {
        string path = Path.Combine(directory, "region.bmp");
        var source = BitmapSource.Create(64, 32, 96, 96, PixelFormats.Rgb24, null, new byte[64 * 32 * 3], 64 * 3);
        var encoder = new BmpBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using (var stream = File.Create(path)) encoder.Save(stream);
        var fingerprint = FilePaths.Inspect(path);
        var region = new ImageRegion("0", "test region", 0.25, 0.25, 0.75, 0.75, 64, 32);
        var result = new SearchResult(path, "region.bmp", "Image", fingerprint.Size, fingerprint.MtimeNs, region.Text, 1,
            Region: region);
        var thumbnail = ThumbnailLoader.LoadAsync(result, CancellationToken.None).GetAwaiter().GetResult();
        Require(thumbnail.IsFrozen && thumbnail.PixelWidth == 360 && thumbnail.PixelHeight == 180,
            "The region thumbnail lost the inference image's aspect ratio.");
        void Reject(SearchResult changed)
        {
            try { ThumbnailLoader.LoadAsync(changed, CancellationToken.None).GetAwaiter().GetResult(); }
            catch (IOException) { return; }
            throw new InvalidOperationException("A stale region overlay was displayed.");
        }
        Reject(result with { Region = region with { SourceWidth = 32 } });
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(1));
        Reject(result);
    });

    private static void ProviderChoices() => InTemporaryDirectory(directory =>
    {
        var code = new ProviderModelReference("microsoft", "1.2.3.4", "code-text");
        var image = new ProviderModelReference("microsoft", "1.2.3.4", "image-text");
        var selection = new ModelSelection(ModelSelection.DefaultUrl, code.Key, image.Key,
            CodeOptions: new(Device: "QNNExecutionProvider:NPU"));
        var model = new MainViewModel();
        model.ApplyModels(selection);
        ProviderDevice[] devices = [new("CPU", "CPU", true), new("QNNExecutionProvider:NPU", "NPU", true)];
        var catalog = new ProviderCatalog("microsoft", "1.2.3.4", [new("1.2.3.4", true)],
            [new("code-text", "Code/text encoder", ["Document", "Code"], devices, "Available", ""),
             new("image-text", "Paired image encoder", ["Image"], devices, "Available", "")]);
        model.CodeSource.Apply(catalog); model.ImageSource.Apply(catalog);
        model.ApplyModels(selection);
        model.CodeSource.Apply(catalog);
        Require(model.DraftModels == selection && model.TextSource.IsHuggingFace && model.CodeSource.IsMicrosoft &&
            model.ImageSource.IsMicrosoft && !model.ImageProcessing.IsVisible, "Mixed sources, native device choices or simple image controls were lost.");
        var settings = new SettingsService(directory);
        settings.Save(settings.Current with { Models = selection });
        settings.Save(settings.Current with { Theme = "Dark", LoggingEnabled = false });
        Require(new SettingsService(directory).Current.ModelSelection == selection &&
            (string?)XDocument.Load(settings.FilePath).Root!.Attribute("version") == "4", "Saving unrelated preferences lost provider bindings.");
        model.CodeSource.Version = "1.2.3.5";
        model.CodeSource.Apply(new("microsoft", "1.2.3.5", [new("1.2.3.5", true)], []));
        Require(model.CodeSource.Model == "code-text" && !model.CodeSource.IsReady,
            "An unavailable model silently changed to a fallback when the drop changed.");
        model.ApplyModels(ModelSelection.Default);
        Require(model.ModelSources.All(source => source.IsHuggingFace) && model.DraftModels == ModelSelection.Default,
            "Returning to default models retained inactive provider controls/options.");
    });

    private static void LoadingState()
    {
        var model = new MainViewModel();
        Require(model.IsEngineLoading && !model.ShowEngineState, "Startup must display progress instead of a static status badge.");
        model.ModelState = "Loading";
        Require(model.IsEngineLoading, "Progress disappeared during model loading.");
        model.ModelState = "Ready";
        Require(!model.IsEngineLoading && model.ShowEngineState, "Progress must stop once the engine is ready.");
        model.ModelState = "Error";
        Require(!model.IsEngineLoading && model.ShowEngineState, "Startup failures must not leave endless progress.");
        var loading = JsonSerializer.Deserialize<StartupStatus>(
            """{"sequence":1,"percent":44,"completed_steps":4,"total_steps":9,"stage":"Importing embedding libraries","state":"Loading","error":""}""",
            EngineClient.JsonOptions) ?? throw new InvalidDataException("Startup IPC could not be decoded.");
        model.ApplyStartup(loading);
        Require(model.IsEngineLoading && model.StartupPercent == 44 && model.StartupLabel.Contains("44%"),
            "The UI must display the reported percentage and phase.");
        Require(model.StartupStage == loading.Stage, "The current startup phase was lost.");
        model.ApplyStartup(new StartupStatus(2, 100, 9, 9, "Search engine ready", "Ready", ""));
        model.Apply(new EngineStatus("Loading", "Old loading status", "", [], 0, 0, "", "",
            new IndexMetadata("DiskANN", @"C:\fixtures", 0, 0, 0, 0, null),
            new CpuStatus(12, 6, 1, 3), 0, 0, [], new LoggingStatus(false, "", ""), loading));
        Require(model.CanSearch && !model.IsEngineLoading && model.StartupPercent == 100,
            "A delayed status snapshot must not return a ready engine to the loading state.");
    }

    private static void LayoutContracts(string root)
    {
        var document = XDocument.Load(Path.Combine(root, "src", "FuzzyFindAnything.App", "MainWindow.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XElement Find(string id) => document.Descendants().Single(element =>
            (string?)element.Attribute("AutomationProperties.AutomationId") == id);
        var statusGrid = Find("IndexingActivity").Ancestors(presentation + "Grid").First();
        Require(ReferenceEquals(statusGrid, Find("Attribution").Ancestors(presentation + "Grid").First()),
            "Attribution must share the status grid rather than occupy a separate footer row.");
        Require((string?)Find("Attribution").Attribute("Text") == "\u00a9 2026 Suman Ghosh", "The footer must use the copyright symbol.");
        Require(ReferenceEquals(statusGrid, Find("LinkedInLink").Ancestors(presentation + "Grid").First()) &&
                ReferenceEquals(statusGrid, Find("GitHubLink").Ancestors(presentation + "Grid").First()),
            "Profile buttons are not aligned with application status.");
        var codePane = Find("CodeResults").Parent;
        Require(codePane?.Parent == Find("DocumentResults").Parent?.Parent &&
                codePane?.Parent == Find("ImageGallery").Parent?.Parent,
            "Code, document and image sections must share the results pane.");
        Require(Find("StartupProgress").Name == presentation + "ProgressBar", "Startup is missing a progress control.");
        Require((string?)Find("StartupProgress").Attribute("IsIndeterminate") == "False" &&
                ((string?)Find("StartupProgress").Attribute("Value"))?.Contains("StartupPercent") == true,
            "Startup progress must be determinate and driven by loader progress.");
    }

    private static void ReconciliationState()
    {
        var model = new MainViewModel();
        model.Apply(new EngineStatus("Ready", "Search engine ready", "", [], 1, 1, "Monitoring", "",
            new IndexMetadata("DiskANN", @"C:\fixtures", 1, 1, 0, 1024, null),
            new CpuStatus(12, 6, 1, 3, 6, 0), 0, 0, [], new LoggingStatus(false, "", ""),
            new StartupStatus(10, 100, 9, 9, "Search engine ready", "Ready", ""), ReconcilingFolders: 1));
        Require(model.CanSearch && model.StartupPercent == 100 && !model.IsEngineLoading &&
            model.EngineMessage == "Checking indexed folders for changes...",
            "Background reconciliation must remain visible without blocking search.");
    }

    private static void ModelsRoundTrip() => InTemporaryDirectory(directory =>
    {
        var settings = new SettingsService(directory);
        Require(settings.Current.ModelSelection == ModelSelection.Default, "All model defaults must be EmbeddingGemma2.");
        var chosen = new ModelSelection("https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2",
            ModelSelection.DefaultUrl, "https://huggingface.co/Xenova/clip-vit-base-patch32");
        settings.Save(settings.Current with { Models = chosen, Search = new(DocumentThreshold: 0.75), Theme = "Dark" });
        var restored = new SettingsService(directory);
        Require(restored.Current.ModelSelection == chosen && restored.Current.SearchSettings.DocumentThreshold == 0.75 && restored.Current.Theme == "Dark",
            "Model URLs or existing preferences did not survive XML reload.");
        var model = new MainViewModel();
        model.ApplyModels(restored.Current.ModelSelection);
        Require(model.TextModelUrl == chosen.Text && model.CodeModelUrl == chosen.Code && model.ImageModelUrl == chosen.Image,
            "Model configuration fields did not restore their applied values.");
        byte[] before = File.ReadAllBytes(settings.FilePath);
        CheckInvalid();
        Require(before.SequenceEqual(File.ReadAllBytes(settings.FilePath)), "An invalid model URL overwrote preferences.");
        void CheckInvalid()
        {
            try { settings.Save(settings.Current with { Models = chosen with { Image = "https://example.com/model" } }); }
            catch (ArgumentException) { return; }
            throw new InvalidOperationException("An unsupported model host was accepted.");
        }
    });

    private static void ModelReindexFeedback()
    {
        var selection = ModelSelection.Default with { Image = "https://huggingface.co/Xenova/clip-vit-base-patch32" };
        EngineStatus Status(ModelSelection current, ModelReindexState reindex) => new("Ready", "Ready", "", [], 3, 3, "", "",
            new IndexMetadata("DiskANN", @"C:\fixtures", 3, 1, 1, 1024, null, 1),
            new CpuStatus(12, 6, 1, 3, 6, 0), 0, 0, [], new LoggingStatus(false, "", ""),
            Models: new(current, [], Reindex: reindex));
        var model = new MainViewModel();
        model.BeginModelConfiguration();
        model.FinishModelConfiguration(new(true, selection, ["Image"]));
        string pending = model.ModelConfigurationMessage;
        model.Apply(Status(ModelSelection.Default, new(["Image"], true, 20)));
        Require(model.ModelConfigurationMessage == pending, "A stale model selection completed the new reindex.");
        model.Apply(Status(selection, new(["Image"])));
        Require(model.ModelConfigurationMessage.Contains("background"), "The active reindex message disappeared.");
        model.Apply(Status(selection, new(["Image"], true, 22)));
        Require(model.ModelConfigurationMessage.Contains("Reindexing complete") && model.ModelConfigurationMessage.Contains("22"),
            "Model reindex completion was not shown.");
        model.Apply(Status(selection, new(["Image"], true, 22, 1)));
        Require(model.ModelConfigurationMessage.Contains("finished with errors") && model.ModelConfigurationMessage.Contains("1 failed"),
            "Partial failure was incorrectly reported as successful completion.");
        var restarted = new MainViewModel();
        restarted.Apply(Status(selection, new(["Image"], true, 22)));
        Require(restarted.ModelConfigurationMessage.Contains("Reindexing complete"), "Saved completion was not restored after restart.");
        model.BeginModelConfiguration();
        model.ModelConfigurationMessage = "Model configuration was not applied: incompatible model.";
        model.Apply(Status(selection, new(["Image"], true, 22)));
        Require(model.ModelConfigurationMessage.StartsWith("Model configuration was not applied:", StringComparison.Ordinal),
            "An old completion snapshot hid a newer validation failure.");
        model.Apply(Status(selection, new(["Image"])) with
        {
            ModelState = "Error",
            Error = "Indexing stopped after a storage failure.",
            Startup = new(9, 100, 9, 9, "Previously ready", "Ready", "")
        });
        Require(model.ModelState == "Error" && !model.CanSearch, "A stale startup-ready snapshot hid a post-commit engine failure.");
    }

    private static void SearchOptions() => InTemporaryDirectory(directory =>
    {
        string path = Path.Combine(directory, "settings.xml");
        new XDocument(new XElement("FuzzyFindAnythingConfiguration", new XAttribute("version", "1"),
            new XElement("Search", new XAttribute("minimumSimilarity", "0.35")))).Save(path);
        byte[] legacy = File.ReadAllBytes(path);
        var settings = new SettingsService(directory);
        Require(settings.Current.SearchSettings == new SearchPreferences(true, true, true, 0.35, 0.35, 0.35) &&
            legacy.SequenceEqual(File.ReadAllBytes(path)), "The legacy threshold did not migrate read-only into all three types.");
        var model = new MainViewModel();
        model.RestoreSearchOptions(settings.Current.SearchSettings);
        model.SearchCode = false; model.SearchImages = false;
        Require(!model.CanToggleDocuments && model.SearchDocuments, "The final selected search type can be unchecked.");
        model.SearchDocuments = false;
        Require(model.SearchDocuments && model.Error.Contains("at least one"), "The view model accepted an empty selection.");
        model.CodeThreshold = 0.2; model.ImageThreshold = 0.1;
        Require(model.DocumentThreshold == 0.35 && model.CodeThreshold == 0.2 && model.ImageThreshold == 0.1,
            "Per-type thresholds changed each other.");
        settings.Save(settings.Current with { Search = model.SearchOptions });
        Require(new SettingsService(directory).Current.SearchSettings == model.SearchOptions &&
            XDocument.Load(path).Root!.Attribute("version")!.Value == "3", "Per-type search settings did not persist.");
        byte[] valid = File.ReadAllBytes(path);
        try { settings.Save(settings.Current with { Search = new(false, false, false) }); throw new InvalidOperationException("Empty search preferences were saved."); }
        catch (InvalidDataException) { }
        Require(valid.SequenceEqual(File.ReadAllBytes(path)), "Invalid search preferences changed the configuration.");
        var match = new SearchResult("code.cs", "code.cs", "Code", 100, 0, "short", 0.8, MatchedChunk: "    return a + b;\n",
            Chunk: new(ChunkingProfiles.Code, "c-sharp", 10, 18, 4, 5, "Add", ""));
        Require(match.ChunkText.StartsWith("    ") && match.ChunkLabel.Contains("4-5") && match.ChunkLabel.Contains("Add"),
            "Full code chunk text or provenance was lost from the hover view model.");
    });

    private static void QueryPurposeBinding()
    {
        var model = new ModelRequirementViewModel();
        var recipe = EmbeddingRecipes.Find("https://huggingface.co/Qwen/Qwen3-Embedding-0.6B")!.Recipe;
        var requirement = new ModelRequirement("Code", "https://huggingface.co/Qwen/Qwen3-Embedding-0.6B",
            "Available", "Automatic", "Native model", recipe.QueryProfiles.ToArray(), recipe.CodeQuery, false);
        model.Apply(requirement);
        var selector = new System.Windows.Controls.ComboBox
        {
            DataContext = model,
            DisplayMemberPath = nameof(QueryProfile.Label),
            SelectedValuePath = nameof(QueryProfile.Id)
        };
        selector.SetBinding(System.Windows.Controls.ItemsControl.ItemsSourceProperty,
            new System.Windows.Data.Binding(nameof(ModelRequirementViewModel.QueryProfiles)));
        selector.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedValueProperty,
            new System.Windows.Data.Binding(nameof(ModelRequirementViewModel.SelectedQueryProfile))
            { Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
        foreach (string profile in new[] { "document-search-v1", "code-search-v1", "" })
        {
            selector.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedValueProperty, profile);
            selector.GetBindingExpression(System.Windows.Controls.Primitives.Selector.SelectedValueProperty)!.UpdateSource();
            Require(model.SelectedQueryProfile == profile, "The simulated user selection did not reach the binding.");
            model.Restore(new ModelOptions(profile));
            model.Apply(requirement);
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
            Require(selector.SelectedItem is QueryProfile selected && selected.Id == profile &&
                model.SelectedQueryProfile == profile, "The visible Query Purpose selection was cleared by the post-Apply requirement refresh.");
        }
        model.Reset(false);
        model.Restore(new("document-search-v1"));
        model.Apply(requirement);
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
        Require(selector.SelectedItem is QueryProfile { Id: "document-search-v1" },
            "A saved query purpose was not selected when requirements arrived after settings reload.");
    }

    private static void ImageRequirements()
    {
        var model = new MainViewModel();
        Require(!model.ImageProcessing.IsVisible && model.DraftModels == ModelSelection.Default,
            "Default Gemma configuration exposes image-pipeline setup.");
        model.ImageModelUrl = "https://huggingface.co/microsoft/Florence-2-base-ft";
        Require(model.ImageProcessing.IsVisible && model.ImageProcessing.UseTextModel &&
            model.ImageProcessing.Caption && !model.ImageProcessing.Ocr && model.DraftModels.UsesImageUnderstanding,
            "The automatic described-image defaults were not resolved.");
        model.ImageProcessing.Ocr = true;
        model.ImageProcessing.RegionTask = "objects";
        var selection = model.DraftModels;
        Require(selection.EffectiveImagePipeline.Ocr && selection.EffectiveImagePipeline.RegionTask == "objects" &&
            selection.EmbeddingSelection().Image == selection.Text,
            "Image task/embedding dependencies were not captured.");
        model.ApplyModels(selection);
        Require(model.ImageProcessing.Ocr && model.ImageProcessing.RegionTask == "objects" && model.ImageProcessing.UseTextModel,
            "Applied image options were not retained.");
        model.ImageProcessing.UseTextModel = false;
        model.ImageProcessing.EmbeddingModelUrl = "https://example.com/invalid";
        try { _ = model.DraftModels; throw new InvalidOperationException("An invalid image text-embedder URL was accepted."); }
        catch (ArgumentException) { }
        model.ImageModelUrl = ModelSelection.DefaultUrl;
        Require(!model.ImageProcessing.IsVisible && model.DraftModels == ModelSelection.Default,
            "Returning to a direct model left hidden image pipeline requirements active.");
    }

    private static void AdaptiveModels() => InTemporaryDirectory(directory =>
    {
        var settings = new SettingsService(directory);
        var document = XDocument.Load(settings.FilePath);
        document.Root!.Add(new XElement("ExtensionPreference", new XAttribute("preserve", "true")));
        document.Save(settings.FilePath);
        var chosen = ModelSelection.Default with
        { Code = "https://huggingface.co/Qwen/Qwen3-Embedding-0.6B", CodeOptions = new("document-search-v1", "CPU") };
        settings.Save(settings.Current with { Models = chosen });
        settings.Reload();
        Require(settings.Current.ModelSelection == chosen &&
            XDocument.Load(settings.FilePath).Root!.Element("ExtensionPreference")?.Attribute("preserve")?.Value == "true",
            "Extended settings lost query/device options or unrelated XML.");
        var model = new MainViewModel();
        model.ApplyModels(chosen);
        Require(model.DraftModels == chosen && !model.TextRequirements.IsVisible && !model.ImageRequirements.IsVisible,
            "Simple Gemma fields acquired unnecessary setup or saved query choices were lost.");
        var recipe = EmbeddingRecipes.Find(chosen.Code)!.Recipe;
        model.ApplyModelInspection(new(chosen, [new("Code", chosen.Code, "Available", "Automatic", "Native model",
            recipe.QueryProfiles.ToArray(), recipe.CodeQuery, false)]));
        Require(model.CodeRequirements.HasQueryOptions && !model.CodeRequirements.RequiresQueryChoice &&
            model.DraftModels == chosen, "Optional model-specific query profiles were not restored.");
        model.CodeModelUrl = ModelSelection.DefaultUrl;
        model.ApplyModelInspection(new(chosen, [new("Code", chosen.Code, "Available", "Stale", "Stale",
            recipe.QueryProfiles.ToArray(), "choose", false)]));
        Require(!model.CodeRequirements.IsVisible && model.DraftModels == ModelSelection.Default,
            "Switching back to Gemma retained hidden options or accepted stale requirement responses.");
        model.CodeModelUrl = chosen.Code;
        model.ApplyModelInspection(new(chosen, [new("Code", chosen.Code, "Available", "Choose a purpose", "Native",
            recipe.QueryProfiles.ToArray(), "choose", false)]));
        Require(model.CodeRequirements.RequiresQueryChoice, "A genuine unresolved requirement was hidden.");
        model.CodeRequirements.Unavailable("Code", chosen.Code, "Metadata unavailable offline.");
        Require(model.CodeRequirements.HasError && !model.CodeRequirements.HasDetails &&
            !model.CodeRequirements.RequiresQueryChoice, "An offline failure exposed an expert form instead of a blocker.");
    });
}
