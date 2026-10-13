using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace FuzzyFindAnything.Extractor;

internal static class FileFormatProbe
{
    private static readonly byte[] CompoundSignature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    public static string Detect(string path)
    {
        byte[] header = new byte[1024];
        int length;
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            length = file.Read(header, 0, header.Length);
        if (length == 0)
            throw new FilterFailure("empty_file", "The file is empty (0 bytes); there is no content to index.");
        ReadOnlySpan<byte> bytes = header.AsSpan(0, length);
        string declared = Path.GetExtension(path).ToLowerInvariant();
        if (bytes.IndexOf("Standard ACE DB"u8) >= 0 || bytes.IndexOf("Standard Jet DB"u8) >= 0)
            throw new FilterFailure("unsupported_content",
                $"This file contains a Microsoft Access database, not {declared} document content. Its filename extension is incorrect.");
        if (bytes.IndexOf("%PDF-"u8) >= 0)
            return ".pdf";
        string prefix = Encoding.ASCII.GetString(bytes).TrimStart();
        if (prefix.StartsWith(@"{\rtf", StringComparison.Ordinal))
            return ".rtf";
        if (bytes.StartsWith(CompoundSignature))
        {
            var names = ReadStorageNames(path);
            if (names.Contains("EncryptedPackage") || names.Contains("EncryptionInfo"))
                throw new FilterFailure("encrypted_document",
                    "This is an encrypted Office document. Save an unencrypted copy before indexing it.");
            if (names.Contains("WordDocument"))
                return ".doc";
            if (names.Contains("Workbook") || names.Contains("Book"))
                return ".xls";
            if (names.Contains("PowerPoint Document"))
                return ".ppt";
            throw new FilterFailure("unsupported_content",
                "The file is an OLE compound container, but it does not contain a supported Word, Excel or PowerPoint document.");
        }
        if (bytes.StartsWith("PK\x03\x04"u8) || bytes.StartsWith("PK\x05\x06"u8))
        {
            try
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var archive = new ZipArchive(file, ZipArchiveMode.Read);
                if (archive.GetEntry("word/document.xml") is not null)
                    return ".docx";
                if (archive.GetEntry("xl/workbook.xml") is not null)
                    return ".xlsx";
                if (archive.GetEntry("ppt/presentation.xml") is not null)
                    return ".pptx";
                throw new FilterFailure("unsupported_content", "The ZIP container has no supported Office document part.");
            }
            catch (InvalidDataException error)
            {
                throw new FilterFailure("invalid_document", "The Office/ZIP document container is damaged.", inner: error);
            }
        }
        if (declared == ".pdf")
            throw new FilterFailure("format_mismatch",
                "The file has a .pdf extension but no PDF header. The installed PDF IFilter cannot read non-PDF content.");
        return declared;
    }

    private static HashSet<string> ReadStorageNames(string path)
    {
        int result = StgOpenStorage(path, IntPtr.Zero, 0x20, IntPtr.Zero, 0, out IStorage? storage);
        if (result < 0 || storage is null)
            throw FilterFailure.AtStage("opening the compound container", Path.GetExtension(path),
                Marshal.GetExceptionForHR(result) ?? new IOException("Cannot open the compound container."));
        try
        {
            storage.EnumElements(0, IntPtr.Zero, 0, out IEnumStatStg elements);
            try
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var item = new STATSTG[1];
                for (int count = 0; count < 4096; count++)
                {
                    result = elements.Next(1, item, out uint fetched);
                    Marshal.ThrowExceptionForHR(result);
                    if (fetched == 0)
                        return names;
                    names.Add(item[0].pwcsName);
                }
                throw new FilterFailure("resource_limit", "The compound document exceeds the directory-entry safety limit.");
            }
            finally
            {
                Marshal.FinalReleaseComObject(elements);
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(storage);
        }
    }

    [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
    private static extern int StgOpenStorage(string name, IntPtr priority, uint mode, IntPtr exclude, uint reserved,
        [MarshalAs(UnmanagedType.Interface)] out IStorage? storage);

    [ComImport, Guid("0000000B-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IStorage
    {
        void CreateStream([MarshalAs(UnmanagedType.LPWStr)] string name, uint mode, uint reserved1, uint reserved2, out IntPtr stream);
        void OpenStream([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr reserved1, uint mode, uint reserved2, out IntPtr stream);
        void CreateStorage([MarshalAs(UnmanagedType.LPWStr)] string name, uint mode, uint reserved1, uint reserved2, out IntPtr storage);
        void OpenStorage([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr priority, uint mode, IntPtr exclude, uint reserved, out IntPtr storage);
        void CopyTo(uint excludeCount, IntPtr excludedInterfaces, IntPtr excludedNames, IntPtr destination);
        void MoveElementTo([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr destination, [MarshalAs(UnmanagedType.LPWStr)] string newName, uint flags);
        void Commit(uint flags);
        void Revert();
        void EnumElements(uint reserved1, IntPtr reserved2, uint reserved3, out IEnumStatStg elements);
    }

    [ComImport, Guid("0000000D-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumStatStg
    {
        [PreserveSig]
        int Next(uint count, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] STATSTG[] values, out uint fetched);
    }
}
