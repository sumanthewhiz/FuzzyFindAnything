using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using FuzzyFindAnything.Core;

namespace FuzzyFindAnything.App.Services;

public sealed class EngineClient : IAsyncDisposable
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    private readonly string dataDirectory;
    private readonly DiagnosticLog diagnostics;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> pending = new();
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private Process? process;
    private SafeFileHandle? job;
    private Task? outputReader;
    private Task? errorReader;
    private bool stopping;
    private string? lastModelState;

    public event Action<EngineStatus>? StatusChanged;
    public event Action<StartupStatus>? StartupChanged;
    public event Action<string>? Failed;
    public event Action<string>? Warning;
    public event Action<string>? ModelStatusChanged;

    public EngineClient(string dataDirectory, ApplicationSettings settings)
    {
        this.dataDirectory = dataDirectory;
        diagnostics = new DiagnosticLog(settings);
        diagnostics.Failed += message => Warning?.Invoke(message);
    }

    public void ConfigureLogging(ApplicationSettings settings) => diagnostics.Configure(settings);

    public void Start()
    {
        if (process is not null)
            throw new InvalidOperationException("The local engine has already been started.");
        CpuBudget budget = CpuBudget.Apply();
        string appDirectory = AppContext.BaseDirectory;
        string configurationPath = Path.Combine(appDirectory, "runtime.json");
        if (!File.Exists(configurationPath))
            throw new FileNotFoundException("Runtime configuration is missing. Run Build.ps1 and launch the app in artifacts.", configurationPath);
        var configuration = JsonSerializer.Deserialize<RuntimeConfiguration>(File.ReadAllText(configurationPath), JsonOptions)
            ?? throw new InvalidDataException("runtime.json is empty.");
        string Resolve(string path) => Path.GetFullPath(path, appDirectory);
        string engine = Resolve(configuration.Engine);
        string inference = Resolve(configuration.Inference);
        string model = Resolve(configuration.Model);
        string extractor = Resolve(configuration.Extractor);
        if (!File.Exists(engine) || !File.Exists(inference) || !File.Exists(extractor))
            throw new FileNotFoundException("The native engine, inference host, or IFilter helper is missing. Extract the complete application package.");
        Directory.CreateDirectory(dataDirectory);
        var start = new ProcessStartInfo(engine)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = appDirectory
        };
        foreach (string argument in new[]
        {
            "--data-dir", dataDirectory, "--model", model, "--extractor", extractor,
            "--inference", inference, "--app-dir", appDirectory
        })
            start.ArgumentList.Add(argument);
        process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the local indexing engine.");
        try
        {
            job = CreateJobObject(IntPtr.Zero, null);
            if (job.IsInvalid)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var limits = new ExtendedLimitInformation();
            limits.BasicLimitInformation.LimitFlags = 0x2000 | 0x10;
            limits.BasicLimitInformation.Affinity = budget.AffinityMask;
            if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimitInformation>()) ||
                !AssignProcessToJobObject(job, process.SafeHandle))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not bind the background engine to the app lifetime.");
        }
        catch
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            job?.Dispose();
            throw;
        }
        outputReader = Task.Run(() => ReadOutputAsync(process));
        errorReader = Task.Run(() => ReadErrorsAsync(process));
    }

    public async Task<T> RequestAsync<T>(string command, object? arguments = null, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        Process active = process ?? throw new InvalidOperationException("The local engine is not started.");
        if (active.HasExited)
            throw new InvalidOperationException("The local engine has stopped. Restart the app; details are in the engine log.");
        string identifier = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[identifier] = completion;
        using var requestLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        try
        {
            string message = JsonSerializer.Serialize(new { id = identifier, command, args = arguments }, JsonOptions);
            await writeLock.WaitAsync(requestLifetime.Token);
            try
            {
                requestLifetime.Token.ThrowIfCancellationRequested();
                // Once writing starts, finish the complete JSON frame even if this request is cancelled.
                await active.StandardInput.WriteLineAsync(message);
                await active.StandardInput.FlushAsync(lifetime.Token);
            }
            finally
            {
                writeLock.Release();
            }
            JsonElement result = await completion.Task.WaitAsync(timeout ?? TimeSpan.FromMinutes(3), requestLifetime.Token);
            return result.Deserialize<T>(JsonOptions)
                ?? throw new InvalidDataException("The engine returned an empty response.");
        }
        finally
        {
            pending.TryRemove(identifier, out _);
        }
    }

    private async Task ReadOutputAsync(Process active)
    {
        try
        {
            while (await active.StandardOutput.ReadLineAsync(lifetime.Token) is { } line)
            {
                using var document = JsonDocument.Parse(line);
                JsonElement root = document.RootElement;
                if (root.TryGetProperty("event", out JsonElement eventName))
                {
                    if (eventName.GetString() == "startup")
                    {
                        var startup = root.GetProperty("data").Deserialize<StartupStatus>(JsonOptions)
                            ?? throw new InvalidDataException("The engine returned empty startup progress.");
                        if (startup.Sequence < 0 || startup.Percent is < 0 or > 100 ||
                            startup.State is not ("Loading" or "Ready" or "Error") ||
                            (startup.State == "Ready" && startup.Percent != 100))
                            throw new InvalidDataException("The engine returned invalid startup progress.");
                        StartupChanged?.Invoke(startup);
                    }
                    else if (eventName.GetString() == "model_status")
                        ModelStatusChanged?.Invoke(root.GetProperty("message").GetString() ?? "");
                    else if (eventName.GetString() == "status")
                    {
                        var status = root.GetProperty("data").Deserialize<EngineStatus>(JsonOptions)
                            ?? throw new InvalidDataException("The engine returned an empty status.");
                        if (status.ModelState != lastModelState)
                        {
                            lastModelState = status.ModelState;
                            Log($"Received engine state: {status.ModelState}; folders: {status.Folders.Count}");
                        }
                        StatusChanged?.Invoke(status);
                    }
                    else if (eventName.GetString() == "fatal")
                    {
                        throw new InvalidOperationException(root.GetProperty("error").GetString());
                    }
                }
                else if (root.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.String &&
                         pending.TryGetValue(id.GetString()!, out var completion))
                {
                    if (root.GetProperty("ok").GetBoolean())
                        completion.TrySetResult(root.GetProperty("result").Clone());
                    else
                        completion.TrySetException(new EngineRequestException(root.GetProperty("error").GetString() ?? "The engine request failed.",
                            root.TryGetProperty("configuration_applied", out var applied) && applied.GetBoolean()));
                }
            }
            if (!stopping)
                throw new IOException("The local indexing engine exited. See engine.log and native-engine.log for details.");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Log(exception.ToString());
            foreach (var completion in pending.Values)
                completion.TrySetException(exception);
            if (!stopping)
                Failed?.Invoke(exception.Message);
        }
    }

    private async Task ReadErrorsAsync(Process active)
    {
        try
        {
            while (await active.StandardError.ReadLineAsync(lifetime.Token) is { } line)
                diagnostics.Native(line);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Log($"Could not capture engine diagnostics: {exception}");
            if (!stopping)
                Failed?.Invoke($"Could not capture engine diagnostics: {exception.Message}");
        }
    }

    public void Log(string message) => diagnostics.App(message);

    public async ValueTask DisposeAsync()
    {
        if (stopping)
            return;
        stopping = true;
        if (process is { HasExited: false } active)
        {
            try
            {
                await RequestAsync<JsonElement>("shutdown", timeout: TimeSpan.FromSeconds(10));
                await active.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(35));
            }
            catch (Exception exception) when (exception is TimeoutException or IOException or InvalidOperationException)
            {
                Log($"Stopping the engine after a shutdown failure. Committed work is recoverable. {exception}");
                if (!active.HasExited)
                    active.Kill(entireProcessTree: true);
            }
        }
        lifetime.Cancel();
        foreach (var completion in pending.Values)
            completion.TrySetCanceled();
        if (outputReader is not null)
            await outputReader;
        if (errorReader is not null)
            await errorReader;
        job?.Dispose();
        process?.Dispose();
        lifetime.Dispose();
        writeLock.Dispose();
    }

    private sealed record RuntimeConfiguration(string Engine, string Inference, string Model, string Extractor);

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass,
        ref ExtendedLimitInformation information, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
}

public sealed class EngineRequestException(string message, bool configurationApplied) : InvalidOperationException(message)
{
    public bool ConfigurationApplied { get; } = configurationApplied;
}

public sealed record EngineStatus(
    string ModelState, string Message, string Error, List<FolderStatus> Folders,
    long FileCount, long VectorCount, string Monitor, string Recovery, IndexMetadata IndexInfo,
    CpuStatus Cpu, int QueueCount, int ActiveCount, List<IndexActivity> Activities, LoggingStatus Logging,
    StartupStatus? Startup = null, int ReconcilingFolders = 0, ModelConfigurationState? Models = null);

public sealed record StartupStatus(
    int Sequence, int Percent, int CompletedSteps, int TotalSteps, string Stage, string State, string Error);

public sealed record LoggingStatus(bool Enabled, string Directory, string Error);

public sealed record CpuStatus(int TotalCores, int CoreLimit, int WorkerCount, int ThreadsPerWorker,
    int FileWorkerCount = 0, int LastBatchSize = 0);

public sealed record IndexActivity(string Path, string Stage, int CompletedPassages, int TotalPassages);

public sealed record IndexMetadata(
    string TypeVersion, string DatabaseLocation, long TotalFiles, long TotalDocuments, long TotalImages,
    long DatabaseSizeBytes, string? LastRefreshed, long TotalCodeFiles = 0);

public sealed record FolderStatus(
    int Id, string Path, string Status, string Detail, int Completed, int Total, int Errors,
    int Succeeded, int Failed, int Queued, int Active);

public sealed record SearchResult(
    string Path, string Name, string Kind, long Size, long MtimeNs, string Snippet, double Score)
{
    public string Glyph => Kind switch { "Image" => "\uEB9F", "Code" => "\uE943", _ => "\uE8A5" };
    public string Extension => System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant() is { Length: > 0 } extension
        ? extension : Kind.ToUpperInvariant();
    public string ScoreLabel => $"{Score:0.000} similarity";
    public string Metadata => $"{Extension}   /   {(Size >= 1048576 ? $"{Size / 1048576d:0.#} MB" : $"{Math.Max(1, Size / 1024d):0.#} KB")}";
}
