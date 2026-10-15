using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.Json;

namespace FuzzyFindAnything.Core;

public sealed record ModelAssetFile(string Name, long Bytes, string Sha256);
public sealed record ModelAssetManifest(string AssetId, string SourceModel, string Revision, int Dimensions,
    string Precision, string OnnxRuntime, int Opset, ModelAssetFile[] Files);

public static class ModelAssets
{
    public static ModelAssetManifest Manifest { get; } = ReadManifest();
    private static ModelAssetManifest ReadManifest()
    {
        using var stream = typeof(ModelAssets).Assembly.GetManifestResourceStream("FuzzyFindAnything.NativeModel.json")
            ?? throw new InvalidDataException("The native model manifest is missing.");
        return JsonSerializer.Deserialize<ModelAssetManifest>(stream, Protocol.Json)
            ?? throw new InvalidDataException("The native model manifest is invalid.");
    }

    public static void Verify(string directory, Action<int, int, string>? progress = null, CancellationToken cancellation = default)
    {
        for (int index = 0; index < Manifest.Files.Length; index++)
        {
            cancellation.ThrowIfCancellationRequested();
            var file = Manifest.Files[index];
            string path = Path.Combine(directory, file.Name);
            if (!File.Exists(path) || new FileInfo(path).Length != file.Bytes)
                throw new FileNotFoundException($"Native model asset is missing or incomplete: {file.Name}. Run Get-Model.ps1.", path);
            if (Hash(path, cancellation) != file.Sha256)
                throw new InvalidDataException($"Native model asset checksum mismatch: {file.Name}.");
            progress?.Invoke(index + 1, Manifest.Files.Length, file.Name);
        }
    }

    public static IDisposable LockForInference(string directory)
    {
        var files = new List<FileStream>();
        try
        {
            foreach (var file in Manifest.Files)
                files.Add(new FileStream(Path.Combine(directory, file.Name), FileMode.Open, FileAccess.Read, FileShare.Read));
            return new AssetLease(files);
        }
        catch
        {
            foreach (var file in files) file.Dispose();
            throw;
        }
    }

    private sealed class AssetLease(List<FileStream> files) : IDisposable
    {
        public void Dispose() { foreach (var file in files) file.Dispose(); }
    }

    private static string Hash(string path, CancellationToken cancellation)
    {
        using var stream = FilePaths.OpenRead(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[1024 * 1024];
        int count;
        while ((count = stream.Read(buffer)) != 0)
        {
            cancellation.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, count);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public static async Task AcquireAsync(string destination, string? sourceDirectory, Uri? baseUri,
        Action<string>? report = null, CancellationToken cancellation = default)
        => await AcquireFilesAsync(Manifest.Files, destination, sourceDirectory, baseUri, report, cancellation);

    internal static async Task AcquireFilesAsync(IReadOnlyList<ModelAssetFile> files, string destination,
        string? sourceDirectory, Uri? baseUri, Action<string>? report = null, CancellationToken cancellation = default)
    {
        if ((sourceDirectory is null) == (baseUri is null))
            throw new ArgumentException("Specify exactly one approved source directory or model base URI.");
        if (baseUri is not null && (!baseUri.IsAbsoluteUri || baseUri.UserInfo.Length != 0 || baseUri.Fragment.Length != 0 ||
            (baseUri.Scheme != "https" && !(baseUri.Scheme == "http" && baseUri.IsLoopback))))
            throw new ArgumentException("Model downloads require HTTPS (HTTP is permitted only for loopback testing).");
        if (files.Any(file => Path.GetFileName(file.Name) != file.Name || file.Bytes < 0 ||
            file.Sha256.Length != 64 || file.Sha256.Any(value => !char.IsAsciiHexDigit(value))))
            throw new InvalidDataException("Invalid model asset manifest.");
        Directory.CreateDirectory(destination);
        using var lease = new FileStream(Path.Combine(destination, ".acquisition.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        using var handler = new HttpClientHandler { SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        foreach (var file in files)
        {
            cancellation.ThrowIfCancellationRequested();
            string target = Path.Combine(destination, file.Name);
            if (File.Exists(target) && new FileInfo(target).Length == file.Bytes && Hash(target, cancellation) == file.Sha256)
            {
                report?.Invoke($"Verified {file.Name}");
                continue;
            }
            string partial = target + ".partial";
            if (sourceDirectory is not null)
            {
                string source = Path.Combine(sourceDirectory, file.Name);
                if (!File.Exists(source) || new FileInfo(source).Length != file.Bytes)
                    throw new FileNotFoundException("The model source file is missing or incomplete.", source);
                await using var input = FilePaths.OpenRead(source);
                await using var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None);
                await CopyBoundedAsync(input, output, file.Bytes, cancellation);
                output.Flush(true);
            }
            else
            {
                long offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
                if (offset == file.Bytes && Hash(partial, cancellation) == file.Sha256)
                {
                    Promote(partial, target);
                    report?.Invoke($"Acquired {file.Name}");
                    continue;
                }
                if (offset >= file.Bytes) { File.Delete(partial); offset = 0; }
                var uri = new UriBuilder(baseUri!)
                {
                    Path = baseUri!.AbsolutePath.TrimEnd('/') + "/" + Uri.EscapeDataString(file.Name)
                };
                using var request = new HttpRequestMessage(HttpMethod.Get, uri.Uri);
                if (offset > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(offset, null);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                deadline.CancelAfter(TimeSpan.FromMinutes(30));
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                response.EnsureSuccessStatusCode();
                bool ranged = response.StatusCode == System.Net.HttpStatusCode.PartialContent;
                if (ranged && (response.Content.Headers.ContentRange is not { } range ||
                    range.From != offset || range.Length != file.Bytes || range.To != file.Bytes - 1))
                    throw new InvalidDataException("The model server returned an inconsistent resume range.");
                if (!ranged && response.StatusCode != System.Net.HttpStatusCode.OK)
                    throw new InvalidDataException("The model server returned no asset content.");
                bool append = ranged && offset > 0;
                long expected = file.Bytes - (append ? offset : 0);
                if (response.Content.Headers.ContentLength is { } length && length != expected)
                    throw new InvalidDataException("The model server returned an unexpected asset size.");
                await using var input = await response.Content.ReadAsStreamAsync(deadline.Token);
                await using var output = new FileStream(partial, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None);
                await CopyBoundedAsync(input, output, expected, deadline.Token);
                output.Flush(true);
            }
            if (new FileInfo(partial).Length != file.Bytes || Hash(partial, cancellation) != file.Sha256)
            {
                File.Delete(partial);
                throw new InvalidDataException($"Downloaded model asset failed verification: {file.Name}.");
            }
            Promote(partial, target);
            report?.Invoke($"Acquired {file.Name}");
        }
    }

    private static void Promote(string partial, string target)
    {
        try { File.Move(partial, target, overwrite: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"Cannot replace {Path.GetFileName(target)}. Quit applications using this model and verify folder permissions. " +
                "The existing asset and verified partial download were retained.", error);
        }
    }

    private static async Task CopyBoundedAsync(Stream input, Stream output, long expected, CancellationToken cancellation)
    {
        byte[] buffer = new byte[1024 * 1024];
        long copied = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, cancellation)) != 0)
        {
            if (count > expected - copied)
                throw new InvalidDataException("The model asset exceeded its manifest size.");
            await output.WriteAsync(buffer.AsMemory(0, count), cancellation);
            copied += count;
        }
        if (copied != expected) throw new IOException("The model transfer was interrupted. Run Get-Model.ps1 again to resume.");
    }
}
