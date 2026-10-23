using System.IO;
using System.Text.RegularExpressions;
using System.Text.Json;
using FuzzyFindAnything.Core;
using FuzzyFindAnything.Chunking;

namespace FuzzyFindAnything.NativeTests;

internal static class ContentChunkingTests
{
    private static int Tokens(string value) => Regex.Matches(value, @"\w+|[^\w\s]").Count + 2;
    public static void Run()
    {
        Program.Run("Content-aware native syntax boundaries and provenance", Code);
        Program.Run("Document sections, paragraphs and sentence fallback", Documents);
        Program.Run("Oversized code, Unicode and whitespace-sensitive identity", Oversized);
        Program.Run("Unsupported/incomplete syntax preserves source with warning", Fallback);
        Program.Run("Chunk profile migration retains unrelated vectors and full matches", Migration);
    }
    private static void Coverage(string source, ChunkingResult result, int budget)
    {
        bool[] covered = new bool[source.Length];
        foreach (var chunk in result.Chunks)
        {
            var metadata = chunk.Metadata;
            Program.Require(Tokens(chunk.Content) <= budget && metadata.StartOffset >= 0 && metadata.Length > 0 &&
                metadata.StartOffset + metadata.Length <= source.Length &&
                chunk.Content == metadata.Context + source.Substring(metadata.StartOffset, metadata.Length),
                "Chunk content, budget and source provenance disagree.");
            Program.Require(!char.IsLowSurrogate(source[metadata.StartOffset]) &&
                !char.IsHighSurrogate(source[metadata.StartOffset + metadata.Length - 1]), "Chunking split a Unicode scalar.");
            for (int index = metadata.StartOffset; index < metadata.StartOffset + metadata.Length; index++) covered[index] = true;
        }
        Program.Require(Enumerable.Range(0, source.Length).All(index => char.IsWhiteSpace(source[index]) || covered[index]),
            "Structured chunking lost source content.");
    }
    private static void Code()
    {
        string source = "namespace Demo {\nclass Calculator {\n/// Adds values.\npublic int Add(int a, int b) { return a + b; }\npublic int Multiply(int a, int b) { return a * b; }\n}\n}";
        var result = ContentChunker.Split(source, new("Code", "c-sharp", "Calculator.cs"), 96, Tokens);
        Coverage(source, result, 96);
        Program.Require(result.Warning is null &&
            result.Chunks.Any(chunk => chunk.Metadata.Symbol.Contains("Add") && chunk.Content.Contains("/// Adds values.")) &&
            result.Chunks.Any(chunk => chunk.Metadata.Symbol.Contains("Multiply")) &&
            result.Chunks.All(chunk => !(chunk.Content.Contains("return a + b") && chunk.Content.Contains("return a * b"))),
            "Functions or their leading documentation were mixed/lost.");
        string documented = "class C {\n/*\nUseful method documentation.\n*/\nint answer() { return 42; }\n}";
        var comments = ContentChunker.Split(documented, new("Code", "java", "C.java"), 96, Tokens);
        Program.Require(comments.Chunks.Any(chunk => chunk.Metadata.Symbol.Contains("answer") &&
            chunk.Content.Contains("Useful method documentation.")), "A multi-line documentation block was detached from its function.");
        foreach (var (language, code) in new[]
        {
            ("python", "class C:\n    def add(self, a, b):\n        return a + b\n"),
            ("javascript", "function add(a,b){return a+b;} function mul(a,b){return a*b;}"),
            ("typescript", "function add(a:number,b:number):number { return a+b; }"),
            ("tsx", "function View() { return <div>Hello</div>; }"),
            ("java", "class C { int add(int a,int b) { return a+b; } }"),
            ("c", "int add(int a,int b) { return a+b; }"),
            ("cpp", "class C { public: int add(int a,int b) { return a+b; } };"),
            ("go", "package main\nfunc add(a int, b int) int { return a + b }\n"),
            ("rust", "fn add(a:i32,b:i32)->i32 { a+b }\n"),
            ("ruby", "def add(a, b)\n  a + b\nend\n"),
            ("php", "<?php function add($a,$b) { return $a+$b; }"),
            ("bash", "add() {\n echo test\n}\n"),
            ("javascript", "const f = () => 3;")
        })
        {
            Console.WriteLine("Checking native grammar: " + language);
            var chunks = ContentChunker.Split(code, new("Code", language, "source"), 96, Tokens);
            Coverage(code, chunks, 96);
            Program.Require(chunks.Warning is null, "A packaged grammar could not parse its basic fixture: " + language);
        }
    }
    private static void Documents()
    {
        string text = "# Apples\n\nFarmers grow fruit. Harvest begins in autumn.\n\n# Trains\n\nPassengers travel through mountain valleys.";
        var result = ContentChunker.Split(text, new("Document", "text", "notes.txt"), 48, Tokens);
        Coverage(text, result, 48);
        Program.Require(result.Chunks.Any(chunk => chunk.Metadata.Context.Contains("Apples")) &&
            result.Chunks.Any(chunk => chunk.Metadata.Context.Contains("Trains")) &&
            result.Chunks.All(chunk => !(chunk.Content.Contains("Farmers") && chunk.Content.Contains("Passengers"))),
            "Document section boundaries were not respected.");
    }
    private static void Oversized()
    {
        string source = "def same():\n    return 1\n\ndef same():\n        return 1\n";
        var repeated = ContentChunker.Split(source, new("Code", "python", "example.py"), 48, Tokens);
        Coverage(source, repeated, 48);
        Program.Require(repeated.Chunks.Count(chunk => chunk.Content.Contains("return 1")) == 2,
            "Distinct source units were deduplicated by whitespace.");
        string large = "function large() {\n" + string.Join("\n", Enumerable.Range(0, 80).Select(index =>
            $"  const item{index} = \"caf\u00e9 \U0001F600\";")) + "\n  return item79;\n}";
        var result = ContentChunker.Split(large, new("Code", "javascript", "large.js"), 48, Tokens);
        Coverage(large, result, 48);
        Program.Require(result.Chunks.Length > 3 && result.Chunks.All(chunk => chunk.Metadata.Symbol.Contains("large") &&
            chunk.Metadata.Context.Contains("large")), "Oversized function chunks lost their parent/signature context.");
    }
    private static void Fallback()
    {
        foreach (var context in new[] { new ChunkingContext("Code", "sql", "query.sql"), new("Code", "python", "broken.py"), new("Code", "swift", "sample.swift") })
        {
            string source = context.Language == "sql" ? "SELECT name\nFROM records\nWHERE active = 1;\n" :
                context.Language == "swift" ? "func add(_ a: Int, _ b: Int) -> Int { return a + b }\n" : "def broken(\n    return 1\n";
            var result = ContentChunker.Split(source, context, 32, Tokens);
            Coverage(source, result, 32);
            Program.Require(result.Warning?.Contains("line-aware", StringComparison.OrdinalIgnoreCase) == true, "Fallback was silent or source was discarded.");
        }
    }
    public static async Task RuntimeAsync(string package, string defaultModel, string cacheDirectory, string output)
    {
        var cache = new HuggingFaceModels(cacheDirectory, defaultModel);
        var spaces = new[] { EmbeddingSpace.Default, cache.FindCached("https://huggingface.co/Qwen/Qwen3-Embedding-0.6B") };
        var results = new List<object>();
        foreach (var space in spaces)
        {
            string executable = space == EmbeddingSpace.Default
                ? Path.Combine(package, "inference", "FuzzyFindAnything.Inference.exe")
                : Path.Combine(package, "hf-inference", "FuzzyFindAnything.HuggingFaceInference.exe");
            await using var client = await InferenceClient.StartAsync(executable, cache.DirectoryFor(space), 2, null,
                Console.Error.WriteLine, CancellationToken.None);
            foreach (var context in new[] { new ChunkingContext("Document", "text", "notes.txt"), new("Code", "python", "functions.py") })
            {
                string text = context.Kind == "Code" ? "class Arithmetic:\n    def calculate(self, value):\n" +
                    string.Join("\n", Enumerable.Range(0, 180).Select(index => $"        value += {index}  # caf\u00e9 \U0001F600")) +
                    "\n        return value\n\n    def unchanged(self):\n        return 42\n" :
                    "# Orchard\n\n" + string.Join("\n\n", Enumerable.Range(0, 100).Select(index =>
                        $"Paragraph {index}: Farmers harvest ripe apples. Their orchard needs water and careful pruning."));
                var chunks = (await client.RequestAsync("split_content", new { text, context }, CancellationToken.None)).Deserialize<ChunkingResult>(Protocol.Json)!;
                bool[] covered = new bool[text.Length];
                string framing = space == EmbeddingSpace.Default ? $"title: {context.Title} | text: " : space.Recipe?.DocumentPrefix ?? "";
                int framingTokens = (await client.RequestAsync("tokens", new { text = framing }, CancellationToken.None)).GetArrayLength();
                int budget = space == EmbeddingSpace.Default ? 512 + framingTokens : space.Recipe!.ChunkTokens;
                foreach (var chunk in chunks.Chunks)
                {
                    var metadata = chunk.Metadata;
                    int tokens = (await client.RequestAsync("tokens", new { text = framing + chunk.Content }, CancellationToken.None)).GetArrayLength();
                    Program.Require(tokens <= budget && chunk.Content == metadata.Context + text.Substring(metadata.StartOffset, metadata.Length),
                        "Real-model chunk content or formatted token budget differs from its provenance.");
                    for (int index = metadata.StartOffset; index < metadata.StartOffset + metadata.Length; index++) covered[index] = true;
                }
                Program.Require(Enumerable.Range(0, text.Length).All(index => char.IsWhiteSpace(text[index]) || covered[index]),
                    "Real token-aware preparation lost source content.");
                var vectors = (await client.RequestAsync("documents", new
                { texts = chunks.Chunks.Take(2).Select(chunk => space == EmbeddingSpace.Default ? framing + chunk.Content : chunk.Content).ToArray() },
                    CancellationToken.None)).Deserialize<float[][]>()!;
                Program.Require(vectors.All(vector => vector.Length == space.Dimensions), "Prepared chunks did not embed through the real model.");
                results.Add(new { model = space.ModelUrl, context.Kind, count = chunks.Chunks.Length, budget });
                Console.WriteLine($"PASS real {context.Kind} chunking/token budget: {space.ModelUrl}");
            }
        }
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "chunking-runtime.json"), JsonSerializer.Serialize(new { failures = 0, results },
            new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void Migration() => Program.Temporary(root =>
    {
        string data = Path.Combine(root, "index"), source = Path.Combine(root, "files");
        Directory.CreateDirectory(source);
        using var catalog = new Catalog(data, 1);
        int folder = catalog.AddFolder(source);
        string document = Path.Combine(source, "document.txt"), code = Path.Combine(source, "code.cs"), image = Path.Combine(source, "image.png");
        Program.Complete(catalog, Program.Enqueue(catalog, folder, document, "document content"));
        Program.Complete(catalog, Program.Enqueue(catalog, folder, code, "int Add() { return 1; }", "Code"));
        Program.Complete(catalog, Program.Enqueue(catalog, folder, image, "image data", "Image"));
        long imageVector = Check.Scalar(data, "SELECT v.id FROM vectors v JOIN files f ON f.id=v.file_id WHERE f.kind='Image'");
        long codeFile = catalog.FindFile(code)!.Id;
        var profiles = new Dictionary<string, string> { ["Document"] = ChunkingProfiles.Documents, ["Code"] = ChunkingProfiles.Code };
        Program.Require(catalog.ConfigureChunkingProfiles(profiles).Order().SequenceEqual(new[] { "Code", "Document" }) &&
            catalog.FindFile(code)!.Id == codeFile &&
            Check.Scalar(data, "SELECT v.id FROM vectors v JOIN files f ON f.id=v.file_id WHERE f.kind='Image'") == imageVector,
            "Chunking upgrade reset image vectors or source identities.");
        catalog.Enqueue(folder, code, FilePaths.Inspect(code), "Code", FilePaths.Hash(code), false);
        var job = catalog.Claim()!;
        var chunks = ContentChunker.Split(File.ReadAllText(code), new("Code", "c-sharp", "code.cs"), 96, Tokens);
        catalog.PrepareContent(job, chunks);
        catalog.SaveBatch(job, catalog.Remaining(job), chunks.Chunks.Select(_ => Program.Vector(0)).ToArray());
        Program.Require(catalog.Complete(job), "Structured chunks did not publish.");
        var match = catalog.Search(Program.Vector(0), 1, 0, kinds: ["Code"]).Single();
        Program.Require(match.MatchedChunk == chunks.Chunks[0].Content && match.Chunk?.Profile == ChunkingProfiles.Code &&
            match.Chunk.StartLine > 0, "The matched chunk/provenance was not persisted.");
        Program.Require(catalog.ConfigureChunkingProfiles(profiles).Length == 0, "Unchanged chunk profiles reindexed again.");
        profiles["Code"] += ":next";
        Program.Require(catalog.ConfigureChunkingProfiles(profiles).SequenceEqual(["Code"]) &&
            Check.Scalar(data, "SELECT v.id FROM vectors v JOIN files f ON f.id=v.file_id WHERE f.kind='Image'") == imageVector,
            "Changing only the code chunker invalidated another source kind.");
    });
}
