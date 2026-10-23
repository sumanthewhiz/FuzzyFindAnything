using FuzzyFindAnything.Core;

namespace FuzzyFindAnything.App.ViewModels;

public sealed class ImagePipelineViewModel : ObservableObject
{
    private ImageModelDefinition? definition;
    private bool caption = true, ocr, useText = true;
    private string embeddingModel = ModelSelection.DefaultUrl;
    private string embeddingDevice = "Automatic";
    private string regionTask = "";
    public sealed record RegionChoice(string Id, string Label)
    {
        public override string ToString() => Label;
    }
    public IReadOnlyList<RegionChoice> RegionChoices { get; private set; } = [];
    public bool SupportsRegions => definition?.Tasks.Any(task => task.OutputKind == "regions") == true;
    public string RegionTask
    {
        get => regionTask;
        set { if (value is not null && regionTask != value) { Set(ref regionTask, value); OptionsChanged?.Invoke(); } }
    }
    public event Action? OptionsChanged;
    public bool IsVisible => definition is not null;
    public bool SupportsCaption => definition?.Tasks.Any(task => task.Id == "caption") == true;
    public bool SupportsOcr => definition?.Tasks.Any(task => task.Id == "ocr") == true;
    public bool UsesCustomEmbedding => !UseTextModel;
    public bool Caption { get => caption; set { if (caption != value) { Set(ref caption, value); OptionsChanged?.Invoke(); } } }
    public bool Ocr { get => ocr; set { if (ocr != value) { Set(ref ocr, value); OptionsChanged?.Invoke(); } } }
    public bool UseTextModel
    {
        get => useText;
        set { if (useText != value) { Set(ref useText, value); Changed(nameof(UsesCustomEmbedding)); OptionsChanged?.Invoke(); } }
    }
    public string EmbeddingModelUrl
    {
        get => embeddingModel;
        set { if (embeddingModel != value) { Set(ref embeddingModel, value); OptionsChanged?.Invoke(); } }
    }
    public void Reset(string url)
    {
        definition = ImageModelRecipes.Find(url.Trim().TrimEnd('/'));
        caption = definition?.Tasks.Any(task => task.Id == "caption") ?? true;
        ocr = definition is not null && !caption && definition.Tasks.Any(task => task.Id == "ocr");
        useText = true; embeddingModel = ModelSelection.DefaultUrl; embeddingDevice = "Automatic"; regionTask = "";
        UpdateRegionChoices();
        Changed(null);
    }
    public void Restore(ModelSelection selection)
    {
        definition = ImageModelRecipes.Find(selection.Image);
        var options = selection.EffectiveImagePipeline;
        caption = options.Caption; ocr = options.Ocr; useText = options.EmbeddingModel is null;
        embeddingModel = options.EmbeddingModel ?? selection.Text;
        embeddingDevice = options.EmbeddingOptions?.Device ?? "Automatic";
        regionTask = options.RegionTask;
        UpdateRegionChoices();
        Changed(null);
    }
    public void ApplyDefinition(ImageModelDefinition? value)
    {
        definition = value;
        UpdateRegionChoices();
        Changed(nameof(IsVisible)); Changed(nameof(SupportsCaption)); Changed(nameof(SupportsOcr));
    }
    private void UpdateRegionChoices()
    {
        RegionChoice[] choices = [new("", "No region task"), .. (definition?.Tasks.Where(task => task.OutputKind == "regions")
            .Select(task => new RegionChoice(task.Id, task.Label)) ?? [])];
        if (!RegionChoices.SequenceEqual(choices)) { RegionChoices = choices; Changed(nameof(RegionChoices)); }
        Changed(nameof(RegionTask)); Changed(nameof(SupportsRegions));
    }
    public ImagePipelineOptions? Options(string queryProfile)
    {
        if (!IsVisible) return null;
        var options = new ModelOptions(queryProfile, useText ? "Automatic" : embeddingDevice).Normalize();
        return new ImagePipelineOptions(Caption, Ocr, useText ? null : embeddingModel,
            options == ModelOptions.Default ? null : options, regionTask).Normalize();
    }
}
