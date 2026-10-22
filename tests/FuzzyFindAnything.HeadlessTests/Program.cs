using System.Globalization;
using System.IO;
using System.Text.Json;
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
        Run("Loading indicator state", LoadingState);
        Run("Startup reconciliation does not disable search", ReconciliationState);
        Run("Model defaults and XML round-trip", ModelsRoundTrip);
        Run("Model reindex completion, errors and validation feedback", ModelReindexFeedback);
        Run("Adaptive model requirements and extended XML bindings", AdaptiveModels);
        Run("WPF query-purpose selection survives Apply refresh", QueryPurposeBinding);
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
        Require(settings.Current.MinimumSimilarity == 0.60, "The first-time threshold is not 0.60.");
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            settings.Save(settings.Current with { MinimumSimilarity = 0.75 });
            settings.Save(settings.Current with { Theme = "Dark", LoggingEnabled = false });
            var restored = new SettingsService(directory);
            Require(restored.Current.MinimumSimilarity == 0.75, "Similarity was not retained across reload.");
            Require(restored.Current.Theme == "Dark" && !restored.Current.LoggingEnabled, "Other preferences were lost.");
            Require((string?)XDocument.Load(settings.FilePath).Root?.Element("Search")?.Attribute("minimumSimilarity") == "0.75",
                "Similarity must use an invariant XML numeric representation.");
            var model = new MainViewModel();
            model.ApplySettings(restored.Current, restored.FilePath);
            Require(model.Threshold == 0.75, "The view model did not restore the configured slider value.");
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
        Require(settings.Current.MinimumSimilarity == 0.60, "Older XML must receive the new default.");
        Require(previous.SequenceEqual(File.ReadAllBytes(settingsPath)), "Reading existing preferences rewrote them.");
        File.WriteAllText(settings.Current.LogDirectory, "unavailable log destination");
        settings.Save(settings.Current with { MinimumSimilarity = 0.65 });
        Require(new SettingsService(directory).Current.MinimumSimilarity == 0.65, "An unchanged log path blocked search preferences.");
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
                settings.Save(settings.Current with { MinimumSimilarity = value });
                throw new InvalidOperationException($"Invalid threshold {value} was accepted.");
            }
            catch (InvalidDataException) { }
            Require(previous.SequenceEqual(File.ReadAllBytes(settings.FilePath)), "An invalid preference overwrote valid XML.");
        }
        foreach (string value in new[] { "NaN", "INF", "0,60", "1.2", "invalid" })
        {
            var document = XDocument.Load(settings.FilePath);
            document.Root!.Element("Search")!.SetAttributeValue("minimumSimilarity", value);
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
        var model = new MainViewModel { Query = "parsing files", Threshold = 0.65 };
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
            model.DocumentResults.Count == 0 && model.ImageResults.Count == 0 && model.Threshold == 0.65,
            "Clear must remove all result categories without resetting the threshold.");
        model.FinishSearch(results, TimeSpan.Zero);
        model.ClearResults();
        Require(model.CodeResults.Count == 0 && model.NoCode, "Folder changes retained code results.");
    }

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
        settings.Save(settings.Current with { Models = chosen, MinimumSimilarity = 0.75, Theme = "Dark" });
        var restored = new SettingsService(directory);
        Require(restored.Current.ModelSelection == chosen && restored.Current.MinimumSimilarity == 0.75 && restored.Current.Theme == "Dark",
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
