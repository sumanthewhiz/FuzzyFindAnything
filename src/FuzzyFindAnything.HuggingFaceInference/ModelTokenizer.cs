using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FuzzyFindAnything.Core;
using Microsoft.ML.Tokenizers;

namespace FuzzyFindAnything.HuggingFaceInference;

internal sealed class ModelTokenizer
{
    private readonly BertTokenizer? bert;
    private readonly BpeTokenizer? clip;
    private readonly Dictionary<string, int>? clipVocabulary;
    private static readonly Regex clipWords = new(@"<\|startoftext\|>|<\|endoftext\|>|'s|'t|'re|'ve|'m|'ll|'d|[\p{L}]+|[\p{N}]|[^\s\p{L}\p{N}]+",
        RegexOptions.Compiled, TimeSpan.FromSeconds(5));
    public int Beginning { get; }
    public int End { get; }
    public int Padding { get; }
    public ModelTokenizer(string directory, EmbeddingSpace space)
    {
        using var configuration = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "tokenizer_config.json")));
        using var definition = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "tokenizer.json")));
        var config = configuration.RootElement;
        var native = definition.RootElement;
        if (space.Adapter == "bert-mean")
        {
            if (native.GetProperty("model").GetProperty("type").GetString() != "WordPiece" ||
                native.GetProperty("normalizer").GetProperty("type").GetString() != "BertNormalizer" ||
                native.GetProperty("pre_tokenizer").GetProperty("type").GetString() != "BertPreTokenizer")
                throw new NotSupportedException("The model does not use the supported native BERT WordPiece tokenizer contract.");
            bool lower = !config.TryGetProperty("do_lower_case", out var value) || value.GetBoolean();
            bert = BertTokenizer.Create(Path.Combine(directory, "vocab.txt"), new BertOptions
            {
                LowerCaseBeforeTokenization = lower,
                ApplyBasicTokenization = !config.TryGetProperty("do_basic_tokenize", out var basic) || basic.GetBoolean(),
                IndividuallyTokenizeCjk = !config.TryGetProperty("tokenize_chinese_chars", out var cjk) || cjk.GetBoolean(),
                RemoveNonSpacingMarks = config.TryGetProperty("strip_accents", out var accents) && accents.ValueKind != JsonValueKind.Null
                    ? accents.GetBoolean() : lower
            });
            string[] vocabulary = File.ReadAllLines(Path.Combine(directory, "vocab.txt"));
            Beginning = Array.IndexOf(vocabulary, "[CLS]"); End = Array.IndexOf(vocabulary, "[SEP]");
            Padding = Array.IndexOf(vocabulary, "[PAD]");
        }
        else
        {
            if (native.GetProperty("model").GetProperty("type").GetString() != "BPE" ||
                native.GetProperty("model").GetProperty("end_of_word_suffix").GetString() != "</w>")
                throw new NotSupportedException("The model does not use the supported CLIP byte-level BPE tokenizer contract.");
            clipVocabulary = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(Path.Combine(directory, "vocab.json")))!;
            clip = BpeTokenizer.Create(new BpeOptions(Path.Combine(directory, "vocab.json"), Path.Combine(directory, "merges.txt"))
            { ByteLevel = true, EndOfWordSuffix = "</w>", UnknownToken = "<|endoftext|>" });
            Beginning = clipVocabulary["<|startoftext|>"]; End = clipVocabulary["<|endoftext|>"]; Padding = End;
        }
        if (Beginning < 0 || End < 0 || Padding < 0) throw new InvalidDataException("Model special tokens are incomplete.");
    }
    public int[] Encode(string text)
    {
        if (bert is not null) return bert.EncodeToIds(text, addSpecialTokens: false).ToArray();
        text = Regex.Replace(text.Normalize(NormalizationForm.FormC), @"\s+", " ").ToLowerInvariant();
        var ids = new List<int>();
        foreach (Match word in clipWords.Matches(text))
        {
            if (word.Value is "<|startoftext|>" or "<|endoftext|>") ids.Add(clipVocabulary![word.Value]);
            else ids.AddRange(clip!.EncodeToIds(word.Value, considerPreTokenization: false, considerNormalization: false));
        }
        return ids.ToArray();
    }
}
