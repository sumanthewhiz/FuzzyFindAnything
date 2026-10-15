using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using FuzzyFindAnything.Core;

namespace FuzzyFindAnything.NativeTests;

internal static class AssetTests
{
    public static async Task RunAsync()
    {
        await AcquisitionAsync();
        Program.Run("XML logging, outcomes, rotation and visible access failures", Logging);
    }
    private static async Task AcquisitionAsync() => await Check.TemporaryAsync(async root =>
    {
        byte[] content = Encoding.UTF8.GetBytes("validated native model fixture");
        var asset = new ModelAssetFile("test.data", content.Length, Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant());
        string source = Path.Combine(root, "source"), destination = Path.Combine(root, "target");
        Directory.CreateDirectory(source);
        await File.WriteAllBytesAsync(Path.Combine(source, asset.Name), content);
        await ModelAssets.AcquireFilesAsync([asset], destination, source, null);
        string target = Path.Combine(destination, asset.Name);
        Program.Require(File.ReadAllBytes(target).SequenceEqual(content), "Local model acquisition changed bytes.");
        var timestamp = File.GetLastWriteTimeUtc(target);
        await ModelAssets.AcquireFilesAsync([asset], destination, source, null);
        Program.Require(File.GetLastWriteTimeUtc(target) == timestamp, "Verified assets were rewritten.");
        string missing = Path.Combine(root, "missing");
        Check.Throws<FileNotFoundException>(() => ModelAssets.Verify(missing));
        Program.Require(!Directory.Exists(missing), "Read-only verification created files.");
        File.Delete(target);
        await File.WriteAllBytesAsync(target + ".partial", content[..7]);
        await using (var server = new Server(request =>
        {
            Program.Require(request.Contains("GET /model/test.data?token=fixture HTTP/1.1") &&
                request.Contains("Range: bytes=7-"), "Download URI/query or resume offset was changed.");
            return ("206 Partial Content", $"Content-Range: bytes 7-{content.Length - 1}/{content.Length}\r\n", content[7..]);
        }))
        {
            await ModelAssets.AcquireFilesAsync([asset], destination, null, new Uri(server.BaseUri, "model?token=fixture"));
            await server.Completion;
        }
        Program.Require(File.ReadAllBytes(target).SequenceEqual(content) && !File.Exists(target + ".partial"), "Resumed asset was not atomically promoted.");
        File.Delete(target); await File.WriteAllBytesAsync(target + ".partial", content[..5]);
        await using (var server = new Server(_ => ("200 OK", "", content)))
            await ModelAssets.AcquireFilesAsync([asset], destination, null, server.BaseUri);
        Program.Require(File.ReadAllBytes(target).SequenceEqual(content), "A server that ignores Range duplicated partial data.");
        File.Delete(target); File.Delete(target + ".partial");
        await using (var server = new Server(_ => ("200 OK", "", new byte[content.Length + 1])))
            await Check.ThrowsAsync<InvalidDataException>(() => ModelAssets.AcquireFilesAsync([asset], destination, null, server.BaseUri));
        Program.Require(!File.Exists(target), "An oversized asset was published.");
        File.Delete(target + ".partial");
        await using (var server = new Server(_ => ("200 OK", "", new byte[content.Length])))
            await Check.ThrowsAsync<InvalidDataException>(() => ModelAssets.AcquireFilesAsync([asset], destination, null, server.BaseUri));
        Program.Require(!File.Exists(target) && !File.Exists(target + ".partial"), "A corrupt asset was retained as a valid download.");
        await File.WriteAllBytesAsync(target, content.Select(value => (byte)(value ^ 1)).ToArray());
        byte[] previous = File.ReadAllBytes(target);
        using (var lease = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
            await Check.ThrowsAsync<IOException>(() => ModelAssets.AcquireFilesAsync([asset], destination, source, null));
        Program.Require(File.ReadAllBytes(target).SequenceEqual(previous), "An active model asset was overwritten.");
        await Check.ThrowsAsync<ArgumentException>(() => ModelAssets.AcquireFilesAsync([asset], destination, null, new Uri("http://example.com/model")));
        Console.WriteLine("PASS acquisition hashes, read-only checks, resumable HTTPS layout, bounds and active-file safety");
    });
    private static void Configuration(string root, bool enabled, string directory) =>
        new XDocument(new XElement("FuzzyFindAnythingConfiguration", new XAttribute("version", "1"),
            new XElement("Logging", new XAttribute("enabled", enabled), new XAttribute("directory", directory))))
            .Save(Path.Combine(root, "settings.xml"));
    private static void Logging() => Program.Temporary(root =>
    {
        var logs = new EngineLog(root);
        Program.Require(logs.State.Enabled, "Logging was not enabled by default.");
        var job = new IndexJob(1, Path.Combine(root, "source.py"), "source", new(0, 0, "1"), "", "Code", 2, true, 0, 0);
        logs.RecordFile(job, "Failed", 12.5, failure: new ContentException("Bad source", "invalid_document"), code: "invalid_document");
        string file = Path.Combine(logs.State.Directory, "indexer.jsonl");
        using (var result = JsonDocument.Parse(File.ReadAllLines(file).Single()))
        {
            Program.Require(result.RootElement.GetProperty("path").GetString() == job.Path &&
                result.RootElement.GetProperty("error").GetProperty("code").GetString() == "invalid_document" &&
                result.RootElement.GetProperty("duration_ms").GetDouble() == 12.5, "File outcomes lost path, duration or classified errors.");
        }
        byte[] original = File.ReadAllBytes(file);
        Configuration(root, false, logs.State.Directory); logs.Reload();
        logs.RecordFile(job, "Succeeded", 1);
        Program.Require(original.SequenceEqual(File.ReadAllBytes(file)), "Disabled logging continued writing.");
        string changed = Path.Combine(root, "\u7d22\u5f15 & logs");
        Configuration(root, true, changed); logs.Reload(); logs.RecordFile(job, "Succeeded", 3);
        Program.Require(File.Exists(Path.Combine(changed, "indexer.jsonl")) && logs.Owns(Path.Combine(changed, "indexer.jsonl.1")) &&
            !logs.Owns(Path.Combine(changed, "notes.txt")), "Log relocation excluded unrelated documents or lost outcomes.");
        string invalid = Path.Combine(root, "not-directory"); File.WriteAllText(invalid, "file");
        Configuration(root, true, invalid);
        Check.Throws<IOException>(logs.Reload);
        Program.Require(logs.State.Directory == changed, "Failed configuration did not retain the active log destination.");
        Configuration(root, true, changed); logs.Reload();
        for (int index = 0; index < 5; index++)
        {
            using (var stream = new FileStream(Path.Combine(changed, "engine.log"), FileMode.Create, FileAccess.Write)) stream.SetLength(4_000_001);
            logs.Write("rotation", "entry " + index);
        }
        Program.Require(Directory.GetFiles(changed, "engine.log*").Length == 4 &&
            File.ReadAllText(Path.Combine(changed, "engine.log")).Contains("entry 4"), "Diagnostic rotation did not bound history.");
        using (var locked = new FileStream(Path.Combine(changed, "engine.log"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            logs.Write("failure", "visible error");
        Program.Require(logs.State.Error.Length > 0, "Log access failure was hidden.");
        File.WriteAllText(Path.Combine(root, "settings.xml"), "<!DOCTYPE x [<!ENTITY x 'bad'>]><FuzzyFindAnythingConfiguration version='1'/>");
        Check.Throws<XmlException>(() => new EngineLog(root));
        File.WriteAllText(Path.Combine(root, "settings.xml"), "<FuzzyFindAnythingConfiguration version='1'><Logging directory='relative'/></FuzzyFindAnythingConfiguration>");
        Check.Throws<InvalidDataException>(() => new EngineLog(root));
    });
    private sealed class Server : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(10));
        public Uri BaseUri { get; }
        public Task Completion { get; }
        public Server(Func<string, (string Status, string Headers, byte[] Body)> response)
        {
            listener.Start();
            BaseUri = new($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");
            Completion = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync(lifetime.Token);
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, leaveOpen: true);
                var request = new StringBuilder();
                string? line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(lifetime.Token))) request.AppendLine(line);
                var value = response(request.ToString());
                byte[] header = Encoding.ASCII.GetBytes($"HTTP/1.1 {value.Status}\r\nContent-Length: {value.Body.Length}\r\n{value.Headers}Connection: close\r\n\r\n");
                await stream.WriteAsync(header, lifetime.Token); await stream.WriteAsync(value.Body, lifetime.Token);
            });
        }
        public async ValueTask DisposeAsync()
        {
            try { await Completion; }
            finally { lifetime.Cancel(); listener.Stop(); lifetime.Dispose(); }
        }
    }
}
