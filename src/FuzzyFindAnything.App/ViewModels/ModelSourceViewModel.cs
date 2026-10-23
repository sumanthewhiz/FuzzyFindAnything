using System.IO;
using FuzzyFindAnything.Core;

namespace FuzzyFindAnything.App.ViewModels;

public sealed class ModelSourceViewModel(string kind) : ObservableObject
{
    public sealed record Choice(string Id, string Label, bool Available = true);
    private string source = "huggingface", version = "", model = "", message = "";
    private bool loading;
    public string Kind { get; } = kind;
    public string Prefix => Kind == "Document" ? "Text" : Kind;
    public string SourceControlId => Prefix + "ModelSource";
    public string ModelControlId => Prefix + "MicrosoftModel";
    public string VersionControlId => Prefix + "PsapiVersion";
    public string RefreshControlId => Prefix + "PsapiRefresh";
    public string VersionOptionsControlId => Prefix + "PsapiOptions";
    public IReadOnlyList<Choice> Sources { get; } = [new("huggingface", "Hugging Face"), new("microsoft", "Microsoft (PSAPI)")];
    public IReadOnlyList<Choice> Models { get; private set; } = [];
    public IReadOnlyList<Choice> Versions { get; private set; } = [];
    public event Action<bool>? SelectionChanged;
    public string Source
    {
        get => source;
        set
        {
            if (value is null || value == source) return;
            if (value is not ("huggingface" or "microsoft")) throw new ArgumentException("Choose a supported model source.");
            source = value; Changed(null); SelectionChanged?.Invoke(IsMicrosoft);
        }
    }
    public string Version
    {
        get => version;
        set
        {
            if (value is null || value == version) return;
            version = value; Changed(null); SelectionChanged?.Invoke(true);
        }
    }
    public string Model
    {
        get => model;
        set
        {
            if (value is null || value == model) return;
            model = value; Changed(null); SelectionChanged?.Invoke(false);
        }
    }
    public bool IsMicrosoft => source == "microsoft";
    public bool IsHuggingFace => !IsMicrosoft;
    public bool IsLoading { get => loading; set { loading = value; Changed(null); } }
    public bool CanSelect => !loading;
    public bool IsReady => !IsMicrosoft || !loading && version.Length > 0 && model.Length > 0 && Models.Any(choice => choice.Id == model && choice.Available);
    public string Message { get => message; set => Set(ref message, value); }

    public string Reference(string huggingFace)
    {
        if (!IsMicrosoft) return ModelSelection.Canonical(huggingFace);
        if (version.Length == 0 || model.Length == 0) throw new ArgumentException("Choose an available Microsoft model and PSAPI drop.");
        return ModelReference.Parse(new ProviderModelReference("microsoft", version, model).Key).Key;
    }
    public void Restore(ModelReference reference)
    {
        if (reference is ProviderModelReference provider)
        {
            if (provider.Provider != "microsoft") throw new NotSupportedException("This interface supports Microsoft and Hugging Face model sources.");
            source = "microsoft"; version = provider.Version; model = provider.Model;
            if (!Versions.Any(choice => choice.Id == version)) Versions = [new(version, version)];
            if (!Models.Any(choice => choice.Id == model)) Models = [new(model, model)];
        }
        else source = "huggingface";
        Changed(null);
    }
    public void Apply(ProviderCatalog catalog)
    {
        if (catalog.Provider != "microsoft") throw new InvalidDataException("The model catalog has a different provider.");
        string previousVersion = version, previousModel = model;
        version = catalog.Version;
        var versionChoices = catalog.Versions.Select(value => new Choice(value.Id,
            value.Id + (value.Supported ? "" : " (requires qualification)"))).ToList();
        if (!versionChoices.Any(choice => choice.Id == version)) versionChoices.Add(new(version, version));
        if (!Versions.SequenceEqual(versionChoices)) Versions = versionChoices;
        Choice[] modelChoices = catalog.Models.Where(value => value.Kinds.Contains(Kind) && value.Devices.Any(device => device.Available))
            .Select(value => new Choice(value.Id, value.Label)).ToArray();
        if (model.Length > 0 && !modelChoices.Any(choice => choice.Id == model))
            modelChoices = [.. modelChoices, new(model, model + " (unavailable in this drop)", false)];
        if (!Models.SequenceEqual(modelChoices)) Models = modelChoices;
        if (model.Length == 0) model = Models.FirstOrDefault(choice => choice.Available)?.Id ?? "";
        var selected = catalog.Models.SingleOrDefault(value => value.Id == model);
        message = selected?.Detail ?? "This drop has no qualifying model/package/device combination for this file type.";
        loading = false;
        Changed(null);
        if (version != previousVersion || model != previousModel) SelectionChanged?.Invoke(false);
    }
}
