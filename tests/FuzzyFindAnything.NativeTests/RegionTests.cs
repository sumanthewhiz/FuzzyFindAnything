using System.IO;
using FuzzyFindAnything.Core;

namespace FuzzyFindAnything.NativeTests;

internal static class RegionTests
{
    public static void Run()
    {
        Program.Run("Typed region parsing, bounds and partial coverage", Parser);
        Program.Run("Region persistence, retrieval and selective cache reuse", Storage);
    }
    private static void Parser()
    {
        var result = RegionPostprocessor.Parse("objects", "cat<loc_0><loc_20><loc_400><loc_900>remote control<loc_500><loc_500><loc_999><loc_950>",
            640, 480, true, 24);
        Program.Require(result.State == "Completed" && result.Regions?.Length == 2 &&
            result.Regions[0].Left == 0.0005 && result.Regions[0].Right == 0.4005 &&
            result.Regions[1].Confidence is null, "Region geometry or detector confidence was fabricated.");
        result.Validate("objects");
        var definition = ImageModelRecipes.Find("https://huggingface.co/microsoft/Florence-2-base-ft")!;
        result.Validate(definition.Task("objects"));
        Check.Throws<InvalidDataException>(() =>
            new ImageTaskResult("objects", "cat<loc_0><loc_0><loc_999><loc_999>", "Completed").Validate(definition.Task("objects")));
        Check.Throws<InvalidDataException>(() => (result with { Task = "caption" }).Validate(definition.Task("caption")));
        var repeated = RegionPostprocessor.Parse("objects",
            "wheel<loc_10><loc_20><loc_200><loc_800><loc_600><loc_20><loc_900><loc_800>", 640, 480, true, 12);
        Program.Require(repeated.State == "Completed" && repeated.Regions?.Length == 2 &&
            repeated.Regions.All(region => region.Text == "wheel"), "A label shared by multiple boxes was lost.");
        var partial = RegionPostprocessor.Parse("region-descriptions",
            "red car<loc_10><loc_20><loc_600><loc_800>broken<loc_20><loc_10>", 100, 200, false, 768);
        Program.Require(partial.State == "Partial" && partial.Regions?.Length == 1 &&
            !partial.Text.Contains("<loc_"), "Incomplete region tokens became successful embedding text.");
        var capped = RegionPostprocessor.Parse("objects", string.Concat(Enumerable.Range(0, 66)
            .Select(index => $"item{index}<loc_0><loc_0><loc_999><loc_999>")), 100, 100, true, 768);
        Program.Require(capped.State == "Partial" && capped.Regions?.Length == 64 &&
            capped.Warning?.Contains("limit", StringComparison.Ordinal) == true,
            "Valid output beyond the region limit was misreported as malformed.");
        Program.Require(RegionPostprocessor.Parse("objects", "", 100, 100, true, 2).State == "Empty", "No detections was not a completed-empty outcome.");
        Program.Require(RegionPostprocessor.Parse("objects", "cat<loc_999><loc_0><loc_0><loc_999>", 100, 100, true, 10).State == "Failed",
            "Reversed boxes were silently repaired.");
        Check.Throws<InvalidDataException>(() => new ImageRegion("0", "cat", double.NaN, 0, 1, 1, 100, 100).Validate());
        var selected = ModelSelection.Default with
        {
            Image = "https://huggingface.co/microsoft/Florence-2-base-ft",
            ImagePipeline = new(false, false, RegionTask: "objects")
        };
        var root = new System.Xml.Linq.XElement("FuzzyFindAnythingConfiguration", new System.Xml.Linq.XAttribute("version", "3"), selected.ToXml(true));
        Program.Require(ModelSelection.Read(root) == selected && selected.EffectiveImagePipeline.Tasks.SequenceEqual(["objects"]),
            "Region-only image selection did not round-trip.");
    }
    private static void Storage() => Program.Temporary(root =>
    {
        string data = Path.Combine(root, "index"), folderPath = Path.Combine(root, "images");
        Directory.CreateDirectory(folderPath);
        var definition = ImageModelRecipes.Find("https://huggingface.co/microsoft/Florence-2-base-ft")!;
        var selected = ModelSelection.Default with { Image = definition.ModelUrls[0], ImagePipeline = new(false, false, RegionTask: "objects") };
        var pipeline = new ImagePipelineDescriptor(selected.Image, definition, ["objects"], EmbeddingSpace.Default, ModelOptions.Default, "Automatic");
        long artifact;
        using (var catalog = new Catalog(data, 1))
        {
            int folder = catalog.AddFolder(folderPath);
            var routes = new Dictionary<string, EmbeddingSpace> { ["Document"] = EmbeddingSpace.Default, ["Code"] = EmbeddingSpace.Default, ["Image"] = EmbeddingSpace.Default };
            catalog.ReconfigureModels(routes, () => { }, selection: selected, pipeline: pipeline);
            var job = Program.Enqueue(catalog, folder, Path.Combine(folderPath, "two-objects.png"), "region fixture", "Image");
            var result = RegionPostprocessor.Parse("objects", "cat<loc_10><loc_10><loc_400><loc_900>cup<loc_500><loc_200><loc_800><loc_700>", 400, 300, true, 25);
            var stored = catalog.SaveImageTask(job, definition.TaskKey("objects"), result); artifact = stored.Id;
            var passages = result.Regions!.Select((region, index) => new Passage(index, region.Text, "objects", stored.Id, region)).ToArray();
            catalog.PrepareImage(job, passages, null);
            catalog.SaveBatch(job, catalog.Remaining(job), [Program.Vector(0), Program.Vector(1)]);
            catalog.Complete(job);
            var matches = catalog.Search(Program.Vector(1), 10, 0, kinds: ["Image"]);
            Program.Require(matches.Count == 1 && matches[0].Region is { Text: "cup", SourceWidth: 400 } &&
                matches[0].Kind == "Image" && catalog.Counts() == (1, 2), "Regional matches lost parent deduplication or geometry.");
        }
        using (var catalog = new Catalog(data, 1))
        {
            Program.Require(catalog.Setting("schema_version") == "9" &&
                catalog.Search(Program.Vector(0), 1, 0, kinds: ["Image"]).Single().Region?.Text == "cat" &&
                Check.Scalar(data, "SELECT id FROM image_representations") == artifact,
                "Region provenance did not survive reopening the index.");
            catalog.RemoveFolders(catalog.Folders().Select(folder => folder.Id).ToArray());
            Program.Require(Check.Scalar(data, "SELECT COUNT(*) FROM image_representations") == 0 && catalog.Counts() == (0, 0),
                "Region text or vectors remained after the last source owner was removed.");
        }
    });
}
