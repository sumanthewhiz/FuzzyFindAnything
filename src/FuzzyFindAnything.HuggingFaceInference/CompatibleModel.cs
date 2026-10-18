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
            text = new InferenceSession(Path.Combine(directory, space.Adapter == "clip" ? @"onnx\text_model.onnx" : @"onnx\model.onnx"), options);
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
        int budget = space.MaximumTokens - 2, start = 0;
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
                if (length == 0 || tokenizer.Encode(content.Substring(start, length)).Length > budget) high = middle - 1;
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
    public float[] Query(string query) => Texts([query])[0];
    public int[] TokenIds(string value) => new[] { tokenizer.Beginning }.Concat(tokenizer.Encode(value).Take(space.MaximumTokens - 2)).Append(tokenizer.End).ToArray();
    public float[][] Texts(IReadOnlyList<string> inputs)
    {
        if (inputs.Count is < 1 or > 8) throw new ArgumentException("A text batch must contain 1-8 inputs.");
        if (text.InputMetadata["input_ids"].Dimensions[0] == 1 && inputs.Count > 1)
            return inputs.Select(value => Texts([value])[0]).ToArray();
        int sequence = space.MaximumTokens;
        long[] ids = Enumerable.Repeat((long)tokenizer.Padding, inputs.Count * sequence).ToArray();
        long[] masks = new long[ids.Length], segments = new long[ids.Length];
        for (int row = 0; row < inputs.Count; row++)
        {
            int[] tokens = tokenizer.Encode(inputs[row]);
            int count = Math.Min(tokens.Length, sequence - 2);
            ids[row * sequence] = tokenizer.Beginning;
            for (int index = 0; index < count; index++) ids[row * sequence + index + 1] = tokens[index];
            ids[row * sequence + count + 1] = tokenizer.End;
            Array.Fill(masks, 1L, row * sequence, count + 2);
        }
        var values = new Dictionary<string, OrtValue>();
        try
        {
            foreach (string name in text.InputNames)
            {
                long[] data = name switch
                {
                    "input_ids" => ids,
                    "attention_mask" => masks,
                    "token_type_ids" => segments,
                    _ => throw new NotSupportedException("Unsupported text graph input: " + name)
                };
                values[name] = text.InputMetadata[name].ElementType == typeof(long)
                    ? OrtValue.CreateTensorValueFromMemory(data, [inputs.Count, sequence])
                    : text.InputMetadata[name].ElementType == typeof(int)
                        ? OrtValue.CreateTensorValueFromMemory(data.Select(value => checked((int)value)).ToArray(), [inputs.Count, sequence])
                        : throw new NotSupportedException("Unsupported text token tensor type.");
            }
            string output = space.Adapter == "clip" ? "text_embeds" : "last_hidden_state";
            if (!text.OutputNames.Contains(output)) throw new NotSupportedException("The ONNX graph has no expected embedding output: " + output);
            using var options = new RunOptions();
            using var encoded = text.Run(options, values, [output]);
            float[] tensor = encoded[0].GetTensorDataAsSpan<float>().ToArray();
            var result = new float[inputs.Count][];
            int expected = inputs.Count * space.Dimensions * (space.Adapter == "clip" ? 1 : sequence);
            if (tensor.Length != expected) throw new InvalidDataException("The native embedding output has incompatible dimensions.");
            for (int row = 0; row < inputs.Count; row++)
            {
                if (space.Adapter == "clip") result[row] = space.Normalize(tensor.AsSpan(row * space.Dimensions, space.Dimensions));
                else
                {
                    float[] pooled = new float[space.Dimensions];
                    int length = (int)masks.AsSpan(row * sequence, sequence).ToArray().Sum();
                    for (int column = 0; column < pooled.Length; column++)
                    {
                        double sum = 0;
                        for (int token = 0; token < length; token++) sum += tensor[(row * sequence + token) * pooled.Length + column];
                        pooled[column] = (float)(sum / length);
                    }
                    result[row] = space.Normalize(pooled);
                }
            }
            return result;
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
            double scale = edge / (double)Math.Min(original.Width, original.Height);
            int resizedWidth = checked((int)(original.Width * scale)), resizedHeight = checked((int)(original.Height * scale));
            if ((long)resizedWidth * resizedHeight > 16_000_000)
                throw new ContentException("The image aspect ratio exceeds the CLIP preprocessing memory limit.", "resource_limit");
            var resized = ImagePipeline.Resize(original, resizedWidth, resizedHeight);
            int left = (resized.Width - width) / 2, top = (resized.Height - height) / 2;
            if (left < 0 || top < 0) throw new NotSupportedException("This image processor requires unsupported padding.");
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
