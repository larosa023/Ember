using CommunityToolkit.Mvvm.ComponentModel;
using Ember.Services;

namespace Ember.ViewModels;

/// <summary>
/// Shared widths for the process list columns. The header dividers change these and every row
/// binds to them, so resizing is instant. Saved to settings when a drag ends.
/// </summary>
public partial class ColumnLayout : ObservableObject
{
    private static readonly Dictionary<string, (double Default, double Min)> Limits = new()
    {
        ["name"] = (340, 140),
        ["pid"] = (64, 40),
        ["cpu"] = (70, 44),
        ["mem"] = (90, 56),
    };

    // Must come after Limits: static fields initialize top to bottom, and the constructor reads Limits.
    public static ColumnLayout Instance { get; } = new();

    private SettingsService? _settings;

    [ObservableProperty] private double _nameWidth = Limits["name"].Default;
    [ObservableProperty] private double _pidWidth = Limits["pid"].Default;
    [ObservableProperty] private double _cpuWidth = Limits["cpu"].Default;
    [ObservableProperty] private double _memWidth = Limits["mem"].Default;

    public void Attach(SettingsService settings)
    {
        _settings = settings;
        foreach (var (key, width) in settings.Current.ProcessColumns)
            if (Limits.ContainsKey(key)) Set(key, width);
    }

    public double Get(string key) => key switch
    {
        "name" => NameWidth,
        "pid" => PidWidth,
        "cpu" => CpuWidth,
        "mem" => MemWidth,
        _ => 0,
    };

    public void Set(string key, double width)
    {
        if (!Limits.TryGetValue(key, out var limit)) return;
        width = Math.Clamp(Math.Round(width), limit.Min, 1600);
        switch (key)
        {
            case "name": NameWidth = width; break;
            case "pid": PidWidth = width; break;
            case "cpu": CpuWidth = width; break;
            case "mem": MemWidth = width; break;
        }
    }

    public void Reset(string key)
    {
        if (Limits.TryGetValue(key, out var limit)) Set(key, limit.Default);
        Save();
    }

    public void Save()
    {
        if (_settings is null) return;
        _settings.Current.ProcessColumns = new Dictionary<string, double>
        {
            ["name"] = NameWidth,
            ["pid"] = PidWidth,
            ["cpu"] = CpuWidth,
            ["mem"] = MemWidth,
        };
        _settings.Save();
    }
}
