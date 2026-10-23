using FuzzyFindAnything.Core;

namespace FuzzyFindAnything.App.ViewModels;

public sealed class ModelRequirementViewModel : ObservableObject
{
    private ModelRequirement? requirement;
    private string selectedQueryProfile = "";
    private string device = "Automatic";
    private bool checking;
    public IReadOnlyList<QueryProfile> QueryProfiles { get; private set; } = [];
    public IReadOnlyList<ProviderDevice> Devices { get; private set; } = [];
    public bool HasDeviceOptions => Devices.Count > 2;
    public string SelectedDevice
    {
        get => device;
        set { if (value is not null) Set(ref device, value); }
    }
    public string SelectedQueryProfile
    {
        get => selectedQueryProfile;
        set { if (value is not null) Set(ref selectedQueryProfile, value); }
    }
    public string Summary => checking ? "Checking model requirements..." : requirement?.Summary ?? "";
    public string Details => requirement?.Details ?? "";
    public string Error => requirement?.Error ?? "";
    public bool HasError => Error.Length > 0;
    public bool HasDetails => requirement is { IsSimple: false } && Details.Length > 0;
    public bool RequiresQueryChoice => requirement?.DefaultQuery == "choose";
    public bool HasQueryOptions => QueryProfiles.Count > 2 && !RequiresQueryChoice;
    public bool IsVisible => checking || HasError || HasDetails || RequiresQueryChoice;
    public ModelOptions? Options => new ModelOptions(SelectedQueryProfile, device).Normalize() is var value &&
        value != ModelOptions.Default ? value : null;

    public void Reset(bool isDefault)
    {
        requirement = null; checking = !isDefault;
        SelectedQueryProfile = ""; device = "Automatic"; QueryProfiles = []; Devices = [];
        Changed(null);
    }
    public void Restore(ModelOptions options)
    {
        SelectedQueryProfile = options.QueryProfile; SelectedDevice = options.Device;
    }
    public void Apply(ModelRequirement value)
    {
        requirement = value; checking = false;
        string previous = selectedQueryProfile;
        QueryProfile[] choices = [new("", value.DefaultQuery == "choose" ? "Choose query purpose" : "Automatic for this file type", ""), .. value.QueryProfiles];
        if (!QueryProfiles.SequenceEqual(choices))
        {
            QueryProfiles = choices;
            Changed(nameof(QueryProfiles));
        }
        SelectedQueryProfile = value.State != "Available" || choices.Any(profile => profile.Id == previous) ? previous : "";
        ProviderDevice[] devices = value.Devices is null ? [] :
            [new("Automatic", "Automatic (qualified local device)", true), .. value.Devices];
        if (device != "Automatic" && devices.Length > 0 && !devices.Any(value => value.Id == device))
            devices = [.. devices, new(device, device + " (currently unavailable)", false)];
        if (!Devices.SequenceEqual(devices)) Devices = devices;
        Changed(null);
    }
    public void Unavailable(string kind, string url, string message) =>
        Apply(new(kind, url, "Unavailable", "Model requirements could not be resolved.", "", [], "", true, message));
}
