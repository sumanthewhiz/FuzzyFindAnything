using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FuzzyFindAnything.Core;
using FuzzyFindAnything.Tools;

namespace FuzzyFindAnything.NativeTests;

internal static class ReleaseTests
{
    private const string Version = "2.0.0";
    private static readonly string[] Names =
    [
        "FuzzyFindAnything.exe", "FuzzyFindAnything.dll", "FuzzyFindAnything.runtimeconfig.json",
        "Get-Model.ps1", "Reset-Index.ps1", "QUICKSTART.md", "THIRD-PARTY-NOTICES.txt",
        "LICENSE", "APPLICATION-LICENSE", "CONTRIBUTING.md", "SECURITY.md", "THIRD-PARTY-NOTICES.md",
        "engine/FuzzyFindAnything.Engine.exe", "engine/ffa_diskann.dll", "engine/libiomp5md.dll", "engine/e_sqlite3.dll",
        "inference/FuzzyFindAnything.Inference.exe", "inference/onnxruntime.dll",
        "hf-inference/FuzzyFindAnything.HuggingFaceInference.exe", "hf-inference/onnxruntime.dll", "hf-inference/DirectML.dll",
        "extractor/FuzzyFindAnything.Extractor.exe", "tools/FuzzyFindAnything.Tools.exe", "tools/ffa_diskann.dll",
        "engine/msvcp140.dll", "engine/vcruntime140.dll", "inference/msvcp140.dll", "inference/vcruntime140.dll"
    ];
    public static void Run() => Program.Run("Release payload, relocation paths, checksums and PE boundaries", () => Program.Temporary(root =>
    {
        string archive = Path.Combine(root, "package.zip");
        Create(archive);
        ReleasePackage.Validate(archive, "win-arm64", Version);
        foreach (string forbidden in new[] { "../outside.txt", "models/language.onnx", "MODELS/weights.txt", ".runtime/python.exe",
            "module.py", "python311.dll", "settings.xml", "folder\\escape.txt", "folder/../escape", "folder//alias.txt",
            "provider-settings.json", "provider.json", "provider-model.json", "ffa_provider_bridge.dll",
            "model.quant.onnxe", "internal-sdk.nupkg", "FuzzyFindAnything.MicrosoftProvider.exe" })
        {
            Create(archive, payload => payload[forbidden] = [1]);
            Check.Throws<InvalidDataException>(() => ReleasePackage.Validate(archive, "win-arm64", Version));
        }
        Create(archive, payload => payload["runtime.json"] = JsonSerializer.SerializeToUtf8Bytes(new
        {
            engine = @"C:\build-machine\engine.exe",
            inference = ReleasePackage.RuntimePaths["inference"],
            model = ReleasePackage.RuntimePaths["model"],
            extractor = ReleasePackage.RuntimePaths["extractor"]
        }));
        Check.Throws<InvalidDataException>(() => ReleasePackage.Validate(archive, "win-arm64", Version));
        Create(archive, payload => payload["engine/ffa_diskann.dll"] = Pe(0xAA64));
        Check.Throws<InvalidDataException>(() => ReleasePackage.Validate(archive, "win-arm64", Version));
        Create(archive, payload => payload.Remove("inference/onnxruntime.dll"));
        Check.Throws<InvalidDataException>(() => ReleasePackage.Validate(archive, "win-arm64", Version));
        Create(archive, payload => payload.Remove("APPLICATION-LICENSE"));
        Check.Throws<InvalidDataException>(() => ReleasePackage.Validate(archive, "win-arm64", Version));
        Create(archive, payload => payload.Remove("APPLICATION-LICENSE"), version: "1.1.0");
        ReleasePackage.Validate(archive, "win-arm64", "1.1.0");
        Create(archive, null, tamper: true);
        Check.Throws<InvalidDataException>(() => ReleasePackage.Validate(archive, "win-arm64", Version));
    }));
    private static byte[] Pe(ushort machine)
    {
        byte[] bytes = new byte[512];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, 0x5A4D);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(60), 128);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(128), 0x4550);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(132), machine);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(148), 240);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(150), 0x2022);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(152), 0x20B);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(184), 4096);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(188), 512);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(208), 4096);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(212), 512);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(220), 3);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(260), 16);
        return bytes;
    }
    private static void Create(string path, Action<Dictionary<string, byte[]>>? mutate = null, bool tamper = false, string version = Version)
    {
        var payload = Names.ToDictionary(name => name, name =>
            name.EndsWith(".exe") || name.EndsWith(".dll") ? Pe(name.StartsWith("engine/") || name.StartsWith("extractor/") ||
                name.StartsWith("tools/") ? (ushort)0x8664 : (ushort)0xAA64) : Encoding.UTF8.GetBytes("synthetic test"));
        payload["runtime.json"] = JsonSerializer.SerializeToUtf8Bytes(ReleasePackage.RuntimePaths);
        payload["model-manifest.json"] = JsonSerializer.SerializeToUtf8Bytes(ModelAssets.Manifest, Protocol.Json);
        mutate?.Invoke(payload);
        payload["release-manifest.json"] = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema_version = 2,
            runtime_id = "win-arm64",
            version,
            model = new { bundled = false },
            packages = new[] { new { name = "Microsoft.DiskANN" } },
            files = payload.Select(pair => new
            {
                path = pair.Key,
                bytes = pair.Value.Length,
                sha256 = Convert.ToHexString(SHA256.HashData(pair.Value)).ToLowerInvariant()
            }).ToArray()
        });
        if (tamper) payload["QUICKSTART.md"] = Encoding.UTF8.GetBytes("tampered bytes!");
        File.Delete(path);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var pair in payload)
        {
            using var stream = archive.CreateEntry(pair.Key).Open();
            stream.Write(pair.Value);
        }
    }
}
