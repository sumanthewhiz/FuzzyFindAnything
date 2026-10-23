using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FuzzyFindAnything.Core;

internal sealed record ProviderPackageFile(string Path, long Bytes, string Sha256);
internal sealed record ProviderPackage(string Id, string Runtime, string Version, string DefaultVersion,
    int ProtocolVersion, string Executable, ProviderPackageFile[] Files);

internal static class ProviderBootstrap
{
    internal const string Repository = "experiences-and-devices/APT";
    internal const string Version = "1.5.0-internal.3";
    internal const string ReleaseTag = "ffa-microsoft-provider-v" + Version;
    private const long RepositoryId = 1146158066;
    private static readonly SemaphoreSlim installing = new(1, 1);

    internal static async Task<bool> InstallAsync(string registrationRoot, Action<string> log, CancellationToken cancellation)
    {
        string root = Path.Combine(registrationRoot, "microsoft"), registration = Path.Combine(root, "provider.json");
        await installing.WaitAsync(cancellation);
        string? staging = null, temporary = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            if (File.Exists(registration)) return true;
            string? metadata = await GitHubAsync(["api", "repos/" + Repository], log, deadline.Token);
            if (metadata is null) return false;
            using (var repository = JsonDocument.Parse(metadata))
                ValidateRepository(repository.RootElement);
            string? selected = await GitHubAsync(["api", "repos/" + Repository + "/releases/tags/" + ReleaseTag], log, deadline.Token);
            if (selected is null) return false;
            using var release = JsonDocument.Parse(selected);
            ValidateRelease(release.RootElement);
            string runtime = RuntimeInformation.OSArchitecture switch
            {
                Architecture.Arm64 => "win-arm64",
                Architecture.X64 => "win-x64",
                _ => throw new PlatformNotSupportedException("The local provider requires Windows ARM64 or x64.")
            };
            string name = "FuzzyFindAnything-MicrosoftProvider-" + runtime + ".zip";
            var assets = release.RootElement.GetProperty("assets").EnumerateArray().ToArray();
            var archive = assets.SingleOrDefault(asset => asset.GetProperty("name").GetString() == name);
            var checksum = assets.SingleOrDefault(asset => asset.GetProperty("name").GetString() == name + ".sha256");
            if (archive.ValueKind == JsonValueKind.Undefined || checksum.ValueKind == JsonValueKind.Undefined)
                throw new InvalidDataException("The private release has no complete package for this architecture.");
            long size = archive.GetProperty("size").GetInt64();
            if (size is < 1 or > 1_073_741_824 || checksum.GetProperty("size").GetInt64() is < 64 or > 4096)
                throw new InvalidDataException("The optional provider package exceeds its download bounds.");
            staging = Path.Combine(root, "install-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            log("Acquiring an authorized optional model provider from its private release channel.");
            if (await GitHubAsync(["release", "download", ReleaseTag, "--repo", Repository, "--dir", staging,
                "--pattern", name, "--pattern", name + ".sha256"], log, deadline.Token) is null) return false;
            string zip = Path.Combine(staging, name);
            string checksumPath = Path.Combine(staging, name + ".sha256");
            if (new FileInfo(checksumPath).Length > 4096) throw new InvalidDataException("The package checksum file exceeds its bound.");
            string hash = File.ReadAllText(checksumPath).Split((char[]?)null,
                StringSplitOptions.RemoveEmptyEntries)[0];
            if (new FileInfo(zip).Length != size)
                throw new InvalidDataException("The optional provider archive failed its size/checksum check.");
            ValidateArchive(runtime, hash, FilePaths.Hash(zip));
            string extracted = Path.Combine(staging, "payload");
            var package = Extract(zip, extracted, runtime);
            if (package.Version != Version) throw new InvalidDataException("The companion package does not match the approved release version.");
            string destination = Path.Combine(root, "packages", runtime + "-" + hash[..16].ToLowerInvariant());
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            try { Directory.Move(extracted, destination); }
            catch (IOException) when (Directory.Exists(destination)) { ValidateFiles(destination, package); }
            temporary = Path.Combine(root, "provider-" + Guid.NewGuid().ToString("N") + ".tmp");
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new ProviderRegistration("microsoft",
                Path.Combine(destination, package.Executable), package.DefaultVersion, package.ProtocolVersion), Protocol.Json), deadline.Token);
            try { File.Move(temporary, registration); }
            catch (IOException) when (File.Exists(registration)) { log("Retained a provider registration created concurrently by another process."); }
            log("The optional model provider is installed. Registry access will be checked before offering its models.");
            return true;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            log("Optional provider discovery/download timed out; Hugging Face remains available. Refresh model sources to retry.");
            return false;
        }
        finally
        {
            if (temporary is not null) File.Delete(temporary);
            if (staging is not null && Directory.Exists(staging)) Directory.Delete(staging, true);
            installing.Release();
        }
    }

    internal static void ValidateRepository(JsonElement repository)
    {
        if (repository.GetProperty("id").GetInt64() != RepositoryId ||
            !repository.GetProperty("private").GetBoolean() ||
            repository.GetProperty("visibility").GetString() != "internal")
            throw new InvalidDataException("The optional provider channel is not the approved enterprise-internal repository.");
    }

    internal static void ValidateRelease(JsonElement release)
    {
        if (release.GetProperty("tag_name").GetString() != ReleaseTag || release.GetProperty("draft").GetBoolean())
            throw new InvalidDataException("The optional provider release is not the approved published companion package.");
    }

    internal static void ValidateArchive(string runtime, string advertised, string actual)
    {
        string expected = runtime switch
        {
            "win-arm64" => "e3be8e5cdce55c21a9b6027b4abbbfac4077c0899d9b3c609b4d1b6621b27ec7",
            "win-x64" => "7920da89da958e1c919c2160f04170cd8a16c96722b6de368dc7fcfb313967b0",
            _ => throw new InvalidDataException("The companion package targets an unsupported architecture.")
        };
        if (!expected.Equals(advertised, StringComparison.OrdinalIgnoreCase) ||
            !expected.Equals(actual, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The companion archive does not match the application's approved package checksum.");
    }

    private static async Task<string?> GitHubAsync(string[] arguments, Action<string> log, CancellationToken cancellation)
    {
        var start = new ProcessStartInfo("gh")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        start.Environment["GH_HOST"] = "github.com";
        start.Environment["GH_PROMPT_DISABLED"] = "1";
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        Process process;
        try { process = Process.Start(start) ?? throw new IOException("GitHub CLI could not start."); }
        catch (Win32Exception error) when (error.NativeErrorCode == 2)
        {
            log("Optional private-provider discovery requires an installed, authenticated GitHub CLI; no provider was installed.");
            return null;
        }
        using (process)
        {
            var output = process.StandardOutput.ReadToEndAsync(cancellation);
            var errors = process.StandardError.ReadToEndAsync(cancellation);
            try
            {
                await process.WaitForExitAsync(cancellation);
                string result = await output;
                _ = await errors;
                if (process.ExitCode != 0)
                {
                    log($"The authenticated private provider channel is unavailable (GitHub CLI exit {process.ExitCode}); no provider was installed.");
                    return null;
                }
                if (result.Length > 4_194_304) throw new InvalidDataException("The private release response exceeds its bound.");
                return result;
            }
            finally
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
                await Task.WhenAll(output, errors);
            }
        }
    }

    private static string Relative(string path)
    {
        if (path.Length == 0 || path.Contains('\\') || path.Contains(':') || path.Any(char.IsControl) ||
            path.Split('/').Any(part => part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
                part.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0))
            throw new InvalidDataException("The provider archive contains an unsafe path.");
        return path.Replace('/', Path.DirectorySeparatorChar);
    }

    internal static ProviderPackage Extract(string archive, string destination, string runtime)
    {
        using var zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count > 10000 || zip.Entries.Sum(entry => entry.Length) > 2_147_483_648L)
            throw new InvalidDataException("The expanded provider package exceeds its bound.");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            string name = Relative(entry.FullName.TrimEnd('/'));
            if ((entry.ExternalAttributes >> 16 & 0xf000) == 0xa000 || !entries.TryAdd(name, entry))
                throw new InvalidDataException("The provider package contains a link or duplicate entry.");
        }
        if (!entries.TryGetValue("provider-package.json", out var manifest) || manifest.Length > 4_194_304)
            throw new InvalidDataException("The provider package manifest is missing or exceeds its bound.");
        ProviderPackage package;
        using (var input = manifest.Open())
            package = JsonSerializer.Deserialize<ProviderPackage>(input, Protocol.Json)
                ?? throw new InvalidDataException("The provider package manifest is empty.");
        if (package.Id != "microsoft" || package.Runtime != runtime || package.ProtocolVersion != 2 ||
            package.Executable != "FuzzyFindAnything.MicrosoftProvider.exe" || package.Files is not { Length: > 0 and < 10000 })
            throw new InvalidDataException("The provider package has an incompatible identity or execution contract.");
        _ = ModelReference.Parse($"provider://microsoft/{package.DefaultVersion}/model");
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in package.Files)
        {
            string name = Relative(file.Path);
            if (!listed.Add(name) || !entries.TryGetValue(name, out var entry) || entry.Length != file.Bytes ||
                !Regex.IsMatch(file.Sha256, "^[a-fA-F0-9]{64}$"))
                throw new InvalidDataException("The provider inventory does not match its archive.");
        }
        if (entries.Values.Count(entry => entry.Name.Length > 0 && entry.FullName != "provider-package.json") != listed.Count)
            throw new InvalidDataException("The provider package contains unrecorded files.");
        Directory.CreateDirectory(destination);
        foreach (var (name, entry) in entries)
        {
            string path = Path.Combine(destination, name);
            if (entry.Name.Length == 0) Directory.CreateDirectory(path);
            else { Directory.CreateDirectory(Path.GetDirectoryName(path)!); entry.ExtractToFile(path); }
        }
        ValidateFiles(destination, package);
        return package;
    }

    private static void ValidateFiles(string directory, ProviderPackage package)
    {
        foreach (var file in package.Files)
        {
            string path = Path.Combine(directory, Relative(file.Path));
            if (!File.Exists(path) || new FileInfo(path).Length != file.Bytes ||
                !FilePaths.Hash(path).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The installed provider failed its file-integrity check.");
            if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            ushort actual = (ushort)pe.PEHeaders.CoffHeader.Machine;
            bool neutral = actual == 0x14c && pe.PEHeaders.CorHeader is { } clr &&
                (clr.Flags & CorFlags.Requires32Bit) == 0 && (clr.Flags & CorFlags.ILOnly) != 0;
            ushort expected = package.Runtime == "win-arm64" ? (ushort)0xaa64 : (ushort)0x8664;
            if (!neutral && actual != expected) throw new InvalidDataException("The provider contains a binary for a different architecture.");
        }
        if (!package.Files.Any(file => file.Path == package.Executable))
            throw new InvalidDataException("The provider executable is absent from its inventory.");
    }
}
