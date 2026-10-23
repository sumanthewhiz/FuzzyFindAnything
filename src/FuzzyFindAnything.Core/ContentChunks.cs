namespace FuzzyFindAnything.Core;

public sealed record ChunkingContext(string Kind, string Language, string Title)
{
    public static ChunkingContext ForFile(string path, string kind) => new(kind, kind == "Code" ? LanguageFor(path) : "text", Path.GetFileName(path));
    private static string LanguageFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".cs" or ".csx" => "c-sharp",
        ".py" or ".pyw" or ".pyi" => "python",
        ".js" or ".jsx" or ".mjs" or ".cjs" => "javascript",
        ".ts" or ".mts" or ".cts" => "typescript",
        ".tsx" => "tsx",
        ".java" => "java",
        ".c" or ".h" => "c",
        ".cc" or ".cpp" or ".cxx" or ".c++" or ".hh" or ".hpp" or ".hxx" or ".h++" => "cpp",
        ".go" => "go",
        ".rs" => "rust",
        ".swift" => "swift",
        ".php" or ".phtml" or ".php3" or ".php4" or ".php5" or ".php7" or ".php8" or ".phps" => "php",
        ".rb" or ".rake" or ".gemspec" => "ruby",
        ".sh" or ".bash" => "bash",
        ".sql" => "sql",
        ".kt" or ".kts" => "kotlin",
        ".r" => "r",
        _ => Path.GetFileName(path).ToLowerInvariant() switch
        { ".bashrc" or ".bash_profile" or ".bash_login" or ".bash_logout" => "bash", "gemfile" or "rakefile" => "ruby", _ => "unknown" }
    };
}

public static class ChunkingProfiles
{
    public const string Legacy = "model-split-v1";
    public const string Documents = "document-structure-v1:overlap64tokens:context96";
    public const string Code = "code-structure-v1:treesitter1.3.0:overlap64tokens:context96";
    public static string ForKind(string kind) => kind switch
    { "Document" => Documents, "Code" => Code, "Image" => Legacy, _ => throw new ArgumentException("Unknown content kind.") };
}

public sealed record ChunkMetadata(string Profile, string Language, int StartOffset, int Length,
    int StartLine, int EndLine, string Symbol, string Context);
public sealed record ContentChunk(string Content, ChunkMetadata Metadata);
public sealed record ChunkingResult(ContentChunk[] Chunks, string? Warning = null);
