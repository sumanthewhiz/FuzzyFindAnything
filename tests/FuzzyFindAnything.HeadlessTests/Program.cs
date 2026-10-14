using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Xml.Linq;
using FuzzyFindAnything.App.Services;
using FuzzyFindAnything.App.ViewModels;

namespace FuzzyFindAnything.HeadlessTests;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 1)
            throw new ArgumentException("Pass the repository root. This test executable never starts the application.");
        Run("XML search defaults and persistence", SettingsRoundTrip);
        Run("Existing preferences and index remain intact", ExistingSettings);
        Run("Invalid similarity is rejected without overwriting preferences", InvalidSimilarity);
        Run("Three result groups and clearing", ResultGroups);
        Run("Loading indicator state", LoadingState);
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
}
