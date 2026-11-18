using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using FuzzyFindAnything.Core;

namespace FuzzyFindAnything.NativeTests;

internal static class PublicModelTests
{
    public static async Task RunAsync()
    {
        Program.Run("Public package identity, aliases, legacy preservation and supplemental provenance", Contracts);
        Program.Run("Unfinished onboarding eligibility and read-only catalog bindings", Onboarding);
        await DownloadsAsync();
        await CachedRepairAsync();
    }

    private static void Contracts()
    {
        var entry = EmbeddingRecipes.PublicDefault;
        var files = entry.Files.Select(name =>
        {
            var source = entry.Supplemental!.SingleOrDefault(asset => asset.Name == name);
            return new ModelAssetFile(name, entry.Sizes![name], entry.Checksums[name],
                source?.SourceUrl ?? entry.ArtifactUrl, source?.Revision ?? entry.Revision);
        }).ToArray();
        var space = new EmbeddingSpace(ModelSelection.DefaultUrl, entry.Revision, entry.Adapter, entry.Dimensions,
            entry.MaximumTokens, true, entry.MemoryBytes, entry.Recipe, entry.ArtifactUrl,
            AssetIdentity: Gemma2ModelContract.AssetIdentity(files));
        new CachedModel(space, files, entry.SourceUrl, entry.SourceRevision).Validate();
        Program.Require(ModelSelection.Default.DistinctUrls.SequenceEqual([entry.ArtifactUrl]) &&
            EmbeddingSpace.LegacyDefault.Key == "9333bc67c9e0e61c593ac65e" && space.Key != EmbeddingSpace.LegacyDefault.Key,
            "The public default overwrote the legacy vector identity.");
        var alias = space with { ModelUrl = ModelSelection.LegacyDefaultUrl };
        Program.Require(alias.Key == space.Key && alias.CacheKey == space.CacheKey, "An exact public package alias changed vector identity.");
        var noticeOnly = files.Select(file => file.Name == "README.md" ? file with { Sha256 = new string('a', 64) } : file);
        Program.Require(Gemma2ModelContract.AssetIdentity(noticeOnly) == space.AssetIdentity, "A descriptive notice changed vector identity.");
        var differentTokenizer = files.Select(file => file.Name == "tokenizer.model" ? file with { Revision = new string('1', 40) } : file).ToArray();
        Program.Require(Gemma2ModelContract.AssetIdentity(differentTokenizer) != space.AssetIdentity, "Tokenizer provenance was omitted from identity.");
        Check.Throws<InvalidDataException>(() => new CachedModel(space, differentTokenizer).Validate());
        Check.Throws<NotSupportedException>(() => (entry.Recipe with { Multimodal = new(PixelPositions: "wrong_positions") }).Validate(2048));
        Check.Throws<NotSupportedException>(() => (entry.Recipe with { PaddingToken = 7 }).Validate(2048));
        Gemma2ModelContract.ValidateTensor([new("pixels", 1, [-1, -1, 768])], "pixels", 1, [1, 2520, 768], dynamicLeading: true);
        Gemma2ModelContract.ValidateTensor([new("pixels", 1, [1, 2520, 768])], "pixels", 1, [1, 2520, 768], dynamicLeading: true);
        foreach (var invalid in new[] { new OnnxTensor("pixels", 1, [-1, -1, 512]), new("pixels", 7, [-1, -1, 768]),
            new("pixels", 1, [2, 2520, 768]), new("pixels", 1, [-1, 768]) })
            Check.Throws<NotSupportedException>(() => Gemma2ModelContract.ValidateTensor([invalid], "pixels", 1, [1, 2520, 768], dynamicLeading: true));
        Program.Require(space.FormatDocument("file{text}.txt", "literal {filename}") == "title: file{text}.txt | text: literal {filename}",
            "Document framing interpreted source braces as template substitutions.");
        var legacy = ModelSelection.Read(XElement.Parse("<FuzzyFindAnythingConfiguration version='1'/>"));
        Program.Require(legacy == ModelSelection.LegacyDefault && !legacy.RequiresVersion2, "Legacy XML defaults changed meaning.");
        var restored = JsonSerializer.Deserialize<EmbeddingSpace>(JsonSerializer.Serialize(space, Protocol.Json), Protocol.Json)!;
        Program.Require(restored.Fingerprint == space.Fingerprint, "Public identity changed after serialization.");
        var startup = new StartupProgress();
        startup.Fail(new IOException("download interrupted"));
        int failed = startup.Snapshot.Sequence;
        startup.Recovered();
        Program.Require(startup.Snapshot.State == "Ready" && startup.Snapshot.Sequence > failed && startup.Snapshot.Error == "",
            "Recovery did not publish a newer Ready snapshot.");
    }

    private static void Onboarding() => Program.Temporary(root =>
    {
        Program.Require(ModelSelection.Load(root) == ModelSelection.Default, "A fresh data directory did not select the public model.");
        string data = Path.Combine(root, "index"), source = Path.Combine(root, "files");
        Directory.CreateDirectory(source);
        using (var catalog = new Catalog(data, 1))
        {
            int folder = catalog.AddFolder(source);
            var job = Program.Enqueue(catalog, folder, Path.Combine(source, "pending.txt"), "pending file");
            Program.Require(catalog.IsUnfinishedOnboarding, "A folder or unprepared source incorrectly blocked recovery.");
            Program.Require(ModelManager.IsUnfinishedOnboarding(ModelSelection.LegacyDefault, catalog) &&
                !ModelManager.IsUnfinishedOnboarding(ModelSelection.LegacyDefault with { Code = "https://huggingface.co/test/custom" }, catalog) &&
                !ModelManager.IsUnfinishedOnboarding(ModelSelection.LegacyDefault with { TextOptions = new(Device: "CPU") }, catalog),
                "Customized selections or devices were included in automatic recovery.");
            Program.Require(ModelSelection.Load(data) == ModelSelection.LegacyDefault, "A catalog without XML lost its existing binding.");
            catalog.Prepare(job, ["pending passage"], null);
            Program.Require(!catalog.IsUnfinishedOnboarding, "Prepared passage progress was eligible for migration.");
        }
        foreach (string marker in new[] { "model_selection", "embedding_routes" })
        {
            using var catalog = new Catalog(Path.Combine(root, marker), 1);
            catalog.SetSetting(marker, "{}");
            Program.Require(!catalog.IsUnfinishedOnboarding, "Committed model metadata was eligible for migration.");
        }
        using (var catalog = new Catalog(Path.Combine(root, "results"), 1))
        {
            int folder = catalog.AddFolder(source);
            Program.Complete(catalog, Program.Enqueue(catalog, folder, Path.Combine(source, "done.txt"), "done"));
            Program.Require(!catalog.IsUnfinishedOnboarding, "Published vectors were eligible for migration.");
        }
        string selectedData = Path.Combine(root, "selected");
        using (var catalog = new Catalog(selectedData, 1))
            catalog.SetSetting("model_selection", JsonSerializer.Serialize(ModelSelection.Default, Protocol.Json));
        string hash = FilePaths.Hash(Path.Combine(selectedData, "catalog.sqlite3"));
        Program.Require(Catalog.ReadSelection(selectedData) == ModelSelection.Default &&
            FilePaths.Hash(Path.Combine(selectedData, "catalog.sqlite3")) == hash, "Read-only binding inspection modified the catalog.");
    });

    private static async Task CachedRepairAsync() => await Check.TemporaryAsync(async root =>
    {
        var space = new EmbeddingSpace("https://huggingface.co/test/immutable-cache-fixture", new string('1', 40),
            "bert-mean", 384, 128, false, 1L << 30);
        byte[] bytes = Encoding.UTF8.GetBytes("saved revision, not repository HEAD");
        var asset = new ModelAssetFile("fixture.data", bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), space.ModelUrl, space.Revision);
        var manifest = new CachedModel(space, [asset]);
        var cache = new HuggingFaceModels(Path.Combine(root, "models"), Path.Combine(root, "missing-legacy"));
        var legacy = await cache.InspectAsync("Image", ModelSelection.LegacyDefaultUrl, default, EmbeddingSpace.LegacyDefault);
        Program.Require(legacy.State == "Available" && legacy.IsSimple && legacy.Error is null &&
            !legacy.Details.Contains(EmbeddingRecipes.PublicDefault.ArtifactUrl, StringComparison.Ordinal),
            "Offline inspection replaced an initialized legacy descriptor with the public export.");
        string directory = cache.DirectoryFor(space);
        Directory.CreateDirectory(directory);
        string metadata = Path.Combine(directory, CachedModel.ManifestName);
        await File.WriteAllTextAsync(metadata, JsonSerializer.Serialize(manifest, Protocol.Json));
        string partial = Path.Combine(root, "models", ".acquiring-" + space.CacheKey, "fixture.data.partial");
        Directory.CreateDirectory(Path.GetDirectoryName(partial)!);
        await File.WriteAllBytesAsync(partial, bytes);
        var repaired = await cache.ResolveAsync(space.ModelUrl, _ => { }, default, space);
        Program.Require(repaired.Fingerprint == space.Fingerprint && !File.Exists(partial) &&
            File.ReadAllBytes(Path.Combine(directory, asset.Name)).SequenceEqual(bytes),
            "Saved-revision cache repair did not consume its verified partial offline.");
        string original = FilePaths.Hash(metadata);
        var reused = await cache.ResolveAsync(space.ModelUrl, _ => { }, default);
        Program.Require(reused == space && FilePaths.Hash(metadata) == original, "Offline cached startup rewrote identity or rediscovered HEAD.");
        Console.WriteLine("PASS saved-revision cache repair and offline manifest reuse without repository discovery");
    });

    private static async Task DownloadsAsync() => await Check.TemporaryAsync(async root =>
    {
        byte[] bytes = Encoding.UTF8.GetBytes("resumable public model test bytes");
        string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        string target = Path.Combine(root, "asset.bin"), partial = target + ".partial";
        Task<ModelAssetFile> Download(Uri uri, CancellationToken token = default) =>
            HuggingFaceModels.DownloadAsync(uri, root, "asset.bin", bytes.Length, hash, _ => { }, token);
        var unavailable = new Uri("http://127.0.0.1:9/asset");
        await File.WriteAllBytesAsync(partial, bytes);
        await Download(unavailable);
        await Download(unavailable);
        Program.Require(File.ReadAllBytes(target).SequenceEqual(bytes), "Complete partial promotion/offline reuse failed.");
        File.Delete(target);
        await File.WriteAllBytesAsync(partial, bytes[..5]);
        await using (var server = new AssetTests.Server(request =>
        {
            Program.Require(request.Contains("Range: bytes=5-"), "The saved resume offset was ignored.");
            Program.Require(request.Contains("Accept-Encoding: identity", StringComparison.OrdinalIgnoreCase) &&
                !request.Contains("gzip", StringComparison.OrdinalIgnoreCase),
                "Compressed transfer negotiation would make saved asset-byte offsets ambiguous.");
            return ("206 Partial Content", $"Content-Range: bytes 5-{bytes.Length - 1}/{bytes.Length}\r\n", bytes[5..]);
        })) await Download(server.BaseUri);
        File.Delete(target); await File.WriteAllBytesAsync(partial, bytes[..7]);
        await using (var server = new AssetTests.Server(_ => ("200 OK", "", bytes))) await Download(server.BaseUri);
        Program.Require(File.ReadAllBytes(target).SequenceEqual(bytes), "Ignoring Range duplicated saved bytes.");
        File.Delete(target); await File.WriteAllBytesAsync(partial, bytes[..5]);
        await using (var server = new AssetTests.Server(_ =>
            ("206 Partial Content", $"Content-Range: bytes 5-{bytes.Length - 1}/{bytes.Length + 1}\r\n", bytes[5..])))
            await Check.ThrowsAsync<InvalidDataException>(() => Download(server.BaseUri));
        File.Delete(partial);
        await using (var server = new AssetTests.Server(_ => ("200 OK", "", bytes[..7]), bytes.Length))
            await Check.ThrowsAsync<HttpIOException>(() => Download(server.BaseUri));
        Program.Require(File.Exists(partial) && new FileInfo(partial).Length == 7, "Interrupted transfer did not retain the resumable bytes.");
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            await Check.ThrowsAsync<OperationCanceledException>(() => Download(unavailable, cancellation.Token));
        }
        Program.Require(new FileInfo(partial).Length == 7, "Cancellation removed valid partial progress.");
        File.Delete(partial);
        await using (var server = new AssetTests.Server(_ => ("200 OK", "", new byte[bytes.Length])))
            await Check.ThrowsAsync<InvalidDataException>(() => Download(server.BaseUri));
        Program.Require(!File.Exists(target) && !File.Exists(partial), "Bad hashes were published or retained.");
        await using (var server = new AssetTests.Server(_ => ("200 OK", "", new byte[bytes.Length + 1])))
            await Check.ThrowsAsync<InvalidDataException>(() => Download(server.BaseUri));
        await Check.ThrowsAsync<ArgumentException>(() => Download(new Uri("http://example.org/asset")));
        Check.Throws<InvalidDataException>(() => CachedModel.LocalName("../escape.bin"));
        Check.Throws<InvalidDataException>(() => CachedModel.LocalName("folder./escape.bin"));
        Console.WriteLine("PASS public downloads: offline reuse, complete partial, resume, ignored/bad ranges, interruption, cancellation and integrity bounds");
    });
}
