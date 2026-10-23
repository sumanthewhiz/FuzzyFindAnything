using System.IO;
using FuzzyFindAnything.Core;

namespace FuzzyFindAnything.App.ViewModels;

public sealed class ModelSourceViewModel(string kind) : ObservableObject
{
    public sealed record Choice(string Id, string Label, bool Available = true, string? ModelId = null, string? Device = null);
    private string source = "huggingface", version = "", model = "", message = "", variant = "", devicePolicy = "Automatic";
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
            source = value; Invalidate(""); SelectionChanged?.Invoke(IsMicrosoft);
        }
    }
    public string Version
    {
        get => version;
        set
        {
            if (value is null || value == version) return;
            version = value; Invalidate(""); SelectionChanged?.Invoke(true);
        }
    }
    public string Model => model;
    public string Device => devicePolicy;
    public string SelectedVariant
    {
        get => variant;
        set
        {
            if (value is null || value == variant) return;
            var choice = Models.SingleOrDefault(choice => choice.Id == value && choice.Available);
            if (choice is not { ModelId: not null, Device: not null })
            { Invalidate("Choose an available model and device."); return; }
            variant = value; model = choice.ModelId; devicePolicy = choice.Device;
            Changed(null); SelectionChanged?.Invoke(false);
        }
    }
    public bool IsMicrosoft => source == "microsoft";
    public bool IsHuggingFace => !IsMicrosoft;
    public bool IsLoading
    {
        get => loading;
        set { loading = value; if (value) { Models = []; variant = ""; } Changed(null); }
    }
    public bool CanSelect => !loading;
    public bool IsReady => !IsMicrosoft || !loading && Versions.Any(choice => choice.Id == version && choice.Available) &&
        Models.Any(choice => choice.Id == variant && choice.Available);
    public string Message { get => message; set => Set(ref message, value); }

    public string Reference(string huggingFace)
    {
        if (!IsMicrosoft) return ModelSelection.Canonical(huggingFace);
        if (version.Length == 0 || model.Length == 0) throw new ArgumentException("Choose an available Microsoft model and PSAPI drop.");
        return ModelReference.Parse(new ProviderModelReference("microsoft", version, model).Key).Key;
    }
    public ModelOptions? Options(ModelOptions? value)
    {
        if (!IsMicrosoft) return value;
        var result = (value ?? ModelOptions.Default) with { Device = devicePolicy };
        return result == ModelOptions.Default ? null : result;
    }
    public void Invalidate(string notice)
    {
        Models = []; variant = ""; message = notice; Changed(null);
    }
    private string VariantForSelection()
    {
        var candidates = Models.Where(choice => choice.ModelId == model && choice.Available);
        return (devicePolicy == "Automatic"
            ? candidates.FirstOrDefault(choice => choice.Device == "CPU") ?? candidates.FirstOrDefault()
            : candidates.FirstOrDefault(choice => choice.Device == devicePolicy))?.Id ?? "";
    }
    public void Restore(ModelReference reference, ModelOptions options)
    {
        if (reference is ProviderModelReference provider)
        {
            if (provider.Provider != "microsoft") throw new NotSupportedException("This interface supports Microsoft and Hugging Face model sources.");
            bool sameCatalog = source == "microsoft" && version == provider.Version;
            if (!sameCatalog) { Models = []; Versions = []; }
            source = "microsoft"; version = provider.Version; model = provider.Model;
            devicePolicy = options.Device; variant = VariantForSelection();
        }
        else { source = "huggingface"; Models = []; variant = ""; }
        Changed(null);
    }
    public void Apply(ProviderCatalog catalog)
    {
        if (catalog.Provider != "microsoft") throw new InvalidDataException("The model catalog has a different provider.");
        catalog = catalog.ForCaller();
        string previousVersion = version, previousModel = model;
        version = catalog.Version;
        var versionChoices = catalog.Versions.Select(value => new Choice(value.Id, value.Id)).ToList();
        if (!Versions.SequenceEqual(versionChoices)) Versions = versionChoices;
        Choice[] modelChoices = catalog.Models.Where(value => value.State == "Available" && value.Kinds.Contains(Kind))
            .SelectMany(value => value.Devices.Where(device => device.Available).Select(device =>
                new Choice(value.Id + "|" + device.Id, value.Label + " / " + device.Label, true, value.Id, device.Id))).ToArray();
        if (!Models.SequenceEqual(modelChoices)) Models = modelChoices;
        if (model.Length == 0 && Models.FirstOrDefault() is { } first)
        { model = first.ModelId!; devicePolicy = first.Device!; }
        variant = VariantForSelection();
        var selected = catalog.Models.SingleOrDefault(value => value.Id == model);
        string detail = selected?.Detail ?? "The configured model is unavailable for this caller. Choose an available model and device.";
        message = catalog.AvailabilityNotice.Length == 0 ? detail : catalog.AvailabilityNotice + " " + detail;
        loading = false;
        Changed(null);
        if (version != previousVersion || model != previousModel) SelectionChanged?.Invoke(false);
    }
}
