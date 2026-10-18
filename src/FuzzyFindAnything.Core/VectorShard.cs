using System.Text.Json;
using System.Text.RegularExpressions;

namespace FuzzyFindAnything.Core;

internal sealed class VectorShard : IDisposable
{
    private readonly EmbeddingSpace space;
    private readonly int threads;
    private readonly Func<List<(uint Id, float[] Vector)>> readVectors;
    private readonly Func<long> readGeneration;
    private readonly Action<string> log;
    private NativeIndex? index;
    private Exception? failure;
    private int capacity, allocated;
    private long checkpointGeneration = -1;
    public string DirectoryPath { get; }
    public string Recovery { get; private set; } = "";

    public VectorShard(string directory, EmbeddingSpace space, int threads,
        Func<List<(uint Id, float[] Vector)>> readVectors, Func<long> readGeneration, Action<string> log)
    {
        DirectoryPath = directory; this.space = space; this.threads = threads;
        this.readVectors = readVectors; this.readGeneration = readGeneration; this.log = log;
        Directory.CreateDirectory(directory);
        try { Restore(); }
        catch { index?.Dispose(); throw; }
    }
    private static int CapacityFor(int count) =>
        checked((int)Math.Max(128, System.Numerics.BitOperations.RoundUpToPowerOf2(checked((uint)Math.Max(1, checked(count * 2))))));
    private void Rebuild()
    {
        var vectors = readVectors();
        int size = CapacityFor(vectors.Count);
        var replacement = new NativeIndex(space.Dimensions, size, threads);
        try { foreach (var vector in vectors) replacement.Insert(vector.Id, vector.Vector); }
        catch { replacement.Dispose(); throw; }
        index?.Dispose(); index = replacement; capacity = size; allocated = vectors.Count; failure = null;
    }
    public uint[] Search(float[] query, int count)
    {
        if (failure is not null) throw new NativeIndexException("Restart to recover the unavailable native graph: " + failure.Message);
        return index!.Search(query, count);
    }
    public void Apply(IReadOnlyList<uint> deleted, IReadOnlyList<(uint Id, float[] Vector)> inserted)
    {
        try
        {
            if (allocated + inserted.Count > capacity) { Rebuild(); return; }
            foreach (uint id in deleted) index!.Delete(id);
            foreach (var value in inserted) { index!.Insert(value.Id, value.Vector); allocated++; }
        }
        catch (IOException error)
        {
            failure = error;
            log($"Native graph mutation failed for {space.ModelUrl}; rebuilding committed vectors: {error.Message}");
            Rebuild();
        }
    }
    private void Restore()
    {
        string manifest = Path.Combine(DirectoryPath, "current.json");
        if (File.Exists(manifest))
        {
            try
            {
                using var value = JsonDocument.Parse(File.ReadAllText(manifest));
                var root = value.RootElement;
                string name = root.GetProperty("snapshot").GetString()!;
                if (!Regex.IsMatch(name, "^snapshot-[0-9a-f]{32}$")) throw new InvalidDataException("Invalid snapshot name.");
                if (root.GetProperty("generation").GetInt64() != readGeneration() || root.GetProperty("model").GetString() != space.Fingerprint)
                    throw new InvalidDataException("Snapshot generation or model differs from committed state.");
                string directory = Path.Combine(DirectoryPath, name);
                foreach (var file in root.GetProperty("files").EnumerateObject())
                    if (Path.GetFileName(file.Name) != file.Name || FilePaths.Hash(Path.Combine(directory, file.Name)) != file.Value.GetString())
                        throw new InvalidDataException("Snapshot checksum mismatch.");
                int count = readVectors().Count;
                if (count > 0 && new[] { "ann", "ann.data", "ann.tags" }.Any(file => !root.GetProperty("files").TryGetProperty(file, out _)))
                    throw new InvalidDataException("Snapshot files are incomplete.");
                capacity = CapacityFor(count);
                index = new NativeIndex(space.Dimensions, capacity, threads);
                if (count > 0) index.Load(Path.Combine(directory, "ann"));
                allocated = count; checkpointGeneration = readGeneration();
                return;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException or KeyNotFoundException or InvalidOperationException)
            {
                Recovery = "Recovered DiskANN from durable vectors: " + error.Message;
                log(Recovery);
            }
        }
        Rebuild();
    }
    public void Checkpoint()
    {
        long generation = readGeneration();
        if (generation == checkpointGeneration) return;
        string manifest = Path.Combine(DirectoryPath, "current.json");
        string? previous = null;
        if (File.Exists(manifest))
        {
            try { using var old = JsonDocument.Parse(File.ReadAllText(manifest)); previous = old.RootElement.GetProperty("snapshot").GetString(); }
            catch (Exception error) when (error is IOException or JsonException or KeyNotFoundException)
            { log("Previous manifest is unreadable; retaining its snapshot: " + error.Message); }
        }
        string name = "snapshot-" + Guid.NewGuid().ToString("N"), directory = Path.Combine(DirectoryPath, name);
        Directory.CreateDirectory(directory);
        var vectors = readVectors();
        if (vectors.Count > 0) index!.SavePacked(Path.Combine(directory, "ann"), vectors);
        var files = new Dictionary<string, string>();
        foreach (string file in Directory.EnumerateFiles(directory))
        {
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.Read)) stream.Flush(true);
            files[Path.GetFileName(file)] = FilePaths.Hash(file);
        }
        string temporary = manifest + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(stream, new { snapshot = name, generation, model = space.Fingerprint, files }, Protocol.Json);
            stream.Flush(true);
        }
        File.Move(temporary, manifest, true);
        int size = CapacityFor(vectors.Count);
        var replacement = new NativeIndex(space.Dimensions, size, threads);
        try { if (vectors.Count > 0) replacement.Load(Path.Combine(directory, "ann")); }
        catch { replacement.Dispose(); throw; }
        index?.Dispose(); index = replacement; capacity = size; allocated = vectors.Count;
        failure = null; checkpointGeneration = generation;
        if (previous is not null && previous != name && Regex.IsMatch(previous, "^snapshot-[0-9a-f]{32}$"))
        {
            string old = Path.Combine(DirectoryPath, previous);
            try { if (Directory.Exists(old) && !FilePaths.IsNameSurrogate(old)) Directory.Delete(old, true); }
            catch (IOException error) { log("Retained an in-use old snapshot: " + error.Message); }
        }
    }
    public void Discard()
    {
        Dispose();
        if (FilePaths.IsNameSurrogate(DirectoryPath)) throw new IOException("The index directory is a reparse link; cleanup was refused.");
        foreach (string file in new[] { "current.json", "current.json.tmp" }) File.Delete(Path.Combine(DirectoryPath, file));
        foreach (string directory in Directory.EnumerateDirectories(DirectoryPath))
            if (Regex.IsMatch(Path.GetFileName(directory), "^snapshot-[0-9a-f]{32}$") && !FilePaths.IsNameSurrogate(directory))
                Directory.Delete(directory, true);
    }
    public void Dispose() { index?.Dispose(); index = null; }
}
