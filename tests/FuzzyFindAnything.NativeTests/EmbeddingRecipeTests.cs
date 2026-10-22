using System.IO;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using FuzzyFindAnything.Core;

namespace FuzzyFindAnything.NativeTests;

internal static class EmbeddingRecipeTests
{
    public static void Run()
    {
        Program.Run("Reviewed recipe identities, aliases and explicit options", Identities);
        Program.Run("Mean, CLS, last-token and pre-pooled embedding contracts", Pooling);
        Program.Run("Bounded ONNX metadata and external-weight paths", Metadata);
        Program.Run("Shared-space kind filtering and query-only configuration", Bindings);
    }
    private static EmbeddingSpace Space(string url)
    {
        var entry = EmbeddingRecipes.Find(url) ?? throw new InvalidOperationException("Missing reference recipe.");
        return new(url, entry.Revision, "native-text", entry.Dimensions, entry.MaximumTokens, false,
            entry.MemoryBytes, entry.Recipe, entry.ArtifactUrl);
    }
    private static void Identities()
    {
        var qwen = Space("https://huggingface.co/Qwen/Qwen3-Embedding-0.6B");
        qwen.Validate();
        var alias = Space("https://huggingface.co/Qdrant/Qwen3-Embedding-0.6B-onnx");
        Program.Require(qwen.Key == alias.Key && qwen.CacheKey == alias.CacheKey, "An exact artifact alias created a different vector space/cache.");
        var queryChange = qwen with { Recipe = qwen.Recipe! with { CodeQuery = "document-search-v1" } };
        Program.Require(qwen.Fingerprint == queryChange.Fingerprint && qwen.IndexingFingerprint == queryChange.IndexingFingerprint &&
            qwen.Recipe!.ContractFingerprint != queryChange.Recipe!.ContractFingerprint, "Query recipes were confused with document embeddings.");
        var chunks = qwen with { Recipe = qwen.Recipe! with { ChunkTokens = 256 } };
        Program.Require(chunks.Key == qwen.Key && chunks.IndexingFingerprint != qwen.IndexingFingerprint, "Chunk changes must invalidate passages without conflating vector spaces.");
        var prefix = qwen with { Recipe = qwen.Recipe! with { DocumentPrefix = "passage: " } };
        Program.Require(prefix.Key != qwen.Key, "A document formatting change retained the old vector identity.");
        Program.Require(EmbeddingSpace.Default.Fingerprint == ModelContract.Fingerprint &&
            EmbeddingSpace.Default.Key == "9333bc67c9e0e61c593ac65e", "Default migration changed the established Gemma space.");
        Program.Require(new ModelSelection(ModelSelection.DefaultUrl, ModelSelection.DefaultUrl, ModelSelection.DefaultUrl,
            new("automatic", "automatic")).Normalize() == ModelSelection.Default, "Hidden automatic defaults broke no-op equality.");
        var explicitPurpose = ModelSelection.Default with { Code = qwen.ModelUrl, CodeOptions = new("code-search-v1") };
        var normalized = ModelManager.NormalizeSelection(explicitPurpose, new Dictionary<string, EmbeddingSpace>
        { [ModelSelection.DefaultUrl] = EmbeddingSpace.Default, [qwen.ModelUrl] = qwen });
        Program.Require(normalized == explicitPurpose, "An explicitly selected default query purpose was erased before persistence.");
        Check.Throws<ArgumentException>(() => new ModelOptions("not an approved profile").Normalize());
        Check.Throws<NotSupportedException>(() => qwen.Recipe!.Query("unknown-profile"));
        Check.Throws<InvalidDataException>(() => (qwen.Recipe! with { Pooling = "guessed" }).Validate(2048));
        Check.Throws<InvalidDataException>(() => new CachedModel(qwen, []).Verify(Path.GetTempPath()));
        var roundTrip = JsonSerializer.Deserialize<EmbeddingSpace>(JsonSerializer.Serialize(qwen, Protocol.Json), Protocol.Json)!;
        Program.Require(roundTrip.Fingerprint == qwen.Fingerprint && roundTrip.CacheKey == qwen.CacheKey, "Recipe serialization changed the resolved identity.");
    }
    private static void Pooling()
    {
        const int width = 8;
        float[] tokens = new float[2 * 4 * width];
        tokens[width] = 3; tokens[width + 1] = 4;
        tokens[2 * width + 2] = 2;
        tokens[4 * width + 3] = 1;
        tokens[5 * width + 4] = 1;
        long[] mask = [0, 1, 1, 0, 1, 1, 0, 0];
        var first = EmbeddingPooling.Normalize(tokens, mask, 2, 4, width, "cls");
        var last = EmbeddingPooling.Normalize(tokens, mask, 2, 4, width, "last-token");
        var mean = EmbeddingPooling.Normalize(tokens, mask, 2, 4, width, "mean");
        Program.Require(Math.Abs(first[0][0] - 0.6) < 0.00001 && first[1][3] == 1 &&
            last[0][2] == 1 && last[1][4] == 1 && mean[0][2] > 0 &&
            Math.Abs(mean[1][3] - Math.Sqrt(0.5)) < 0.00001, "Pooling ignored the padding mask or selected the wrong token.");
        var pooled = EmbeddingPooling.Normalize(first.SelectMany(value => value).ToArray(), mask, 2, 4, width, "pooled");
        Program.Require(ModelContract.Similarity(pooled[0], first[0]) > 0.999999, "An already-pooled output was pooled again.");
        Check.Throws<InvalidDataException>(() => EmbeddingPooling.Normalize(tokens, new long[8], 2, 4, width, "mean"));
        Check.Throws<InvalidDataException>(() => EmbeddingPooling.Normalize(tokens[..^1], mask, 2, 4, width, "mean"));
        tokens[2 * width] = float.NaN;
        Check.Throws<InvalidDataException>(() => EmbeddingPooling.Normalize(tokens, mask, 2, 4, width, "last-token"));
    }
    private static void Metadata() => Program.Temporary(root =>
    {
        static byte[] Var(ulong value)
        {
            var bytes = new List<byte>();
            while (value >= 128) { bytes.Add((byte)((value & 127) | 128)); value >>= 7; }
            bytes.Add((byte)value); return bytes.ToArray();
        }
        static byte[] Field(int id, byte[] value) => Var((ulong)(id * 8 + 2)).Concat(Var((ulong)value.Length)).Concat(value).ToArray();
        static byte[] Integer(int id, int value) => Var((ulong)(id * 8)).Concat(Var((ulong)value)).ToArray();
        static byte[] Text(int id, string value) => Field(id, Encoding.UTF8.GetBytes(value));
        static byte[] Join(params byte[][] arrays) => arrays.SelectMany(value => value).ToArray();
        byte[] Value(string name, int type) => Join(Text(1, name),
            Field(2, Field(1, Join(Integer(1, type), Field(2, Join(Field(1, Text(2, "batch")), Field(1, Integer(1, 8))))))));
        byte[] Model(string location)
        {
            byte[] external = Join(Field(13, Join(Text(1, "location"), Text(2, location))),
                Field(13, Join(Text(1, "offset"), Text(2, "4"))), Field(13, Join(Text(1, "length"), Text(2, "8"))), Integer(14, 1));
            return Field(7, Join(Field(5, external), Field(11, Value("input_ids", 7)), Field(12, Value("sentence_embedding", 1))));
        }
        string path = Path.Combine(root, "model.onnx");
        File.WriteAllBytes(path, Model("weights.data"));
        var graph = OnnxMetadata.Read(path);
        Program.Require(graph.ExternalData.Single() == new OnnxExternalData("weights.data", 4, 8) &&
            graph.Inputs.Single().ElementType == 7 && graph.Outputs.Single().Dimensions.SequenceEqual([-1L, 8]),
            "ONNX metadata lost its real external weights or tensor contract.");
        File.WriteAllBytes(path, Model("../outside.bin"));
        Check.Throws<InvalidDataException>(() => OnnxMetadata.Read(path));
        File.WriteAllBytes(path, Model("weights.data")[..^1]);
        Check.Throws<InvalidDataException>(() => OnnxMetadata.Read(path));
    });
    private static void Bindings() => Program.Temporary(root =>
    {
        string data = Path.Combine(root, "index"), source = Path.Combine(root, "files");
        Directory.CreateDirectory(source);
        var space = Space("https://huggingface.co/Qwen/Qwen3-Embedding-0.6B");
        var selection = new ModelSelection(space.ModelUrl, space.ModelUrl, ModelSelection.DefaultUrl);
        var routes = new Dictionary<string, EmbeddingSpace>
        { ["Document"] = space, ["Code"] = space, ["Image"] = EmbeddingSpace.Default };
        long generation, codeVector;
        using (var catalog = new Catalog(data, 1))
        {
            int folder = catalog.AddFolder(source);
            using (var update = new ModelSettingsUpdate(data, selection))
                catalog.ReconfigureModels(routes, update.Publish, update.Commit, selection);
            void Publish(string name, string kind, float score)
            {
                var job = Program.Enqueue(catalog, folder, Path.Combine(source, name), "fixture", kind);
                float[] vector = new float[space.Dimensions]; vector[0] = score; vector[1] = MathF.Sqrt(1 - score * score);
                catalog.Prepare(job, ["fixture"], null); catalog.SaveBatch(job, catalog.Remaining(job), [vector]);
                Program.Require(catalog.Complete(job), "The filtered fixture did not publish.");
            }
            Publish("document.txt", "Document", 0.95f); Publish("code.cs", "Code", 0.25f);
            float[] query = new float[space.Dimensions]; query[0] = 1;
            Program.Require(catalog.Search(query, 1, 0, space, ["Code"]).Single().Kind == "Code",
                "A higher document score consumed the code binding's limit before kind filtering.");
            generation = Check.Scalar(data, "SELECT value FROM settings WHERE key='generation'");
            codeVector = Check.Scalar(data, "SELECT v.id FROM vectors v JOIN files f ON f.id=v.file_id WHERE f.kind='Code'");
            selection = selection with { CodeOptions = new("document-search-v1") };
            using var updateQuery = new ModelSettingsUpdate(data, selection);
            Program.Require(catalog.ReconfigureModels(routes, updateQuery.Publish, updateQuery.Commit, selection).Length == 0 &&
                catalog.Selection == selection && ModelSelection.Load(data) == selection &&
                Check.Scalar(data, "SELECT value FROM settings WHERE key='generation'") == generation &&
                Check.Scalar(data, "SELECT v.id FROM vectors v JOIN files f ON f.id=v.file_id WHERE f.kind='Code'") == codeVector,
                "Query-only settings rebuilt embeddings or failed to persist.");
            Program.Require(catalog.ModelReindex(false) is { Completed: false }, "A query-only update discarded an unfinished reindex tracker.");
            Program.Require(XDocument.Load(Path.Combine(data, "settings.xml")).Root!.Attribute("version")!.Value == "2",
                "Extended bindings were written as legacy XML.");
            Program.Require(new EngineLog(data).State.Enabled, "The coordinator could not read logging settings after the configuration upgrade.");
        }
        using var reopened = new Catalog(data, 1);
        Program.Require(reopened.Selection == selection && reopened.Counts() == (2, 2) &&
            Check.Scalar(data, "SELECT value FROM settings WHERE key='schema_version'") == 6 &&
            Check.Scalar(data, "SELECT value FROM settings WHERE key='generation'") == generation,
            "Version-six bindings lost their retained vectors after restart.");
    });
}
