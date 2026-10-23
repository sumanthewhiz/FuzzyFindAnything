using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FuzzyFindAnything.Core;
using FuzzyFindAnything.Inference;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.Tokenizers;

namespace FuzzyFindAnything.HuggingFaceInference;

internal sealed class ImageSequenceModel : IDisposable
{
    private readonly ImageModelDefinition model;
    private readonly BpeTokenizer tokenizer;
    private readonly List<FileStream> leases = [];
    private readonly List<InferenceSession> sessions = [];
    private readonly HashSet<InferenceSession> profilesEnded = [];
    private readonly InferenceSession vision, tokens, encoder, decoder;
    private readonly string[] decoderOutputs;
    public ExecutionDevice Device { get; }

    private ImageSequenceModel(string directory, ImageModelDefinition model, int threads, ExecutionDevice device)
    {
        this.model = model; Device = device;
        var recipe = model.Recipe;
        using var definition = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "tokenizer.json")));
        var root = definition.RootElement;
        var pre = root.GetProperty("pre_tokenizer");
        var post = root.GetProperty("post_processor");
        if (root.GetProperty("model").GetProperty("type").GetString() != "BPE" ||
            root.GetProperty("normalizer").ValueKind != JsonValueKind.Null ||
            pre.GetProperty("type").GetString() != "ByteLevel" || pre.GetProperty("add_prefix_space").GetBoolean() ||
            !pre.GetProperty("use_regex").GetBoolean() || post.GetProperty("type").GetString() != "RobertaProcessing" ||
            post.GetProperty("cls")[1].GetInt32() != recipe.BeginningToken || post.GetProperty("sep")[1].GetInt32() != recipe.EndToken)
            throw new NotSupportedException("The image model's tokenizer needs another reviewed native adapter.");
        var special = root.GetProperty("added_tokens").EnumerateArray().ToDictionary(
            item => item.GetProperty("content").GetString()!, item => item.GetProperty("id").GetInt32());
        tokenizer = BpeTokenizer.Create(new BpeOptions(Path.Combine(directory, "vocab.json"), Path.Combine(directory, "merges.txt"))
        {
            ByteLevel = true,
            SpecialTokens = special,
            PreTokenizer = new RegexPreTokenizer(new Regex(@"'s|'t|'re|'ve|'m|'ll|'d| ?\p{L}+| ?\p{N}+| ?[^\s\p{L}\p{N}]+|\s+(?!\S)|\s+",
                RegexOptions.Compiled, TimeSpan.FromSeconds(5)), special)
        });
        try
        {
            foreach (var file in ImageModelAssets.Load(directory).Files)
                leases.Add(new FileStream(Path.Combine(directory, CachedModel.LocalName(file.Name)), FileMode.Open, FileAccess.Read, FileShare.Read));
            InferenceSession Open(string file)
            {
                using var options = DeviceExecution.Options(device, threads);
                var session = new InferenceSession(Path.Combine(directory, CachedModel.LocalName(file)), options);
                sessions.Add(session); return session;
            }
            vision = Open(recipe.VisionGraph); tokens = Open(recipe.TokenGraph);
            encoder = Open(recipe.EncoderGraph); decoder = Open(recipe.DecoderGraph);
            decoderOutputs = new[] { "logits" }.Concat(Enumerable.Range(0, recipe.Layers).SelectMany(layer =>
                new[] { $"present.{layer}.decoder.key", $"present.{layer}.decoder.value",
                    $"present.{layer}.encoder.key", $"present.{layer}.encoder.value" })).ToArray();
            ValidateGraph(vision, ["pixel_values"], ["image_features"]);
            ValidateGraph(tokens, ["input_ids"], ["inputs_embeds"]);
            ValidateGraph(encoder, ["attention_mask", "inputs_embeds"], ["last_hidden_state"]);
            ValidateGraph(decoder, new[] { "encoder_attention_mask", "encoder_hidden_states", "inputs_embeds", "use_cache_branch" }
                .Concat(decoderOutputs.Skip(1).Select(name => name.Replace("present.", "past_key_values.", StringComparison.Ordinal))).ToArray(), decoderOutputs);
            using var processor = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "preprocessor_config.json")));
            var image = processor.RootElement;
            if (image.GetProperty("size").GetProperty("width").GetInt32() != recipe.Width ||
                image.GetProperty("size").GetProperty("height").GetInt32() != recipe.Height ||
                !image.GetProperty("do_resize").GetBoolean() || image.GetProperty("do_center_crop").GetBoolean() ||
                !image.GetProperty("do_rescale").GetBoolean() || !image.GetProperty("do_normalize").GetBoolean() ||
                Math.Abs(image.GetProperty("rescale_factor").GetDouble() - 1d / 255) > 1e-10 ||
                image.GetProperty("resample").GetInt32() != 3 ||
                !image.GetProperty("image_mean").Deserialize<float[]>()!.SequenceEqual(recipe.Mean) ||
                !image.GetProperty("image_std").Deserialize<float[]>()!.SequenceEqual(recipe.Deviation))
                throw new NotSupportedException("The image processor differs from its reviewed resize/normalization recipe.");
        }
        catch { Dispose(); throw; }
    }
    private static void ValidateGraph(InferenceSession session, string[] inputs, string[] outputs)
    {
        if (!session.InputNames.Order(StringComparer.Ordinal).SequenceEqual(inputs.Order(StringComparer.Ordinal)) ||
            outputs.Any(name => !session.OutputNames.Contains(name)))
            throw new NotSupportedException("An image component graph does not implement the reviewed tensor interface.");
        foreach (var pair in session.InputMetadata)
        {
            Type expected = pair.Key is "input_ids" or "attention_mask" or "encoder_attention_mask" ? typeof(long) :
                pair.Key == "use_cache_branch" ? typeof(bool) : typeof(float);
            if (pair.Value.ElementType != expected) throw new NotSupportedException("Unsupported image component input precision.");
        }
        if (outputs.Any(name => session.OutputMetadata[name].ElementType != typeof(float)))
            throw new NotSupportedException("Unsupported image component output precision.");
    }
    public static ImageSequenceModel Create(string directory, ImageModelDefinition model, int threads,
        Action<string> progress, string? requiredDevice)
    {
        var devices = DeviceExecution.Candidates();
        if (requiredDevice is not null)
        {
            var selector = requiredDevice.Split(':', 2);
            devices = devices.Where(device => device.Provider == selector[0] && (selector.Length == 1 || device.Type == selector[1])).ToArray();
            if (devices.Count == 0) throw new NotSupportedException("The required image execution device is not available: " + requiredDevice);
        }
        var failures = new List<string>();
        foreach (var device in devices)
        {
            ImageSequenceModel? candidate = null;
            string probe = Path.Combine(Path.GetTempPath(), "ffa-image-check-" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                progress($"Checking image generation on {device.Type}: {device.Provider}");
                candidate = new(directory, model, threads, device);
                var image = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Rgb24, null,
                    new byte[] { 255, 0, 0, 255, 0, 0, 255, 0, 0, 255, 0, 0 }, 6);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
                using (var file = File.Create(probe)) png.Save(file);
                foreach (var task in model.Tasks)
                {
                    var result = candidate.Generate(probe, task.Id);
                    result.Validate(task.Id);
                    if (result.State == "Failed") throw new InvalidDataException(result.Error);
                }
                candidate.VerifyExecution();
                return candidate;
            }
            catch (Exception error) when (error is OnnxRuntimeException or InvalidDataException or NotSupportedException or ArgumentException or ContentException)
            {
                candidate?.Dispose();
                string reason = $"{device.Provider} ({device.Type}): {error.Message}";
                failures.Add(reason); Console.Error.WriteLine(reason);
            }
            finally { File.Delete(probe); }
        }
        throw new NotSupportedException("No available CPU/GPU/NPU device passed the image-understanding pipeline: " + string.Join(" | ", failures));
    }
    public int[] TokenIds(string text) => tokenizer.EncodeToIds(text, considerNormalization: false)
        .Prepend(model.Recipe.BeginningToken).Append(model.Recipe.EndToken).ToArray();

    public ImageTaskResult Generate(string path, string taskId)
    {
        var task = model.Task(taskId);
        var recipe = model.Recipe;
        var elapsed = Stopwatch.StartNew();
        using var run = new RunOptions();
        var source = ImagePipeline.Decode(path);
        var image = ImagePipeline.Resize(source, recipe.Width, recipe.Height);
        int area = checked(recipe.Width * recipe.Height);
        float[] pixels = new float[checked(3 * area)];
        for (int channel = 0; channel < 3; channel++)
            for (int pixel = 0; pixel < area; pixel++)
                pixels[channel * area + pixel] = (image.Pixels[pixel * 3 + channel] / 255f - recipe.Mean[channel]) / recipe.Deviation[channel];
        using var imageInput = OrtValue.CreateTensorValueFromMemory(pixels, [1, 3, recipe.Height, recipe.Width]);
        using var visual = vision.Run(run, new Dictionary<string, OrtValue> { ["pixel_values"] = imageInput }, ["image_features"]);
        float[] imageFeatures = visual[0].GetTensorDataAsSpan<float>().ToArray();
        float[] Embed(long[] input)
        {
            using var value = OrtValue.CreateTensorValueFromMemory(input, [1, input.Length]);
            using var embedded = tokens.Run(run, new Dictionary<string, OrtValue> { ["input_ids"] = value }, ["inputs_embeds"]);
            var result = embedded[0].GetTensorDataAsSpan<float>().ToArray();
            if (result.Length != input.Length * recipe.HiddenSize) throw new InvalidDataException("Invalid image text-embedding shape.");
            return result;
        }
        float[] textFeatures = Embed(TokenIds(task.Prompt).Select(value => (long)value).ToArray());
        float[] features = imageFeatures.Concat(textFeatures).ToArray();
        if (features.Length % recipe.HiddenSize != 0) throw new InvalidDataException("Invalid image/text encoder feature shape.");
        int sequence = features.Length / recipe.HiddenSize;
        if (sequence > 1024) throw new ContentException("The image encoder input exceeds its qualified token budget.", "image_resource_limit");
        using var featuresInput = OrtValue.CreateTensorValueFromMemory(features, [1, sequence, recipe.HiddenSize]);
        using var mask = OrtValue.CreateTensorValueFromMemory(Enumerable.Repeat(1L, sequence).ToArray(), [1, sequence]);
        using var encoded = encoder.Run(run, new Dictionary<string, OrtValue>
        { ["attention_mask"] = mask, ["inputs_embeds"] = featuresInput }, ["last_hidden_state"]);
        float[] hidden = encoded[0].GetTensorDataAsSpan<float>().ToArray();
        if (hidden.Length != sequence * recipe.HiddenSize) throw new InvalidDataException("Invalid image encoder state shape.");
        using var encoderInput = OrtValue.CreateTensorValueFromMemory(hidden, [1, sequence, recipe.HiddenSize]);
        var past = decoderOutputs.Skip(1).ToDictionary(name => name.Replace("present.", "past_key_values.", StringComparison.Ordinal),
            _ => OrtValue.CreateTensorValueFromMemory(Array.Empty<float>(), [1, recipe.Heads, 0, recipe.HeadSize]));
        var generated = new List<int>();
        long token = recipe.DecoderStartToken;
        bool finished = false;
        try
        {
            for (int step = 0; step < task.MaximumNewTokens; step++)
            {
                if (elapsed.Elapsed > TimeSpan.FromSeconds(150))
                    throw new ContentException("Image text generation exceeded its bounded time budget.", "image_generation_timeout", true);
                using var tokenInput = OrtValue.CreateTensorValueFromMemory(Embed([token]), [1, 1, recipe.HiddenSize]);
                using var branch = OrtValue.CreateTensorValueFromMemory(new[] { step != 0 }, [1]);
                var inputs = new Dictionary<string, OrtValue>(past)
                { ["encoder_attention_mask"] = mask, ["encoder_hidden_states"] = encoderInput, ["inputs_embeds"] = tokenInput, ["use_cache_branch"] = branch };
                using var decoded = decoder.Run(run, inputs, decoderOutputs);
                var logits = decoded[0].GetTensorDataAsSpan<float>();
                if (logits.Length is < 1 or > 1_000_000) throw new InvalidDataException("Invalid image decoder vocabulary.");
                var banned = new HashSet<int>();
                int n = recipe.NoRepeatNgram;
                if (n > 0 && generated.Count >= n - 1)
                    for (int start = 0; start + n <= generated.Count; start++)
                        if (Enumerable.Range(0, n - 1).All(offset => generated[start + offset] == generated[generated.Count - n + 1 + offset]))
                            banned.Add(generated[start + n - 1]);
                int best = -1;
                float maximum = float.NegativeInfinity;
                for (int index = 0; index < logits.Length; index++)
                    if (!banned.Contains(index) && float.IsFinite(logits[index]) && logits[index] > maximum)
                    { best = index; maximum = logits[index]; }
                if (best < 0) throw new InvalidDataException("Image generation produced no valid next token.");
                token = step == 0 ? recipe.BeginningToken : best;
                generated.Add((int)token);
                if (token == recipe.EndToken) { finished = true; break; }
                for (int index = 1; index < decoderOutputs.Length; index++)
                {
                    string name = decoderOutputs[index].Replace("present.", "past_key_values.", StringComparison.Ordinal);
                    // The cached decoder returns empty cross-attention outputs after the first step.
                    if (step != 0 && name.Contains(".encoder.", StringComparison.Ordinal)) continue;
                    var shape = decoded[index].GetTensorTypeAndShape().Shape;
                    var state = OrtValue.CreateTensorValueFromMemory(decoded[index].GetTensorDataAsSpan<float>().ToArray(), shape);
                    past[name].Dispose(); past[name] = state;
                }
            }
        }
        finally { foreach (var state in past.Values) state.Dispose(); }
        string text = tokenizer.Decode(generated.Where(value =>
            value != recipe.BeginningToken && value != recipe.EndToken && value != recipe.PaddingToken)).Trim();
        if (text.Length == 0)
            return taskId == "ocr" && finished ? new(taskId, "", "Empty", generated.Count) :
                new(taskId, "", "Failed", generated.Count, Error: "No valid image description was generated.", ErrorCode: "image_no_description");
        bool controls = Regex.IsMatch(text, @"<(?:loc_\d+|ocr|od|poly|sep)>", RegexOptions.None, TimeSpan.FromSeconds(2));
        string? warning = !finished ? "Generated text reached its output limit and may be incomplete." :
            controls ? "Generated text contains unexpected task-control markers." : null;
        return new(taskId, text, warning is null ? "Completed" : "Partial", generated.Count, warning);
    }
    private void VerifyExecution()
    {
        if (Device.NativeDevice is null) return;
        bool used = false;
        foreach (var session in sessions)
        {
            string file = session.EndProfiling(); profilesEnded.Add(session);
            try
            {
                using var profile = JsonDocument.Parse(File.ReadAllText(file));
                if (session != tokens)
                    used |= profile.RootElement.EnumerateArray().Any(value => value.TryGetProperty("args", out var args) &&
                        args.TryGetProperty("provider", out var provider) && provider.GetString() == Device.Provider);
            }
            finally { File.Delete(file); }
        }
        if (!used) throw new NotSupportedException("The selected image accelerator fell back entirely to CPU.");
    }
    public void Dispose()
    {
        foreach (var session in sessions)
        {
            if (Device.NativeDevice is not null && profilesEnded.Add(session))
            {
                try { File.Delete(session.EndProfiling()); }
                catch (Exception error) when (error is IOException or OnnxRuntimeException)
                { Console.Error.WriteLine("Could not remove image execution profile: " + error.Message); }
            }
            session.Dispose();
        }
        sessions.Clear();
        foreach (var lease in leases) lease.Dispose();
        leases.Clear();
    }
}
