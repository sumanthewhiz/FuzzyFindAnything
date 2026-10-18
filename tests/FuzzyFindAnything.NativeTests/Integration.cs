using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Text.Json;
using FuzzyFindAnything.Core;

namespace FuzzyFindAnything.NativeTests;

internal static class Integration
{
    public static async Task RunAsync(string package, string models, string output)
    {
        package = Path.GetFullPath(package); models = Path.GetFullPath(models); output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        await Check.TemporaryAsync(async root =>
        {
            string corpus = Path.Combine(root, "files"), data = Path.Combine(root, "index");
            Fixtures.Corpus(corpus);
            var extractor = new TextExtractor(Path.Combine(package, "extractor", "FuzzyFindAnything.Extractor.exe"));
            foreach (string file in Directory.GetFiles(corpus, "*", SearchOption.AllDirectories).Where(path =>
                FilePaths.Kind(path) == "Document" && Path.GetExtension(path) != ".txt" && Path.GetFileName(path) != "broken.pdf"))
            {
                string hash = FilePaths.Hash(file);
                var extracted = await extractor.ExtractAsync(file, default);
                Program.Require(extracted.Text.Length > 20 && FilePaths.Hash(file) == hash, "Real IFilter failed or modified source: " + Path.GetFileName(file));
                Console.WriteLine("PASS installed IFilter: " + Path.GetExtension(file));
            }
            var watch = Stopwatch.StartNew();
            int initialFiles = 9;
            double startup, corpusSeconds, queryMs;
            int committed, total, maximumActive = 0;
            long originalId, affinity, peakMemory;
            BatchState statistics;
            List<StartupState> progress;
            List<ProcessInventory.Snapshot> processes;
            await using (var engine = new EngineProcess(package, models, data))
            {
                await engine.ReadyAsync(); startup = watch.Elapsed.TotalSeconds;
                progress = engine.Progress.ToList();
                Program.Require(progress.Select(value => value.Percent).SequenceEqual(progress.Select(value => value.Percent).Order()) &&
                    progress.Last().Percent == 100, "Native startup progress regressed.");
                await engine.RequestAsync<JsonElement>("add_folder", new { path = corpus });
                watch.Restart();
                await Check.UntilAsync(() =>
                {
                    if (engine.Status is { } state)
                    {
                        maximumActive = Math.Max(maximumActive, state.Activities.Count);
                        if (state.ModelState == "Error") throw new IOException(state.Error);
                        if (state.Folders.Count > 0 && state.QueueCount == 0 && state.Folders.All(value => value.Status == "Indexed") &&
                            state.FileCount != initialFiles)
                        {
                            File.Copy(Path.Combine(data, "Logs", "indexer.jsonl"), Path.Combine(output, "failed-corpus.jsonl"), true);
                            throw new IOException("Mixed corpus completed with unexpected outcomes; see failed-corpus.jsonl.");
                        }
                        return state.FileCount == initialFiles && state.QueueCount == 0;
                    }
                    return false;
                }, "The real mixed-format corpus did not finish.", 300000);
                corpusSeconds = watch.Elapsed.TotalSeconds;
                var state = await engine.RequestAsync<EngineState>("status");
                Program.Require(state.IndexInfo.TotalDocuments == 6 && state.IndexInfo.TotalCodeFiles == 1 &&
                    state.IndexInfo.TotalImages == 2 && state.Folders.Single().Failed == 2,
                    "Real corpus metadata or permanent error counts differ.");
                Program.Require(Check.Scalar(data, "SELECT COUNT(*) FROM files WHERE error_code='invalid_document'") == 1 &&
                    Check.Scalar(data, "SELECT COUNT(*) FROM files WHERE error_code='empty_file'") == 1, "Real PDF/empty-file classification regressed.");
                watch.Restart();
                var orchard = await engine.RequestAsync<List<ResultFile>>("search", new { query = "farmers harvesting ripe fruit from apple trees", threshold = 0.3 });
                queryMs = watch.Elapsed.TotalMilliseconds;
                Program.Require(orchard.First(value => value.Kind == "Document").Name == "orchard.txt", "Real document retrieval missed the orchard.");
                var code = await engine.RequestAsync<List<ResultFile>>("search", new { query = "Python function to parse a comma-separated CSV file", threshold = 0.3 });
                Program.Require(code.First(value => value.Kind == "Code").Name == "parser.py" && code.First(value => value.Kind == "Code").Snippet.Contains('\n'),
                    "Code retrieval or multiline previews regressed.");
                var image = await engine.RequestAsync<List<ResultFile>>("search", new { query = "a red circle on a white background", threshold = 0.3 });
                Program.Require(image.First(value => value.Kind == "Image").Name == "red-circle.png", "Real image retrieval returned the wrong shape.");
                originalId = Check.Scalar(data, "SELECT MIN(id) FROM vectors");
                affinity = engine.Affinity; peakMemory = engine.PeakMemory; statistics = state.InferenceBatches;
                processes = ProcessInventory.Capture(engine.Id);
                File.WriteAllText(Path.Combine(output, "processes.json"), JsonSerializer.Serialize(processes, new JsonSerializerOptions { WriteIndented = true }));
                Program.Require(processes.All(process => process.Affinity == affinity &&
                    !process.Name.Contains("python", StringComparison.OrdinalIgnoreCase) &&
                    process.Modules.All(module => !Path.GetFileName(module).StartsWith("python", StringComparison.OrdinalIgnoreCase))),
                    "The native process tree widened affinity or loaded an interpreter.");
                Program.Require(processes.Any(process => process.Modules.Any(module => Path.GetFileName(module).Equals("onnxruntime.dll", StringComparison.OrdinalIgnoreCase) &&
                    FilePaths.Within(module, package))) && processes.Any(process => process.Modules.Any(module => Path.GetFileName(module).Equals("ffa_diskann.dll", StringComparison.OrdinalIgnoreCase) &&
                    FilePaths.Within(module, package))), "The package did not load its own ONNX Runtime and DiskANN.");
                Program.Require(processes.SelectMany(process => process.Modules).Where(module =>
                    Path.GetFileName(module).Equals("msvcp140.dll", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(module).Equals("vcruntime140.dll", StringComparison.OrdinalIgnoreCase)).All(module => FilePaths.Within(module, package)),
                    "Native inference/indexing depended on a machine-installed C++ runtime.");
                Program.Require(BitOperations.PopCount((ulong)affinity) <= state.Cpu.TotalCores / 2 &&
                    state.Cpu.FileWorkerCount == Math.Min(8, state.Cpu.CoreLimit), "The coordinator exceeded its CPU budget or collapsed file concurrency.");
                Console.WriteLine($"PASS real corpus: {corpusSeconds:F2}s, maximum active files={maximumActive}, query={queryMs:F1}ms");
                string live = Path.Combine(corpus, "Live update.kt");
                await File.WriteAllTextAsync(live, "fun harvestApple() = \"fresh apples from the orchard\"");
                await engine.WaitAsync(value => value.FileCount == initialFiles + 1 && value.QueueCount == 0, "Live code creation was not indexed.");
                File.Move(live, Path.Combine(corpus, "Renamed update.kt"));
                await engine.WaitAsync(value => value.FileCount == initialFiles + 1 && value.QueueCount == 0 &&
                    Check.Scalar(data, "SELECT COUNT(*) FROM files WHERE path LIKE '%Renamed update.kt' AND error IS NULL") == 1, "Live code rename did not converge.");
                File.Delete(Path.Combine(corpus, "Renamed update.kt"));
                await engine.WaitAsync(value => value.FileCount == initialFiles && value.QueueCount == 0, "Live deletion was not indexed.");
                Program.Require(Check.Scalar(data, "SELECT MIN(id) FROM vectors") == originalId, "Live deltas reindexed unrelated completed files.");
                Console.WriteLine("PASS real change monitoring and delta-only vector publication");
                string large = Path.Combine(corpus, "large-resume.txt");
                await File.WriteAllTextAsync(large, string.Join("\n", Enumerable.Range(0, 1000).Select(index =>
                    $"Record {index}: orchard irrigation analysis, harvest estimates, seasonal apple production and distribution planning.")));
                await Check.UntilAsync(() => Check.Scalar(data, "SELECT COUNT(embedding) FROM job_passages") >= 4 &&
                    Check.Scalar(data, "SELECT COUNT(*) FROM job_passages WHERE embedding IS NULL") > 4,
                    "The large-file crash point was not reached.", 120000);
                await engine.CrashAsync();
                committed = (int)Check.Scalar(data, "SELECT COUNT(embedding) FROM job_passages");
                total = (int)Check.Scalar(data, "SELECT COUNT(*) FROM job_passages");
                Program.Require(committed > 0 && committed < total, "Crash did not interrupt a partially committed file.");
            }
            await using (var resumed = new EngineProcess(package, models, data))
            {
                await resumed.ReadyAsync();
                await resumed.WaitAsync(value => value.FileCount == initialFiles + 1 && value.QueueCount == 0, "Real native crash recovery did not finish.", 600000);
                var state = await resumed.RequestAsync<EngineState>("status");
                Program.Require(state.InferenceBatches.CompletedInputs == total - committed &&
                    Check.Scalar(data, "SELECT MIN(id) FROM vectors") == originalId,
                    "Restart recomputed committed passages or changed unrelated vectors.");
                var matches = await resumed.RequestAsync<List<ResultFile>>("search", new { query = "seasonal orchard production and irrigation planning", threshold = 0.3 });
                Program.Require(matches.Any(value => value.Name == "large-resume.txt"), "The resumed file was not searchable.");
                Console.WriteLine($"PASS real abrupt-exit resume: preserved {committed}/{total} passages; computed only {total - committed}");
            }
            long retainedVector = Check.Scalar(data, "SELECT v.id FROM vectors v JOIN files f ON f.id=v.file_id WHERE f.path LIKE '%parser.py'");
            string offlineChanged = Path.Combine(corpus, "orchard.txt");
            var previousTimestamp = File.GetLastWriteTimeUtc(offlineChanged);
            await File.WriteAllTextAsync(offlineChanged, "An updated orchard plan for apple harvesting and irrigation after restarting the application.");
            File.SetLastWriteTimeUtc(offlineChanged, previousTimestamp.AddSeconds(20));
            File.Delete(Path.Combine(corpus, "launch.pptx"));
            string offlineDirectory = Path.Combine(corpus, "offline-additions");
            Directory.CreateDirectory(offlineDirectory);
            await File.WriteAllTextAsync(Path.Combine(offlineDirectory, "Added.cs"),
                "public static string HarvestApples() => \"Fresh orchard apples\";");
            await using (var updated = new EngineProcess(package, models, data))
            {
                await updated.ReadyAsync();
                await updated.WaitAsync(value => value.FileCount == initialFiles + 1 && value.QueueCount == 0 &&
                    value.ReconcilingFolders == 0 && Check.Scalar(data, "SELECT COUNT(*) FROM files WHERE path LIKE '%Added.cs'") == 1,
                    "Restart did not incrementally reconcile offline changes.", 120000);
                var status = await updated.RequestAsync<EngineState>("status");
                Program.Require(status.InferenceBatches.CompletedInputs == 2 &&
                    Check.Scalar(data, "SELECT v.id FROM vectors v JOIN files f ON f.id=v.file_id WHERE f.path LIKE '%parser.py'") == retainedVector &&
                    Check.Scalar(data, "SELECT COUNT(*) FROM files WHERE path LIKE '%launch.pptx'") == 0,
                    "Restart reindexed unchanged files or missed an offline deletion.");
                Console.WriteLine("PASS real offline restart: two changed/new files embedded; unchanged vectors retained and deleted file removed");
            }
            File.WriteAllText(Path.Combine(output, "integration.json"), JsonSerializer.Serialize(new
            {
                failures = 0,
                files = initialFiles,
                documents = 6,
                code_files = 1,
                images = 2,
                permanent_failures = 2,
                startup_seconds = startup,
                corpus_seconds = corpusSeconds,
                query_ms = queryMs,
                maximum_active_files = maximumActive,
                affinity_mask = affinity,
                engine_peak_working_set = peakMemory,
                inference_batches = statistics,
                committed_before_crash = committed,
                total_passages = total,
                resumed_passages = total - committed,
                offline_reconciliation_inputs = 2,
                startup = progress,
                processes
            }, new JsonSerializerOptions { WriteIndented = true }));
        });
    }
}
