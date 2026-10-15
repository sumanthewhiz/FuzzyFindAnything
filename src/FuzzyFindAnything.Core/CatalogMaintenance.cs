using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace FuzzyFindAnything.Core;

public sealed class EngineLease : IDisposable
{
    private readonly FileStream file;
    public EngineLease(string directory)
    {
        Directory.CreateDirectory(directory);
        file = new FileStream(Path.Combine(directory, "engine.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        try
        {
            file.Lock(0, 1);
            if (file.Length == 0) { file.WriteByte(0); file.Flush(true); }
        }
        catch (IOException error)
        {
            file.Dispose();
            throw new IOException("Another indexing engine owns this catalog. Quit that application before maintenance.", error);
        }
    }
    public void Dispose() => file.Dispose();
}

public static class CatalogMaintenance
{
    public sealed record ResetResult(int FoldersPreserved, int FoldersCleared, string FolderBackup, string Catalog);

    public static ResetResult Reset(string directory, bool clearFolders)
    {
        directory = Path.GetFullPath(directory);
        string catalog = Path.Combine(directory, "catalog.sqlite3"), index = Path.Combine(directory, "diskann");
        if (!File.Exists(catalog)) throw new FileNotFoundException("No existing catalog was found.", catalog);
        if (Directory.Exists(index) && FilePaths.IsNameSurrogate(index))
            throw new IOException("The DiskANN directory is a reparse link; resolve its storage location before maintenance.");
        using var lease = new EngineLease(directory);
        SQLitePCL.Batteries_V2.Init();
        using var database = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = catalog, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        database.Open();
        using var command = database.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL";
        command.ExecuteNonQuery();
        command.CommandText = "SELECT id,path,path_key FROM folders ORDER BY id";
        var folders = new List<object>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) folders.Add(new { id = reader.GetInt32(0), path = reader.GetString(1), path_key = reader.GetString(2) });
        string backup = Path.Combine(directory, $"configured-folders-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
        using (var stream = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        { JsonSerializer.Serialize(stream, folders); stream.Flush(true); }
        using (var transaction = database.BeginTransaction())
        {
            command.Transaction = transaction;
            command.CommandText = """
                DELETE FROM index_jobs;
                DELETE FROM folder_files;
                DELETE FROM vectors;
                DELETE FROM files;
                DELETE FROM sqlite_sequence WHERE name IN ('files','vectors','index_jobs');
                DELETE FROM settings WHERE key LIKE 'usn:%' OR key LIKE 'scan_error:%' OR key='last_refreshed';
                UPDATE settings SET value=CAST(value AS INTEGER)+1 WHERE key='generation';
                """;
            command.ExecuteNonQuery();
            command.CommandText = clearFolders
                ? "DELETE FROM folders; DELETE FROM sqlite_sequence WHERE name='folders'"
                : "UPDATE folders SET status='In Progress',detail='Waiting for full reindex',completed=0,total=0,errors=0";
            command.ExecuteNonQuery();
            transaction.Commit();
        }
        command.Transaction = null;
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE); VACUUM";
        command.ExecuteNonQuery();
        if (Directory.Exists(index))
        {
            foreach (string name in new[] { "current.json", "current.tmp", "current.json.tmp" }) File.Delete(Path.Combine(index, name));
            foreach (string snapshot in Directory.EnumerateDirectories(index))
                if (Regex.IsMatch(Path.GetFileName(snapshot), "^snapshot-[0-9a-f]{32}$") && !FilePaths.IsNameSurrogate(snapshot))
                    Directory.Delete(snapshot, true);
        }
        return new(clearFolders ? 0 : folders.Count, clearFolders ? folders.Count : 0, backup, catalog);
    }
}
