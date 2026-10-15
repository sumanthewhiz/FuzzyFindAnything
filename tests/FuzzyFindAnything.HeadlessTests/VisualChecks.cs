using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using FuzzyFindAnything.App.Services;
using FuzzyFindAnything.App.ViewModels;

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
        var events = new HashSet<string> { "Click", "Checked", "Unchecked", "KeyDown", "ValueChanged", "SelectionChanged", "Loaded", "Closing" };
        foreach (var attribute in content.DescendantsAndSelf().Attributes().Where(attribute => events.Contains(attribute.Name.LocalName)).ToArray())
            attribute.Remove();
        var model = new MainViewModel { Query = "a red circle on a white background", Threshold = 0.35 };
        model.Apply(new EngineStatus("Ready", "Search engine ready", "", [], 3, 3, "USN journal + directory notifications", "",
            new IndexMetadata("DiskANN (0.7.0; abi=1)", @"C:\SearchData", 3, 1, 1, 1048576, DateTimeOffset.UtcNow.ToString("O"), 1),
            new CpuStatus(12, 6, 2, 3, 6, 8), 0, 0, [], new LoggingStatus(true, @"C:\SearchData\Logs", ""),
            new StartupStatus(1, 100, 9, 9, "Search engine ready", "Ready", "")));
        model.FinishSearch([
            new(@"C:\Sample files\Shape guide.txt", "Shape guide.txt", "Document", 400, 0, "A red circle is a round shape on a white background.", 0.87),
            new(@"C:\Sample files\Draw circle.py", "Draw circle.py", "Code", 200, 0, "def draw_circle(canvas):\n    return canvas.create_oval(40, 40, 216, 216)", 0.81),
            new(@"C:\Sample files\Red circle.png", "Red circle.png", "Image", 1024, 0, "Visual content", 0.92)
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
            grid.Measure(new Size(1600, 950)); grid.Arrange(new Rect(0, 0, 1600, 950)); grid.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Render);
            var bitmap = new RenderTargetBitmap(1600, 950, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(grid);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(output, $"search-{theme.ToLowerInvariant()}.png"));
            encoder.Save(stream);
        }
        Console.WriteLine("Rendered the actual XAML/resources with synthetic data; no application window, engine or live settings were started.");
    }
}
