using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using FuzzyFindAnything.Core;

namespace FuzzyFindAnything.NativeTests;

internal static class TestWorker
{
    public static int Run(string[] args)
    {
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);
        CpuBudget.Apply();
        string root = args[1];
        using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "scenario.json")));
        bool Flag(string name) => settings.RootElement.TryGetProperty(name, out var value) && value.GetBoolean();
        int ordinal;
        while (true)
        {
            try
            {
                using var file = new FileStream(Path.Combine(root, "counter"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                using var reader = new StreamReader(file, leaveOpen: true);
                string text = reader.ReadToEnd();
                ordinal = text.Length == 0 ? 1 : int.Parse(text) + 1;
                file.Position = 0; file.SetLength(0);
                byte[] bytes = Encoding.ASCII.GetBytes(ordinal.ToString());
                file.Write(bytes); file.Flush(true);
                break;
            }
            catch (IOException) { Thread.Sleep(10); }
        }
        File.WriteAllText(Path.Combine(root, $"worker-{ordinal}.pid"), Environment.ProcessId.ToString());
        using (var process = Process.GetCurrentProcess())
            File.WriteAllText(Path.Combine(root, $"worker-{ordinal}.mask"), process.ProcessorAffinity.ToInt64().ToString());
        if (Flag("hang_startup") || ordinal > 1 && Flag("hang_secondary"))
            while (true)
            {
                Send(new { @event = "startup", completed = 2, total = 6, stage = "Blocked test initialization" });
                Thread.Sleep(25);
            }
        if (ordinal > 1 && Flag("fail_secondary"))
        { Send(new { ok = false, error = "secondary failed", code = "test", retryable = true }); return 2; }
        for (int step = 1; step <= 6; step++)
            Send(new { @event = "startup", completed = step, total = 6, stage = "Native test checkpoint " + step });
        Send(new { ok = true, result = "ready" });
        string? line;
        int operations = 0;
        while ((line = Console.ReadLine()) is not null)
        {
            using var request = JsonDocument.Parse(line);
            string operation = request.RootElement.GetProperty("operation").GetString()!;
            var values = request.RootElement.GetProperty("args");
            if (operation == "capabilities")
            {
                Send(new { ok = true, result = new { space = EmbeddingSpace.Default, provider = "CPUExecutionProvider", device = "Test CPU" } });
                continue;
            }
            File.AppendAllText(Path.Combine(root, $"operations-{ordinal}.txt"), operation + "\n");
            if (operation == "shutdown") { Send(new { ok = true, result = (object?)null }); return 0; }
            operations++;
            if (operations == 1 && Flag("block_first"))
            {
                File.WriteAllText(Path.Combine(root, "entered"), operation);
                var timer = Stopwatch.StartNew();
                while (!File.Exists(Path.Combine(root, "release")) && timer.Elapsed < TimeSpan.FromSeconds(30)) Thread.Sleep(10);
            }
            if (Flag("malformed")) { Console.WriteLine("not-json"); continue; }
            object result = operation switch
            {
                "query" => Program.Vector(0),
                "split" => new[] { values.GetProperty("text").GetString()! },
                "documents" => values.GetProperty("texts").EnumerateArray().Select((_, index) => Program.Vector(index)).ToArray(),
                "images" => values.GetProperty("paths").EnumerateArray().Select((_, index) => Program.Vector(index)).ToArray(),
                _ => throw new InvalidDataException("Unexpected test operation.")
            };
            Send(new { ok = true, result });
        }
        return 0;
    }
    private static void Send(object value) => Console.WriteLine(JsonSerializer.Serialize(value, Protocol.Json));

    public static int Filter(string path)
    {
        string root = Path.GetDirectoryName(path)!;
        File.WriteAllText(Path.Combine(root, Path.GetFileNameWithoutExtension(path) + ".entered"), Environment.ProcessId.ToString());
        if (File.Exists(Path.Combine(root, "barrier")))
        {
            var timer = Stopwatch.StartNew();
            while (Directory.GetFiles(root, "*.entered").Length < 6 && timer.Elapsed < TimeSpan.FromSeconds(15)) Thread.Sleep(10);
        }
        if (File.Exists(Path.Combine(root, "hang"))) Thread.Sleep(TimeSpan.FromSeconds(30));
        Send(new { text = "apple " + Path.GetFileName(path), warnings = Array.Empty<string>(), format = ".pdf" });
        return 0;
    }
}
