using System.Xml.Linq;

namespace FuzzyFindAnything.Core;

internal sealed class ModelSettingsUpdate : IDisposable
{
    private readonly string target, temporary;
    private readonly byte[]? original;
    private bool published, committed;
    public ModelSettingsUpdate(string directory, ModelSelection selection)
    {
        target = Path.Combine(directory, "settings.xml");
        original = File.Exists(target) ? File.ReadAllBytes(target) : null;
        var document = original is null ? new XDocument(new XElement("FuzzyFindAnythingConfiguration", new XAttribute("version", "1"))) :
            ModelSelection.ReadDocument(target);
        document.Root!.Element("Models")?.Remove();
        document.Root.Add(selection.ToXml());
        temporary = Path.Combine(directory, ".model-settings-" + Guid.NewGuid().ToString("N") + ".tmp");
        using var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        document.Save(stream); stream.Flush(true);
    }
    public void Publish() { File.Move(temporary, target, true); published = true; }
    public void Commit() => committed = true;
    public void Dispose()
    {
        try
        {
            if (published && !committed)
            {
                if (original is null) File.Delete(target);
                else
                {
                    using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                    { stream.Write(original); stream.Flush(true); }
                    File.Move(temporary, target, true);
                }
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
