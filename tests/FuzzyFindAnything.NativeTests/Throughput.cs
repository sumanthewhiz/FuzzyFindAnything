using System.Diagnostics;
using System.IO;
using System.Text.Json;
using FuzzyFindAnything.Core;

namespace FuzzyFindAnything.NativeTests;

internal static class Throughput
{
    public static async Task RunAsync(string package, string models, string output)
    {
        package = Path.GetFullPath(package); models = Path.GetFullPath(models);
        Directory.CreateDirectory(output);
        CpuBudget budget = CpuBudget.Apply();
        await using var pool = new ModelPool(Path.Combine(package, "inference", "FuzzyFindAnything.Inference.exe"),
            models, budget, Console.Error.WriteLine, () => 7L << 30);
        await pool.StartAsync((_, _) => { }, default);
        await pool.QueryAsync("parsing structured records", default);
        var results = new List<object>();
        var elapsed = new List<double>();
        foreach (int count in new[] { 1, Math.Min(8, budget.Processors) }.Distinct())
            await Check.TemporaryAsync(async root =>
            {
                string corpus = Path.Combine(root, "files"); Directory.CreateDirectory(corpus);
                for (int index = 0; index < 18; index++)
                    await File.WriteAllTextAsync(Path.Combine(corpus, $"source-{index}.py"),
                        $"def parse_records_{index}(rows):\n" +
                        "    # Validate records, discard missing values, normalize fields, and store the result.\n" +
                        "    return [row.strip().lower() for row in rows if row]\n");
                string data = Path.Combine(root, "index");
                await using var engine = new IndexingEngine(data, models, Path.Combine(root, "unused.exe"), package,
                    budget, new Borrowed(pool), new EngineLog(data), new(), watch: false)
                { FileWorkerCount = count };
                engine.AddFolder(corpus);
                BatchState before = pool.Statistics;
                double cpuBefore = ProcessInventory.Capture(Environment.ProcessId, false).Sum(value => value.CpuSeconds);
                var timer = Stopwatch.StartNew();
                engine.Start();
                int active = 0;
                await Check.UntilAsync(() =>
                {
                    var state = engine.Status(); active = Math.Max(active, state.ActiveCount);
                    if (state.ModelState == "Error") throw new IOException(state.Error);
                    return engine.Store.Counts().Files == 18;
                }, "Native warm benchmark did not finish.", 240000);
                double seconds = timer.Elapsed.TotalSeconds;
                double cpu = ProcessInventory.Capture(Environment.ProcessId, false).Sum(value => value.CpuSeconds) - cpuBefore;
                BatchState after = pool.Statistics;
                long batches = after.CompletedBatches - before.CompletedBatches, inputs = after.CompletedInputs - before.CompletedInputs;
                var result = new
                {
                    file_workers = count,
                    files = 18,
                    seconds,
                    files_per_second = 18 / seconds,
                    maximum_active_files = active,
                    inference_batches = batches,
                    mean_batch_size = inputs / (double)batches,
                    average_cpu_cores = cpu / seconds,
                    core_limit = budget.Processors,
                    model_processes = pool.ReadyWorkers,
                    model_threads = pool.Threads
                };
                results.Add(result); elapsed.Add(seconds); Console.WriteLine(JsonSerializer.Serialize(result));
            });
        var inventory = ProcessInventory.Capture(Environment.ProcessId);
        Program.Require(inventory.All(process => !process.Name.Contains("python", StringComparison.OrdinalIgnoreCase) &&
            process.Modules.All(path => !Path.GetFileName(path).StartsWith("python", StringComparison.OrdinalIgnoreCase))),
            "An interpreter appeared in the native process/module inventory.");
        Program.Require(inventory.All(process => (ulong)process.Affinity == (ulong)budget.AffinityMask), "A descendant widened the shared CPU mask.");
        File.WriteAllText(Path.Combine(output, "throughput.json"), JsonSerializer.Serialize(new
        {
            results,
            throughput_ratio = elapsed.Count == 2 ? elapsed[0] / elapsed[1] : 1,
            processes = inventory
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private sealed class Borrowed(IEmbeddings model) : IEmbeddings
    {
        public int ReadyWorkers => model.ReadyWorkers;
        public int Threads => model.Threads;
        public int BatchConcurrency => model.BatchConcurrency;
        public string Warning => model.Warning;
        public BatchState Statistics => model.Statistics;
        public Task StartAsync(Action<int, string> progress, CancellationToken cancellation) { progress(6, "Warm benchmark model"); return Task.CompletedTask; }
        public Task<string[]> SplitAsync(string text, CancellationToken cancellation) => model.SplitAsync(text, cancellation);
        public Task<float[][]> DocumentsAsync(string title, string[] passages, CancellationToken cancellation) => model.DocumentsAsync(title, passages, cancellation);
        public Task<float[]> ImageAsync(string path, CancellationToken cancellation) => model.ImageAsync(path, cancellation);
        public Task<float[]> QueryAsync(string text, CancellationToken cancellation) => model.QueryAsync(text, cancellation);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
