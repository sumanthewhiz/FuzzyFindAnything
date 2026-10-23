using System.Text.Json;

namespace FuzzyFindAnything.Core;

public static class Protocol
{
    public static JsonSerializerOptions Json { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };
}

public static class ModelContract
{
    public const int Dimensions = 768;
    public const string Fingerprint = "google/embeddinggemma-2@914f7f89142e33e77833254d9c9b90c3cef7303b:768:f32:v1";
    public const string Revision = "914f7f89142e33e77833254d9c9b90c3cef7303b";
    public const string NativeAssetId = "embeddinggemma-2-onnx-v1";
    public const int TextBatchSize = 8;
    public const int ImageBatchSize = 2;
    public const int PassageTokens = 512;
    public const int PassageStride = 448;

    public static float[] Normalize(ReadOnlySpan<float> values, int dimensions = Dimensions)
    {
        if (values.Length != dimensions)
            throw new InvalidDataException($"Expected {dimensions} vector dimensions.");
        double sum = 0;
        foreach (float value in values)
        {
            if (!float.IsFinite(value)) throw new InvalidDataException("The embedding contains a non-finite value.");
            sum += (double)value * value;
        }
        double norm = Math.Sqrt(sum);
        if (norm < 1e-12) throw new InvalidDataException("The embedding is empty or zero-length.");
        float[] result = new float[dimensions];
        for (int i = 0; i < result.Length; i++) result[i] = (float)(values[i] / norm);
        return result;
    }

    public static byte[] Bytes(float[] vector)
    {
        byte[] bytes = new byte[checked(vector.Length * sizeof(float))];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public static float[] Vector(byte[] bytes, int dimensions = Dimensions)
    {
        if (bytes.Length != dimensions * sizeof(float))
            throw new InvalidDataException("Invalid durable embedding length.");
        float[] vector = new float[dimensions];
        Buffer.BlockCopy(bytes, 0, vector, 0, bytes.Length);
        if (vector.Any(value => !float.IsFinite(value)))
            throw new InvalidDataException("Invalid durable embedding values.");
        return vector;
    }

    public static double Similarity(float[] first, float[] second)
    {
        if (first.Length != second.Length) throw new InvalidDataException("Cannot compare different embedding spaces.");
        double value = 0;
        for (int i = 0; i < first.Length; i++) value += (double)first[i] * second[i];
        return Math.Clamp(value, -1, 1);
    }
}

public sealed record FolderState(int Id, string Path, string Status = "In Progress", string Detail = "",
    int Completed = 0, int Total = 0, int Errors = 0, int Succeeded = 0, int Failed = 0, int Queued = 0, int Active = 0,
    int Partial = 0, int Skipped = 0);
public sealed record IndexInfo(string TypeVersion, string DatabaseLocation, long TotalFiles, long TotalDocuments,
    long TotalImages, long DatabaseSizeBytes, string? LastRefreshed, long TotalCodeFiles = 0);
public sealed record ResultFile(string Path, string Name, string Kind, long Size, long MtimeNs, string Snippet, double Score,
    string Evidence = "", string MatchedChunk = "", ChunkMetadata? Chunk = null, ImageRegion? Region = null);
public sealed record CpuState(int TotalCores, int CoreLimit, int WorkerCount, int ThreadsPerWorker,
    int FileWorkerCount, int LastBatchSize);
public sealed record ActivityState(string Path, string Stage, int CompletedPassages, int TotalPassages);
public sealed record LogState(bool Enabled, string Directory, string Error);
public sealed record StartupState(int Sequence, int Percent, int CompletedSteps, int TotalSteps,
    string Stage, string State, string Error = "");
public sealed record BatchState(long CompletedBatches, long CompletedInputs, int LastBatchSize,
    int LargestBatch, int ActiveBatches, int PendingRequests);
public sealed record EngineState(string ModelState, string Message, string Error, List<FolderState> Folders,
    long FileCount, long VectorCount, string Monitor, string Recovery, IndexInfo IndexInfo, CpuState Cpu,
    int QueueCount, int ActiveCount, List<ActivityState> Activities, LogState Logging, StartupState Startup,
    BatchState InferenceBatches, int ReconcilingFolders = 0, ModelConfigurationState? Models = null);
public sealed record ExtractedContent(string Text, string[] Warnings, string Format);
public sealed class ContentException(string message, string code, bool retryable = false) : IOException(message)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
}
public sealed class NativeIndexException(string message) : IOException(message);

public sealed record Fingerprint(long MtimeNs, long Size, string Identity);
public sealed record IndexedFile(long Id, string Path, string PathKey, Fingerprint Fingerprint, string Kind,
    string? Error, string? ErrorCode, double? RetryAfter, int FailureCount, string? Warning, string ContentHash);
public sealed record IndexJob(long Id, string Path, string PathKey, Fingerprint Fingerprint, string ContentHash,
    string Kind, int Revision, bool Prepared, int Attempts, double ElapsedMs);
public sealed record Passage(int Ordinal, string Content, string Representation = "", long? RepresentationId = null, ImageRegion? Region = null);

public sealed class StartupProgress(Action<StartupState>? report = null)
{
    private readonly object sync = new();
    private StartupState value = new(0, 0, 0, 9, "Starting local engine", "Loading");
    public StartupState Snapshot { get { lock (sync) return value; } }

    public void Update(int completed, string stage, string state = "Loading", string error = "")
    {
        if (completed is < 0 or > 9 || state is not ("Loading" or "Ready" or "Error") || (state == "Ready" && completed != 9))
            throw new ArgumentException("Invalid startup checkpoint.");
        StartupState next;
        lock (sync)
        {
            if (value.State is "Ready" or "Error" || completed < value.CompletedSteps) return;
            next = value = new(value.Sequence + 1, completed * 100 / 9, completed, 9, stage, state, error);
        }
        report?.Invoke(next);
    }

    public void ModelProgress(int completed, string stage) => Update(2 + completed, stage);
    public void Ready() => Update(9, "Search engine ready", "Ready");
    public void Fail(Exception error) => Update(Snapshot.CompletedSteps, "Search engine unavailable", "Error", error.Message);
}
