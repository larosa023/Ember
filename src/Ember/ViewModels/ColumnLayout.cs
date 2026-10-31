using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ember.Services;

namespace Ember.ViewModels;

/// <summary>
/// Shared widths and visibility for the process list columns. The header dividers change the
/// widths, the header's right-click menu toggles columns, and every row binds to both.
/// Saved to settings.
/// </summary>
public partial class ColumnLayout : ObservableObject
{
    private static readonly Dictionary<string, (double Default, double Min)> Limits = new()
    {
        ["name"] = (320, 140),
        ["status"] = (52, 36),
        ["pid"] = (64, 40),
        ["cpu"] = (64, 44),
        ["gpu"] = (64, 44),
        ["mem"] = (96, 56),
        ["disk"] = (84, 50),
        ["net"] = (88, 50),
    };

    // Must come after Limits: static fields initialize top to bottom, and the constructor reads Limits.
    public static ColumnLayout Instance { get; } = new();

    private SettingsService? _settings;

    [ObservableProperty] private double _nameWidth = Limits["name"].Default;
    [ObservableProperty] private double _statusWidth = Limits["status"].Default;
    [ObservableProperty] private double _pidWidth = Limits["pid"].Default;
    [ObservableProperty] private double _cpuWidth = Limits["cpu"].Default;
    [ObservableProperty] private double _gpuWidth = Limits["gpu"].Default;
    [ObservableProperty] private double _memWidth = Limits["mem"].Default;
    [ObservableProperty] private double _diskWidth = Limits["disk"].Default;
    [ObservableProperty] private double _netWidth = Limits["net"].Default;

    [ObservableProperty] private bool _showStatus = true;
    [ObservableProperty] private bool _showPid = true;
    [ObservableProperty] private bool _showCpu = true;
    [ObservableProperty] private bool _showGpu = true;
    [ObservableProperty] private bool _showMem = true;
    [ObservableProperty] private bool _showDisk = true;
    [ObservableProperty] private bool _showNet = true;

    public void Attach(SettingsService settings)
    {
        _settings = settings;
        foreach (var (key, width) in settings.Current.ProcessColumns)
            if (Limits.ContainsKey(key)) Set(key, width);
        foreach (var key in settings.Current.HiddenColumns)
            SetVisible(key, false, save: false);
        SyncServiceFlags();
    }

    public double Get(string key) => key switch
    {
        "name" => NameWidth,
        "status" => StatusWidth,
        "pid" => PidWidth,
        "cpu" => CpuWidth,
        "gpu" => GpuWidth,
        "mem" => MemWidth,
        "disk" => DiskWidth,
        "net" => NetWidth,
        _ => 0,
    };

    public void Set(string key, double width)
    {
        if (!Limits.TryGetValue(key, out var limit)) return;
        width = Math.Clamp(Math.Round(width), limit.Min, 1600);
        switch (key)
        {
            case "name": NameWidth = width; break;
            case "status": StatusWidth = width; break;
            case "pid": PidWidth = width; break;
            case "cpu": CpuWidth = width; break;
            case "gpu": GpuWidth = width; break;
            case "mem": MemWidth = width; break;
            case "disk": DiskWidth = width; break;
            case "net": NetWidth = width; break;
        }
    }

    public void Reset(string key)
    {
        if (Limits.TryGetValue(key, out var limit)) Set(key, limit.Default);
        Save();
    }

    /// <summary>Header right-click menu: show or hide a column.</summary>
    [RelayCommand]
    private void ToggleColumn(string key) => SetVisible(key, !IsVisible(key), save: true);

    private bool IsVisible(string key) => key switch
    {
        "status" => ShowStatus,
        "pid" => ShowPid,
        "cpu" => ShowCpu,
        "gpu" => ShowGpu,
        "mem" => ShowMem,
        "disk" => ShowDisk,
        "net" => ShowNet,
        _ => true,
    };

    private void SetVisible(string key, bool visible, bool save)
    {
        switch (key)
        {
            case "status": ShowStatus = visible; break;
            case "pid": ShowPid = visible; break;
            case "cpu": ShowCpu = visible; break;
            case "gpu": ShowGpu = visible; break;
            case "mem": ShowMem = visible; break;
            case "disk": ShowDisk = visible; break;
            case "net": ShowNet = visible; break;
        }
        SyncServiceFlags();
        if (save) Save();
    }

    /// <summary>Hidden columns cost nothing: the sampler skips their work.</summary>
    private void SyncServiceFlags()
    {
        ProcessService.WantNetwork = ShowNet;
        ProcessService.WantStatus = ShowStatus;
    }

    public void Save()
    {
        if (_settings is null) return;
        _settings.Current.ProcessColumns = new Dictionary<string, double>
        {
            ["name"] = NameWidth,
            ["status"] = StatusWidth,
            ["pid"] = PidWidth,
            ["cpu"] = CpuWidth,
            ["gpu"] = GpuWidth,
            ["mem"] = MemWidth,
            ["disk"] = DiskWidth,
            ["net"] = NetWidth,
        };
        _settings.Current.HiddenColumns = new[] { "status", "pid", "cpu", "gpu", "mem", "disk", "net" }
            .Where(k => !IsVisible(k)).ToList();
        _settings.Save();
    }
}
