using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FuzzyFindAnything.Core;
using FuzzyFindAnything.Inference;
using Microsoft.ML.OnnxRuntime;

namespace FuzzyFindAnything.HuggingFaceInference;

internal sealed class CompatibleModel : IDisposable
{
    private readonly EmbeddingSpace space;
    private readonly ModelTokenizer tokenizer;
    private readonly InferenceSession text;
    private readonly InferenceSession? vision;
    private readonly JsonDocument? processor;
    private readonly List<FileStream> assets = [];
    private readonly HashSet<InferenceSession> endedProfiles = [];
    public ExecutionDevice Device { get; }
    private CompatibleModel(string directory, EmbeddingSpace space, int threads, ExecutionDevice device)
    {
        this.space = space; Device = device;
        tokenizer = new(directory, space);
        try
        {
            foreach (var file in HuggingFaceModels.Load(directory).Files)
                assets.Add(new FileStream(Path.Combine(directory, CachedModel.LocalName(file.Name)), FileMode.Open, FileAccess.Read, FileShare.Read));
            using var options = DeviceExecution.Options(device, threads);
            text = new InferenceSession(Path.Combine(directory, space.Recipe is { } recipe ? CachedModel.LocalName(recipe.Graph) :
                space.Adapter == "clip" ? @"onnx\text_model.onnx" : @"onnx\model.onnx"), options);
            ValidateTextGraph();
            if (space.SupportsImages)
            {
                if (device.NativeDevice is not null)
                    options.ProfileOutputPathPrefix = Path.Combine(Path.GetTempPath(), "ffa-device-" + Guid.NewGuid().ToString("N"));
                vision = new InferenceSession(Path.Combine(directory, @"onnx\vision_model.onnx"), options);
                processor = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "preprocessor_config.json")));
            }
        }
        catch
        {
            CleanProfile(text); CleanProfile(vision);
            text?.Dispose(); vision?.Dispose(); processor?.Dispose();
            foreach (var asset in assets) asset.Dispose();
            throw;
        }
    }
    public static CompatibleModel Create(string directory, EmbeddingSpace space, int threads, Action<string> progress, string? requiredDevice = null)
    {
        var failures = new List<string>();
        var devices = DeviceExecution.Candidates();
        if (requiredDevice is not null)
        {
            string[] selector = requiredDevice.Split(':', 2);
            devices = devices.Where(device => device.Provider == selector[0] && (selector.Length == 1 || device.Type == selector[1])).ToArray();
            if (devices.Count == 0) throw new NotSupportedException($"Required execution device '{requiredDevice}' is not available on this PC. " +
                string.Join(" ", DeviceExecution.Diagnostics));
        }
        foreach (var device in devices)
        {
            CompatibleModel? candidate = null;
            try
            {
                progress($"Validating {device.Type}: {device.Provider}");
                candidate = new(directory, space, threads, device);
                var values = candidate.Texts(["red circles and blue squares", "quarterly financial report"]);
                if (values.Length != 2 || ModelContract.Similarity(values[0], values[1]) > 0.9999)
                    throw new InvalidDataException("The model did not produce distinguishable text embeddings.");
                if (space.Recipe is { } recipe)
                {
                    progress($"Checking qualified {space.MaximumTokens}-token workload on {device.Type}");
                    string probeText = string.Concat(Enumerable.Repeat(" test", Math.Max(1, space.MaximumTokens - 32)));
                    int count = candidate.RawTokenIds(recipe.DocumentPrefix + probeText).Length;
                    while (count < space.MaximumTokens)
                    {
                        string next = probeText + " test";
                        int nextCount = candidate.RawTokenIds(recipe.DocumentPrefix + next).Length;
                        if (nextCount > space.MaximumTokens || nextCount <= count) break;
                        probeText = next; count = nextCount;
                    }
                    _ = candidate.Texts([probeText]);
                }
                if (space.SupportsImages)
                {
                    string probe = Path.Combine(Path.GetTempPath(), "ffa-model-probe-" + Guid.NewGuid().ToString("N") + ".png");
                    try
                    {
                        var image = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Rgb24, null,
                            new byte[] { 255, 0, 0, 255, 0, 0, 255, 0, 0, 255, 0, 0 }, 6);
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                        using (var stream = File.Create(probe)) encoder.Save(stream);
                        _ = candidate.Images([probe, probe]);
                    }
                    finally { File.Delete(probe); }
                }
                candidate.VerifyDeviceExecution();
                return candidate;
            }
            catch (Exception error) when (error is OnnxRuntimeException or InvalidDataException or NotSupportedException or ArgumentException)
            {
                candidate?.Dispose();
                string reason = $"{device.Provider} ({device.Type}): {error.Message}";
                failures.Add(reason); Console.Error.WriteLine(reason);
            }
        }
        throw new NotSupportedException("No available CPU/GPU/NPU execution device passed this model's local embedding probe. " +
            string.Join(" | ", failures.Concat(DeviceExecution.Diagnostics)));
    }

    private void VerifyDeviceExecution()
    {
        if (Device.NativeDevice is null) return;
        foreach (var session in new[] { text, vision }.Where(value => value is not null))
        {
            string profile = session!.EndProfiling();
            endedProfiles.Add(session);
            try
            {
                using var trace = JsonDocument.Parse(File.ReadAllText(profile));
                bool used = trace.RootElement.EnumerateArray().Any(item =>
                    item.TryGetProperty("args", out var args) && args.TryGetProperty("provider", out var provider) &&
                    provider.GetString() == Device.Provider);
                if (!used) throw new NotSupportedException($"The graph fell back entirely to CPU; {Device.Provider} did not execute its embedding kernels.");
            }

            finally { File.Delete(profile); }
        }
    }

    private void CleanProfile(InferenceSession? session)
    {
        if (session is null || Device.NativeDevice is null || !endedProfiles.Add(session)) return;
        try
        {
            string profile = session.EndProfiling();
            if (profile.Length > 0) File.Delete(profile);
        }
        catch (Exception error) when (error is IOException or OnnxRuntimeException)
        { Console.Error.WriteLine("Could not clean an execution-provider probe trace: " + error.Message); }
    }

    public string[] Split(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) throw new ContentException("No searchable text was extracted.", "no_text");
        int budget = space.Recipe?.ChunkTokens ?? space.MaximumTokens - 2, start = 0;
        var result = new List<string>();
        var seen = new HashSet<string>();
        while (start < content.Length)
        {
            int low = 1, high = Math.Min(content.Length - start, budget * 16), fit = 0;
            while (low <= high)
            {
                int middle = low + (high - low) / 2;
                int length = middle;
                if (start + length < content.Length && char.IsHighSurrogate(content[start + length - 1])) length--;
                int count = length == 0 ? int.MaxValue : space.Recipe is { } recipe
                    ? RawTokenIds(recipe.DocumentPrefix + content.Substring(start, length)).Length
                    : tokenizer.Encode(content.Substring(start, length)).Length;
                if (count > budget) high = middle - 1;
                else { fit = length; low = middle + 1; }
            }
            if (fit == 0) throw new ContentException("A text fragment exceeds the tokenizer's native input bound.", "resource_limit");
            string passage = content.Substring(start, fit);
            string key = FilePaths.CollapseWhitespace(passage);
            if (key.Length > 0 && seen.Add(key)) result.Add(passage);
            if (start + fit >= content.Length) break;
            int advance = Math.Max(1, fit - Math.Min(fit / 8, 128));
            if (char.IsHighSurrogate(content[start + advance - 1])) advance++;
            start += advance;
        }
        return result.ToArray();
    }
    public float[] Query(string query, string? profile = null)
    {
        if (space.Recipe is null)
        {
            if (profile is not (null or "" or "default")) throw new NotSupportedException("This legacy model has no configurable query profiles.");
            return Texts([query], false)[0];
        }
        return Texts([space.Recipe.Query(profile ?? space.Recipe.TextQuery).Prefix + query], false)[0];
    }
    private int[] RawTokenIds(string value)
    {
        IEnumerable<int> tokens = tokenizer.Encode(value);
        if (tokenizer.Beginning >= 0) tokens = tokens.Prepend(tokenizer.Beginning);
        if (tokenizer.End >= 0) tokens = tokens.Append(tokenizer.End);
        return tokens.ToArray();
    }
    public int[] TokenIds(string value) => space.Recipe is null
        ? new[] { tokenizer.Beginning }.Concat(tokenizer.Encode(value).Take(space.MaximumTokens - 2)).Append(tokenizer.End).ToArray()
        : RawTokenIds(value);

    private void ValidateTextGraph()
    {
        var recipe = space.Recipe;
        string ids = recipe?.InputIds ?? "input_ids";
        string output = recipe?.Output ?? (space.Adapter == "clip" ? "text_embeds" : "last_hidden_state");
        var allowed = new[] { ids, recipe?.AttentionMask ?? "attention_mask", recipe?.TokenTypeIds ?? "token_type_ids",
            recipe?.PositionIds ?? "position_ids" };
        if (!text.InputMetadata.ContainsKey(ids) || text.InputNames.Any(name => !allowed.Contains(name)))
            throw new NotSupportedException("The text graph input interface needs a different reviewed adapter.");
        foreach (var input in text.InputMetadata.Values)
            if (input.Dimensions.Length != 2 || input.ElementType != typeof(long) && input.ElementType != typeof(int) ||
                input.Dimensions[0] > 1 || input.Dimensions[1] > space.MaximumTokens)
                throw new NotSupportedException("Unsupported embedding input type or fixed batch/sequence shape.");
        if (!text.OutputMetadata.TryGetValue(output, out var tensor) ||
            tensor.Dimensions.Length != (recipe?.Pooling == "pooled" || space.Adapter == "clip" ? 2 : 3) ||
            tensor.Dimensions[^1] > 0 && tensor.Dimensions[^1] != space.Dimensions ||
            tensor.ElementType != typeof(float) && (recipe is null || tensor.ElementType != typeof(Float16)))
            throw new NotSupportedException("The graph's embedding output does not match the native recipe.");
    }

    public float[][] Texts(IReadOnlyList<string> inputs, bool documents = true)
    {
        if (inputs.Count is < 1 or > 8) throw new ArgumentException("A text batch must contain 1-8 inputs.");
        var recipe = space.Recipe;
        string idsName = recipe?.InputIds ?? "input_ids";
        if (text.InputMetadata[idsName].Dimensions[0] == 1 && inputs.Count > 1)
            return inputs.Select(value => Texts([value], documents)[0]).ToArray();
        int[][] rows = inputs.Select(value => TokenIds((documents ? recipe?.DocumentPrefix ?? "" : "") + value)).ToArray();
        if (rows.Any(tokens => tokens.Length == 0 || tokens.Length > space.MaximumTokens))
            throw new ContentException("The embedding input exceeds the qualified token limit. Shorten the query or re-prepare the document passages.", "resource_limit");
        int sequence = recipe is null ? space.MaximumTokens : text.InputMetadata[idsName].Dimensions[1] > 0
            ? text.InputMetadata[idsName].Dimensions[1] : rows.Max(value => value.Length);
        if (rows.Any(tokens => tokens.Length > sequence)) throw new NotSupportedException("The graph's fixed sequence length is too short.");
        if (recipe is not null && inputs.Count > 1 && inputs.Count * sequence > recipe.MaximumBatchTokens)
            return inputs.Select(value => Texts([value], documents)[0]).ToArray();
        long[] ids = Enumerable.Repeat((long)tokenizer.Padding, inputs.Count * sequence).ToArray();
        long[] masks = new long[ids.Length], segments = new long[ids.Length], positions = new long[ids.Length];
        for (int row = 0; row < inputs.Count; row++)
        {
            int offset = row * sequence + (recipe?.Padding == "left" ? sequence - rows[row].Length : 0);
            for (int index = 0; index < rows[row].Length; index++)
            { ids[offset + index] = rows[row][index]; positions[offset + index] = index; }
            Array.Fill(masks, 1L, offset, rows[row].Length);
        }
        var values = new Dictionary<string, OrtValue>();
        try
        {
            foreach (string name in text.InputNames)
            {
                long[] data = name == idsName ? ids : name == (recipe?.AttentionMask ?? "attention_mask") ? masks :
                    name == (recipe?.TokenTypeIds ?? "token_type_ids") ? segments :
                    name == (recipe?.PositionIds ?? "position_ids") ? positions :
                    throw new NotSupportedException("Unsupported text graph input: " + name);
                values[name] = text.InputMetadata[name].ElementType == typeof(long)
                    ? OrtValue.CreateTensorValueFromMemory(data, [inputs.Count, sequence])
                    : text.InputMetadata[name].ElementType == typeof(int)
                        ? OrtValue.CreateTensorValueFromMemory(data.Select(value => checked((int)value)).ToArray(), [inputs.Count, sequence])
                        : throw new NotSupportedException("Unsupported text token tensor type.");
            }
            string output = recipe?.Output ?? (space.Adapter == "clip" ? "text_embeds" : "last_hidden_state");
            if (!text.OutputNames.Contains(output)) throw new NotSupportedException("The ONNX graph has no expected embedding output: " + output);
            using var options = new RunOptions();
            using var encoded = text.Run(options, values, [output]);
            float[] tensor = text.OutputMetadata[output].ElementType == typeof(float) ? encoded[0].GetTensorDataAsSpan<float>().ToArray()
                : encoded[0].GetTensorDataAsSpan<Float16>().ToArray().Select(value => (float)value).ToArray();
            return EmbeddingPooling.Normalize(tensor, masks, inputs.Count, sequence, space.Dimensions,
                recipe?.Pooling ?? (space.Adapter == "clip" ? "pooled" : "mean"));
        }
        finally { foreach (var value in values.Values) value.Dispose(); }
    }
    public float[][] Images(IReadOnlyList<string> paths)
    {
        if (vision is null || processor is null) throw new NotSupportedException("This model cannot embed images.");
        if (paths.Count is < 1 or > 2) throw new ArgumentException("An image batch must contain 1-2 inputs.");
        var config = processor.RootElement;
        int edge = config.GetProperty("size").GetProperty("shortest_edge").GetInt32();
        int width = config.GetProperty("crop_size").GetProperty("width").GetInt32();
        int height = config.GetProperty("crop_size").GetProperty("height").GetInt32();
        if (edge is < 16 or > 1024 || width is < 16 or > 1024 || height is < 16 or > 1024)
            throw new InvalidDataException("The model's image dimensions exceed supported limits.");
        float[] mean = config.GetProperty("image_mean").Deserialize<float[]>()!, standard = config.GetProperty("image_std").Deserialize<float[]>()!;
        if (mean.Length != 3 || standard.Length != 3 || standard.Any(value => value <= 0 || !float.IsFinite(value)))
            throw new InvalidDataException("Invalid RGB normalization metadata.");
        var results = new List<float[]>();
        foreach (string path in paths)
        {
            var original = ImagePipeline.Decode(path);
            long resizedWidth = original.Width <= original.Height ? edge : (long)original.Width * edge / original.Height;
            long resizedHeight = original.Height <= original.Width ? edge : (long)original.Height * edge / original.Width;
            if (resizedWidth * resizedHeight > 16_000_000)
                throw new ContentException("The image aspect ratio exceeds the CLIP preprocessing memory limit.", "resource_limit");
            var resized = ImagePipeline.Resize(original, checked((int)resizedWidth), checked((int)resizedHeight));
            if (resized.Width < width || resized.Height < height)
                throw new NotSupportedException("This image processor requires unsupported padding.");
            int left = (resized.Width - width) / 2, top = (resized.Height - height) / 2;
            float[] pixels = new float[3 * width * height];
            for (int channel = 0; channel < 3; channel++)
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                        pixels[channel * width * height + y * width + x] =
                            (resized.Pixels[((y + top) * resized.Width + x + left) * 3 + channel] / 255f - mean[channel]) / standard[channel];
            using var input = OrtValue.CreateTensorValueFromMemory(pixels, [1, 3, height, width]);
            using var run = new RunOptions();
            using var output = vision.Run(run, new Dictionary<string, OrtValue> { ["pixel_values"] = input }, ["image_embeds"]);
            results.Add(space.Normalize(output[0].GetTensorDataAsSpan<float>()));
        }
        return results.ToArray();
    }
    public void Dispose()
    {
        CleanProfile(text); CleanProfile(vision);
        text.Dispose(); vision?.Dispose(); processor?.Dispose();
        foreach (var asset in assets) asset.Dispose();
    }
}
