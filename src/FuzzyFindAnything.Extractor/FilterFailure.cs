using System.Runtime.InteropServices;

namespace FuzzyFindAnything.Extractor;

internal sealed class FilterFailure(string code, string message, bool retryable = false, Exception? inner = null)
    : Exception(message, inner)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;

    public static FilterFailure AtStage(string stage, string extension, Exception error)
    {
        uint result = unchecked((uint)error.HResult);
        string code = result switch
        {
            0x8004170B => "encrypted_document",
            0x80070020 or 0x80070021 or 0x80030020 or 0x80030021 => "file_busy",
            0x80070005 or 0x80041703 or 0x80030005 => "access_denied",
            0x80040154 or 0x80040111 or 0x8007007E or 0x800700C1 or 0x80004002 => "filter_unavailable",
            _ => "invalid_document"
        };
        bool retryable = code is "file_busy" or "access_denied" or "filter_unavailable";
        string explanation = code == "encrypted_document" ? "The document is password protected."
            : $"The {extension} IFilter was found, but {stage} failed: {error.Message}";
        return new FilterFailure(code, $"{explanation} (0x{result:X8})", retryable, error);
    }
}
