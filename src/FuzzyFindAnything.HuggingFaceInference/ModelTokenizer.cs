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
    private readonly BpeTokenizer? byteBpe;
    private readonly Dictionary<string, int>? clipVocabulary;
    private static readonly Regex clipWords = new(@"<\|startoftext\|>|<\|endoftext\|>|'s|'t|'re|'ve|'m|'ll|'d|[\p{L}]+|[\p{N}]|[^\s\p{L}\p{N}]+",
        RegexOptions.Compiled, TimeSpan.FromSeconds(5));
    private const string ByteBpePattern = @"(?i:'s|'t|'re|'ve|'m|'ll|'d)|[^\r\n\p{L}\p{N}]?\p{L}+|\p{N}| ?[^\s\p{L}\p{N}]+[\r\n]*|\s*[\r\n]+|\s+(?!\S)|\s+";
    public int Beginning { get; }
    public int End { get; }
    public int Padding { get; }
    public ModelTokenizer(string directory, EmbeddingSpace space)
    {
        using var configuration = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "tokenizer_config.json")));
        using var definition = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "tokenizer.json")));
        var config = configuration.RootElement;
        var native = definition.RootElement;
        if (space.Recipe?.Tokenizer == "byte-bpe-nfc")
        {
            var model = native.GetProperty("model");
            var pre = native.GetProperty("pre_tokenizer");
            var processors = native.GetProperty("post_processor").GetProperty("processors");
            if (model.GetProperty("type").GetString() != "BPE" ||
                model.GetProperty("end_of_word_suffix").GetString() is not ("" or null) ||
                model.GetProperty("continuing_subword_prefix").GetString() is not ("" or null) ||
                native.GetProperty("normalizer").GetProperty("type").GetString() != "NFC" ||
                pre.GetProperty("type").GetString() != "Sequence" ||
                pre.GetProperty("pretokenizers").GetArrayLength() != 2 ||
                pre.GetProperty("pretokenizers")[0].GetProperty("pattern").GetProperty("Regex").GetString() != ByteBpePattern ||
                pre.GetProperty("pretokenizers")[0].GetProperty("behavior").GetString() != "Isolated" ||
                pre.GetProperty("pretokenizers")[1].GetProperty("type").GetString() != "ByteLevel" ||
                pre.GetProperty("pretokenizers")[1].GetProperty("add_prefix_space").GetBoolean() ||
                pre.GetProperty("pretokenizers")[1].GetProperty("use_regex").GetBoolean() ||
                processors.GetArrayLength() != 2 ||
                processors[1].GetProperty("type").GetString() != "TemplateProcessing")
                throw new NotSupportedException("This byte-level BPE processor needs a different reviewed tokenizer adapter.");
            var specialTokens = native.GetProperty("added_tokens").EnumerateArray().ToArray();
            if (specialTokens.Any(token => token.GetProperty("single_word").GetBoolean() ||
                token.GetProperty("lstrip").GetBoolean() || token.GetProperty("rstrip").GetBoolean() || token.GetProperty("normalized").GetBoolean()))
                throw new NotSupportedException("Unsupported added-token matching behavior.");
            var specials = specialTokens.ToDictionary(item => item.GetProperty("content").GetString()!, item => item.GetProperty("id").GetInt32());
            var single = processors[1].GetProperty("single");
            if (single.GetArrayLength() != 2 || !single[0].TryGetProperty("Sequence", out _) ||
                !single[1].TryGetProperty("SpecialToken", out var terminal) ||
                !specials.TryGetValue(terminal.GetProperty("id").GetString()!, out int end) || end != space.Recipe.EndToken)
                throw new NotSupportedException("The tokenizer's terminal-token recipe differs from its approved embedding contract.");
            byteBpe = BpeTokenizer.Create(new BpeOptions(Path.Combine(directory, "vocab.json"), Path.Combine(directory, "merges.txt"))
            {
                ByteLevel = true,
                SpecialTokens = specials,
                PreTokenizer = new RegexPreTokenizer(new Regex(ByteBpePattern, RegexOptions.Compiled, TimeSpan.FromSeconds(5)), specials)
            });
            Beginning = space.Recipe.BeginningToken ?? -1;
            End = end; Padding = space.Recipe.PaddingToken;
            return;
        }
        if (space.Adapter == "bert-mean" || space.Recipe?.Tokenizer == "bert-wordpiece")
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
            if (space.Recipe is { } recipe && (recipe.BeginningToken != Beginning || recipe.EndToken != End || recipe.PaddingToken != Padding))
                throw new NotSupportedException("BERT special tokens differ from the approved embedding recipe.");
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
        if (byteBpe is not null) return byteBpe.EncodeToIds(text.Normalize(NormalizationForm.FormC), considerNormalization: false).ToArray();
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
