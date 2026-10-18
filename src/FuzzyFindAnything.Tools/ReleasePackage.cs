using System.Diagnostics;
using System.IO.Compression;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FuzzyFindAnything.Tools;

public static class ReleasePackage
{
    public static IReadOnlyDictionary<string, string> RuntimePaths { get; } = new Dictionary<string, string>
    {
        ["engine"] = @"engine\FuzzyFindAnything.Engine.exe",
        ["inference"] = @"inference\FuzzyFindAnything.Inference.exe",
        ["model"] = @"models\embeddinggemma-2-onnx",
        ["extractor"] = @"extractor\FuzzyFindAnything.Extractor.exe"
    };
    private static readonly string[] Required =
    [
        "FuzzyFindAnything.exe", "FuzzyFindAnything.dll", "FuzzyFindAnything.runtimeconfig.json", "runtime.json",
        "Get-Model.ps1", "Reset-Index.ps1", "QUICKSTART.md", "THIRD-PARTY-NOTICES.txt", "release-manifest.json", "model-manifest.json",
        "engine/FuzzyFindAnything.Engine.exe", "engine/ffa_diskann.dll", "engine/libiomp5md.dll", "engine/e_sqlite3.dll",
        "inference/FuzzyFindAnything.Inference.exe", "inference/onnxruntime.dll",
        "hf-inference/FuzzyFindAnything.HuggingFaceInference.exe", "hf-inference/onnxruntime.dll", "hf-inference/DirectML.dll",
        "extractor/FuzzyFindAnything.Extractor.exe", "tools/FuzzyFindAnything.Tools.exe", "tools/ffa_diskann.dll",
        "engine/msvcp140.dll", "engine/vcruntime140.dll", "inference/msvcp140.dll", "inference/vcruntime140.dll"
    ];

    public static void Validate(string path, string runtime, string version)
    {
        ushort machine = runtime switch { "win-x64" => 0x8664, "win-arm64" => 0xAA64, _ => throw new ArgumentException("Unknown runtime.") };
        using var archive = ZipFile.OpenRead(path);
        if (archive.Entries.Count > 20000 || archive.Entries.Sum(entry => entry.Length) > 4L * 1024 * 1024 * 1024)
            throw new InvalidDataException("The archive exceeds the application package size/entry limits.");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            string name = entry.FullName;
            string[] parts = name.Split('/');
            if (name.StartsWith('/') || name.Contains('\\') || name.Contains(':') || name.Any(char.IsControl) ||
                parts.Take(entry.Name.Length == 0 ? parts.Length - 1 : parts.Length).Any(part => part.Length == 0 ||
                    part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0) ||
                parts.Any(part => part == ".." || part == ".") || !entries.TryAdd(name, entry))
                throw new InvalidDataException("Unsafe or duplicate archive path: " + name);
            string leaf = parts[^1].ToLowerInvariant();
            if (parts.Any(part => part.ToLowerInvariant() is ".git" or ".github" or ".runtime" or "__pycache__" or "site-packages" or "models" or "data" or "corpus" or "index") ||
                leaf is "settings.xml" or "engine.lock" or "catalog.sqlite3" or "catalog.sqlite3-wal" or "catalog.sqlite3-shm" or ".env" ||
                leaf.EndsWith(".py", StringComparison.OrdinalIgnoreCase) || leaf.EndsWith(".pyc", StringComparison.OrdinalIgnoreCase) ||
                leaf.EndsWith(".pyd", StringComparison.OrdinalIgnoreCase) || leaf.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase) ||
                leaf.EndsWith(".onnx.data", StringComparison.OrdinalIgnoreCase) || leaf.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase) ||
                leaf.StartsWith("python", StringComparison.OrdinalIgnoreCase) ||
                leaf.EndsWith(".log", StringComparison.OrdinalIgnoreCase) || leaf.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Development, user, model or interpreter payload found: " + name);
        }
        foreach (string name in Required)
            if (!entries.ContainsKey(name)) throw new InvalidDataException("Required release file is missing: " + name);
        using var configuration = ReadJson(entries["runtime.json"]);
        if (configuration.RootElement.EnumerateObject().Count() != RuntimePaths.Count)
            throw new InvalidDataException("Unexpected runtime configuration.");
        foreach (var field in RuntimePaths)
            if (!configuration.RootElement.TryGetProperty(field.Key, out var value) || value.GetString() != field.Value)
                throw new InvalidDataException("Runtime paths must be relative and match the supported deployment layout.");
        using var manifest = ReadJson(entries["release-manifest.json"]);
        var root = manifest.RootElement;
        if (root.GetProperty("schema_version").GetInt32() != 2 || root.GetProperty("runtime_id").GetString() != runtime ||
            root.GetProperty("version").GetString() != version || root.GetProperty("model").GetProperty("bundled").GetBoolean())
            throw new InvalidDataException("Release/model identity differs from the requested package.");
        if (!root.GetProperty("packages").EnumerateArray().Any(value => value.GetProperty("name").GetString() == "Microsoft.DiskANN"))
            throw new InvalidDataException("The native component inventory is incomplete.");
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in root.GetProperty("files").EnumerateArray())
        {
            string name = file.GetProperty("path").GetString()!;
            if (!listed.Add(name) || !entries.TryGetValue(name, out var entry) || entry.Length != file.GetProperty("bytes").GetInt64())
                throw new InvalidDataException("Release inventory does not match its payload: " + name);
            using (var input = entry.Open())
                if (!Convert.ToHexString(SHA256.HashData(input)).Equals(file.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Release file checksum mismatch: " + name);
            if (!name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            using var bytes = new MemoryStream();
            using (var input = entry.Open()) input.CopyTo(bytes);
            bytes.Position = 0;
            using var pe = new PEReader(bytes);
            ushort actual = (ushort)pe.PEHeaders.CoffHeader.Machine;
            bool neutral = actual == 0x14c && pe.PEHeaders.CorHeader is { } clr &&
                (clr.Flags & CorFlags.Requires32Bit) == 0 && (clr.Flags & CorFlags.ILOnly) != 0;
            ushort expected = name.StartsWith("engine/", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("extractor/", StringComparison.OrdinalIgnoreCase) || name.StartsWith("tools/", StringComparison.OrdinalIgnoreCase) ? (ushort)0x8664 : machine;
            if (!neutral && actual != expected) throw new InvalidDataException("Wrong PE architecture: " + name);
        }
        if (entries.Values.Count(entry => entry.Name.Length > 0 && entry.FullName != "release-manifest.json") != listed.Count)
            throw new InvalidDataException("The release has unrecorded files.");
        Console.WriteLine($"Verified {runtime} {version}: {listed.Count} checksummed files, architecture boundaries and interpreter-free contents.");
    }
    private static JsonDocument ReadJson(ZipArchiveEntry entry)
    {
        if (entry.Length > 16 * 1024 * 1024) throw new InvalidDataException("Oversized package manifest.");
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8, true);
        return JsonDocument.Parse(reader.ReadToEnd());
    }
    public static async Task SmokeAsync(string archive, string runtime, CancellationToken cancellation)
    {
        string root = Path.Combine(Path.GetTempPath(), "ffa relocated-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            ZipFile.ExtractToDirectory(archive, root);
            await RunAsync(Path.Combine(root, "tools", "FuzzyFindAnything.Tools.exe"), ["native-smoke"], true, cancellation);
            await RunAsync(Path.Combine(root, "tools", "FuzzyFindAnything.Tools.exe"),
                ["verify-model", "--destination", Path.Combine(root, "missing-model")], false, cancellation);
            if (runtime == "win-x64" || RuntimeInformation.OSArchitecture == Architecture.Arm64)
            {
                await RunAsync(Path.Combine(root, "inference", "FuzzyFindAnything.Inference.exe"), ["--self-test"], true, cancellation);
                await RunAsync(Path.Combine(root, "hf-inference", "FuzzyFindAnything.HuggingFaceInference.exe"), ["--devices"], true, cancellation);
            }
            else
                Console.WriteLine("ARM64 inference execution requires an ARM64 host; this host checked its PE layout only.");
            if (Directory.Exists(Path.Combine(root, "missing-model"))) throw new InvalidDataException("Verification created an empty model directory.");
            Console.WriteLine("Relocated native runtime smoke passed with Python absent from PATH.");
        }
        finally
        {
            for (int attempt = 0; Directory.Exists(root); attempt++)
            {
                try { Directory.Delete(root, true); }
                catch (Exception error) when (attempt < 60 && error is IOException or UnauthorizedAccessException)
                { await Task.Delay(500, CancellationToken.None); }
            }
        }
    }
    private static async Task RunAsync(string executable, string[] arguments, bool success, CancellationToken cancellation)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetTempPath()
        };
        start.Environment["PATH"] = Environment.GetFolderPath(Environment.SpecialFolder.System);
        start.Environment.Remove("PYTHONHOME"); start.Environment.Remove("PYTHONPATH");
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Native smoke host failed to start.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(), errors = process.StandardError.ReadToEndAsync();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(); await Task.WhenAll(output, errors);
            throw;
        }
        string text = await output, error = await errors;
        if ((process.ExitCode == 0) != success || !success && !error.Contains("missing or incomplete", StringComparison.Ordinal))
            throw new IOException($"Relocated runtime probe failed: {Path.GetFileName(executable)}: {text} {error}");
        Console.Write(text);
    }
}
