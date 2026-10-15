using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace FuzzyFindAnything.Core;

public sealed class TextExtractor(string executable, TimeSpan? timeout = null)
{
    private const int MaximumCharacters = 8_000_000;
    private readonly TimeSpan timeout = timeout ?? TimeSpan.FromSeconds(60);
    static TextExtractor() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public async Task<ExtractedContent> ExtractAsync(string path, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (FilePaths.Kind(path) == "Code")
            return new(await ReadTextAsync(path, cancellation), [], Path.GetExtension(path));
        if (Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase))
        {
            byte[] header = new byte[1024];
            await using var stream = FilePaths.OpenRead(path);
            int count = await stream.ReadAsync(header, cancellation);
            bool binaryDocument = header.AsSpan(0, count).IndexOf("%PDF-"u8) >= 0 ||
                header.AsSpan(0, count).StartsWith(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }) ||
                header.AsSpan(0, count).StartsWith("PK\u0003\u0004"u8) ||
                Encoding.ASCII.GetString(header, 0, count).TrimStart().StartsWith(@"{\rtf", StringComparison.Ordinal);
            if (!binaryDocument) return new(await ReadTextAsync(path, cancellation), [], ".txt");
        }
        if (!File.Exists(executable)) throw new FileNotFoundException("The Windows IFilter helper is missing.", executable);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        start.ArgumentList.Add(path);
        using var process = Process.Start(start) ?? throw new IOException("Could not start the Windows IFilter helper.");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        lifetime.CancelAfter(timeout);
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> diagnostics = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(lifetime.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await Task.WhenAll(output, diagnostics);
            cancellation.ThrowIfCancellationRequested();
            throw new ContentException($"Windows IFilter exceeded {timeout.TotalSeconds:0} seconds; its isolated helper was stopped.", "filter_timeout", true);
        }
        string response = await output, errors = await diagnostics;
        JsonDocument data;
        try { data = JsonDocument.Parse(response.TrimStart('\uFEFF')); }
        catch (JsonException error)
        {
            throw new ContentException($"Windows IFilter returned no valid response (exit {process.ExitCode}): {errors}. {error.Message}", "filter_crash", true);
        }
        using (data)
        {
            var root = data.RootElement;
            if (process.ExitCode != 0)
            {
                var failure = root.GetProperty("error");
                throw new ContentException(failure.GetProperty("message").GetString() ?? "IFilter failed.",
                    failure.GetProperty("code").GetString() ?? "filter_failure",
                    failure.TryGetProperty("retryable", out var retryable) && retryable.GetBoolean());
            }
            string text = root.GetProperty("text").GetString()?.Trim() ?? "";
            if (text.Length == 0) throw new ContentException("The document contains no extractable text.", "no_text");
            string[] warnings = root.TryGetProperty("warnings", out var warning) ? warning.Deserialize<string[]>() ?? [] : [];
            return new(text, warnings, root.TryGetProperty("format", out var format) ? format.GetString() ?? "" : Path.GetExtension(path));
        }
    }

    public static async Task<string> ReadTextAsync(string path, CancellationToken cancellation)
    {
        await using var stream = FilePaths.OpenRead(path);
        using var memory = new MemoryStream();
        byte[] buffer = new byte[64 * 1024];
        while (true)
        {
            int count = await stream.ReadAsync(buffer, cancellation);
            if (count == 0) break;
            if (memory.Length + count > MaximumCharacters * 4L)
                throw new ContentException("Text file exceeds the 32 MB extraction safety limit.", "resource_limit");
            memory.Write(buffer, 0, count);
        }
        byte[] raw = memory.ToArray();
        if (raw.Length == 0) throw new ContentException("The file is empty (0 bytes); there is no content to index.", "empty_file");
        bool utf32le = raw.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE, 0, 0 });
        bool utf32be = raw.AsSpan().StartsWith(new byte[] { 0, 0, 0xFE, 0xFF });
        bool utf16le = raw.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE });
        bool utf16be = raw.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF });
        if (raw.AsSpan().StartsWith(new byte[] { 0, 1, 0, 0 }) &&
            (raw.AsSpan(0, Math.Min(raw.Length, 64)).IndexOf("Standard ACE DB"u8) >= 0 ||
             raw.AsSpan(0, Math.Min(raw.Length, 64)).IndexOf("Standard Jet DB"u8) >= 0))
            throw new ContentException("The file contains a Microsoft Access database, not plain text.", "unsupported_content");
        if (FilePaths.Kind(path) == "Code" && raw.Contains((byte)0) && !(utf16le || utf16be || utf32le || utf32be))
            throw new ContentException("The code file contains binary data rather than plain-text source.", "unsupported_content");
        string text;
        try
        {
            if (utf32le || utf32be) text = new UTF32Encoding(utf32be, false, true).GetString(raw, 4, raw.Length - 4);
            else if (utf16le || utf16be) text = new UnicodeEncoding(utf16be, false, true).GetString(raw, 2, raw.Length - 2);
            else
            {
                try
                {
                    int skip = raw.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? 3 : 0;
                    text = new UTF8Encoding(false, true).GetString(raw, skip, raw.Length - skip);
                }
                catch (DecoderFallbackException)
                {
                    var codePage = new StringBuilder(16);
                    if (GetLocaleInfo(0x800, 0x1004, codePage, codePage.Capacity) == 0)
                        throw new IOException("Cannot determine the Windows text code page.");
                    text = Encoding.GetEncoding(int.Parse(codePage.ToString(), CultureInfo.InvariantCulture),
                        EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetString(raw);
                }
            }
        }
        catch (DecoderFallbackException error)
        {
            throw new ContentException("The file is not valid UTF-8, BOM-marked UTF-16/32, or Windows-encoded text. " + error.Message,
                "invalid_text_encoding");
        }
        if (FilePaths.ScalarLength(text) > MaximumCharacters)
            throw new ContentException("Text exceeds the 8 million character safety limit.", "resource_limit");
        text = text.Replace('\0', ' ').Trim();
        if (text.Length == 0) throw new ContentException("The file contains no searchable text.", "no_text");
        return text;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetLocaleInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetLocaleInfo(uint locale, uint type, StringBuilder value, int count);
}
