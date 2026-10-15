using System.IO;
using System.Text.Json;
using FuzzyFindAnything.Core;
using FuzzyFindAnything.Inference;

namespace FuzzyFindAnything.NativeTests;

internal static class Parity
{
    public static int Run(string modelDirectory, string evidence, string output)
    {
        Directory.CreateDirectory(output);
        using var model = new NativeModel(modelDirectory, 2, (done, stage) => Console.WriteLine($"MODEL {done}/6 {stage}"));
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(evidence, "reference", "manifest.json")));
        var results = new List<object>();
        var references = new List<float[]>();
        var native = new List<float[]>();
        var textCases = new List<(string Text, float[] Vector)>();
        var imageCases = new List<(string Path, float[] Vector)>();
        foreach (var item in manifest.RootElement.GetProperty("cases").EnumerateArray())
        {
            string id = item.GetProperty("id").GetString()!;
            bool image = item.GetProperty("kind").GetString() == "image";
            float[] reference = ReadFloat(evidence, item.GetProperty("outputs").GetProperty("embedding"));
            float[] actual;
            if (image)
            {
                string path = Path.Combine(evidence, item.GetProperty("file").GetString()!);
                var prepared = ImagePipeline.Prepare(path);
                Program.Require(prepared.InputIds.SequenceEqual(ReadLong(evidence, item.GetProperty("inputs").GetProperty("input_ids"))),
                    "Image tokens differ: " + id);
                Program.Require(prepared.Positions.SequenceEqual(ReadLong(evidence, item.GetProperty("inputs").GetProperty("image_position_ids"))),
                    "Image positions differ: " + id);
                actual = model.EncodeImages([path])[0];
                imageCases.Add((path, actual));
            }
            else
            {
                string formatted = item.GetProperty("formatted").GetString()!;
                Program.Require(model.TokenIds(formatted).SequenceEqual(ReadLong(evidence, item.GetProperty("inputs").GetProperty("input_ids"))),
                    "Text tokens differ: " + id);
                actual = model.EncodeTexts([formatted])[0];
                textCases.Add((formatted, actual));
            }
            double cosine = Cosine(reference, actual), maximum = reference.Zip(actual, (a, b) => Math.Abs((double)a - b)).Max();
            Program.Require(cosine >= (image ? 0.999 : 0.99999) && (image || maximum <= 0.0002), "Embedding parity failed: " + id);
            references.Add(reference); native.Add(actual);
            results.Add(new { id, cosine, maximum });
            Console.WriteLine($"PARITY {id}: cosine={cosine:F9}, max={maximum:E3}");
        }
        using var stress = JsonDocument.Parse(File.ReadAllText(Path.Combine(evidence, "reference", "tokenizer-stress.json")));
        foreach (var item in stress.RootElement.EnumerateArray())
            Program.Require(model.TokenIds(item.GetProperty("text").GetString()!).SequenceEqual(
                item.GetProperty("ids").EnumerateArray().Select(value => value.GetInt64())), "Tokenizer stress failed.");
        foreach (int size in new[] { 1, 2, 4, 6, 8 })
        {
            var group = textCases.Take(size).ToArray();
            var batch = model.EncodeTexts(group.Select(item => item.Text).ToArray());
            for (int i = 0; i < size; i++)
                Program.Require(Cosine(group[i].Vector, batch[i]) >= 0.99999, $"Native text batch {size} differs.");
        }
        var images = model.EncodeImages(imageCases.Take(2).Select(item => item.Path).ToArray());
        for (int i = 0; i < 2; i++) Program.Require(Cosine(imageCases[i].Vector, images[i]) >= 0.99999, "Two-image scheduling differs.");
        double drift = 0;
        for (int i = 0; i < native.Count; i++)
            for (int j = 0; j < native.Count; j++)
                drift = Math.Max(drift, Math.Abs(ModelContract.Similarity(references[i], references[j]) - ModelContract.Similarity(native[i], native[j])));
        Program.Require(drift <= 0.005, "Retrieval similarity drift exceeds the parity gate.");
        File.WriteAllText(Path.Combine(output, "parity.json"), JsonSerializer.Serialize(new { cases = results, pairwise_drift = drift, failures = 0 },
            new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("NATIVE TOKENIZATION, TEXT, IMAGE AND BATCH PARITY PASSED");
        return 0;
    }
    private static double Cosine(float[] first, float[] second)
    {
        double dot = 0, a = 0, b = 0;
        for (int i = 0; i < first.Length; i++) { dot += (double)first[i] * second[i]; a += (double)first[i] * first[i]; b += (double)second[i] * second[i]; }
        return dot / Math.Sqrt(a * b);
    }
    private static byte[] Read(string root, JsonElement spec) => File.ReadAllBytes(Path.Combine(root, spec.GetProperty("path").GetString()!));
    private static float[] ReadFloat(string root, JsonElement spec)
    {
        byte[] bytes = Read(root, spec); float[] result = new float[bytes.Length / 4]; Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length); return result;
    }
    private static long[] ReadLong(string root, JsonElement spec)
    {
        byte[] bytes = Read(root, spec); long[] result = new long[bytes.Length / 8]; Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length); return result;
    }
}
