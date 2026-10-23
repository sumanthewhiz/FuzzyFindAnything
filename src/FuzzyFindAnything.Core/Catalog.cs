using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FuzzyFindAnything.Core;

public sealed partial class Catalog : IDisposable
{
    private readonly object gate = new();
    private readonly SqliteConnection database;
    private SqliteTransaction? transaction;
    private readonly Action<string> log;
    private readonly Dictionary<string, string> pathKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, VectorShard> shards = [];
    private Dictionary<string, EmbeddingSpace> routes = new(StringComparer.Ordinal)
    { ["Document"] = EmbeddingSpace.Default, ["Code"] = EmbeddingSpace.Default, ["Image"] = EmbeddingSpace.Default };
    private readonly int threads;
    private ModelSelection? configuredSelection;
    private ImagePipelineDescriptor? imagePipeline;
    private bool disposed;
    public string DirectoryPath { get; }
    public string IndexDirectory { get; }
    public string Recovery => string.Join(" ", shards.Values.Select(shard => shard.Recovery).Where(value => value.Length > 0));
    public IReadOnlyList<EmbeddingSpace> Spaces { get { lock (gate) return routes.Values.DistinctBy(space => space.Key).ToArray(); } }
    public ModelSelection Selection { get { lock (gate) return configuredSelection ?? new(routes["Document"].ModelUrl, routes["Code"].ModelUrl, routes["Image"].ModelUrl); } }
    public EmbeddingSpace SpaceFor(string kind) { lock (gate) return routes[kind]; }
    public ImagePipelineDescriptor? ImagePipeline { get { lock (gate) return imagePipeline; } }

    public Catalog(string directory, int nativeThreads, Action<string>? diagnostic = null)
    {
        DirectoryPath = Path.GetFullPath(directory);
        IndexDirectory = Path.Combine(DirectoryPath, "diskann");
        Directory.CreateDirectory(IndexDirectory);
        log = diagnostic ?? (_ => { });
        threads = Math.Max(1, nativeThreads);
        SQLitePCL.Batteries_V2.Init();
        database = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(DirectoryPath, "catalog.sqlite3"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        try
        {
            database.Open();
            Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;");
            InitializeSchema();
            if (Setting("embedding_routes") is { } saved)
            {
                routes = JsonSerializer.Deserialize<Dictionary<string, EmbeddingSpace>>(saved, Protocol.Json)
                    ?? throw new InvalidDataException("Invalid saved model configuration.");
                ValidateRoutes(routes, Setting("image_pipeline") is not null);
            }
            if (Setting("image_pipeline") is { } imageSettings)
                imagePipeline = JsonSerializer.Deserialize<ImagePipelineDescriptor>(imageSettings, Protocol.Json)
                    ?? throw new InvalidDataException("Invalid saved image pipeline.");
            if (Setting("model_selection") is { } selected)
            {
                configuredSelection = JsonSerializer.Deserialize<ModelSelection>(selected, Protocol.Json)?.Normalize()
                    ?? throw new InvalidDataException("Invalid saved model bindings.");
                var embeddings = configuredSelection.EmbeddingSelection();
                if (routes.Any(pair => embeddings.ForKind(pair.Key) != pair.Value.ModelUrl))
                    throw new InvalidDataException("Saved model bindings do not match their vector routes.");
            }
            foreach (var row in Query("SELECT path,path_key FROM files UNION SELECT path,path_key FROM index_jobs",
                         reader => (Path: reader.GetString(0), Key: reader.GetString(1))))
                pathKeys[row.Path] = row.Key;
            Transaction(() =>
            {
                Execute("UPDATE index_jobs SET state='pending',scheduled=0 WHERE state='active'");
                Execute("DELETE FROM index_jobs WHERE NOT EXISTS(SELECT 1 FROM job_folders WHERE job_id=index_jobs.id)");
            });
            foreach (var space in Spaces) shards.Add(space.Key, OpenShard(space));
        }
        catch
        {
            foreach (var shard in shards.Values) shard.Dispose();
            database.Dispose();
            throw;
        }
    }

    private SqliteCommand Command(string sql, params object?[] values)
    {
        var command = database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        for (int i = 0; i < values.Length; i++) command.Parameters.AddWithValue("$p" + i, values[i] ?? DBNull.Value);
        return command;
    }
    private int Execute(string sql, params object?[] values)
    {
        using var command = Command(sql, values);
        return command.ExecuteNonQuery();
    }
    private T Scalar<T>(string sql, params object?[] values)
    {
        using var command = Command(sql, values);
        object? value = command.ExecuteScalar();
        if (value is null or DBNull) throw new InvalidDataException("A required catalog value is missing.");
        return (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
    }
    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> read, params object?[] values)
    {
        using var command = Command(sql, values);
        using var reader = command.ExecuteReader();
        var result = new List<T>();
        while (reader.Read()) result.Add(read(reader));
        return result;
    }
    private void Transaction(Action action)
    {
        lock (gate)
        {
            if (transaction is not null) { action(); return; }
            using var current = database.BeginTransaction();
            transaction = current;
            try { action(); current.Commit(); }
            finally { transaction = null; }
        }
    }

    private void InitializeSchema()
    {
        Execute("""
            CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY,value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS folders(id INTEGER PRIMARY KEY AUTOINCREMENT,path TEXT NOT NULL,path_key TEXT NOT NULL UNIQUE,
              status TEXT NOT NULL DEFAULT 'In Progress',detail TEXT NOT NULL DEFAULT 'Waiting to index',
              completed INTEGER NOT NULL DEFAULT 0,total INTEGER NOT NULL DEFAULT 0,errors INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS files(id INTEGER PRIMARY KEY AUTOINCREMENT,path TEXT NOT NULL,path_key TEXT NOT NULL UNIQUE,
              mtime_ns INTEGER NOT NULL,size INTEGER NOT NULL,kind TEXT NOT NULL,error TEXT,file_identity TEXT NOT NULL DEFAULT '',
              error_code TEXT,retry_after REAL,failure_count INTEGER NOT NULL DEFAULT 0,warning TEXT,content_hash TEXT NOT NULL DEFAULT '');
            CREATE TABLE IF NOT EXISTS folder_files(folder_id INTEGER NOT NULL REFERENCES folders(id) ON DELETE CASCADE,
              file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,PRIMARY KEY(folder_id,file_id));
            CREATE INDEX IF NOT EXISTS folder_files_by_file ON folder_files(file_id);
            CREATE TABLE IF NOT EXISTS vectors(id INTEGER PRIMARY KEY AUTOINCREMENT,file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
              ordinal INTEGER NOT NULL,snippet TEXT NOT NULL,embedding BLOB NOT NULL);
            CREATE INDEX IF NOT EXISTS vectors_by_file ON vectors(file_id);
            CREATE TABLE IF NOT EXISTS index_jobs(id INTEGER PRIMARY KEY AUTOINCREMENT,path TEXT NOT NULL,path_key TEXT NOT NULL UNIQUE,
              mtime_ns INTEGER NOT NULL,size INTEGER NOT NULL,file_identity TEXT NOT NULL,content_hash TEXT NOT NULL,kind TEXT NOT NULL,
              revision INTEGER NOT NULL DEFAULT 1,state TEXT NOT NULL DEFAULT 'pending',prepared INTEGER NOT NULL DEFAULT 0,
              warning TEXT,attempts INTEGER NOT NULL DEFAULT 0,retry_at REAL NOT NULL DEFAULT 0,scheduled REAL NOT NULL,
              elapsed_ms REAL NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS job_folders(job_id INTEGER NOT NULL REFERENCES index_jobs(id) ON DELETE CASCADE,
              folder_id INTEGER NOT NULL REFERENCES folders(id) ON DELETE CASCADE,PRIMARY KEY(job_id,folder_id));
            CREATE TABLE IF NOT EXISTS job_passages(job_id INTEGER NOT NULL REFERENCES index_jobs(id) ON DELETE CASCADE,
              ordinal INTEGER NOT NULL,content TEXT NOT NULL,embedding BLOB,PRIMARY KEY(job_id,ordinal));
            INSERT OR IGNORE INTO settings VALUES('generation','0');
            INSERT OR IGNORE INTO settings VALUES('schema_version','1');
            """);
        string version = Setting("schema_version")!;
        if (version is not ("1" or "2" or "3" or "4" or "5" or "6" or "7" or "8"))
            throw new InvalidDataException($"Unsupported catalog schema: {version}.");
        string? model = Setting("model");
        if (model is not null && model != ModelContract.Fingerprint)
            throw new InvalidDataException("The catalog belongs to a different embedding space; no data was reset.");
        var columns = Query("PRAGMA table_info(files)", reader => reader.GetString(1)).ToHashSet();
        Transaction(() =>
        {
            foreach (var column in new (string Name, string Definition)[]
            {
                ("file_identity","TEXT NOT NULL DEFAULT ''"),("error_code","TEXT"),("retry_after","REAL"),
                ("failure_count","INTEGER NOT NULL DEFAULT 0"),("warning","TEXT"),("content_hash","TEXT NOT NULL DEFAULT ''")
            })
                if (!columns.Contains(column.Name)) Execute($"ALTER TABLE files ADD COLUMN {column.Name} {column.Definition}");
            if (version is "1" or "2") Execute("UPDATE files SET retry_after=0 WHERE error IS NOT NULL");
            SetSetting("schema_version", version is "5" or "6" or "7" or "8" ? version : "4");
            SetSetting("model", ModelContract.Fingerprint);
        });
        if (version is "7" or "8") EnsureImageSchema();
        if (version == "8") EnsureChunkSchema();
    }

    public string? Setting(string key)
    {
        lock (gate) return Query("SELECT value FROM settings WHERE key=$p0", reader => reader.GetString(0), key).FirstOrDefault();
    }
    public void SetSetting(string key, string value) => Transaction(() =>
        Execute("INSERT INTO settings VALUES($p0,$p1) ON CONFLICT(key) DO UPDATE SET value=excluded.value", key, value));
    private long Generation => long.Parse(Setting("generation")!, CultureInfo.InvariantCulture);
    private void Bump(IEnumerable<EmbeddingSpace> changed)
    {
        var generations = changed.DistinctBy(space => space.Key).ToDictionary(space => space.Key, GenerationFor);
        Execute("UPDATE settings SET value=CAST(value AS INTEGER)+1 WHERE key='generation'");
        if (Setting("schema_version") is "5" or "6" or "7" or "8")
            foreach (var pair in generations) SetSetting("generation:" + pair.Key, (pair.Value + 1).ToString(CultureInfo.InvariantCulture));
        SetSetting("last_refreshed", DateTimeOffset.UtcNow.ToString("O"));
    }
    private string Key(string path) => pathKeys.TryGetValue(path, out string? value) ? value : FilePaths.Key(path);
    private static string? NullableString(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);

    public List<FolderState> Folders()
    {
        lock (gate) return Query("SELECT id,path,status,detail,completed,total,errors FROM folders ORDER BY id",
            row => new FolderState(row.GetInt32(0), row.GetString(1), row.GetString(2), row.GetString(3),
                row.GetInt32(4), row.GetInt32(5), row.GetInt32(6)));
    }
    public bool HasFolder(int id) { lock (gate) return Scalar<long>("SELECT COUNT(*) FROM folders WHERE id=$p0", id) != 0; }
    public int AddFolder(string path)
    {
        lock (gate)
        {
            FolderState? existing = Folders().FirstOrDefault(folder => folder.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) return existing.Id;
            int result = 0;
            Transaction(() =>
            {
                Execute("INSERT INTO folders(path,path_key) VALUES($p0,$p1)", path, FilePaths.Key(path));
                result = Scalar<int>("SELECT last_insert_rowid()");
            });
            return result;
        }
    }

    private IndexedFile ReadFile(SqliteDataReader row) => new(row.GetInt64(0), row.GetString(1), row.GetString(2),
        new(row.GetInt64(3), row.GetInt64(4), row.GetString(5)), row.GetString(6), NullableString(row, 7), NullableString(row, 8),
        row.IsDBNull(9) ? null : row.GetDouble(9), row.GetInt32(10), NullableString(row, 11), row.GetString(12));
    private const string FileColumns = "id,path,path_key,mtime_ns,size,file_identity,kind,error,error_code,retry_after,failure_count,warning,content_hash";
    public IndexedFile? FindFile(string path)
    {
        lock (gate) return Query($"SELECT {FileColumns} FROM files WHERE path_key=$p0", ReadFile, Key(path)).FirstOrDefault();
    }

    public bool Enqueue(int folder, string path, Fingerprint signature, string kind, string digest, bool force)
    {
        bool queued = false;
        Transaction(() =>
        {
            if (!HasFolder(folder)) return;
            string key = Key(path);
            IndexedFile? old = FindFile(path);
            if (digest.Length == 0 && old?.Fingerprint == signature) digest = old.ContentHash;
            bool canReuse = old is not null && (old.RetryAfter is null || old.RetryAfter > FilePaths.UnixNow);
            if (old is not null && ((!force && old.Fingerprint == signature && canReuse) ||
                (digest.Length > 0 && old.ContentHash == digest && canReuse)))
            {
                Execute("INSERT OR IGNORE INTO folder_files VALUES($p0,$p1)", folder, old.Id);
                if (digest.Length > 0 && old.ContentHash == digest)
                {
                    Execute("UPDATE files SET path=$p0,mtime_ns=$p1,size=$p2,file_identity=$p3 WHERE id=$p4",
                        path, signature.MtimeNs, signature.Size, signature.Identity, old.Id);
                    if (old.Error is null && old.RetryAfter is null) Execute("DELETE FROM index_jobs WHERE path_key=$p0", key);
                }
                return;
            }
            var current = Query("SELECT id,mtime_ns,size,file_identity,content_hash FROM index_jobs WHERE path_key=$p0",
                row => (Id: row.GetInt64(0), Signature: new Fingerprint(row.GetInt64(1), row.GetInt64(2), row.GetString(3)), Hash: row.GetString(4)), key);
            if (current.Count > 0)
            {
                var job = current[0];
                Execute("INSERT OR IGNORE INTO job_folders VALUES($p0,$p1)", job.Id, folder);
                if (job.Signature == signature && (!force || digest.Length == 0 || job.Hash == digest)) return;
                Execute("DELETE FROM job_passages WHERE job_id=$p0", job.Id);
                Execute("""
                    UPDATE index_jobs SET path=$p0,mtime_ns=$p1,size=$p2,file_identity=$p3,content_hash=$p4,kind=$p5,
                      revision=revision+1,state='pending',prepared=0,warning=NULL,attempts=0,retry_at=0,scheduled=$p6,elapsed_ms=0 WHERE id=$p7
                    """, path, signature.MtimeNs, signature.Size, signature.Identity, digest, kind, FilePaths.UnixNow, job.Id);
            }
            else
            {
                Execute("INSERT INTO index_jobs(path,path_key,mtime_ns,size,file_identity,content_hash,kind,scheduled) VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7)",
                    path, key, signature.MtimeNs, signature.Size, signature.Identity, digest, kind, FilePaths.UnixNow);
                Execute("INSERT INTO job_folders VALUES($p0,$p1)", Scalar<long>("SELECT last_insert_rowid()"), folder);
            }
            pathKeys[path] = key;
            if (HasImageSchema)
            {
                Execute("UPDATE index_jobs SET pipeline_key=$p0 WHERE path_key=$p1", IndexingKey(kind), key);
                TrackReindexMember(key, kind);
            }
            queued = true;
        });
        return queued;
    }

    public IndexJob? Claim()
    {
        IndexJob? result = null;
        Transaction(() =>
        {
            result = Query("""
                SELECT id,path,path_key,mtime_ns,size,file_identity,content_hash,kind,revision,prepared,attempts,elapsed_ms
                FROM index_jobs WHERE state='pending' AND retry_at <= $p0
                AND EXISTS(SELECT 1 FROM job_folders WHERE job_id=index_jobs.id) ORDER BY scheduled,size,id LIMIT 1
                """, row => new IndexJob(row.GetInt64(0), row.GetString(1), row.GetString(2),
                    new(row.GetInt64(3), row.GetInt64(4), row.GetString(5)), row.GetString(6), row.GetString(7),
                    row.GetInt32(8), row.GetBoolean(9), row.GetInt32(10), row.GetDouble(11)), FilePaths.UnixNow).FirstOrDefault();
            if (result is not null) Execute("UPDATE index_jobs SET state='active' WHERE id=$p0", result.Id);
        });
        return result;
    }
    public bool IsCurrent(IndexJob job)
    {
        lock (gate) return Scalar<long>("SELECT COUNT(*) FROM index_jobs WHERE id=$p0 AND revision=$p1 AND EXISTS(SELECT 1 FROM job_folders WHERE job_id=index_jobs.id)",
            job.Id, job.Revision) != 0;
    }
    public List<int> Owners(IndexJob job)
    {
        lock (gate) return Query("SELECT folder_id FROM job_folders WHERE job_id=$p0 ORDER BY folder_id", row => row.GetInt32(0), job.Id);
    }
    public bool Prepare(IndexJob job, IReadOnlyList<string> passages, string? warning)
    {
        if (passages.Count == 0) throw new ContentException("No searchable text was extracted.", "no_text");
        bool result = false;
        Transaction(() =>
        {
            if (!IsCurrent(job)) return;
            if (Scalar<long>("SELECT prepared FROM index_jobs WHERE id=$p0", job.Id) == 0)
            {
                for (int i = 0; i < passages.Count; i++)
                    Execute("INSERT INTO job_passages(job_id,ordinal,content) VALUES($p0,$p1,$p2)", job.Id, i, passages[i]);
                Execute("UPDATE index_jobs SET prepared=1,warning=$p0 WHERE id=$p1", warning, job.Id);
            }
            result = true;
        });
        return result;
    }
    public List<Passage> Remaining(IndexJob job, int count = 16)
    {
        lock (gate) return !IsCurrent(job) ? [] : Query(
            $"SELECT ordinal,content,{(HasImageSchema ? "representation,representation_id" : "'' AS representation,NULL AS representation_id")} FROM job_passages WHERE job_id=$p0 AND embedding IS NULL ORDER BY ordinal LIMIT $p1",
            row => new Passage(row.GetInt32(0), row.GetString(1), row.GetString(2), row.IsDBNull(3) ? null : row.GetInt64(3)), job.Id, count);
    }
    public bool SaveBatch(IndexJob job, IReadOnlyList<Passage> passages, IReadOnlyList<float[]> vectors)
    {
        if (passages.Count != vectors.Count) throw new InvalidDataException("Incomplete inference response.");
        bool result = false;
        Transaction(() =>
        {
            if (!IsCurrent(job)) return;
            var bytes = vectors.Select(vector => ModelContract.Bytes(routes[job.Kind].Normalize(vector))).ToArray();
            for (int i = 0; i < passages.Count; i++)
                Execute("UPDATE job_passages SET embedding=$p0 WHERE job_id=$p1 AND ordinal=$p2 AND embedding IS NULL",
                    bytes[i], job.Id, passages[i].Ordinal);
            result = true;
        });
        return result;
    }
    public (int Completed, int Total) Progress(IndexJob job)
    {
        lock (gate) return Query("SELECT COUNT(embedding),COUNT(*) FROM job_passages WHERE job_id=$p0",
            row => (row.GetInt32(0), row.GetInt32(1)), job.Id)[0];
    }
    public void Release(IndexJob job, double elapsedMs) => Transaction(() =>
        Execute("UPDATE index_jobs SET state='pending',scheduled=$p0,elapsed_ms=elapsed_ms+$p1,retry_at=0 WHERE id=$p2 AND revision=$p3",
            FilePaths.UnixNow, elapsedMs, job.Id, job.Revision));

    public bool Complete(IndexJob job)
    {
        lock (gate)
        {
            if (!IsCurrent(job)) return false;
            var rows = Query($"SELECT content,embedding,{(HasImageSchema ? "representation,representation_id" : "'' AS representation,NULL AS representation_id")},{(HasChunkSchema ? "chunk_json" : "NULL")} FROM job_passages WHERE job_id=$p0 ORDER BY ordinal",
                row => (Text: row.GetString(0), Bytes: row.IsDBNull(1) ? null : (byte[])row[1],
                    Representation: row.GetString(2), RepresentationId: row.IsDBNull(3) ? (long?)null : row.GetInt64(3),
                    Chunk: row.IsDBNull(4) ? null : JsonSerializer.Deserialize<ChunkMetadata>(row.GetString(4), Protocol.Json)), job.Id);
            if (rows.Count == 0 || rows.Any(row => row.Bytes is null)) throw new InvalidDataException("Cannot publish an incomplete file.");
            string? warning = Query("SELECT warning FROM index_jobs WHERE id=$p0", row => NullableString(row, 0), job.Id)[0];
            double? retry = job.Kind == "Image" && imagePipeline is not null ? NextImageRetry(job) : null;
            return Publish(job, rows.Select(row => new PublishedChunk(FilePaths.ScalarPrefix(job.Kind == "Code" ? row.Text.Trim() : FilePaths.CollapseWhitespace(row.Text), 400),
                routes[job.Kind].Vector(row.Bytes!), row.Representation, row.RepresentationId, row.Text, row.Chunk)).ToArray(), warning, null, null, retry, job.Attempts,
                coverage: job.Kind == "Image" ? warning is null ? "Complete" : "Partial" : "");
        }
    }
    public bool Fail(IndexJob job, string error, string code, bool retryable, double elapsedMs)
    {
        lock (gate)
        {
            int attempts = job.Attempts + 1;
            double? retry = retryable ? FilePaths.UnixNow + Math.Min(3600, 300 * Math.Pow(2, Math.Min(attempts - 1, 4))) : null;
            return Publish(job, [], null, error, code, retry, attempts, elapsedMs);
        }
    }
    private sealed record PublishedChunk(string Text, float[] Vector, string Representation, long? RepresentationId,
        string FullText, ChunkMetadata? Chunk);
    private bool Publish(IndexJob job, IReadOnlyList<PublishedChunk> chunks, string? warning,
        string? error, string? errorCode, double? retryAt, int attempts, double elapsedMs = 0, string coverage = "")
    {
        if (!IsCurrent(job)) return false;
        var owners = Owners(job);
        if (owners.Count == 0) return false;
        var space = routes[job.Kind];
        var oldSpace = FindFile(job.Path) is { } old ? routes[old.Kind] : space;
        var deleted = new List<RemovedVector>();
        var inserted = new List<(uint Id, float[] Vector)>();
        Transaction(() =>
        {
            Execute("""
                INSERT INTO files(path,path_key,mtime_ns,size,file_identity,kind,error,error_code,retry_after,failure_count,warning,content_hash)
                VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,$p8,$p9,$p10,$p11)
                ON CONFLICT(path_key) DO UPDATE SET path=excluded.path,mtime_ns=excluded.mtime_ns,size=excluded.size,
                  file_identity=excluded.file_identity,kind=excluded.kind,error=excluded.error,error_code=excluded.error_code,
                  retry_after=excluded.retry_after,failure_count=excluded.failure_count,warning=excluded.warning,content_hash=excluded.content_hash
                """, job.Path, job.PathKey, job.Fingerprint.MtimeNs, job.Fingerprint.Size, job.Fingerprint.Identity,
                job.Kind, error, errorCode, retryAt, attempts, warning, job.ContentHash);
            long fileId = Scalar<long>("SELECT id FROM files WHERE path_key=$p0", job.PathKey);
            foreach (int owner in owners) Execute("INSERT OR IGNORE INTO folder_files VALUES($p0,$p1)", owner, fileId);
            deleted = Query("SELECT id FROM vectors WHERE file_id=$p0", row => new RemovedVector(checked((uint)row.GetInt64(0)), oldSpace), fileId);
            Execute("DELETE FROM vectors WHERE file_id=$p0", fileId);
            for (int ordinal = 0; ordinal < chunks.Count; ordinal++)
            {
                float[] vector = space.Normalize(chunks[ordinal].Vector);
                Execute("INSERT INTO vectors(file_id,ordinal,snippet,embedding) VALUES($p0,$p1,$p2,$p3)",
                    fileId, ordinal, chunks[ordinal].Text, ModelContract.Bytes(vector));
                inserted.Add((checked((uint)Scalar<long>("SELECT last_insert_rowid()")), vector));
                if (HasImageSchema)
                    Execute("UPDATE vectors SET representation=$p0,representation_id=$p1,space_key=$p2,pipeline_key=$p3 WHERE id=$p4",
                        chunks[ordinal].Representation, chunks[ordinal].RepresentationId, space.Key, IndexingKey(job.Kind), inserted[^1].Id);
                if (HasChunkSchema)
                    Execute("UPDATE vectors SET chunk_text=$p0,chunk_json=$p1 WHERE id=$p2", chunks[ordinal].FullText,
                        chunks[ordinal].Chunk is null ? null : JsonSerializer.Serialize(chunks[ordinal].Chunk, Protocol.Json), inserted[^1].Id);
            }
            if (HasImageSchema)
                Execute("UPDATE files SET image_coverage=$p0 WHERE id=$p1",
                    job.Kind == "Image" ? coverage.Length > 0 ? coverage : error is null ? "Complete" : "Failed" : "", fileId);
            if (deleted.Count != 0 || inserted.Count != 0) Bump(deleted.Select(value => value.Space).Append(space));
            if (retryAt is null)
                Execute("DELETE FROM index_jobs WHERE id=$p0 AND revision=$p1", job.Id, job.Revision);
            else
                Execute("UPDATE index_jobs SET state='pending',attempts=$p0,retry_at=$p1,scheduled=$p2,elapsed_ms=$p3 WHERE id=$p4 AND revision=$p5",
                    attempts, retryAt, FilePaths.UnixNow, elapsedMs, job.Id, job.Revision);
            if (retryAt is not null && error is null && job.Kind == "Image" && imagePipeline is not null)
            {
                Execute("DELETE FROM job_passages WHERE job_id=$p0", job.Id);
                Execute("UPDATE index_jobs SET prepared=0 WHERE id=$p0", job.Id);
            }
        });
        pathKeys[job.Path] = job.PathKey;
        Apply(deleted, inserted, space);
        return true;
    }

    public void RemovePath(string path, bool recursive = false)
    {
        lock (gate)
        {
            string targetKey = Key(path);
            var deleted = new List<RemovedVector>();
            Transaction(() =>
            {
                foreach (var file in Query("SELECT id,path,path_key,kind FROM files", row => (Id: row.GetInt64(0), Path: row.GetString(1), Key: row.GetString(2), Kind: row.GetString(3))))
                {
                    if (!file.Key.Equals(targetKey, StringComparison.Ordinal) && !(recursive && FilePaths.Within(file.Path, path))) continue;
                    deleted.AddRange(Query("SELECT id FROM vectors WHERE file_id=$p0", row => new RemovedVector(checked((uint)row.GetInt64(0)), routes[file.Kind]), file.Id));
                    Execute("DELETE FROM files WHERE id=$p0", file.Id);
                    pathKeys.Remove(file.Path);
                }
                foreach (var job in Query("SELECT id,path,path_key FROM index_jobs", row => (Id: row.GetInt64(0), Path: row.GetString(1), Key: row.GetString(2))))
                    if (job.Key == targetKey || recursive && FilePaths.Within(job.Path, path))
                    {
                        Execute("DELETE FROM index_jobs WHERE id=$p0", job.Id);
                        pathKeys.Remove(job.Path);
                    }
                if (deleted.Count > 0) Bump(deleted.Select(value => value.Space));
            });
            Apply(deleted, [], null);
        }
    }

    public void RemoveFolders(IReadOnlyList<int> ids)
    {
        lock (gate)
        {
            var deleted = new List<RemovedVector>();
            Transaction(() =>
            {
                foreach (int id in ids) Execute("DELETE FROM folders WHERE id=$p0", id);
                var remaining = Folders();
                foreach (var file in Query("SELECT id,path FROM files", row => (Id: row.GetInt64(0), Path: row.GetString(1))))
                    foreach (var folder in remaining.Where(folder => FilePaths.Within(file.Path, folder.Path)))
                        Execute("INSERT OR IGNORE INTO folder_files VALUES($p0,$p1)", folder.Id, file.Id);
                foreach (var job in Query("SELECT id,path FROM index_jobs", row => (Id: row.GetInt64(0), Path: row.GetString(1))))
                    foreach (var folder in remaining.Where(folder => FilePaths.Within(job.Path, folder.Path)))
                        Execute("INSERT OR IGNORE INTO job_folders VALUES($p0,$p1)", job.Id, folder.Id);
                Execute("DELETE FROM index_jobs WHERE NOT EXISTS(SELECT 1 FROM job_folders WHERE job_id=index_jobs.id)");
                deleted = Query("SELECT v.id,f.kind FROM vectors v JOIN files f ON f.id=v.file_id WHERE NOT EXISTS(SELECT 1 FROM folder_files ff WHERE ff.file_id=v.file_id)",
                    row => new RemovedVector(checked((uint)row.GetInt64(0)), routes[row.GetString(1)]));
                Execute("DELETE FROM files WHERE NOT EXISTS(SELECT 1 FROM folder_files WHERE file_id=files.id)");
                if (deleted.Count > 0) Bump(deleted.Select(value => value.Space));
            });
            Apply(deleted, [], null);
            Checkpoint();
        }
    }

    public void Prune(int folder, string scope, HashSet<string> seen)
    {
        lock (gate)
        {
            var paths = Query("""
                SELECT f.path FROM files f JOIN folder_files ff ON ff.file_id=f.id WHERE ff.folder_id=$p0
                UNION SELECT j.path FROM index_jobs j JOIN job_folders jf ON jf.job_id=j.id WHERE jf.folder_id=$p0
                """, row => row.GetString(0), folder);
            foreach (string path in paths)
                if (FilePaths.Within(path, scope) && !seen.Contains(FilePaths.Key(path)) && !File.Exists(path))
                    RemovePath(path);
        }
    }

    public (int Queued, int Active) QueueCounts()
    {
        lock (gate) return Query("SELECT COUNT(*),COALESCE(SUM(state='active'),0) FROM index_jobs",
            row => (row.GetInt32(0), row.GetInt32(1)))[0];
    }
    private ModelReindexState? ReadModelReindex()
    {
        if (Setting("model_reindex") is not { } saved) return null;
        var state = JsonSerializer.Deserialize<ModelReindexState>(saved, Protocol.Json)
            ?? throw new InvalidDataException("Invalid saved model reindex status.");
        if (state.Kinds is not { Length: > 0 and <= 3 } ||
            state.Kinds.Any(kind => kind is not ("Document" or "Code" or "Image")))
            throw new InvalidDataException("Invalid file types in saved model reindex status.");
        return state;
    }
    public ModelReindexState? ModelReindex(bool canComplete, bool discoveryIdle = true)
    {
        lock (gate)
        {
            var state = ReadModelReindex();
            if (state is null || state.Completed) return state;
            if (HasImageSchema && state.OperationId is not null) return ImageReindexState(state, canComplete);
            string kinds = string.Join(",", state.Kinds.Select(kind => "'" + kind + "'"));
            var counts = Query($"""
                SELECT COALESCE(SUM(f.error IS NULL),0),COALESCE(SUM(f.error IS NOT NULL),0)
                FROM files f WHERE f.kind IN ({kinds}) AND EXISTS(SELECT 1 FROM folder_files WHERE file_id=f.id)
                AND NOT EXISTS(SELECT 1 FROM index_jobs j WHERE j.path_key=f.path_key)
                """, row => (Succeeded: row.GetInt64(0), Failed: row.GetInt64(1)))[0];
            bool completed = canComplete && discoveryIdle && Scalar<long>($"SELECT COUNT(*) FROM index_jobs WHERE kind IN ({kinds})") == 0 &&
                Folders().All(folder => string.IsNullOrEmpty(Setting("scan_error:" + folder.Id)));
            var next = state with { Completed = completed, Succeeded = counts.Succeeded, Failed = counts.Failed };
            if (completed) SetSetting("model_reindex", JsonSerializer.Serialize(next, Protocol.Json));
            return next;
        }
    }
    public int RetryClipResizeFailures()
    {
        int retried = 0;
        Transaction(() =>
        {
            if (routes["Image"].Adapter != "clip") return;
            // Older CLIP builds could round the short edge to 223 pixels and permanently fail a valid image.
            retried = Execute("""
                UPDATE files SET retry_after=0 WHERE kind='Image' AND error_code='model_inference'
                AND error='Index was outside the bounds of the array.' AND retry_after IS NULL
                """);
            if (retried > 0) TrackModelReindex(["Image"]);
        });
        return retried;
    }
    private void TrackModelReindex(string[] changed)
    {
        var previous = ReadModelReindex();
        string[] kinds = previous is { Completed: false }
            ? previous.Kinds.Concat(changed).Distinct(StringComparer.Ordinal).ToArray() : changed;
        var next = new ModelReindexState(kinds, OperationId: HasImageSchema ? Guid.NewGuid().ToString("N") : null);
        SetSetting("model_reindex", JsonSerializer.Serialize(next, Protocol.Json));
        if (HasImageSchema) BeginReindexCohort(next);
    }
    public (long Files, long Vectors) Counts()
    {
        lock (gate) return (Scalar<long>("SELECT COUNT(*) FROM files WHERE error IS NULL AND EXISTS(SELECT 1 FROM vectors WHERE file_id=files.id)"), Scalar<long>("SELECT COUNT(*) FROM vectors"));
    }
    public FolderState FolderCounts(FolderState folder, bool scanning)
    {
        lock (gate)
        {
            int succeeded = Scalar<int>($"""
                SELECT COUNT(*) FROM files f JOIN folder_files ff ON ff.file_id=f.id WHERE ff.folder_id=$p0 AND f.error IS NULL
                AND NOT EXISTS(SELECT 1 FROM index_jobs j WHERE j.path_key=f.path_key)
                {(HasImageSchema ? "AND f.image_coverage<>'Partial'" : "")}
                """, folder.Id);
            int failed = Scalar<int>("SELECT COUNT(*) FROM files f JOIN folder_files ff ON ff.file_id=f.id WHERE ff.folder_id=$p0 AND f.error IS NOT NULL AND COALESCE(error_code,'') NOT IN ('index_pending','image_no_text')", folder.Id);
            int partial = HasImageSchema ? Scalar<int>("SELECT COUNT(*) FROM files f JOIN folder_files ff ON ff.file_id=f.id WHERE ff.folder_id=$p0 AND image_coverage='Partial'", folder.Id) : 0;
            int skipped = Scalar<int>("SELECT COUNT(*) FROM files f JOIN folder_files ff ON ff.file_id=f.id WHERE ff.folder_id=$p0 AND error_code='image_no_text'", folder.Id);
            var (queued, active) = Query("""
                SELECT COUNT(*),COALESCE(SUM(j.state='active'),0) FROM index_jobs j JOIN job_folders jf ON jf.job_id=j.id WHERE jf.folder_id=$p0
                """, row => (row.GetInt32(0), row.GetInt32(1)), folder.Id)[0];
            int total = Scalar<int>("""
                SELECT COUNT(*) FROM (
                  SELECT f.path_key FROM files f JOIN folder_files ff ON ff.file_id=f.id WHERE ff.folder_id=$p0
                  UNION SELECT j.path_key FROM index_jobs j JOIN job_folders jf ON jf.job_id=j.id WHERE jf.folder_id=$p0)
                """, folder.Id);
            string status = scanning || queued > 0 || !string.IsNullOrEmpty(Setting("scan_error:" + folder.Id)) ? "In Progress" : "Indexed";
            return folder with
            {
                Status = status,
                Detail = $"{succeeded:N0} succeeded  |  {failed:N0} failed",
                Completed = Math.Max(0, total - queued),
                Total = total,
                Errors = failed,
                Succeeded = succeeded,
                Failed = failed,
                Queued = queued,
                Active = active,
                Partial = partial,
                Skipped = skipped
            };
        }
    }
    public IndexInfo Metadata()
    {
        lock (gate)
        {
            var counts = Query("""
                SELECT COALESCE(SUM(kind='Document'),0),COALESCE(SUM(kind='Code'),0),COALESCE(SUM(kind='Image'),0)
                FROM files WHERE error IS NULL AND EXISTS(SELECT 1 FROM vectors WHERE file_id=files.id)
                """, row => (Documents: row.GetInt64(0), Code: row.GetInt64(1), Images: row.GetInt64(2)))[0];
            long size = 0;
            foreach (string file in new[] { "catalog.sqlite3", "catalog.sqlite3-wal", "catalog.sqlite3-shm" }.Select(name => Path.Combine(DirectoryPath, name))
                         .Concat(shards.Values.SelectMany(shard => Directory.EnumerateFiles(shard.DirectoryPath, "*", SearchOption.AllDirectories))))
            {
                try { size += new FileInfo(file).Length; }
                catch (FileNotFoundException) { }
            }
            return new($"DiskANN ({NativeIndex.Version})", DirectoryPath, counts.Documents + counts.Code + counts.Images,
                counts.Documents, counts.Images, size, Setting("last_refreshed"), counts.Code);
        }
    }
    public List<ResultFile> Search(float[] query, int limit, double threshold, EmbeddingSpace? space = null,
        IReadOnlyCollection<string>? kinds = null)
    {
        if (limit is < 1 or > 100 || !double.IsFinite(threshold) || threshold is < 0 or > 1)
            throw new ArgumentException("Search limit must be 1-100 and similarity 0-1.");
        space ??= EmbeddingSpace.Default;
        if (kinds is not null && (kinds.Count == 0 || kinds.Any(kind => kind is not ("Document" or "Code" or "Image"))))
            throw new ArgumentException("Choose supported source kinds for the embedding query.");
        query = space.Normalize(query);
        lock (gate)
        {
            if (!shards.TryGetValue(space.Key, out var shard)) return [];
            int count = Scalar<int>($"SELECT COUNT(*) FROM vectors v JOIN files f ON f.id=v.file_id WHERE f.kind IN ({KindsSql(space)})");
            if (count == 0) return [];
            int k = Math.Min(count, Math.Max(64, limit * 4));
            while (true)
            {
                var matches = new Dictionary<long, ResultFile>();
                foreach (uint id in shard.Search(query, k))
                {
                    var records = Query($"""
                        SELECT f.id,f.path,f.kind,f.size,f.mtime_ns,v.snippet,v.embedding,{(HasImageSchema ? "v.representation" : "''")},
                          {(HasChunkSchema ? "v.chunk_text,v.chunk_json" : "v.snippet,NULL")} FROM vectors v JOIN files f ON f.id=v.file_id
                        WHERE v.id=$p0 AND f.error IS NULL AND EXISTS(SELECT 1 FROM folder_files WHERE file_id=f.id)
                        """, row => (Id: row.GetInt64(0), Path: row.GetString(1), Kind: row.GetString(2), Size: row.GetInt64(3),
                            Mtime: row.GetInt64(4), Snippet: row.GetString(5), Vector: space.Vector((byte[])row[6]), Evidence: row.GetString(7),
                            ChunkText: row.GetString(8), Chunk: row.IsDBNull(9) ? null : JsonSerializer.Deserialize<ChunkMetadata>(row.GetString(9), Protocol.Json)), id);
                    if (records.Count == 0) continue;
                    var record = records[0];
                    if (!File.Exists(record.Path) || routes[record.Kind].Key != space.Key || kinds is not null && !kinds.Contains(record.Kind)) continue;
                    double score = ModelContract.Similarity(query, record.Vector);
                    if (score < threshold || matches.TryGetValue(record.Id, out var old) && old.Score >= score) continue;
                    matches[record.Id] = new(record.Path, Path.GetFileName(record.Path), record.Kind, record.Size, record.Mtime, record.Snippet, score, record.Evidence,
                        record.ChunkText.Length > 0 ? record.ChunkText : record.Snippet, record.Chunk);
                }
                if (matches.Count >= limit || k == count) return matches.Values.OrderByDescending(value => value.Score)
                    .ThenBy(value => value.Path, StringComparer.Ordinal).Take(limit).ToList();
                k = Math.Min(count, k * 2);
            }
        }
    }

    private sealed record RemovedVector(uint Id, EmbeddingSpace Space);
    private long GenerationFor(EmbeddingSpace space) => Setting("schema_version") is "5" or "6" or "7" or "8"
        ? long.Parse(Setting("generation:" + space.Key) ?? "0", CultureInfo.InvariantCulture) : Generation;
    private string KindsSql(EmbeddingSpace space)
    {
        string[] kinds = routes.Where(pair => pair.Value.Key == space.Key).Select(pair => "'" + pair.Key + "'").ToArray();
        return kinds.Length == 0 ? "NULL" : string.Join(",", kinds);
    }
    private List<(uint Id, float[] Vector)> Vectors(EmbeddingSpace space) =>
        Query($"SELECT v.id,v.embedding FROM vectors v JOIN files f ON f.id=v.file_id WHERE f.kind IN ({KindsSql(space)}) ORDER BY v.id",
            row => (checked((uint)row.GetInt64(0)), space.Vector((byte[])row[1])));
    private VectorShard OpenShard(EmbeddingSpace space) => new(
        space == EmbeddingSpace.Default ? IndexDirectory : Path.Combine(DirectoryPath, "model-indexes", space.Key),
        space, threads, () => Vectors(space), () => GenerationFor(space), log);
    private void Apply(IReadOnlyList<RemovedVector> deleted, IReadOnlyList<(uint Id, float[] Vector)> inserted, EmbeddingSpace? destination)
    {
        foreach (string key in deleted.Select(value => value.Space.Key).Concat(destination is null ? [] : new[] { destination.Key }).Distinct())
            shards[key].Apply(deleted.Where(value => value.Space.Key == key).Select(value => value.Id).ToArray(),
                destination?.Key == key ? inserted : []);
    }
    private static void ValidateRoutes(IReadOnlyDictionary<string, EmbeddingSpace> value, bool describedImages = false)
    {
        if (value.Count != 3 || new[] { "Document", "Code", "Image" }.Any(kind => !value.ContainsKey(kind)))
            throw new InvalidDataException("A model is required for documents, code and images.");
        foreach (var space in value.Values) space.Validate();
        if (!describedImages && !value["Image"].SupportsImages) throw new InvalidDataException("The selected image model cannot create image embeddings.");
    }
    public string[] ReconfigureModels(IReadOnlyDictionary<string, EmbeddingSpace> next, Action persistConfiguration,
        Action? configurationCommitted = null, ModelSelection? selection = null, ImagePipelineDescriptor? pipeline = null)
    {
        ValidateRoutes(next, pipeline is not null);
        lock (gate)
        {
            selection ??= new(next["Document"].ModelUrl, next["Code"].ModelUrl, next["Image"].ModelUrl);
            selection = selection.Normalize();
            if (next.Any(pair => selection.EmbeddingSelection().ForKind(pair.Key) != pair.Value.ModelUrl))
                throw new InvalidDataException("The selected model URLs do not match the prepared routes.");
            string[] changed = routes.Keys.Where(kind => routes[kind].IndexingFingerprint != next[kind].IndexingFingerprint).ToArray();
            if (imagePipeline?.Fingerprint != pipeline?.Fingerprint) changed = changed.Append("Image").Distinct().ToArray();
            if (changed.Length == 0 && selection == Selection) return [];
            var prepared = new Dictionary<string, VectorShard>();
            try
            {
                foreach (var space in next.Values.DistinctBy(value => value.Key))
                    if (!shards.ContainsKey(space.Key)) prepared.Add(space.Key, OpenShard(space));
                var deleted = Query("SELECT v.id,f.kind FROM vectors v JOIN files f ON f.id=v.file_id",
                    row => (Id: checked((uint)row.GetInt64(0)), Kind: row.GetString(1)))
                    .Where(row => changed.Contains(row.Kind)).Select(row => new RemovedVector(row.Id, routes[row.Kind])).ToArray();
                Transaction(() =>
                {
                    if (pipeline is not null || HasImageSchema) EnsureImageSchema();
                    foreach (var space in Spaces.Concat(next.Values).DistinctBy(value => value.Key))
                        if (Setting("generation:" + space.Key) is null)
                            SetSetting("generation:" + space.Key, (space == EmbeddingSpace.Default ? Generation : 0).ToString(CultureInfo.InvariantCulture));
                    foreach (string kind in changed)
                    {
                        Execute("DELETE FROM index_jobs WHERE kind=$p0", kind);
                        if (HasImageSchema)
                        {
                            Execute("DELETE FROM vectors WHERE file_id IN (SELECT id FROM files WHERE kind=$p0)", kind);
                            Execute("UPDATE files SET error='Waiting for updated model indexing',error_code='index_pending',retry_after=0,warning=NULL,image_coverage=CASE WHEN kind='Image' THEN 'Pending' ELSE '' END WHERE kind=$p0", kind);
                        }
                        else Execute("DELETE FROM files WHERE kind=$p0", kind);
                    }
                    SetSetting("schema_version", HasChunkSchema ? "8" : HasImageSchema ? "7" : Setting("schema_version") == "6" || next.Values.Any(space => space.Recipe is not null) ||
                        selection.RequiresVersion2 ? "6" : "5");
                    SetSetting("embedding_routes", JsonSerializer.Serialize(next, Protocol.Json));
                    SetSetting("model_selection", JsonSerializer.Serialize(selection, Protocol.Json));
                    if (pipeline is null) Execute("DELETE FROM settings WHERE key='image_pipeline'");
                    else SetSetting("image_pipeline", JsonSerializer.Serialize(pipeline, Protocol.Json));
                    SetSetting("model_apply_message", changed.Length == 0
                        ? "Configuration saved. Query/device settings updated; existing embeddings were retained." : "");
                    if (changed.Length > 0)
                    {
                        TrackModelReindex(changed);
                        Bump(changed.Select(kind => routes[kind]).Concat(changed.Select(kind => next[kind])));
                    }
                    else if (ReadModelReindex() is not { Completed: false })
                        Execute("DELETE FROM settings WHERE key='model_reindex'");
                    // XML is published before the SQL commit; a crash is reconciled against the saved intent on restart.
                    persistConfiguration();
                });
                routes = next.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                configuredSelection = selection;
                imagePipeline = pipeline;
                configurationCommitted?.Invoke();
                foreach (var pair in prepared) shards.Add(pair.Key, pair.Value);
                prepared.Clear();
                Apply(deleted, [], null);
                foreach (string key in shards.Keys.Where(key => !routes.Values.Any(space => space.Key == key)).ToArray())
                {
                    var obsolete = shards[key]; shards.Remove(key);
                    try { obsolete.Discard(); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    { log("Inactive model index cleanup requires attention: " + error.Message); }
                }
                return changed;
            }
            finally { foreach (var shard in prepared.Values) shard.Dispose(); }
        }
    }

    public IReadOnlyList<ModelIndexState> ModelIndexes()
    {
        lock (gate) return Spaces.Select(space => new ModelIndexState(space.ModelUrl,
            routes.Where(pair => pair.Value.Key == space.Key).Select(pair => pair.Key).ToArray(), space.Dimensions,
            Scalar<long>($"SELECT COUNT(*) FROM files f WHERE f.error IS NULL AND f.kind IN ({KindsSql(space)})"),
            Scalar<long>($"SELECT COUNT(*) FROM vectors v JOIN files f ON f.id=v.file_id WHERE f.kind IN ({KindsSql(space)})"),
            shards[space.Key].DirectoryPath, "", "")).ToArray();
    }

    public void Checkpoint() { lock (gate) foreach (var shard in shards.Values) shard.Checkpoint(); }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            try { Checkpoint(); }
            finally { disposed = true; foreach (var shard in shards.Values) shard.Dispose(); database.Dispose(); }
        }
    }
}
