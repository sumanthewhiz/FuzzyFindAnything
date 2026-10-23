using System.IO;
using System.Globalization;
using System.Text;
using System.Text.Json;
using FuzzyFindAnything.Core;
using FuzzyFindAnything.Chunking;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.Tokenizers;

namespace FuzzyFindAnything.Inference;

public sealed class NativeModel : IDisposable
{
    private readonly SentencePieceTokenizer tokenizer;
    private readonly HashSet<int> specialIds;
    private readonly Dictionary<int, string> decoderTokens;
    private readonly InferenceSession language;
    private readonly InferenceSession vision;
    private readonly IDisposable assetLease;
    private readonly object gate = new();

    public NativeModel(string directory, int threads, Action<int, string>? progress = null)
    {
        progress?.Invoke(1, "Verifying native model assets");
        assetLease = ModelAssets.LockForInference(directory);
        try
        {
            ModelAssets.Verify(directory);
            progress?.Invoke(2, "Preparing tokenizer and image processor");
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "tokenizer.json")));
            var special = json.RootElement.GetProperty("added_tokens").EnumerateArray()
                .Where(item => item.GetProperty("special").GetBoolean())
                .ToDictionary(item => item.GetProperty("content").GetString()!, item => item.GetProperty("id").GetInt32(), StringComparer.Ordinal);
            specialIds = special.Values.ToHashSet();
            decoderTokens = json.RootElement.GetProperty("model").GetProperty("vocab").EnumerateObject()
                .ToDictionary(item => item.Value.GetInt32(), item => item.Name);
            foreach (var token in json.RootElement.GetProperty("added_tokens").EnumerateArray())
                decoderTokens[token.GetProperty("id").GetInt32()] = token.GetProperty("content").GetString()!;
            using (var stream = FilePaths.OpenRead(Path.Combine(directory, "tokenizer.model")))
                tokenizer = SentencePieceTokenizer.Create(stream, addBeginningOfSentence: true, addEndOfSentence: true, specialTokens: special);
            progress?.Invoke(3, "Initializing native inference runtime");
            OrtEnv.Instance().DisableTelemetryEvents();
            using var options = new SessionOptions
            {
                IntraOpNumThreads = Math.Max(1, threads),
                InterOpNumThreads = 1,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
            };
            // Yield the shared CPU set between requests instead of starving coordinator/extraction work with spin waits.
            options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
            options.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");
            progress?.Invoke(4, "Loading the language graph");
            language = new InferenceSession(Path.Combine(directory, "language.onnx"), options);
            try
            {
                progress?.Invoke(5, "Loading the vision graph");
                vision = new InferenceSession(Path.Combine(directory, "vision.onnx"), options);
                progress?.Invoke(6, "Native text and vision model ready");
            }
            catch { language.Dispose(); throw; }
        }
        catch { assetLease.Dispose(); throw; }
    }

    public long[] TokenIds(string text, bool special = true)
    {
        lock (gate) return tokenizer.EncodeToIds(text, addBeginningOfSentence: special, addEndOfSentence: special)
            .Select(value => (long)value).ToArray();
    }

    public string[] Split(string text)
    {
        lock (gate)
        {
            int[] ids = tokenizer.EncodeToIds(text, addBeginningOfSentence: false, addEndOfSentence: false).ToArray();
            if (ids.Length == 0) throw new ContentException("No searchable text was extracted.", "no_text");
            var chunks = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int start = 0; start < ids.Length; start += ModelContract.PassageStride)
            {
                int length = Math.Min(ModelContract.PassageTokens, ids.Length - start);
                string value = DecodePassage(ids.AsSpan(start, length));
                string key = FilePaths.CollapseWhitespace(value);
                if (key.Length > 0 && seen.Add(key)) chunks.Add(value);
                if (start + ModelContract.PassageTokens >= ids.Length) break;
            }
            return chunks.ToArray();
        }
    }

    public ChunkingResult SplitContent(string text, ChunkingContext context)
    {
        lock (gate)
        {
            string framing = $"title: {FilePaths.ScalarPrefix(context.Title, 160)} | text: ";
            int budget = Math.Min(8192, ModelContract.PassageTokens + TokenIds(framing).Length);
            return ContentChunker.Split(text, context, budget, value => TokenIds(framing + value).Length);
        }
    }

    private string DecodePassage(ReadOnlySpan<int> ids)
    {
        // Reproduce tokenizer.json's Replace/ByteFallback/Fuse decoder. The generic SentencePiece decoder drops token 107 (newline).
        var text = new StringBuilder();
        var bytes = new List<byte>();
        var utf8 = new UTF8Encoding(false, true);
        void Flush()
        {
            if (bytes.Count == 0) return;
            try { text.Append(utf8.GetString(bytes.ToArray())); }
            catch (DecoderFallbackException) { text.Append('\uFFFD', bytes.Count); }
            bytes.Clear();
        }
        foreach (int id in ids)
        {
            if (specialIds.Contains(id)) continue;
            if (!decoderTokens.TryGetValue(id, out string? token)) throw new InvalidDataException($"Unknown token ID {id}.");
            token = token.Replace('\u2581', ' ');
            if (token.Length == 6 && token.StartsWith("<0x", StringComparison.Ordinal) && token[5] == '>' &&
                byte.TryParse(token.AsSpan(3, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out byte value))
                bytes.Add(value);
            else { Flush(); text.Append(token); }
        }
        Flush();
        return text.ToString();
    }

    public float[] Query(string text) => EncodeTexts(["task: search result | query: " + text])[0];
    public float[][] EncodeTexts(IReadOnlyList<string> texts)
    {
        if (texts.Count is < 1 or > ModelContract.TextBatchSize) throw new ArgumentException("Text batch size must be 1-8.");
        lock (gate)
        {
            long[][] ids = texts.Select(text => TokenIds(text)).ToArray();
            return EncodeRows(ids, ids.Select(value => new float[checked(value.Length * 512)]).ToArray());
        }
    }
    public float[][] EncodeImages(IReadOnlyList<string> paths)
    {
        if (paths.Count is < 1 or > ModelContract.ImageBatchSize) throw new ArgumentException("Image batch size must be 1-2.");
        lock (gate)
        {
            var rows = new List<long[]>();
            var soft = new List<float[]>();
            foreach (string path in paths)
            {
                PreparedImage image;
                try { image = ImagePipeline.Prepare(path); }
                catch (Exception error) when (error is NotSupportedException or FileFormatException or ArgumentException or
                    OverflowException or EndOfStreamException or InvalidDataException)
                { throw new ContentException("The image could not be decoded: " + error.Message, "invalid_image"); }
                float[] features = RunVision(image.Pixels, image.Positions);
                float[] aligned = new float[image.InputIds.Length * 512];
                FillImageEmbeddings(image.InputIds, aligned, features);
                rows.Add(image.InputIds); soft.Add(aligned);
            }
            return EncodeRows(rows.ToArray(), soft.ToArray());
        }
    }

    private float[][] EncodeRows(long[][] rows, float[][] imageEmbeddings)
    {
        int batch = rows.Length, sequence = rows.Max(row => row.Length);
        if (sequence > 8192) throw new ContentException("The input exceeds the model context limit.", "resource_limit");
        long[] ids = new long[checked(batch * sequence)], mask = new long[ids.Length];
        float[] soft = new float[checked(ids.Length * 512)];
        for (int row = 0; row < batch; row++)
        {
            rows[row].CopyTo(ids, row * sequence);
            Array.Fill(mask, 1L, row * sequence, rows[row].Length);
            imageEmbeddings[row].CopyTo(soft, row * sequence * 512);
        }
        float[] projected = RunLanguage(ids, mask, soft, batch, sequence);
        float[][] result = new float[batch][];
        for (int row = 0; row < batch; row++)
        {
            float[] pooled = new float[ModelContract.Dimensions];
            for (int column = 0; column < pooled.Length; column++)
            {
                double sum = 0;
                for (int token = 0; token < rows[row].Length; token++)
                    sum += projected[(row * sequence + token) * pooled.Length + column];
                pooled[column] = (float)(sum / rows[row].Length);
            }
            result[row] = ModelContract.Normalize(pooled);
        }
        return result;
    }

    public float[] RunVision(float[] pixels, long[] positions)
    {
        using var pixelValue = OrtValue.CreateTensorValueFromMemory(pixels, [1, 2520, 768]);
        using var positionValue = OrtValue.CreateTensorValueFromMemory(positions, [1, 2520, 2]);
        using var options = new RunOptions();
        using var result = vision.Run(options, new Dictionary<string, OrtValue>
        { ["pixel_values"] = pixelValue, ["image_position_ids"] = positionValue }, vision.OutputNames);
        return result[0].GetTensorDataAsSpan<float>().ToArray();
    }
    public float[] RunLanguage(long[] ids, long[] mask, float[] soft, int batch, int sequence)
    {
        using var idValue = OrtValue.CreateTensorValueFromMemory(ids, [batch, sequence]);
        using var maskValue = OrtValue.CreateTensorValueFromMemory(mask, [batch, sequence]);
        using var softValue = OrtValue.CreateTensorValueFromMemory(soft, [batch, sequence, 512]);
        using var options = new RunOptions();
        using var result = language.Run(options, new Dictionary<string, OrtValue>
        { ["input_ids"] = idValue, ["attention_mask"] = maskValue, ["image_embeds"] = softValue }, language.OutputNames);
        return result[0].GetTensorDataAsSpan<float>().ToArray();
    }
    public static void FillImageEmbeddings(long[] ids, float[] target, float[] features)
    {
        int offset = 0;
        for (int token = 0; token < ids.Length; token++)
            if (ids[token] == 258880)
            {
                if (offset + 512 > features.Length) throw new InvalidDataException("Vision output is shorter than its placeholder sequence.");
                Array.Copy(features, offset, target, token * 512, 512);
                offset += 512;
            }
        if (offset != features.Length) throw new InvalidDataException("Vision features do not match their image placeholders.");
    }
    public void Dispose() { language.Dispose(); vision.Dispose(); assetLease.Dispose(); }
}
