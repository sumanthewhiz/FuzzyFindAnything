using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FuzzyFindAnything.Core;
using Microsoft.Data.Sqlite;

namespace FuzzyFindAnything.NativeTests;

internal static class Compatibility
{
    public static void Run(string source, string destination)
    {
        source = Path.GetFullPath(source); destination = Path.GetFullPath(destination);
        if (FilePaths.Within(destination, source) || FilePaths.Within(source, destination) || Directory.Exists(destination))
            throw new ArgumentException("Compatibility validation requires a new, separate destination.");
        Directory.CreateDirectory(destination);
        SQLitePCL.Batteries_V2.Init();
        using (var original = Check.Open(source, true))
        using (var copy = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(destination, "catalog.sqlite3"), Pooling = false }.ToString()))
        {
            copy.Open(); original.BackupDatabase(copy);
        }
        string manifest = Path.Combine(source, "diskann", "current.json");
        if (File.Exists(manifest))
        {
            using var snapshot = JsonDocument.Parse(File.ReadAllText(manifest));
            string name = snapshot.RootElement.GetProperty("snapshot").GetString()!;
            Program.Require(System.Text.RegularExpressions.Regex.IsMatch(name, "^snapshot-[0-9a-f]{32}$"), "Invalid source snapshot name.");
            string sourceSnapshot = Path.Combine(source, "diskann", name), targetSnapshot = Path.Combine(destination, "diskann", name);
            Program.Require(!FilePaths.IsNameSurrogate(sourceSnapshot), "Source snapshot is a reparse link.");
            Directory.CreateDirectory(targetSnapshot);
            foreach (string file in Directory.GetFiles(sourceSnapshot)) File.Copy(file, Path.Combine(targetSnapshot, Path.GetFileName(file)));
            File.Copy(manifest, Path.Combine(destination, "diskann", "current.json"));
        }
        string preferences = Path.Combine(source, "settings.xml");
        if (File.Exists(preferences)) File.Copy(preferences, Path.Combine(destination, "settings.xml"));
        string vectors = Digest(destination, "SELECT id,file_id,ordinal,snippet,embedding FROM vectors ORDER BY id");
        string folders = Digest(destination, "SELECT id,path,path_key FROM folders ORDER BY id");
        string passages = Digest(destination, "SELECT job_id,ordinal,content,embedding FROM job_passages ORDER BY job_id,ordinal");
        long fileCount = Check.Scalar(destination, "SELECT COUNT(*) FROM files"), vectorCount = Check.Scalar(destination, "SELECT COUNT(*) FROM vectors");
        long jobs = Check.Scalar(destination, "SELECT COUNT(*) FROM index_jobs");
        string? xml = File.Exists(preferences) ? FilePaths.Hash(preferences) : null;
        string recovery;
        using (var native = new Catalog(destination, 2))
        {
            recovery = native.Recovery;
            Program.Require(native.Folders().Count == Check.Scalar(destination, "SELECT COUNT(*) FROM folders"), "Configured scope changed.");
            Program.Require(native.QueueCounts().Queued == jobs, "Native startup discarded existing queued work.");
        }
        Program.Require(vectors == Digest(destination, "SELECT id,file_id,ordinal,snippet,embedding FROM vectors ORDER BY id") &&
            folders == Digest(destination, "SELECT id,path,path_key FROM folders ORDER BY id") &&
            passages == Digest(destination, "SELECT job_id,ordinal,content,embedding FROM job_passages ORDER BY job_id,ordinal") &&
            (xml is null || FilePaths.Hash(Path.Combine(destination, "settings.xml")) == xml),
            "Opening the copied Python-era index changed vectors, folder identities, staged passages or XML preferences.");
        File.WriteAllText(Path.Combine(destination, "compatibility.json"), JsonSerializer.Serialize(new
        {
            failures = 0,
            files = fileCount,
            vectors = vectorCount,
            queued_jobs = jobs,
            recovery,
            vectors_sha256 = vectors,
            folders_sha256 = folders,
            passages_sha256 = passages,
            preferences_sha256 = xml
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS existing catalog copy: {fileCount} files, {vectorCount} vectors, {jobs} queued jobs; originals were read-only.");
    }
    private static string Digest(string directory, string sql)
    {
        using var database = Check.Open(directory, true);
        using var command = database.CreateCommand(); command.CommandText = sql;
        using var reader = command.ExecuteReader();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        while (reader.Read())
            for (int index = 0; index < reader.FieldCount; index++)
            {
                object value = reader.GetValue(index);
                byte[] bytes = value is byte[] data ? data : Encoding.UTF8.GetBytes(value.ToString() ?? "");
                hash.AppendData(BitConverter.GetBytes(bytes.Length)); hash.AppendData(bytes);
            }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
