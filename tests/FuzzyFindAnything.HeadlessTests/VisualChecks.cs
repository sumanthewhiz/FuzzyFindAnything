using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using FuzzyFindAnything.App.Services;
using FuzzyFindAnything.App.ViewModels;
using FuzzyFindAnything.Core;

namespace FuzzyFindAnything.HeadlessTests;

internal static class VisualChecks
{
    public static void Render(string repository, string output)
    {
        var application = new Application();
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var context = new ParserContext { BaseUri = new Uri("pack://application:,,,/FuzzyFindAnything;component/") };
        var resources = XDocument.Load(Path.Combine(repository, "src", "FuzzyFindAnything.App", "App.xaml"))
            .Descendants(presentation + "ResourceDictionary").First();
        resources.SetAttributeValue(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml");
        application.Resources = (ResourceDictionary)XamlReader.Parse(resources.ToString(), context);
        var source = XDocument.Load(Path.Combine(repository, "src", "FuzzyFindAnything.App", "MainWindow.xaml")).Root!;
        var content = new XElement(source.Element(presentation + "Grid")!);
        content.SetAttributeValue(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml");
        var localResources = new XElement(presentation + "Grid.Resources",
            source.Element(presentation + "Window.Resources")!.Elements().Select(value => new XElement(value)));
        content.AddFirst(localResources);
        var events = new HashSet<string> { "Click", "Checked", "Unchecked", "KeyDown", "ValueChanged", "SelectionChanged", "TextChanged", "Loaded", "Closing" };
        foreach (var attribute in content.DescendantsAndSelf().Attributes().Where(attribute => events.Contains(attribute.Name.LocalName)).ToArray())
            attribute.Remove();
        var model = new MainViewModel { Query = "a red circle on a white background", DocumentThreshold = 0.35, CodeThreshold = 0.30, ImageThreshold = 0.15 };
        model.Apply(new EngineStatus("Ready", "Search engine ready", "", [], 3, 3, "USN journal + directory notifications", "",
            new IndexMetadata("DiskANN (0.7.0; abi=1)", @"C:\SearchData", 3, 1, 1, 1048576, DateTimeOffset.UtcNow.ToString("O"), 1),
            new CpuStatus(12, 6, 2, 3, 6, 8), 0, 0, [], new LoggingStatus(true, @"C:\SearchData\Logs", ""),
            new StartupStatus(1, 100, 9, 9, "Search engine ready", "Ready", "")));
        model.FinishSearch([
            new(@"C:\Sample files\Shape guide.txt", "Shape guide.txt", "Document", 400, 0, "A red circle is a round shape on a white background.", 0.87),
            new(@"C:\Sample files\Draw circle.py", "Draw circle.py", "Code", 200, 0, "def draw_circle(canvas):\n    return canvas.create_oval(40, 40, 216, 216)", 0.81),
            new(@"C:\Sample files\Red circle.png", "Red circle.png", "Image", 1024, 0, "Red circle", 0.92, Evidence: "objects",
                Region: new("0", "Red circle", 0.15625, 0.15625, 0.84375, 0.84375, 256, 256))
        ], TimeSpan.FromMilliseconds(180));
        var picture = new DrawingVisual();
        using (var drawing = picture.RenderOpen())
        { drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, 256, 256)); drawing.DrawEllipse(Brushes.Red, null, new Point(128, 128), 88, 88); }
        var thumbnail = new RenderTargetBitmap(256, 256, 96, 96, PixelFormats.Pbgra32);
        thumbnail.Render(picture); thumbnail.Freeze(); model.ImageResults[0].Thumbnail = thumbnail;
        Directory.CreateDirectory(output);
        foreach (string theme in new[] { "Light", "Dark" })
        {
            application.Resources.MergedDictionaries[0] = new ResourceDictionary
            { Source = new Uri($"/FuzzyFindAnything;component/Themes/{theme}.xaml", UriKind.Relative) };
            model.IsDarkMode = theme == "Dark";
            var grid = (Grid)XamlReader.Parse(content.ToString(), context);
            grid.SetResourceReference(Panel.BackgroundProperty, "WindowBackground");
            TextElement.SetFontFamily(grid, new FontFamily("Segoe UI Variable, Segoe UI"));
            TextElement.SetFontSize(grid, 14);
            TextElement.SetForeground(grid, (Brush)application.FindResource("Ink"));
            grid.DataContext = model;
            Save(grid, Path.Combine(output, $"search-{theme.ToLowerInvariant()}.png"));

            var tabs = grid.Children.OfType<TabControl>().Single();
            var models = tabs.Items.OfType<TabItem>().Single(tab => AutomationProperties.GetAutomationId(tab) == "ModelsTab");
            tabs.SelectedItem = models;
            Save(grid, Path.Combine(output, $"models-{theme.ToLowerInvariant()}.png"));
            var header = models.Header as StackPanel;
            var icon = header?.Children.OfType<TextBlock>().FirstOrDefault();
            if (icon is null || icon.Text.Length != 1 || icon.Text[0] is < '\uE000' or > '\uF8FF' ||
                ((SolidColorBrush)icon.Foreground).Color != ((SolidColorBrush)models.Foreground).Color)
                throw new InvalidOperationException($"The model tab needs a visible, theme-aware icon in {theme} mode.");
            var panel = (StackPanel)((ScrollViewer)models.Content).Content;
            var card = panel.Children.OfType<Border>().First();
            var messages = ((StackPanel)card.Child).Children.OfType<TextBlock>().TakeLast(3).ToArray();
            if (messages.Select(message => ((SolidColorBrush)message.Foreground).Color).Distinct().Count() != 1 ||
                messages.Select(message => message.FontSize).Distinct().Count() != 1 ||
                messages.Select(message => message.FontFamily.Source).Distinct().Count() != 1)
                throw new InvalidOperationException($"Model guidance typography differs in {theme} mode.");
            Console.WriteLine($"PASS model tab icon and matching guidance typography in {theme} mode.");
            const string encoder = "https://huggingface.co/Qwen/Qwen3-Embedding-0.6B";
            var selection = ModelSelection.Default with { Code = encoder };
            model.ApplyModels(selection);
            var recipe = EmbeddingRecipes.Find(encoder)!.Recipe;
            model.ApplyModelInspection(new(selection, [new("Code", encoder, "Available", "No additional model setup is required.",
                "Reviewed native embedding recipe. Tokenizer, pooling and role formatting are automatic.",
                recipe.QueryProfiles.ToArray(), recipe.CodeQuery, false)]));
            Save(grid, Path.Combine(output, $"models-recipes-collapsed-{theme.ToLowerInvariant()}.png"));
            foreach (var expander in Descendants(grid).OfType<Expander>()) expander.IsExpanded = true;
            Save(grid, Path.Combine(output, $"models-recipes-expanded-{theme.ToLowerInvariant()}.png"));
            const string imageModel = "https://huggingface.co/microsoft/Florence-2-base-ft";
            var described = ModelSelection.Default with { Image = imageModel, ImagePipeline = new(Ocr: true) };
            model.ApplyModels(described);
            model.ApplyModelInspection(new(described, [new("Image", imageModel, "Available",
                "Image descriptions (caption + OCR) -> Documents / text",
                "Native image understanding. Task prompts, tokenizer and stages are automatic.",
                [], "default", false, ImageModel: ImageModelRecipes.Find(imageModel), EmbeddingModel: ModelSelection.DefaultUrl)]));
            Save(grid, Path.Combine(output, $"models-images-{theme.ToLowerInvariant()}.png"));
            foreach (var expander in Descendants(grid).OfType<Expander>()) expander.IsExpanded = true;
            Save(grid, Path.Combine(output, $"models-images-expanded-{theme.ToLowerInvariant()}.png"));
            model.ApplyModels(ModelSelection.Default);
        }
        Console.WriteLine("Rendered the actual XAML/resources with synthetic data; no application window, engine or live settings were started.");
    }

    private static void Save(Grid grid, string path)
    {
        grid.Measure(new Size(1600, 950)); grid.Arrange(new Rect(0, 0, 1600, 950)); grid.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Render);
        var bitmap = new RenderTargetBitmap(1600, 950, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(grid);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
