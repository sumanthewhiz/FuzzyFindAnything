using System.Text.RegularExpressions;
using FuzzyFindAnything.Core;
using TreeSitter;

namespace FuzzyFindAnything.Chunking;

public static class ContentChunker
{
    private sealed record Unit(int Start, int End, string Symbol = "", string Context = "");
    private static readonly HashSet<string> Languages = new(StringComparer.Ordinal)
    { "c-sharp", "python", "javascript", "typescript", "tsx", "java", "c", "cpp", "go", "rust", "ruby", "php", "bash" };
    private static readonly HashSet<string> Functions = new(StringComparer.Ordinal)
    {
        "function_definition", "function_declaration", "function_item", "method_declaration", "method_definition",
        "constructor_declaration", "destructor_declaration", "local_function_statement", "method",
        "singleton_method", "operator_declaration", "conversion_operator_declaration", "arrow_function"
    };
    private static readonly HashSet<string> Containers = new(StringComparer.Ordinal)
    {
        "class_definition", "class_declaration", "struct_specifier", "struct_item", "struct_declaration",
        "interface_declaration", "trait_item", "impl_item", "namespace_declaration", "namespace_definition",
        "module", "module_definition"
    };

    public static ChunkingResult Split(string source, ChunkingContext context, int budget, Func<string, int> countTokens)
    {
        if (context.Kind is not ("Document" or "Code")) throw new ArgumentException("Structured chunking is for documents and code.");
        if (budget < 16) throw new ArgumentException("The chunk token budget is too small.");
        if (string.IsNullOrWhiteSpace(source)) throw new ContentException("No searchable text was extracted.", "no_text");
        if (source.Length > 32_000_000) throw new ContentException("Content exceeds the structured preparation bound.", "resource_limit");
        int[] lines = LineStarts(source);
        var preferred = new SortedSet<int>(lines.Append(source.Length));
        string? warning = null;
        List<Unit> units;
        if (context.Kind == "Code") units = CodeUnits(source, context.Language, lines, preferred, out warning);
        else units = MergeDocuments(source, DocumentUnits(source, lines, preferred), budget, countTokens);
        var result = new List<ContentChunk>();
        int baseTokens = countTokens("");
        foreach (var unit in units)
        {
            string prefix = ContextPrefix(unit.Context, budget, countTokens, baseTokens);
            int start = unit.Start;
            while (start < unit.End)
            {
                int end = Fit(source, start, unit.End, prefix, budget, countTokens);
                if (end == start) throw new ContentException("A source character cannot fit the qualified token budget.", "resource_limit");
                if (end < unit.End)
                {
                    int boundary = preferred.GetViewBetween(start + 1, end).LastOrDefault();
                    if (boundary > start && !string.IsNullOrWhiteSpace(source[start..boundary])) end = boundary;
                    while (end > start && countTokens(prefix + source[start..end]) > budget) end = PreviousScalar(source, end);
                }
                string body = source[start..end];
                if (!string.IsNullOrWhiteSpace(body))
                {
                    string content = prefix + body;
                    if (countTokens(content) > budget) throw new InvalidDataException("The prepared chunk exceeds its model token budget.");
                    result.Add(new(content, new(ChunkingProfiles.ForKind(context.Kind), context.Language, start, end - start,
                        LineAt(lines, start), LineAt(lines, Math.Max(start, end - 1)), unit.Symbol, prefix)));
                }
                if (end == unit.End) break;
                int overlap = Math.Min(64, budget / 8);
                int next = OverlapStart(source, start, end, overlap, countTokens, baseTokens);
                int aligned = preferred.GetViewBetween(next, end).FirstOrDefault();
                if (aligned > start && aligned < end) next = aligned;
                start = next > start ? next : end;
            }
        }
        if (result.Count == 0) throw new ContentException("No searchable chunks were prepared.", "no_text");
        return new(result.ToArray(), warning);
    }

    private static int Fit(string source, int start, int end, string prefix, int budget, Func<string, int> count)
    {
        int low = start + 1, high = Math.Min(end, start + checked(budget * 32)), fit = start;
        while (low <= high)
        {
            int middle = low + (high - low) / 2, boundary = middle;
            if (boundary < source.Length && char.IsHighSurrogate(source[boundary - 1]) && char.IsLowSurrogate(source[boundary])) boundary--;
            if (boundary <= start) { low = middle + 1; continue; }
            if (count(prefix + source[start..boundary]) <= budget) { fit = boundary; low = middle + 1; }
            else high = middle - 1;
        }
        return fit;
    }
    private static int OverlapStart(string text, int start, int end, int budget, Func<string, int> count, int baseTokens)
    {
        int low = start + 1, high = end, fit = end;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            if (middle < text.Length && char.IsLowSurrogate(text[middle]) && char.IsHighSurrogate(text[middle - 1])) middle++;
            if (count(text[middle..end]) - baseTokens <= budget) { fit = middle; high = middle - 1; }
            else low = middle + 1;
        }
        return fit;
    }
    private static string ContextPrefix(string context, int budget, Func<string, int> count, int baseTokens)
    {
        if (context.Length == 0) return "";
        string prefix = "Context: " + context + "\n\n";
        int limit = Math.Min(96, budget / 4);
        int end = Fit(prefix, 0, prefix.Length, "", baseTokens + limit, count);
        return end == 0 ? "" : prefix[..end].TrimEnd() + "\n\n";
    }
    private static int PreviousScalar(string text, int end)
    {
        end--;
        if (end > 0 && char.IsLowSurrogate(text[end]) && char.IsHighSurrogate(text[end - 1])) end--;
        return end;
    }
    private static int[] LineStarts(string source)
    {
        var result = new List<int> { 0 };
        for (int index = 0; index < source.Length; index++) if (source[index] == '\n') result.Add(index + 1);
        return result.ToArray();
    }
    private static int LineAt(int[] lines, int offset)
    {
        int index = Array.BinarySearch(lines, offset);
        return index >= 0 ? index + 1 : ~index;
    }
    private static int StartOfLine(int[] lines, int offset) => lines[LineAt(lines, offset) - 1];

    private static List<Unit> DocumentUnits(string text, int[] lines, SortedSet<int> preferred)
    {
        var units = new List<Unit>();
        int start = 0;
        string heading = "";
        for (int index = 0; index < lines.Length; index++)
        {
            int end = index + 1 < lines.Length ? lines[index + 1] : text.Length;
            string line = text[lines[index]..end].Trim();
            bool title = line.Length is > 0 and <= 160 &&
                (Regex.IsMatch(line, @"^#{1,6}\s+\S", RegexOptions.None, TimeSpan.FromSeconds(2)) ||
                 line.EndsWith(':') || index + 1 < lines.Length &&
                 Regex.IsMatch(text[end..(index + 2 < lines.Length ? lines[index + 2] : text.Length)].Trim(),
                     @"^[-=]{3,}$", RegexOptions.None, TimeSpan.FromSeconds(2)));
            if (title && lines[index] > start)
            {
                units.Add(new(start, lines[index], Context: heading)); start = lines[index];
            }
            if (title) heading = line.TrimStart('#', ' ');
            if (line.Length == 0 && end > start)
            {
                units.Add(new(start, end, Context: heading)); start = end;
            }
        }
        if (start < text.Length) units.Add(new(start, text.Length, Context: heading));
        foreach (Match sentence in Regex.Matches(text, @"[.!?](?:[""')\]]*)\s+", RegexOptions.None, TimeSpan.FromSeconds(5)))
            preferred.Add(sentence.Index + sentence.Length);
        return units;
    }
    private static List<Unit> MergeDocuments(string source, List<Unit> units, int budget, Func<string, int> count)
    {
        var result = new List<Unit>();
        foreach (var unit in units)
        {
            if (result.Count > 0 && result[^1].Context == unit.Context && unit.End - result[^1].Start <= budget * 32)
            {
                var previous = result[^1];
                string prefix = ContextPrefix(previous.Context, budget, count, count(""));
                if (count(prefix + source[previous.Start..unit.End]) <= budget)
                { result[^1] = previous with { End = unit.End }; continue; }
            }
            result.Add(unit);
        }
        return result;
    }

    private static List<Unit> CodeUnits(string source, string language, int[] lines, SortedSet<int> preferred, out string? warning)
    {
        warning = null;
        if (!Languages.Contains(language))
        {
            warning = $"No native syntax grammar is configured for {language}; line-aware token-bounded chunking was used.";
            return [new(0, source.Length)];
        }
        using var grammar = new Language($"tree-sitter-{language}.dll", "tree_sitter_" + language.Replace('-', '_'));
        using var parser = new Parser();
        try { parser.Language = grammar; }
        catch (InvalidOperationException error)
        {
            warning = $"The {language} grammar ABI {grammar.AbiVersion} is not usable: {error.Message} Line-aware token-bounded chunking was used.";
            return [new(0, source.Length)];
        }
        using var tree = parser.Parse(source) ?? throw new ContentException("The native syntax parser could not complete.", "syntax_parser");
        if (tree.RootNode.HasError)
        {
            warning = $"The {language} source contains incomplete or unsupported syntax; line-aware token-bounded chunking was used.";
            return [new(0, source.Length)];
        }
        var functions = new List<Node>();
        var stack = new Stack<(Node Node, bool InsideFunction)>();
        stack.Push((tree.RootNode, false));
        int visited = 0;
        while (stack.TryPop(out var item))
        {
            if (++visited > 500_000) throw new ContentException("The syntax tree exceeds the preparation bound.", "resource_limit");
            var node = item.Node;
            bool function = Functions.Contains(node.Type);
            if (function && !item.InsideFunction) functions.Add(node);
            if (node.Type.EndsWith("_statement", StringComparison.Ordinal) || function || Containers.Contains(node.Type))
            {
                preferred.Add(node.StartIndex); preferred.Add(node.EndIndex);
            }
            foreach (var child in node.NamedChildren.Reverse()) stack.Push((child, item.InsideFunction || function));
        }
        if (functions.Count == 0)
        {
            var containers = tree.RootNode.NamedChildren.Where(node => Containers.Contains(node.Type)).ToArray();
            if (containers.Length == 0) return [new(0, source.Length)];
            return Partitions(source, lines, containers);
        }
        return Partitions(source, lines, functions);
    }
    private static List<Unit> Partitions(string source, int[] lines, IEnumerable<Node> nodes)
    {
        var boundaries = new SortedDictionary<int, (string Symbol, string Context)> { [0] = ("", "") };
        foreach (var node in nodes.OrderBy(value => value.StartIndex))
        {
            var container = node.Parent;
            var attached = container is { Type: "decorated_definition" } ? container : node;
            int anchor = attached.StartIndex;
            for (var previous = attached.PreviousNamedSibling; previous is not null &&
                previous.Type.Contains("comment", StringComparison.Ordinal) &&
                string.IsNullOrWhiteSpace(source[previous.EndIndex..anchor]); previous = previous.PreviousNamedSibling)
                anchor = previous.StartIndex;
            int start = StartOfLine(lines, anchor);
            if (!string.IsNullOrWhiteSpace(source[start..anchor])) start = anchor;
            // Keep leading documentation/decorators with the declaration, not with the preceding function.
            while (start > 0)
            {
                int previous = StartOfLine(lines, start - 1);
                string line = source[previous..start].Trim();
                if (!(line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith('#') || line.StartsWith('@') ||
                    line.StartsWith("/*", StringComparison.Ordinal) || line.StartsWith('*'))) break;
                start = previous;
            }
            if (boundaries.Keys.Any(value => value > start && value <= node.StartIndex)) start = node.StartIndex;
            string name = NodeName(node);
            var parents = new List<string>();
            for (var parent = node.Parent; parent is not null; parent = parent.Parent)
                if (Containers.Contains(parent.Type)) parents.Add(NodeName(parent));
            parents.Reverse();
            var body = node.GetChildForField("body");
            int headerEnd = body?.StartIndex ?? Math.Min(node.EndIndex, node.StartIndex + 240);
            string header = FilePaths.ScalarPrefix(source[node.StartIndex..headerEnd].Trim(), 240);
            string scope = string.Join(" / ", parents.Where(value => value.Length > 0).Append(name));
            boundaries[start] = (scope, string.Join("\n", parents.Where(value => value.Length > 0).Append(header)));
        }
        var starts = boundaries.Keys.ToArray();
        return starts.Select((start, index) => new Unit(start, index + 1 < starts.Length ? starts[index + 1] : source.Length,
            boundaries[start].Symbol, boundaries[start].Context)).Where(unit => unit.End > unit.Start).ToList();
    }
    private static string NodeName(Node node)
    {
        var name = node.GetChildForField("name");
        if (name is null && node.Type == "arrow_function") name = node.Parent?.GetChildForField("name");
        if (name is not null) return FilePaths.ScalarPrefix(name.Text, 160);
        var declarator = node.GetChildForField("declarator");
        while (declarator is not null)
        {
            if (declarator.Type is "identifier" or "field_identifier") return FilePaths.ScalarPrefix(declarator.Text, 160);
            declarator = declarator.GetChildForField("declarator");
        }
        return node.Type;
    }
}
