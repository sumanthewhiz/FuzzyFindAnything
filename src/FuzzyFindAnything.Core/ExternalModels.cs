using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FuzzyFindAnything.Core;

public sealed record ExternalEmbeddingContract(string Provider, string VectorIdentity, string TokenizerIdentity,
    int LogicalDimensions, int ChunkTokens, int QueryTokens, string[] Kinds, string Device = "CPU")
{
    public void Validate(EmbeddingSpace space)
    {
        if (Device == "Automatic" || new ModelOptions(Device: Device).Normalize().Device != Device)
            throw new InvalidDataException("The provider vector contract must identify its resolved execution profile.");
        if (ModelReference.Parse(space.ModelUrl) is not ProviderModelReference reference || reference.Provider != Provider ||
            !Regex.IsMatch(VectorIdentity, "^[0-9a-f]{64}$") || !Regex.IsMatch(TokenizerIdentity, "^[0-9a-f]{64}$") ||
            space.Revision != VectorIdentity || space.Adapter != "external-v1" || space.Recipe is not null || space.ArtifactUrl is not null ||
            LogicalDimensions is < 1 or > 4096 || space.Dimensions != (LogicalDimensions + 7) / 8 * 8 ||
            ChunkTokens is < 16 or > 8192 || ChunkTokens > space.MaximumTokens || QueryTokens is < 16 or > 8192 ||
            Kinds.Length is < 1 or > 3 || Kinds.Distinct().Count() != Kinds.Length ||
            Kinds.Any(kind => kind is not ("Document" or "Code" or "Image")) || space.SupportsImages != Kinds.Contains("Image"))
            throw new InvalidDataException("The local provider returned an incompatible embedding contract.");
    }
}

public sealed record ProviderDevice(string Id, string Label, bool Available, string Detail = "");
public sealed record ProviderVersion(string Id, bool Supported, string Detail = "");
public sealed record ProviderModel(string Id, string Label, string[] Kinds, ProviderDevice[] Devices,
    string State, string Detail, bool PubliclyReleased = false);
public sealed record ProviderCatalog(string Provider, string Version, ProviderVersion[] Versions, ProviderModel[] Models,
    bool InternalAccessVerified = false, string AvailabilityNotice = "")
{
    public ProviderCatalog ForCaller() => this with
    {
        Versions = InternalAccessVerified ? Versions.Where(version => version.Supported).ToArray() : [],
        Models = InternalAccessVerified ? Models : []
    };
}
public sealed record ProviderRegistration(string Id, string Executable, string DefaultVersion, int ProtocolVersion = 1);
public sealed record PreparedProviderModel(EmbeddingSpace Space, string Directory);

public sealed class ExternalModels(string cacheDirectory, Action<string> log, string? registrationDirectory = null)
{
    public static string DefaultRegistrationDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FuzzyFindAnythingProviders");
    public string DirectoryFor(ProviderModelReference reference, string device = "Automatic") =>
        Path.Combine(cacheDirectory, EmbeddingRecipe.Hash(reference.Key + "\n" + new ModelOptions(Device: device).Normalize().Device)[..24]);

    public ProviderRegistration Registration(string provider)
    {
        if (!Regex.IsMatch(provider, "^[a-z][a-z0-9-]{0,63}$")) throw new ArgumentException("Invalid provider identifier.");
        string path = Path.Combine(registrationDirectory ?? DefaultRegistrationDirectory, provider, "provider.json");
        if (!File.Exists(path))
            throw new FileNotFoundException("The internal Microsoft model provider is not installed. Install the approved local provider; the existing models have not been changed.", path);
        if (new FileInfo(path).Length > 65536) throw new InvalidDataException("The provider registration is too large.");
        var value = JsonSerializer.Deserialize<ProviderRegistration>(File.ReadAllText(path), Protocol.Json)
            ?? throw new InvalidDataException("The provider registration is empty.");
        if (value.Id != provider || value.ProtocolVersion is not (1 or 2) || !Path.IsPathFullyQualified(value.Executable) ||
            !File.Exists(value.Executable))
            throw new InvalidDataException("The local provider registration or executable is invalid.");
        _ = ModelReference.Parse($"provider://{provider}/{value.DefaultVersion}/model");
        return value;
    }

    public string ExecutableFor(EmbeddingSpace space) =>
        Registration((space.External ?? throw new ArgumentException("This is not an external model.")).Provider).Executable;

    public EmbeddingSpace FindCached(ProviderModelReference reference, ModelOptions? options = null)
    {
        string path = Path.Combine(DirectoryFor(reference, (options ?? ModelOptions.Default).Device), "provider-model.json");
        if (!File.Exists(path)) throw new FileNotFoundException("The selected provider model is not installed. Apply the selection to acquire its approved assets.", path);
        if (new FileInfo(path).Length > 4_194_304) throw new InvalidDataException("The provider manifest exceeds its size bound.");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var space = document.RootElement.GetProperty("space").Deserialize<EmbeddingSpace>(Protocol.Json)
            ?? throw new InvalidDataException("The provider manifest has no embedding contract.");
        space.Validate();
        if (space.ModelUrl != reference.Key) throw new InvalidDataException("The installed provider model does not match the selected reference.");
        return space;
    }

    public async Task<ProviderCatalog> CatalogAsync(string provider, string? version, bool refresh, CancellationToken cancellation)
    {
        ProviderRegistration registration;
        try { registration = Registration(provider); }
        catch (FileNotFoundException)
        {
            if (provider == "microsoft" && registrationDirectory is null &&
                await ProviderBootstrap.InstallAsync(DefaultRegistrationDirectory, log, cancellation))
                registration = Registration(provider);
            else
            {
                log("Optional local model provider is not installed; Hugging Face remains available.");
                return new(provider, version ?? "", [], [], AvailabilityNotice: "The approved local provider is not installed.");
            }
        }
        string selected = string.IsNullOrEmpty(version) ? registration.DefaultVersion : version;
        _ = ModelReference.Parse($"provider://{provider}/{selected}/model");
        var result = await RequestAsync<ProviderCatalog>(registration, "catalog", new { version = selected, refresh },
            null, cancellation);
        if (result.Provider != provider || result.Version != selected || result.Versions.Length > 1000 || result.Models.Length > 100 ||
            result.Models.Select(model => model.Id).Distinct().Count() != result.Models.Length)
            throw new InvalidDataException("The provider returned an invalid model catalog.");
        foreach (var model in result.Models)
        {
            _ = ModelReference.Parse($"provider://{provider}/{selected}/{model.Id}");
            if (model.Kinds.Any(kind => kind is not ("Document" or "Code" or "Image")) || model.Devices.Length > 32)
                throw new InvalidDataException("The provider returned unsupported model roles/devices.");
            foreach (var device in model.Devices) _ = new ModelOptions(Device: device.Id).Normalize();
        }
        return result.ForCaller();
    }

    public async Task<ModelRequirement> InspectAsync(string kind, ProviderModelReference reference, CancellationToken cancellation)
    {
        var result = await RequestAsync<ModelRequirement>(Registration(reference.Provider), "inspect",
            new { kind, reference, directory = DirectoryFor(reference) }, null, cancellation);
        if (result.Kind != kind || result.ModelUrl != reference.Key || result.ImageModel is not null ||
            result.QueryProfiles.Length != 0 || result.DefaultQuery != "default")
            throw new InvalidDataException("The provider returned invalid model requirements.");
        return result;
    }

    public async Task<EmbeddingSpace> ResolveAsync(ProviderModelReference reference, ModelOptions options,
        Action<string> progress, CancellationToken cancellation, ModelMemoryCoordinator? memory = null)
    {
        string directory = DirectoryFor(reference, options.Device);
        var registration = Registration(reference.Provider);
        if (memory is not null && registration.ProtocolVersion < 2)
            throw new NotSupportedException("Update the approved local model provider to support coordinated model memory before applying this selection.");
        var result = await RequestAsync<PreparedProviderModel>(registration, "prepare",
            new { reference, directory, device = options.Device, coordinate_memory = memory is not null },
            progress, cancellation, TimeSpan.FromMinutes(30), memory);
        result.Space.Validate();
        if (result.Space.ModelUrl != reference.Key ||
            !Path.GetFullPath(result.Directory).Equals(Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The provider prepared a different model or cache directory.");
        var cached = FindCached(reference, options);
        if (options.Device != "Automatic" && cached.External?.Device != options.Device)
            throw new InvalidDataException("The provider did not honor the requested execution profile.");
        if (cached.Fingerprint != result.Space.Fingerprint || cached.IndexingFingerprint != result.Space.IndexingFingerprint)
            throw new InvalidDataException("The provider did not persist the prepared embedding contract.");
        return cached;
    }

    private async Task<T> RequestAsync<T>(ProviderRegistration registration, string operation, object arguments,
        Action<string>? progress, CancellationToken cancellation, TimeSpan? timeout = null, ModelMemoryCoordinator? memory = null)
    {
        var start = new ProcessStartInfo(registration.Executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = Path.GetDirectoryName(registration.Executable)!
        };
        start.ArgumentList.Add("--provider-request");
        using var process = Process.Start(start) ?? throw new IOException("Could not start the internal model provider.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(timeout ?? TimeSpan.FromMinutes(3));
        var errors = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
                log("provider: " + FilePaths.ScalarPrefix(line, 4096));
        });
        IDisposable? admission = null;
        try
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { operation, args = arguments }, Protocol.Json));
            await process.StandardInput.FlushAsync();
            if (memory is null) process.StandardInput.Close();
            while (await process.StandardOutput.ReadLineAsync(deadline.Token) is { } line)
            {
                if (line.Length > 4_194_304) throw new InvalidDataException("The provider response exceeds its size bound.");
                using var document = JsonDocument.Parse(line);
                var message = document.RootElement;
                if (message.TryGetProperty("event", out var memoryEvent) && memoryEvent.GetString() == "memory_request")
                {
                    if (operation != "prepare" || memory is null || admission is not null)
                        throw new InvalidDataException("Unexpected provider memory admission request.");
                    long bytes = message.GetProperty("model_bytes").GetInt64();
                    if (bytes < 1 || bytes > (64L << 30)) throw new InvalidDataException("The provider memory allowance is out of bounds.");
                    progress?.Invoke("Making room for native model validation without changing the current index");
                    admission = await memory.AcquireAsync(null, bytes, deadline.Token);
                    await process.StandardInput.WriteLineAsync("{\"operation\":\"memory_ready\"}");
                    process.StandardInput.Close();
                    continue;
                }
                if (message.TryGetProperty("event", out var type) && type.GetString() == "progress")
                {
                    progress?.Invoke(message.GetProperty("message").GetString() ?? "Preparing local model");
                    continue;
                }
                if (!message.GetProperty("ok").GetBoolean())
                    throw new ContentException(message.GetProperty("error").GetString() ?? "The provider request failed.",
                        message.TryGetProperty("code", out var code) ? code.GetString() ?? "provider" : "provider");
                var result = message.GetProperty("result").Deserialize<T>(Protocol.Json)
                    ?? throw new InvalidDataException("The provider returned no result.");
                if (memory is not null && admission is null)
                    throw new InvalidDataException("The provider skipped required memory admission.");
                await process.WaitForExitAsync(deadline.Token);
                if (process.ExitCode != 0) throw new IOException("The model provider exited unsuccessfully.");
                return result;
            }
            throw new IOException("The model provider exited without a response.");
        }
        catch (OperationCanceledException error) when (!cancellation.IsCancellationRequested)
        { throw new TimeoutException("The internal model provider request timed out.", error); }
        finally
        {
            try
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
                await errors;
            }
            finally { admission?.Dispose(); }
        }
    }
}
