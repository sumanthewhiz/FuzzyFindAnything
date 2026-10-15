using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Threading.Channels;
using FuzzyFindAnything.Core;

namespace FuzzyFindAnything.NativeTests;

internal static class SchedulingTests
{
    private const long Gib = 1L << 30;
    private static CpuBudget Budget => new(12, 6, (nuint)Process.GetCurrentProcess().ProcessorAffinity);

    public static async Task RunAsync()
    {
        Program.Run("Physical-core, inherited-affinity and memory plans", Plans);
        Program.Run("Monotonic startup checkpoints and terminal failures", Progress);
        await BatchingAsync();
        await PoolsAsync();
        await PipelinesAsync();
        await ResumeAsync();
        await StartupCancellationAsync();
    }
    private static void Plans()
    {
        var masks = Enumerable.Range(0, 16).Select(index => 3UL << (2 * index)).ToArray();
        ulong selected = CpuBudget.Select(masks, uint.MaxValue, 16);
        Program.Require(BitOperations.PopCount(selected) == 8 && masks.All(mask => BitOperations.PopCount(selected & mask) <= 1),
            "SMT siblings or more than half the physical cores were selected.");
        Program.Require(CpuBudget.Select([1, 2, 4, 8], 5, 4) == 5 &&
            BitOperations.PopCount(CpuBudget.Select([1, 2, 4, 8, 16], 31, 5)) == 2, "Inherited/odd-core restrictions were widened.");
        Check.Throws<InvalidOperationException>(() => CpuBudget.Select([1], 1, 1));
        Program.Require(ModelPool.Plan(6, 10 * Gib) == (2, 3) && ModelPool.Plan(6, 7 * Gib) == (1, 6) &&
            ModelPool.Plan(1, 16 * Gib) == (1, 1), "Memory-aware model/thread planning differs.");
        Check.Throws<IOException>(() => ModelPool.Plan(6, 4 * Gib));
    }
    private static void Progress()
    {
        var states = new List<StartupState>();
        var startup = new StartupProgress(states.Add);
        startup.Update(1, "Catalog ready"); startup.Update(2, "Monitoring ready");
        for (int index = 1; index <= 6; index++) startup.ModelProgress(index, "Checkpoint " + index);
        Program.Require(startup.Snapshot.Percent < 100, "Startup reported readiness before all checkpoints.");
        startup.Ready(); startup.Update(1, "stale");
        Program.Require(startup.Snapshot is { State: "Ready", Percent: 100 } &&
            states.Select(value => value.Sequence).Distinct().Count() == states.Count &&
            states.Select(value => value.Percent).SequenceEqual(states.Select(value => value.Percent).Order()),
            "Startup progress regressed or reused a sequence.");
        var failed = new StartupProgress();
        failed.ModelProgress(2, "Loading"); failed.Fail(new IOException("missing model")); failed.Ready();
        Program.Require(failed.Snapshot.State == "Error" && failed.Snapshot.Percent < 100 &&
            failed.Snapshot.Error.Contains("missing"), "Startup failure became false readiness.");
    }
    private static async Task BatchingAsync()
    {
        var calls = new List<(string Operation, string[] Inputs)>();
        await using (var batcher = new InferenceBatcher((operation, values, _) =>
        {
            calls.Add((operation, values));
            return Task.FromResult(values.Select(value => Program.Vector(int.Parse(value.Split('-')[1]) * 2 +
                (value.EndsWith('b') ? 1 : 0))).ToArray());
        }, _ => { }, default))
        {
            var tasks = Enumerable.Range(0, 6).Select(index => batcher.SubmitAsync("documents",
                [$"file-{index}-a", $"file-{index}-b"], default)).ToArray();
            batcher.AddWorker();
            var result = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
            for (int index = 0; index < 6; index++)
                Program.Require(result[index][0][index * 2] == 1 && result[index][1][index * 2 + 1] == 1, "Cross-file output ownership changed.");
            Program.Require(calls.Select(call => call.Inputs.Length).SequenceEqual([8, 4]) &&
                batcher.Statistics.CompletedInputs == 12, "Text batching bounds/counters differ.");
        }
        Console.WriteLine("PASS cross-file aggregation, output order and batch accounting");
        calls.Clear();
        await using (var batcher = new InferenceBatcher((operation, values, _) =>
        { calls.Add((operation, values)); return Task.FromResult(values.Select(_ => Program.Vector(0)).ToArray()); }, _ => { }, default))
        {
            var tasks = new[]
            {
                batcher.SubmitAsync("images", ["one"], default), batcher.SubmitAsync("images", ["two"], default),
                batcher.SubmitAsync("images", ["three"], default), batcher.SubmitAsync("documents", [new string('x', 64)], default),
                batcher.SubmitAsync("documents", [new string('x', 2048)], default), batcher.SubmitAsync("documents", [new string('x', 64)], default)
            };
            batcher.AddWorker();
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
            Program.Require(calls.Select(call => (call.Operation, call.Inputs.Length)).SequenceEqual(
                [("images", 2), ("images", 1), ("documents", 1), ("documents", 1), ("documents", 1)]),
                "Images/text mixed, exceeded bounds, or padded incompatible lengths.");
        }
        Console.WriteLine("PASS image/text separation and padding buckets");
        var messages = new List<string>();
        await using (var batcher = new InferenceBatcher((_, values, _) =>
        {
            if (values.Contains("bad")) throw new ContentException("Invalid image", "invalid_image");
            return Task.FromResult(values.Select(_ => Program.Vector(1)).ToArray());
        }, messages.Add, default))
        {
            var bad = batcher.SubmitAsync("images", ["bad"], default);
            var good = batcher.SubmitAsync("images", ["good"], default);
            batcher.AddWorker();
            await Check.ThrowsAsync<ContentException>(async () => await bad);
            Program.Require((await good)[0][1] == 1 && messages.Count == 1, "One corrupt image failed a healthy neighbor without isolation diagnostics.");
        }
        await using (var batcher = new InferenceBatcher((_, _, _) => Task.FromResult(new[] { Program.Vector(0) }), _ => { }, default))
        {
            var incomplete = batcher.SubmitAsync("documents", ["one", "two"], default);
            batcher.AddWorker();
            await Check.ThrowsAsync<InvalidDataException>(async () => await incomplete);
        }
        Console.WriteLine("PASS per-request failure isolation and incomplete-output rejection");
        var blocked = new InferenceBatcher((_, _, _) => throw new InvalidOperationException("Cancelled work was executed."), _ => { }, default);
        var waiting = Enumerable.Range(0, 100).Select(_ => blocked.SubmitAsync("documents", ["pending"], default)).ToArray();
        Program.Require(blocked.Statistics.PendingRequests == 64, "Pending producer capacity is not bounded at 64.");
        await blocked.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        foreach (var task in waiting) await Check.ThrowsAsync<OperationCanceledException>(async () => await task);
        await Check.ThrowsAsync<OperationCanceledException>(async () => await blocked.SubmitAsync("documents", ["late"], default));
        Console.WriteLine("PASS queue backpressure and cancellation of pending producers");
    }
    private static async Task PoolsAsync() => await Check.TemporaryAsync(async root =>
    {
        string scenario = Check.Scenario(root, new { block_first = true });
        await using (var pool = new ModelPool(Check.Executable, scenario, Budget, _ => { }, () => 7 * Gib))
        {
            var progress = new List<int>();
            await pool.StartAsync((done, _) => progress.Add(done), default);
            Program.Require(progress.SequenceEqual(Enumerable.Range(1, 6)) && pool.ReadyWorkers == 1, "IPC progress was confused with readiness.");
            var first = pool.DocumentsAsync("first", ["body"], default);
            await Check.UntilAsync(() => File.Exists(Path.Combine(scenario, "entered")), "First batch did not enter.");
            var query = pool.QueryAsync("find", default);
            var second = pool.DocumentsAsync("second", ["body"], default);
            await File.WriteAllTextAsync(Path.Combine(scenario, "release"), "go");
            await Task.WhenAll(first, query, second).WaitAsync(TimeSpan.FromSeconds(5));
            Program.Require(File.ReadAllLines(Path.Combine(scenario, "operations-1.txt")).SequenceEqual(["documents", "query", "documents"]),
                "A pending inference batch overtook an interactive query.");
            int pid = int.Parse(File.ReadAllText(Path.Combine(scenario, "worker-1.pid")));
            using (var worker = Process.GetProcessById(pid)) { worker.Kill(); await worker.WaitForExitAsync(); }
            await pool.QueryAsync("recover", default);
            Program.Require(File.Exists(Path.Combine(scenario, "worker-2.pid")) && pool.ReadyWorkers == 1, "A dead worker was not replaced.");
            using var current = Process.GetCurrentProcess();
            Program.Require(long.Parse(File.ReadAllText(Path.Combine(scenario, "worker-2.mask"))) == current.ProcessorAffinity.ToInt64(),
                "A descendant widened the shared CPU mask.");
            await Check.ThrowsAsync<InvalidOperationException>(() => pool.StartAsync((_, _) => { }, default));
        }
        Console.WriteLine("PASS query priority, process replacement, inherited CPU mask and start-once guard");
        scenario = Check.Scenario(root, new { hang_secondary = true });
        var warming = new ModelPool(Check.Executable, scenario, Budget, _ => { }, () => 16 * Gib);
        await warming.StartAsync((_, _) => { }, default).WaitAsync(TimeSpan.FromSeconds(10));
        await Check.UntilAsync(() => File.Exists(Path.Combine(scenario, "worker-2.pid")), "Secondary startup did not begin.");
        Program.Require(warming.ReadyWorkers == 1 && (await warming.QueryAsync("usable", default))[0] == 1,
            "Optional warmup delayed primary readiness.");
        await warming.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        foreach (string file in Directory.GetFiles(scenario, "*.pid"))
        {
            int pid = int.Parse(File.ReadAllText(file));
            Program.Require(!Alive(pid), "Cancellation left a native inference process running.");
        }
        Console.WriteLine("PASS first-model readiness and cancellation of optional warmup");
        scenario = Check.Scenario(root, new { fail_secondary = true });
        await using (var pool = new ModelPool(Check.Executable, scenario, Budget, _ => { }, () => 16 * Gib))
        {
            await pool.StartAsync((_, _) => { }, default);
            await Check.UntilAsync(() => pool.Warning.Length > 0, "Secondary failure was not reported.");
            Program.Require(pool.Warning.Contains("secondary failed") && pool.ReadyWorkers == 1 &&
                (await pool.QueryAsync("still ready", default))[0] == 1, "Secondary failure disabled the primary model.");
        }
        scenario = Check.Scenario(root, new { hang_startup = true });
        var timer = Stopwatch.StartNew();
        await Check.ThrowsAsync<TimeoutException>(async () =>
            await InferenceClient.StartAsync(Check.Executable, scenario, 1, null, _ => { }, default, TimeSpan.FromSeconds(2)));
        Program.Require(timer.Elapsed < TimeSpan.FromSeconds(5), "Progress events extended the startup deadline.");
        using (var cancellation = new CancellationTokenSource(300))
            await Check.ThrowsAsync<OperationCanceledException>(async () =>
                await InferenceClient.StartAsync(Check.Executable, scenario, 1, null, _ => { }, cancellation.Token));
        scenario = Check.Scenario(root, new { malformed = true });
        await using (var client = await InferenceClient.StartAsync(Check.Executable, scenario, 1, null, _ => { }, default))
        {
            await Check.ThrowsAsync<ChannelClosedException>(async () => await client.RequestAsync("query", new { text = "bad" }, default));
            Program.Require(!client.Alive, "A malformed protocol worker was reused.");
        }
        Console.WriteLine("PASS optional failure isolation, bounded startup deadlines and malformed IPC cleanup");
    });
    private static bool Alive(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }
    private static IndexingEngine Engine(string root, IEmbeddings model, StartupProgress? startup = null, string? helper = null) =>
        new(Path.Combine(root, "index"), Path.Combine(root, "model"), helper ?? Path.Combine(root, "missing.exe"),
            Path.Combine(root, "app"), Budget, model, new EngineLog(Path.Combine(root, "index")), startup ?? new(), watch: false);
    private static async Task PipelinesAsync() => await Check.TemporaryAsync(async root =>
    {
        string source = Path.Combine(root, "files"); Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "barrier"), "six readers");
        for (int index = 0; index < 6; index++) File.WriteAllText(Path.Combine(source, $"source-{index}.pdf"), "isolated helper test");
        await using var engine = Engine(root, new FakeEmbeddings(), helper: Check.Executable);
        engine.AddFolder(source);
        engine.Start();
        int maximum = 0;
        await Check.UntilAsync(() =>
        {
            var state = engine.Status();
            maximum = Math.Max(maximum, state.Activities.Count);
            return engine.Store.Counts() == (6, 6);
        }, "Six independent file readers did not finish.", 30000);
        Program.Require(maximum == 6 && engine.Status().Cpu.FileWorkerCount == 6 && engine.Status().Cpu.WorkerCount == 1,
            "File concurrency collapsed to the number of model copies.");
        Program.Require((await engine.SearchAsync("apple", 0.6)).Count == 6, "Parallel reading lost file results.");
        Console.WriteLine("PASS six simultaneous file pipelines with one model");
    });
    private static async Task ResumeAsync() => await Check.TemporaryAsync(async root =>
    {
        string source = Path.Combine(root, "files"); Directory.CreateDirectory(source);
        string path = Path.Combine(source, "large.txt");
        string[] passages = Enumerable.Range(0, 24).Select(index => "apple passage " + index).ToArray();
        await File.WriteAllTextAsync(path, string.Join("\n", passages));
        var model = new FakeEmbeddings { BlockAfterFirstBatch = true };
        var engine = Engine(root, model);
        engine.AddFolder(source); engine.Start();
        await Check.UntilAsync(() => Check.Scalar(Path.Combine(root, "index"), "SELECT COUNT(embedding) FROM job_passages") >= 4,
            "First batch did not commit.");
        await engine.DisposeAsync();
        Program.Require(Check.Scalar(Path.Combine(root, "index"), "SELECT COUNT(*) FROM index_jobs WHERE state='pending' AND retry_at=0") == 1 &&
            Check.Scalar(Path.Combine(root, "index"), "SELECT COUNT(*) FROM files") == 0, "User exit recorded a retry failure or published a partial file.");
        int saved = (int)Check.Scalar(Path.Combine(root, "index"), "SELECT COUNT(embedding) FROM job_passages");
        var resumedModel = new FakeEmbeddings();
        await using var resumed = Engine(root, resumedModel);
        resumed.Start();
        await Check.UntilAsync(() => resumed.Store.Counts() == (1, 24), "Saved batches did not resume.");
        Program.Require(resumedModel.Embedded.Count == 24 - saved &&
            resumedModel.Embedded.All(value => !passages.Take(saved).Contains(value)), "Resume recomputed committed passages.");
        Console.WriteLine("PASS user-exit cancellation and exact committed-batch resume");
    });
    private static async Task StartupCancellationAsync() => await Check.TemporaryAsync(async root =>
    {
        var model = new FakeEmbeddings { BlockStartup = true };
        var startup = new StartupProgress();
        var engine = Engine(root, model, startup);
        engine.Start();
        await Check.UntilAsync(() => model.Started, "Model initialization did not start.");
        await engine.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Program.Require(startup.Snapshot.State != "Ready" && model.Disposed, "Shutdown published false readiness or retained a model.");
        Console.WriteLine("PASS cancellation during primary initialization");
    });
}

internal sealed class FakeEmbeddings : IEmbeddings
{
    public bool BlockAfterFirstBatch { get; init; }
    public bool BlockStartup { get; init; }
    public bool Started { get; private set; }
    public bool Disposed { get; private set; }
    private int batches;
    public ConcurrentBag<string> Embedded { get; } = [];
    public int ReadyWorkers => Started && !Disposed ? 1 : 0;
    public int Threads => 1;
    public int BatchConcurrency => 1;
    public string Warning => "";
    public BatchState Statistics => new(batches, Embedded.Count, 0, 0, 0, 0);
    public async Task StartAsync(Action<int, string> progress, CancellationToken cancellation)
    {
        Started = true;
        if (BlockStartup) await Task.Delay(Timeout.Infinite, cancellation);
        progress(6, "Test model ready");
    }
    public Task<string[]> SplitAsync(string text, CancellationToken cancellation) => Task.FromResult(text.Split('\n'));
    public async Task<float[][]> DocumentsAsync(string title, string[] passages, CancellationToken cancellation)
    {
        if (Interlocked.Increment(ref batches) > 1 && BlockAfterFirstBatch) await Task.Delay(Timeout.Infinite, cancellation);
        await Task.Delay(15, cancellation);
        foreach (string passage in passages) Embedded.Add(passage);
        return passages.Select(passage => Program.Vector(passage.Contains("apple") ? 0 : 1)).ToArray();
    }
    public Task<float[]> ImageAsync(string path, CancellationToken cancellation) => Task.FromResult(Program.Vector(2));
    public Task<float[]> QueryAsync(string text, CancellationToken cancellation) => Task.FromResult(Program.Vector(text.Contains("apple") ? 0 : 1));
    public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
}
