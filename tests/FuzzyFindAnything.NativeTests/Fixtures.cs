using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using FuzzyFindAnything.Core;

namespace FuzzyFindAnything.NativeTests;

internal static class Fixtures
{
    public static void NumericWorkbook(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using (var input = typeof(Fixtures).Assembly.GetManifestResourceStream("FuzzyFindAnything.Tests.Office.inventory.xlsx")!)
        using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) input.CopyTo(output);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
        archive.GetEntry("xl/worksheets/sheet1.xml")!.Delete();
        XNamespace sheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var data = new XElement(sheet + "sheetData");
        for (int row = 1; row <= 512; row++)
        {
            var cells = new XElement(sheet + "row", new XAttribute("r", row));
            for (int column = 0; column < 12; column++)
                cells.Add(new XElement(sheet + "c", new XAttribute("r", $"{(char)('A' + column)}{row}"),
                    new XElement(sheet + "v", (row * 127 + column * 13).ToString(System.Globalization.CultureInfo.InvariantCulture))));
            data.Add(cells);
        }
        using var stream = archive.CreateEntry("xl/worksheets/sheet1.xml").Open();
        new XDocument(new XElement(sheet + "worksheet", data)).Save(stream);
    }
    public static void Ui(string root)
    {
        string corpus = Path.Combine(root, "files");
        Directory.CreateDirectory(corpus);
        File.WriteAllText(Path.Combine(corpus, "Shape guide.txt"),
            "Geometry notes: a red circle is a round shape. This document describes a red circle on a white background.");
        File.WriteAllText(Path.Combine(corpus, "Draw circle.py"),
            "def draw_red_circle(canvas):\n    # Draw a red circle on a white background.\n    return canvas.create_oval(40, 40, 216, 216, fill='red')\n");
        Image(Path.Combine(corpus, "Red circle.png"), true);
        using var catalog = new Catalog(Path.Combine(root, "index"), 1);
        catalog.AddFolder(corpus);
    }
    public static void Corpus(string root)
    {
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "nested"));
        File.WriteAllText(Path.Combine(root, "orchard.txt"),
            "The apple orchard grows fresh fruit. Farmers harvest ripe apples in autumn, prune trees and manage irrigation.");
        File.WriteAllText(Path.Combine(root, "parser.py"),
            "import csv\n\ndef parse_csv_file(path):\n    # Read comma-separated values and return rows from a CSV file.\n    with open(path) as source:\n        return list(csv.DictReader(source))\n");
        File.WriteAllText(Path.Combine(root, "nested", "travel.rtf"),
            @"{\rtf1\ansi A rail journey through the Swiss Alps. Alpine hiking, glaciers and snow covered mountains.}");
        Pdf(Path.Combine(root, "stars.pdf"), "Astronomers study distant stars and galaxies. Telescopes observe planets and the Milky Way.");
        foreach (string name in new[] { "budget.docx", "inventory.xlsx", "launch.pptx" })
        {
            using var input = typeof(Fixtures).Assembly.GetManifestResourceStream("FuzzyFindAnything.Tests.Office." + name)
                ?? throw new InvalidDataException("Missing synthetic Office fixture: " + name);
            using var output = File.Create(Path.Combine(root, name));
            input.CopyTo(output);
        }
        Image(Path.Combine(root, "red-circle.png"), true);
        Image(Path.Combine(root, "blue-square.jpg"), false);
        File.WriteAllBytes(Path.Combine(root, "empty.txt"), []);
        File.WriteAllText(Path.Combine(root, "broken.pdf"), "%PDF-1.7\nThis is not a valid PDF object graph.");
    }
    public static void Image(string path, bool red)
    {
        const int size = 256;
        byte[] pixels = Enumerable.Repeat((byte)255, size * size * 3).ToArray();
        for (int y = 40; y < 216; y++)
            for (int x = 40; x < 216; x++)
                if (!red || (x - 128) * (x - 128) + (y - 128) * (y - 128) <= 88 * 88)
                {
                    int index = (y * size + x) * 3;
                    pixels[index] = red ? (byte)255 : (byte)0;
                    pixels[index + 1] = 0; pixels[index + 2] = red ? (byte)0 : (byte)255;
                }
        var image = BitmapSource.Create(size, size, 96, 96, PixelFormats.Rgb24, null, pixels, size * 3);
        BitmapEncoder encoder = Path.GetExtension(path) == ".jpg" ? new JpegBitmapEncoder { QualityLevel = 94 } : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    public static void Pdf(string path, string text)
    {
        string content = "BT /F1 12 Tf 40 720 Td (" + text.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)") + ") Tj ET";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
        ];
        var output = new StringBuilder("%PDF-1.4\n");
        var positions = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            positions.Add(Encoding.ASCII.GetByteCount(output.ToString()));
            output.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = Encoding.ASCII.GetByteCount(output.ToString());
        output.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int position in positions) output.Append($"{position:D10} 00000 n \n");
        output.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllText(path, output.ToString(), Encoding.ASCII);
    }
}
