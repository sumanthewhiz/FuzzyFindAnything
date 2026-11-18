using System.Text.Json;

namespace FuzzyFindAnything.Core;

public sealed record OriginalImageRecipe(string Graph, string Output, string Processor, string Pixels,
    string Positions, string Features, string VideoFeatures, string AudioFeatures);

internal static class OriginalGemma2Contract
{
    internal static MultimodalEmbeddingRecipe NativeInterface { get; } = new();
    private static OriginalImageRecipe Image { get; } = new("onnx/vision_encoder.onnx", "image_features",
        "gemma4-patches280-v1", "pixel_values", "pixel_position_ids", "image_features", "video_features", "audio_features");

    internal static void ValidateRecipe(EmbeddingRecipe recipe)
    {
        var current = EmbeddingRecipes.PublicDefault.Recipe;
        if (recipe.Multimodal is not null || recipe.Image != Image ||
            recipe.DocumentTemplate != "title: {title} | text: {text}" || recipe.QueryProfiles.Count != 1 ||
            recipe.QueryProfiles[0].Id != "default" || recipe.QueryProfiles[0].Prefix != current.QueryProfiles[0].Prefix)
            throw new NotSupportedException("The saved image/document recipe is not the reviewed original public Gemma2 contract.");
        var normalized = recipe with { Image = null, DocumentTemplate = null, Multimodal = NativeInterface, Queries = current.Queries };
        if (normalized.ContractFingerprint != current.ContractFingerprint)
            throw new NotSupportedException("The saved Gemma2 processing limits or tensor contract are incompatible.");
    }

    internal static string DocumentFingerprint(EmbeddingRecipe recipe)
    {
        ValidateRecipe(recipe);
        // This reviewed layout predates Multimodal. Its durable identity must not be relabeled.
        return "faccfaabfea9329914c4dc4797313ca260dc483b0cf32ca880afa9b23b65ab9a";
    }

    internal static string AssetFingerprint(IEnumerable<ModelAssetFile> files) => EmbeddingRecipe.Hash(JsonSerializer.Serialize(
        files.Where(file => !Gemma2ModelContract.IsNotice(file.Name)).OrderBy(file => file.Name, StringComparer.Ordinal)
            .Select(file => new { file.Name, file.Bytes, file.Sha256 }), Protocol.Json));

    internal static void ValidateSpace(EmbeddingSpace space)
    {
        var package = EmbeddingRecipes.PublicDefault;
        var expected = package.Files.Select(name => new ModelAssetFile(name, package.Sizes![name], package.Checksums[name]));
        if (space.Adapter != package.Adapter || space.ArtifactUrl != package.ArtifactUrl || space.Revision != package.Revision ||
            !package.ModelUrls.Contains(space.ModelUrl, StringComparer.Ordinal) || space.Dimensions != package.Dimensions ||
            space.MaximumTokens != package.MaximumTokens || !space.SupportsImages || space.MemoryBytes < package.MemoryBytes ||
            space.AssetIdentity is not null || space.AssetFingerprint != AssetFingerprint(expected))
            throw new NotSupportedException("The saved Gemma2 asset identity is not the reviewed original public package.");
    }

    internal static ModelAssetFile ResolveSource(EmbeddingSpace space, ModelAssetFile file)
    {
        var supplemental = EmbeddingRecipes.PublicDefault.Supplemental!.SingleOrDefault(asset => asset.Name == file.Name);
        return file with
        {
            SourceUrl = supplemental?.SourceUrl ?? space.ArtifactUrl,
            Revision = supplemental?.Revision ?? space.Revision
        };
    }

    internal static void ValidateManifest(CachedModel manifest)
    {
        var package = EmbeddingRecipes.PublicDefault;
        if (manifest.SourceUrl != package.SourceUrl || manifest.SourceRevision != package.SourceRevision ||
            manifest.Files.Length != package.Files.Length || AssetFingerprint(manifest.Files) != manifest.Space.AssetFingerprint)
            throw new InvalidDataException("The original public Gemma2 manifest has inconsistent asset provenance.");
        foreach (var file in manifest.Files)
        {
            var source = ResolveSource(manifest.Space, file);
            if (package.Sizes!.GetValueOrDefault(file.Name) != file.Bytes ||
                package.Checksums.GetValueOrDefault(file.Name) != file.Sha256 ||
                file.SourceUrl is not null && file.SourceUrl != source.SourceUrl ||
                file.Revision is not null && file.Revision != source.Revision)
                throw new InvalidDataException("The original public Gemma2 asset differs from its pinned source: " + file.Name);
        }
    }
}
