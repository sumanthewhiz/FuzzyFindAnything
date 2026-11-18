using System.Text.Json;

namespace FuzzyFindAnything.Core;

public static class Gemma2ModelContract
{
    public static string AssetIdentity(IEnumerable<ModelAssetFile> files) => EmbeddingRecipe.Hash(JsonSerializer.Serialize(
        files.Where(file => !IsNotice(file.Name)).OrderBy(file => file.Name, StringComparer.Ordinal)
            .Select(file => new { file.Name, file.Bytes, file.Sha256, file.SourceUrl, file.Revision }), Protocol.Json));

    internal static bool IsNotice(string name) =>
        Path.GetFileName(name).StartsWith("README", StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(name).StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase);

    public static void ValidateMetadata(string directory)
    {
        using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "config.json")));
        using var processor = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "processor_config.json")));
        using var tokenizer = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "tokenizer_config.json")));
        try
        {
            var root = config.RootElement;
            var text = root.GetProperty("text_config");
            var vision = root.GetProperty("vision_config");
            var image = processor.RootElement.GetProperty("image_processor");
            var tokens = tokenizer.RootElement;
            bool valid = root.GetProperty("model_type").GetString() == "embedding_gemma2" &&
                text.GetProperty("hidden_size").GetInt32() == 512 && text.GetProperty("embedding_dim").GetInt32() == 768 &&
                text.GetProperty("bos_token_id").GetInt32() == 2 && text.GetProperty("eos_token_id").GetInt32() == 1 &&
                text.GetProperty("pad_token_id").GetInt32() == 0 && root.GetProperty("image_token_id").GetInt32() == 258880 &&
                root.GetProperty("boi_token_id").GetInt32() == 255999 && root.GetProperty("eoi_token_id").GetInt32() == 258882 &&
                root.GetProperty("vision_soft_tokens_per_image").GetInt32() == 280 &&
                vision.GetProperty("patch_size").GetInt32() == 16 && vision.GetProperty("pooling_kernel_size").GetInt32() == 3 &&
                processor.RootElement.GetProperty("processor_class").GetString() == "EmbeddingGemma2Processor" &&
                image.GetProperty("image_processor_type").GetString() == "Gemma4ImageProcessor" &&
                image.GetProperty("do_convert_rgb").GetBoolean() && image.GetProperty("do_resize").GetBoolean() &&
                image.GetProperty("do_rescale").GetBoolean() && !image.GetProperty("do_normalize").GetBoolean() &&
                Math.Abs(image.GetProperty("rescale_factor").GetDouble() - 1d / 255) < 1e-12 &&
                image.GetProperty("resample").GetInt32() == 3 && image.GetProperty("patch_size").GetInt32() == 16 &&
                image.GetProperty("pooling_kernel_size").GetInt32() == 3 && image.GetProperty("max_soft_tokens").GetInt32() == 280 &&
                image.GetProperty("image_seq_length").GetInt32() == 280 &&
                tokens.GetProperty("tokenizer_class").GetString() == "GemmaTokenizer" &&
                tokens.GetProperty("padding_side").GetString() == "right" &&
                tokens.GetProperty("bos_token").GetString() == "<bos>" && tokens.GetProperty("eos_token").GetString() == "<eos>" &&
                tokens.GetProperty("pad_token").GetString() == "<pad>";
            if (!valid) throw new NotSupportedException("The export has an incompatible Gemma2 tokenizer/image processor contract.");
        }
        catch (KeyNotFoundException error)
        { throw new NotSupportedException("Required Gemma2 processor/tokenizer metadata is missing.", error); }
    }

    public static void ValidateGraphs(string directory, EmbeddingRecipe recipe)
    {
        var binding = recipe.NativeMultimodal ?? throw new InvalidDataException("A multimodal binding is required.");
        var language = OnnxMetadata.Read(Path.Combine(directory, CachedModel.LocalName(recipe.Graph)));
        var vision = OnnxMetadata.Read(Path.Combine(directory, CachedModel.LocalName(binding.VisionGraph)));
        if (language.Inputs.Length != 5 || vision.Inputs.Length != 2)
            throw new NotSupportedException("The Gemma2 graph input interface is not supported.");
        ValidateTensor(language.Inputs, recipe.InputIds, 7, [-1, -1]);
        ValidateTensor(language.Inputs, recipe.AttentionMask, 7, [-1, -1]);
        foreach (string name in new[] { binding.ImageFeatures, binding.VideoFeatures, binding.AudioFeatures })
            ValidateTensor(language.Inputs, name, 1, [-1, 512]);
        ValidateTensor(language.Outputs, recipe.Output, 1, [-1, 768]);
        ValidateTensor(vision.Inputs, binding.PixelValues, 1, [1, 2520, 768], dynamicLeading: true);
        ValidateTensor(vision.Inputs, binding.PixelPositions, 7, [1, 2520, 2], dynamicLeading: true);
        ValidateTensor(vision.Outputs, binding.VisionOutput, 1, [-1, 512]);
    }

    internal static void ValidateTensor(IReadOnlyList<OnnxTensor> values, string name, int type, long[] dimensions,
        bool dynamicLeading = false)
    {
        var value = values.SingleOrDefault(value => value.Name == name);
        if (value is null || value.ElementType != type || value.Dimensions.Length != dimensions.Length ||
            value.Dimensions.Where((actual, index) => dimensions[index] < 0 ? actual > 0 :
                actual != dimensions[index] && !(dynamicLeading && index < dimensions.Length - 1 && actual < 0)).Any())
            throw new NotSupportedException($"The Gemma2 graph tensor '{name}' has an incompatible type, rank or shape.");
    }
}
