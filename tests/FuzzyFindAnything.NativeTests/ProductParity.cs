using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using FuzzyFindAnything.Core;
using FuzzyFindAnything.Inference;

namespace FuzzyFindAnything.NativeTests;

internal static class ProductParity
{
    public static int Run(string models, string fixtures, string output)
    {
        Directory.CreateDirectory(output);
        var elapsed = Stopwatch.StartNew();
        using var model = new NativeModel(models, 3, (step, stage) => Console.WriteLine($"MODEL {step}/6 {stage}"));
        double startup = elapsed.Elapsed.TotalSeconds;
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixtures, "goldens.json")));
        var root = document.RootElement;
        Program.Require(root.GetProperty("revision").GetString() == ModelContract.Revision, "Reference model revision differs.");
        int tokenCases = 0, chunkCases = 0;
        foreach (var item in root.GetProperty("tokenizer_stress").EnumerateArray())
        {
            Program.Require(model.TokenIds(item.GetProperty("text").GetString()!).SequenceEqual(Longs(item, "ids")), "Tokenizer stress differs.");
            tokenCases++;
        }
        foreach (var item in root.GetProperty("chunking").EnumerateArray())
        {
            string name = item.GetProperty("id").GetString()!, text = item.GetProperty("text").GetString()!;
            Program.Require(model.TokenIds(text, false).SequenceEqual(Longs(item, "ids")), "Chunk tokenization differs: " + name);
            string[] actual;
            try { actual = model.Split(text); }
            catch (ContentException error) when (error.Code == "no_text") { actual = []; }
            string[] expected = item.GetProperty("passages").EnumerateArray().Select(value => value.GetString()!).ToArray();
            if (!actual.SequenceEqual(expected))
            {
                File.WriteAllText(Path.Combine(output, "chunk-mismatch.json"), JsonSerializer.Serialize(new { name, expected, actual }));
                throw new InvalidDataException($"Chunk decoding/boundaries differ: {name}; expected {expected.Length}, actual {actual.Length}. See chunk-mismatch.json.");
            }
            chunkCases++;
        }
        Console.WriteLine($"PASS {tokenCases} stress inputs and {chunkCases} exact chunk/decode fixtures");
        var cases = root.GetProperty("cases").EnumerateArray().ToArray();
        var vectors = new Dictionary<string, float[]>();
        var references = new Dictionary<string, float[]>();
        var results = new List<object>();
        foreach (var item in cases)
        {
            string id = item.GetProperty("id").GetString()!;
            bool image = item.GetProperty("kind").GetString() == "image";
            float[] reference = item.GetProperty("embedding").Deserialize<float[]>()!, actual;
            bool? pixelsMatch = null;
            elapsed.Restart();
            if (image)
            {
                string path = Path.Combine(fixtures, item.GetProperty("file").GetString()!);
                var prepared = ImagePipeline.Prepare(path);
                if (item.TryGetProperty("ids", out _))
                    Program.Require(prepared.InputIds.SequenceEqual(Longs(item, "ids")), "Image tokens differ: " + id);
                if (item.TryGetProperty("canonical_size", out var size))
                {
                    Program.Require(prepared.Canonical.Width == size[0].GetInt32() && prepared.Canonical.Height == size[1].GetInt32(),
                        "Canonical image dimensions differ: " + id);
                    pixelsMatch = Convert.ToHexString(SHA256.HashData(prepared.Canonical.Pixels)).Equals(
                        item.GetProperty("canonical_sha256").GetString(), StringComparison.OrdinalIgnoreCase);
                    if (Path.GetExtension(path) != ".jpg" && id != "extra-odd-large")
                        Program.Require(pixelsMatch.Value, "Lossless RGB decoding differs: " + id);
                }
                actual = model.EncodeImages([path])[0];
            }
            else
            {
                string text = item.GetProperty("text").GetString()!;
                Program.Require(model.TokenIds(text).SequenceEqual(Longs(item, "ids")), "Text tokens differ: " + id);
                actual = model.EncodeTexts([text])[0];
            }
            double normError = Math.Abs(Math.Sqrt(actual.Sum(value => (double)value * value)) - 1);
            double cosine = Cosine(reference, actual), maximum = Maximum(reference, actual);
            Program.Require(normError <= 0.00001 && cosine >= (image ? 0.999 : 0.99999) &&
                (image || maximum <= 0.0002), $"Embedding gate failed: {id}; cosine={cosine}, max={maximum}.");
            vectors.Add(id, actual); references.Add(id, reference);
            results.Add(new { id, cosine, maximum, norm_error = normError, canonical_pixels_match = pixelsMatch, seconds = elapsed.Elapsed.TotalSeconds });
            Console.WriteLine($"PARITY {id}: cosine={cosine:F9}, max={maximum:E3}, {elapsed.Elapsed.TotalSeconds:F2}s");
        }
        string[] mixed = ["extra-long-code", "extra-short", "extra-long-unicode", "extra-query-maximum",
            "extra-overlap", "unicode-cjk", "extra-sql", "extra-long-document"];
        foreach (int size in new[] { 1, 2, 4, 6, 8 })
        {
            string[] selected = mixed.Take(size).ToArray();
            var batch = model.EncodeTexts(selected.Select(id => cases.Single(item => item.GetProperty("id").GetString() == id)
                .GetProperty("text").GetString()!).ToArray());
            for (int index = 0; index < selected.Length; index++)
                Program.Require(Cosine(references[selected[index]], batch[index]) >= 0.99999 &&
                    Maximum(references[selected[index]], batch[index]) <= 0.0002, $"Mixed-length batch {size} changed input {selected[index]}.");
            Console.WriteLine($"PASS mixed-length native batch {size}");
        }
        var imageCases = cases.Where(item => item.GetProperty("kind").GetString() == "image").ToArray();
        for (int start = 0; start + 1 < imageCases.Length; start += 2)
        {
            var pair = imageCases.Skip(start).Take(2).ToArray();
            var batch = model.EncodeImages(pair.Select(item => Path.Combine(fixtures, item.GetProperty("file").GetString()!)).ToArray());
            for (int index = 0; index < 2; index++)
                Program.Require(Cosine(vectors[pair[index].GetProperty("id").GetString()!], batch[index]) >= 0.99999,
                    "Two-image scheduling changed identity or numerical output.");
        }
        double drift = 0;
        foreach (string first in vectors.Keys)
            foreach (string second in vectors.Keys)
                drift = Math.Max(drift, Math.Abs(ModelContract.Similarity(references[first], references[second]) -
                    ModelContract.Similarity(vectors[first], vectors[second])));
        Program.Require(drift <= 0.005, "Pairwise similarity drift exceeds 0.005.");
        var candidates = cases.Where(item => item.GetProperty("kind").GetString() != "query").Select(item => item.GetProperty("id").GetString()!).ToArray();
        var rankings = new List<object>();
        foreach (var query in cases.Where(item => item.GetProperty("kind").GetString() == "query"))
        {
            string id = query.GetProperty("id").GetString()!;
            var reference = candidates.OrderByDescending(candidate => ModelContract.Similarity(references[id], references[candidate])).ToArray();
            var actual = candidates.OrderByDescending(candidate => ModelContract.Similarity(vectors[id], vectors[candidate])).ToArray();
            double gap = ModelContract.Similarity(references[id], references[reference[0]]) - ModelContract.Similarity(references[id], references[reference[1]]);
            Program.Require(gap <= 0.01 || reference[0] == actual[0], "A clearly separated retrieval winner changed: " + id);
            rankings.Add(new { query = id, reference = reference[0], actual = actual[0], gap, near_tie = gap <= 0.01 });
        }
        File.WriteAllText(Path.Combine(output, "product-parity.json"), JsonSerializer.Serialize(new
        {
            failures = 0,
            model_revision = ModelContract.Revision,
            architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            startup_seconds = startup,
            token_cases = tokenCases,
            chunk_cases = chunkCases,
            cases = results,
            text_batches = new[] { 1, 2, 4, 6, 8 },
            image_pairs = imageCases.Length / 2,
            pairwise_drift = drift,
            rankings
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PRODUCT TOKENIZATION, CHUNKING, CODECS, EMBEDDINGS, BATCHING AND RANKING GATES PASSED");
        return 0;
    }
    private static long[] Longs(JsonElement value, string key) => value.GetProperty(key).EnumerateArray().Select(item => item.GetInt64()).ToArray();
    private static double Cosine(float[] first, float[] second) => first.Zip(second, (a, b) => (double)a * b).Sum() /
        Math.Sqrt(first.Sum(value => (double)value * value) * second.Sum(value => (double)value * value));
    private static double Maximum(float[] first, float[] second) => first.Zip(second, (a, b) => Math.Abs((double)a - b)).Max();
}
