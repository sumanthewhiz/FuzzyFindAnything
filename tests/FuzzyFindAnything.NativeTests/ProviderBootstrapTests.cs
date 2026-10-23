using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using FuzzyFindAnything.Core;

namespace FuzzyFindAnything.NativeTests;

internal static class ProviderBootstrapTests
{
    public static void Run() => Program.Temporary(root =>
    {
        const string executable = "FuzzyFindAnything.MicrosoftProvider.exe";
        string current = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
        string other = current == "win-arm64" ? "win-x64" : "win-arm64";
        byte[] bytes = File.ReadAllBytes(Check.Executable);
        var inventory = new ProviderPackageFile(executable, bytes.Length, FilePaths.Hash(Check.Executable));
        string Archive(string? runtime = null, bool corrupt = false, string? extra = null)
        {
            string path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".zip");
            using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
            using (var output = archive.CreateEntry(executable).Open())
            {
                byte[] payload = (byte[])bytes.Clone();
                if (corrupt) payload[^1] ^= 1;
                output.Write(payload);
            }
            using (var output = archive.CreateEntry("provider-package.json").Open())
                JsonSerializer.Serialize(output, new ProviderPackage("microsoft", runtime ?? current, "1.0.0", "1.2.3.4", 2, executable, [inventory]), Protocol.Json);
            if (extra is not null)
                using (var output = new StreamWriter(archive.CreateEntry(extra).Open())) output.Write("unlisted");
            return path;
        }
        string Destination() => Path.Combine(root, Guid.NewGuid().ToString("N"));
        string valid = Destination();
        var package = ProviderBootstrap.Extract(Archive(), valid, current);
        Program.Require(package.ProtocolVersion == 2 && FilePaths.Hash(Path.Combine(valid, executable)) == inventory.Sha256,
            "A valid private provider package did not retain its verified executable.");
        Check.Throws<InvalidDataException>(() => ProviderBootstrap.Extract(Archive(corrupt: true), Destination(), current));
        Check.Throws<InvalidDataException>(() => ProviderBootstrap.Extract(Archive(extra: "../outside"), Destination(), current));
        Check.Throws<InvalidDataException>(() => ProviderBootstrap.Extract(Archive(extra: "unlisted.dll"), Destination(), current));
        Check.Throws<InvalidDataException>(() => ProviderBootstrap.Extract(Archive(other), Destination(), other));
        Program.Require(!File.Exists(Path.Combine(root, "outside")), "Archive traversal escaped the installation directory.");
        Console.WriteLine("PASS private provider inventory, checksum, traversal and PE architecture boundaries");
    });

    public static async Task InstallAsync(string directory)
    {
        if (Directory.Exists(directory)) throw new ArgumentException("Choose a new isolated provider installation directory.");
        Program.Require(await ProviderBootstrap.InstallAsync(directory, Console.WriteLine, default),
            "The authorized private provider release was not installed.");
        var provider = new ExternalModels(Path.Combine(directory, "model-cache"), Console.WriteLine, directory);
        var catalog = await provider.CatalogAsync("microsoft", null, false, default);
        Program.Require(catalog.InternalAccessVerified && catalog.Models.Any(model => model.State == "Available") &&
            provider.Registration("microsoft").ProtocolVersion == 2, "The relocated private companion did not pass live registry discovery.");
        Console.WriteLine("PASS authenticated private release bootstrap, relocated execution and registry-gated availability");
    }
}
