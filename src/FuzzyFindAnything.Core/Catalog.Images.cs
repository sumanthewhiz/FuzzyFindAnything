using System.Globalization;
using System.Text.Json;

namespace FuzzyFindAnything.Core;

public sealed record StoredImageTask(long Id, ImageTaskResult Result, int Attempts, double? RetryAt);

public sealed partial class Catalog
{
    private bool HasImageSchema => Setting("schema_version") is "7" or "8" or "9" or "10";
    private string IndexingKey(string kind) => kind == "Image" && imagePipeline is not null
        ? imagePipeline.Fingerprint : routes[kind].IndexingFingerprint +
            (kind is "Document" or "Code" ? ":" + (Setting("chunking:" + kind) ?? ChunkingProfiles.Legacy) : "");
    private static string SourceKey(IndexJob job) => EmbeddingRecipe.Hash(
        $"{job.Fingerprint.Identity}:{job.Fingerprint.MtimeNs}:{job.Fingerprint.Size}:{job.ContentHash}");

    private void EnsureImageSchema()
    {
        Transaction(() =>
        {
            Execute("""
                CREATE TABLE IF NOT EXISTS image_representations(
                  id INTEGER PRIMARY KEY AUTOINCREMENT,file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                  source_key TEXT NOT NULL,task_key TEXT NOT NULL,task TEXT NOT NULL,state TEXT NOT NULL,
                  content TEXT NOT NULL,warning TEXT,error TEXT,error_code TEXT,generated_tokens INTEGER NOT NULL DEFAULT 0,
                  attempts INTEGER NOT NULL DEFAULT 0,retryable INTEGER NOT NULL DEFAULT 0,retry_at REAL,updated TEXT NOT NULL,
                  UNIQUE(file_id,source_key,task_key));
                CREATE TABLE IF NOT EXISTS model_reindex_members(operation TEXT NOT NULL,path_key TEXT NOT NULL,kind TEXT NOT NULL,
                  PRIMARY KEY(operation,path_key));
                CREATE TABLE IF NOT EXISTS model_reindex_scans(operation TEXT NOT NULL,folder_id INTEGER NOT NULL REFERENCES folders(id) ON DELETE CASCADE,
                  PRIMARY KEY(operation,folder_id));
                """);
            void Add(string table, string name, string definition)
            {
                if (!Query($"PRAGMA table_info({table})", row => row.GetString(1)).Contains(name))
                    Execute($"ALTER TABLE {table} ADD COLUMN {name} {definition}");
            }
            Add("files", "image_coverage", "TEXT NOT NULL DEFAULT ''");
            Add("vectors", "representation", "TEXT NOT NULL DEFAULT ''");
            Add("vectors", "representation_id", "INTEGER REFERENCES image_representations(id) ON DELETE SET NULL");
            Add("vectors", "space_key", "TEXT NOT NULL DEFAULT ''");
            Add("vectors", "pipeline_key", "TEXT NOT NULL DEFAULT ''");
            Add("job_passages", "representation", "TEXT NOT NULL DEFAULT ''");
            Add("job_passages", "representation_id", "INTEGER REFERENCES image_representations(id) ON DELETE SET NULL");
            Add("index_jobs", "pipeline_key", "TEXT NOT NULL DEFAULT ''");
            foreach (var pair in routes)
                Execute("UPDATE vectors SET space_key=$p0,pipeline_key=$p1 WHERE space_key='' AND file_id IN (SELECT id FROM files WHERE kind=$p2)",
                    pair.Value.Key, pair.Value.IndexingFingerprint, pair.Key);
            if (!HasChunkSchema) SetSetting("schema_version", "7");
        });
    }

    private long ImageFile(IndexJob job)
    {
        if (!HasImageSchema || job.Kind != "Image" || !IsCurrent(job))
            throw new OperationCanceledException("The image source revision or indexing scope is no longer active.");
        Execute("""
            INSERT OR IGNORE INTO files(path,path_key,mtime_ns,size,file_identity,kind,error,error_code,retry_after,content_hash,image_coverage)
            VALUES($p0,$p1,$p2,$p3,$p4,'Image','Image processing pending','index_pending',0,$p5,'Pending')
            """, job.Path, job.PathKey, job.Fingerprint.MtimeNs, job.Fingerprint.Size, job.Fingerprint.Identity, job.ContentHash);
        long id = Scalar<long>("SELECT id FROM files WHERE path_key=$p0", job.PathKey);
        foreach (int owner in Owners(job)) Execute("INSERT OR IGNORE INTO folder_files VALUES($p0,$p1)", owner, id);
        return id;
    }
    public StoredImageTask? ReadImageTask(IndexJob job, string taskKey)
    {
        StoredImageTask? result = null;
        Transaction(() =>
        {
            long file = ImageFile(job);
            result = Query($"""
                SELECT id,task,content,state,generated_tokens,warning,error,error_code,retryable,attempts,retry_at,{(HasRegionSchema ? "region_json" : "NULL")}
                FROM image_representations WHERE file_id=$p0 AND source_key=$p1 AND task_key=$p2
                """, row => new StoredImageTask(row.GetInt64(0),
                    new(row.GetString(1), row.GetString(2), row.GetString(3), row.GetInt32(4), NullableString(row, 5),
                        NullableString(row, 6), NullableString(row, 7), row.GetBoolean(8),
                        row.IsDBNull(11) ? null : JsonSerializer.Deserialize<ImageRegion[]>(row.GetString(11), Protocol.Json)),
                    row.GetInt32(9), row.IsDBNull(10) ? null : row.GetDouble(10)), file, SourceKey(job), taskKey).FirstOrDefault();
        });
        return result;
    }
    public StoredImageTask SaveImageTask(IndexJob job, string taskKey, ImageTaskResult result)
    {
        result.Validate(result.Task);
        StoredImageTask? saved = null;
        Transaction(() =>
        {
            long file = ImageFile(job);
            int previous = Query("SELECT attempts FROM image_representations WHERE file_id=$p0 AND source_key=$p1 AND task_key=$p2",
                row => row.GetInt32(0), file, SourceKey(job), taskKey).FirstOrDefault();
            int attempts = previous + 1;
            bool retryable = result.State == "Failed" && result.Retryable && attempts < 3;
            double? retryAt = retryable ? FilePaths.UnixNow + 30 * Math.Pow(2, attempts - 1) : null;
            result = result with { Retryable = retryable };
            Execute("""
                INSERT INTO image_representations(file_id,source_key,task_key,task,state,content,warning,error,error_code,generated_tokens,attempts,retryable,retry_at,updated)
                VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,$p8,$p9,$p10,$p11,$p12,$p13)
                ON CONFLICT(file_id,source_key,task_key) DO UPDATE SET state=excluded.state,content=excluded.content,
                  warning=excluded.warning,error=excluded.error,error_code=excluded.error_code,generated_tokens=excluded.generated_tokens,
                  attempts=excluded.attempts,retryable=excluded.retryable,retry_at=excluded.retry_at,updated=excluded.updated
                """, file, SourceKey(job), taskKey, result.Task, result.State, result.Text, result.Warning, result.Error, result.ErrorCode,
                result.GeneratedTokens, attempts, retryable, retryAt, DateTimeOffset.UtcNow.ToString("O"));
            long id = Scalar<long>("SELECT id FROM image_representations WHERE file_id=$p0 AND source_key=$p1 AND task_key=$p2",
                file, SourceKey(job), taskKey);
            if (result.Regions is not null && !HasRegionSchema) throw new InvalidDataException("Region storage is not enabled.");
            if (HasRegionSchema) Execute("UPDATE image_representations SET region_json=$p0 WHERE id=$p1",
                result.Regions is null ? null : JsonSerializer.Serialize(result.Regions, Protocol.Json), id);
            saved = new(id, result, attempts, retryAt);
        });
        return saved!;
    }
    public bool PrepareImage(IndexJob job, IReadOnlyList<Passage> passages, string? warning)
    {
        if (passages.Count == 0) throw new ContentException("No searchable image text was generated.", "image_no_text");
        bool prepared = false;
        Transaction(() =>
        {
            if (!IsCurrent(job)) return;
            for (int index = 0; index < passages.Count; index++)
            {
                Execute("INSERT OR IGNORE INTO job_passages(job_id,ordinal,content,representation,representation_id) VALUES($p0,$p1,$p2,$p3,$p4)",
                    job.Id, index, passages[index].Content, passages[index].Representation, passages[index].RepresentationId);
                if (passages[index].Region is { } region)
                {
                    if (!HasRegionSchema) throw new InvalidDataException("Region passage storage is unavailable.");
                    region.Validate();
                    Execute("UPDATE job_passages SET region_json=$p0 WHERE job_id=$p1 AND ordinal=$p2",
                        JsonSerializer.Serialize(region, Protocol.Json), job.Id, index);
                }
            }
            Execute("UPDATE index_jobs SET prepared=1,warning=$p0 WHERE id=$p1", warning, job.Id);
            prepared = true;
        });
        return prepared;
    }
    private double? NextImageRetry(IndexJob job)
    {
        if (imagePipeline is null) return null;
        var retries = imagePipeline.Tasks.Select(task => ReadImageTask(job, imagePipeline.Model.TaskKey(task)))
            .Where(value => value?.Result.Retryable == true && value.RetryAt is not null).Select(value => value!.RetryAt!.Value).ToArray();
        return retries.Length == 0 ? null : retries.Min();
    }
    public bool CompleteEmptyImage(IndexJob job)
    {
        lock (gate) return Publish(job, [], "The selected image tasks found no searchable text.",
            "No searchable image text.", "image_no_text", null, 0, coverage: "Empty");
    }
    public int RetryImageTasks()
    {
        if (imagePipeline is null || !HasImageSchema) return 0;
        int changed = 0;
        Transaction(() =>
        {
            foreach (string task in imagePipeline.Tasks)
            {
                string key = imagePipeline.Model.TaskKey(task);
                var files = Query("SELECT DISTINCT file_id FROM image_representations WHERE task_key=$p0 AND state IN ('Failed','Partial')",
                    row => row.GetInt64(0), key);
                foreach (long file in files)
                    changed += Execute("UPDATE files SET retry_after=0 WHERE id=$p0 AND EXISTS(SELECT 1 FROM folder_files WHERE file_id=files.id)", file);
                Execute("DELETE FROM image_representations WHERE task_key=$p0 AND state IN ('Failed','Partial')", key);
            }
            if (changed > 0) TrackModelReindex(["Image"]);
        });
        return changed;
    }
    public ImagePipelineStatus? ImageCoverage(ImagePipelineStatus? status)
    {
        if (status is null || !HasImageSchema) return status;
        lock (gate)
        {
            var counts = Query("""
                SELECT COALESCE(SUM(image_coverage='Complete'),0),COALESCE(SUM(image_coverage='Partial'),0),
                  COALESCE(SUM(image_coverage='Failed'),0),COALESCE(SUM(image_coverage='Empty'),0)
                FROM files WHERE kind='Image' AND EXISTS(SELECT 1 FROM folder_files WHERE file_id=files.id)
                """, row => (Complete: row.GetInt64(0), Partial: row.GetInt64(1), Failed: row.GetInt64(2), Empty: row.GetInt64(3)))[0];
            return status with { Generated = counts.Complete, Partial = counts.Partial, Failed = counts.Failed, Empty = counts.Empty };
        }
    }

    private void BeginReindexCohort(ModelReindexState state)
    {
        Execute("DELETE FROM model_reindex_members; DELETE FROM model_reindex_scans");
        foreach (string kind in state.Kinds)
            Execute("""
                INSERT OR IGNORE INTO model_reindex_members(operation,path_key,kind)
                SELECT $p0,path_key,kind FROM files WHERE kind=$p1
                UNION SELECT $p0,path_key,kind FROM index_jobs WHERE kind=$p1
                """, state.OperationId, kind);
        foreach (var folder in Folders())
            Execute("INSERT INTO model_reindex_scans VALUES($p0,$p1)", state.OperationId, folder.Id);
    }
    private void TrackReindexMember(string pathKey, string kind)
    {
        var state = ReadModelReindex();
        if (state is { Completed: false, DiscoveryComplete: false, OperationId: not null } && state.Kinds.Contains(kind))
            Execute("INSERT OR IGNORE INTO model_reindex_members VALUES($p0,$p1,$p2)", state.OperationId, pathKey, kind);
    }
    public string? ReindexOperation()
    {
        lock (gate) return HasImageSchema && ReadModelReindex() is { Completed: false } state ? state.OperationId : null;
    }
    public void ReindexScanCompleted(int folder, string? operation, bool successful)
    {
        if (operation is null || !successful) return;
        lock (gate)
            if (HasImageSchema && ReadModelReindex()?.OperationId == operation)
                Execute("DELETE FROM model_reindex_scans WHERE operation=$p0 AND folder_id=$p1", operation, folder);
    }
    private ModelReindexState ImageReindexState(ModelReindexState state, bool canComplete)
    {
        bool discovered = Scalar<long>("SELECT COUNT(*) FROM model_reindex_scans WHERE operation=$p0", state.OperationId) == 0;
        var counts = Query("""
            SELECT COALESCE(SUM(f.id IS NOT NULL AND f.error IS NULL AND f.image_coverage<>'Partial'),0),
              COALESCE(SUM(f.image_coverage='Partial'),0),
              COALESCE(SUM(f.error IS NOT NULL AND COALESCE(f.error_code,'') NOT IN ('index_pending','image_no_text')),0),
              COALESCE(SUM(f.error_code='image_no_text'),0),
              COALESCE(SUM(f.id IS NULL AND NOT EXISTS(SELECT 1 FROM index_jobs j WHERE j.path_key=m.path_key)),0)
            FROM model_reindex_members m LEFT JOIN files f ON f.path_key=m.path_key WHERE m.operation=$p0
            """, row => (Succeeded: row.GetInt64(0), Partial: row.GetInt64(1), Failed: row.GetInt64(2),
                Skipped: row.GetInt64(3), Removed: row.GetInt64(4)), state.OperationId)[0];
        bool pending = Scalar<long>("""
            SELECT COUNT(*) FROM model_reindex_members m WHERE m.operation=$p0 AND
              (EXISTS(SELECT 1 FROM index_jobs j WHERE j.path_key=m.path_key) OR
               EXISTS(SELECT 1 FROM files f WHERE f.path_key=m.path_key AND f.error_code='index_pending'))
            """, state.OperationId) > 0;
        var next = state with
        {
            Completed = canComplete && discovered && !pending,
            DiscoveryComplete = discovered,
            Succeeded = counts.Succeeded,
            Partial = counts.Partial,
            Failed = counts.Failed,
            Skipped = counts.Skipped,
            Removed = counts.Removed
        };
        if (next.Completed || discovered != state.DiscoveryComplete)
            SetSetting("model_reindex", JsonSerializer.Serialize(next, Protocol.Json));
        return next;
    }
}
