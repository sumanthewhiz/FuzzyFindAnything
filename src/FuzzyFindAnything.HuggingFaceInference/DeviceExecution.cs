using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.Windows.AI.MachineLearning;

namespace FuzzyFindAnything.HuggingFaceInference;

internal sealed record ExecutionDevice(string Provider, string Name, string Type, OrtEpDevice? NativeDevice);
internal static class DeviceExecution
{
    public static IReadOnlyList<string> Diagnostics { get; private set; } = [];
    public static IReadOnlyList<ExecutionDevice> Candidates()
    {
        var diagnostics = new List<string>();
        void Report(string message) { diagnostics.Add(message); Console.Error.WriteLine(message); }
        OrtEnv.Instance().DisableTelemetryEvents();
        var devices = new List<ExecutionDevice>
        { new("CPUExecutionProvider", RuntimeInformation.ProcessArchitecture + " CPU", "CPU", null) };
        try
        {
            var catalog = ExecutionProviderCatalog.GetDefault();
            var providers = catalog?.FindAllProviders();
            if (providers is null)
                Report("Windows execution-provider catalog is unavailable on this host; using CPU and any registered native devices.");
            foreach (var provider in providers ?? [])
            {
                if (provider is null)
                {
                    Report("The Windows execution-provider catalog returned an empty provider entry.");
                    continue;
                }
                if (provider.ReadyState == ExecutionProviderReadyState.NotPresent)
                {
                    Report($"Device execution provider {provider.Name} is compatible with this device but not installed.");
                    continue;
                }
                if (provider.ReadyState == ExecutionProviderReadyState.NotReady)
                {
                    var ready = provider.EnsureReadyAsync().AsTask().GetAwaiter().GetResult();
                    if (ready is null || ready.Status != ExecutionProviderReadyResultState.Success)
                    {
                        Report($"Installed provider {provider.Name} is not usable: {ready?.DiagnosticText ?? "No readiness result was returned."}");
                        continue;
                    }
                }
                if (!provider.TryRegister())
                    Report($"Execution provider {provider.Name} could not be registered.");
            }
        }
        catch (Exception error) when (error is COMException or PlatformNotSupportedException)
        {
            Report("Windows execution-provider catalog is unavailable: " + error.Message);
        }
        var nativeDevices = OrtEnv.Instance().GetEpDevices();
        if (nativeDevices is null) Report("ONNX Runtime reported no registered execution-provider devices.");
        foreach (var device in nativeDevices ?? [])
        {
            var hardware = device?.HardwareDevice;
            if (device is null || hardware is null)
            {
                Report("ONNX Runtime returned an execution-provider entry without hardware metadata.");
                continue;
            }
            string type = hardware.Type.ToString();
            if (device.EpName is "CPUExecutionProvider" or "AzureExecutionProvider") continue;
            if (type is not ("CPU" or "GPU" or "NPU")) continue;
            devices.Add(new(device.EpName, $"{device.EpVendor} {type}", type, device));
        }
        Diagnostics = diagnostics;
        return devices;
    }
    public static object Discover() => Candidates().Select(device => new { device.Provider, device.Name, device.Type }).ToArray();

    public static SessionOptions Options(ExecutionDevice device, int threads)
    {
        var options = new SessionOptions
        {
            IntraOpNumThreads = threads,
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            EnableMemoryPattern = device.Type == "CPU",
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
        };
        options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        options.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");
        if (device.NativeDevice is not null)
        {
            options.AppendExecutionProvider(OrtEnv.Instance(), [device.NativeDevice], new Dictionary<string, string>());
            options.EnableProfiling = true;
            options.ProfileOutputPathPrefix = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ffa-device-" + Guid.NewGuid().ToString("N"));
        }
        return options;
    }
}
