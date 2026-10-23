using System.Text.Json;

namespace FuzzyFindAnything.Core;

public sealed partial class Catalog
{
    private bool HasChunkSchema => Setting("schema_version") is "8" or "9" or "10";
    private void EnsureChunkSchema()
    {
        Transaction(() =>
        {
            foreach (var space in Spaces)
                if (Setting("generation:" + space.Key) is null)
                    SetSetting("generation:" + space.Key, (space == EmbeddingSpace.Default ? Generation : 0)
                        .ToString(System.Globalization.CultureInfo.InvariantCulture));
            EnsureImageSchema();
            void Add(string table, string name, string definition)
            {
                if (!Query($"PRAGMA table_info({table})", row => row.GetString(1)).Contains(name))
                    Execute($"ALTER TABLE {table} ADD COLUMN {name} {definition}");
            }
            Add("vectors", "chunk_text", "TEXT NOT NULL DEFAULT ''");
            Add("vectors", "chunk_json", "TEXT");
            Add("job_passages", "chunk_json", "TEXT");
            if (!HasRegionSchema) SetSetting("schema_version", "8");
        });
    }
    public string[] ConfigureChunkingProfiles(IReadOnlyDictionary<string, string> profiles)
    {
        if (profiles.Count != 2 || !profiles.ContainsKey("Document") || !profiles.ContainsKey("Code") ||
            profiles.Any(pair => pair.Value.Length == 0 || pair.Value.Length > 200))
            throw new ArgumentException("Document and code chunking profiles are required.");
        lock (gate)
        {
            var changed = profiles.Keys.Where(kind => (Setting("chunking:" + kind) ?? ChunkingProfiles.Legacy) != profiles[kind]).ToArray();
            if (changed.Length == 0) return [];
            var affected = changed.Where(kind => Scalar<long>("SELECT COUNT(*) FROM files WHERE kind=$p0", kind) > 0 ||
                Scalar<long>("SELECT COUNT(*) FROM index_jobs WHERE kind=$p0", kind) > 0).ToArray();
            var deleted = Query("SELECT v.id,f.kind FROM vectors v JOIN files f ON f.id=v.file_id",
                row => (Id: checked((uint)row.GetInt64(0)), Kind: row.GetString(1))).Where(row => affected.Contains(row.Kind))
                .Select(row => new RemovedVector(row.Id, routes[row.Kind])).ToArray();
            Transaction(() =>
            {
                EnsureChunkSchema();
                foreach (var pair in profiles) SetSetting("chunking:" + pair.Key, pair.Value);
                foreach (string kind in affected)
                {
                    Execute("DELETE FROM index_jobs WHERE kind=$p0", kind);
                    Execute("DELETE FROM vectors WHERE file_id IN (SELECT id FROM files WHERE kind=$p0)", kind);
                    Execute("UPDATE files SET error='Waiting for content-aware chunking',error_code='index_pending',retry_after=0,warning=NULL WHERE kind=$p0", kind);
                }
                if (affected.Length > 0)
                {
                    TrackModelReindex(affected);
                    Bump(affected.Select(kind => routes[kind]));
                    log("Updated content chunking profiles; only affected document/code representations will be rebuilt.");
                }
            });
            Apply(deleted, [], null);
            return affected;
        }
    }
    public bool PrepareContent(IndexJob job, ChunkingResult chunks)
    {
        if (!HasChunkSchema || chunks.Chunks.Length == 0) throw new InvalidDataException("Structured chunk storage is unavailable or empty.");
        bool prepared = false;
        Transaction(() =>
        {
            if (!IsCurrent(job)) return;
            if (Scalar<long>("SELECT prepared FROM index_jobs WHERE id=$p0", job.Id) == 0)
            {
                for (int index = 0; index < chunks.Chunks.Length; index++)
                {
                    var chunk = chunks.Chunks[index];
                    if (chunk.Metadata.Profile != (Setting("chunking:" + job.Kind) ?? "") ||
                        chunk.Metadata.StartOffset < 0 || chunk.Metadata.Length < 1 || chunk.Metadata.StartLine < 1 ||
                        chunk.Metadata.EndLine < chunk.Metadata.StartLine || string.IsNullOrWhiteSpace(chunk.Content))
                        throw new InvalidDataException("Invalid structured chunk provenance.");
                    Execute("INSERT INTO job_passages(job_id,ordinal,content,chunk_json) VALUES($p0,$p1,$p2,$p3)",
                        job.Id, index, chunk.Content, JsonSerializer.Serialize(chunk.Metadata, Protocol.Json));
                }
                Execute("UPDATE index_jobs SET prepared=1,warning=$p0 WHERE id=$p1", chunks.Warning, job.Id);
            }
            prepared = true;
        });
        return prepared;
    }
}
