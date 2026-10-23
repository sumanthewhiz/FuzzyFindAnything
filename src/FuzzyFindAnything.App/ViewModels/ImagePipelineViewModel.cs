using FuzzyFindAnything.Core;

namespace FuzzyFindAnything.App.ViewModels;

public sealed class ImagePipelineViewModel : ObservableObject
{
    private ImageModelDefinition? definition;
    private bool caption = true, ocr, useText = true;
    private string embeddingModel = ModelSelection.DefaultUrl;
    private string embeddingDevice = "Automatic";
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
        useText = true; embeddingModel = ModelSelection.DefaultUrl; embeddingDevice = "Automatic";
        Changed(null);
    }
    public void Restore(ModelSelection selection)
    {
        definition = ImageModelRecipes.Find(selection.Image);
        var options = selection.EffectiveImagePipeline;
        caption = options.Caption; ocr = options.Ocr; useText = options.EmbeddingModel is null;
        embeddingModel = options.EmbeddingModel ?? selection.Text;
        embeddingDevice = options.EmbeddingOptions?.Device ?? "Automatic";
        Changed(null);
    }
    public void ApplyDefinition(ImageModelDefinition? value)
    {
        definition = value;
        Changed(nameof(IsVisible)); Changed(nameof(SupportsCaption)); Changed(nameof(SupportsOcr));
    }
    public ImagePipelineOptions? Options(string queryProfile)
    {
        if (!IsVisible) return null;
        var options = new ModelOptions(queryProfile, useText ? "Automatic" : embeddingDevice).Normalize();
        return new ImagePipelineOptions(Caption, Ocr, useText ? null : embeddingModel,
            options == ModelOptions.Default ? null : options).Normalize();
    }
}
