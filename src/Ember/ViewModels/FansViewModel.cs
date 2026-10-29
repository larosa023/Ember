using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ember.Models;
using Ember.Services;
using LibreHardwareMonitor.Hardware;

namespace Ember.ViewModels;

public sealed record TempSource(string Id, string Label)
{
    public override string ToString() => Label;
}

public partial class FansViewModel : ViewModelBase
{
    private readonly SettingsService _settings;
    private readonly MonitorHost _host;
    private readonly DispatcherTimer _saveTimer;
    private readonly Dictionary<string, FanViewModel> _byId = new();

    public FansViewModel(SettingsService settings, MonitorHost host)
    {
        _settings = settings;
        _host = host;
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            _settings.Save();
        };

        // Apply saved fan settings right away, even if this page is never opened.
        _host.Fans.SetConfigs(settings.Current.Fans);
    }

    public ObservableCollection<FanViewModel> Fans { get; } = new();
    public ObservableCollection<TempSource> TempSources { get; } = new();

    public bool IsElevated => Environment.IsPrivilegedProcess;

    [ObservableProperty] private string _summary = "Looking for controllable fans…";
    [ObservableProperty] private bool _hasFans;
    [ObservableProperty] private bool _searched;

    public void Update(HardwareSnapshot s)
    {
        UpdateTempSources(s.Sensors);

        var temps = new Dictionary<string, float?>();
        foreach (var r in s.Sensors)
            if (r.Type == SensorType.Temperature) temps[r.Id] = r.Value;

        foreach (var channel in s.FanChannels)
        {
            if (!_byId.TryGetValue(channel.Id, out var fan))
            {
                var saved = _settings.Current.Fans.FirstOrDefault(f => f.Id == channel.Id);
                fan = new FanViewModel(channel, saved, TempSources, DefaultSourceFor(channel), OnFanChanged);
                _byId[channel.Id] = fan;
                Fans.Add(fan);
            }
            fan.Apply(channel, temps);
        }

        HasFans = Fans.Count > 0;
        if (s.Sensors.Count > 0) Searched = true;
        var controlled = Fans.Count(f => f.ModeIndex != 0);
        Summary = HasFans
            ? $"{Fans.Count} controllable fans · {controlled} controlled by Ember"
            : Searched ? "No controllable fans found" : "Looking for controllable fans…";
    }

    private void UpdateTempSources(IReadOnlyList<SensorReading> sensors)
    {
        var sources = sensors
            .Where(r => r.Type == SensorType.Temperature)
            .Select(r => new TempSource(r.Id, $"{r.Hardware} · {r.Name}"))
            .ToList();
        if (sources.SequenceEqual(TempSources)) return;

        // Rebuild without losing each fan's selection (records compare by value).
        var selections = Fans.ToDictionary(f => f.Id, f => f.TempSource);
        foreach (var f in Fans) f.SuspendChanges = true;
        TempSources.Clear();
        foreach (var src in sources) TempSources.Add(src);
        foreach (var f in Fans)
        {
            var keep = selections[f.Id];
            f.TempSource = keep is null ? null : TempSources.FirstOrDefault(t => t.Id == keep.Id) ?? keep;
            f.SuspendChanges = false;
        }
    }

    private string? DefaultSourceFor(FanChannel channel)
    {
        // GPU fans follow the GPU; everything else follows the CPU package.
        var preferred = channel.IsGpu
            ? TempSources.FirstOrDefault(t => t.Label.EndsWith("GPU Core", StringComparison.OrdinalIgnoreCase))
            : TempSources.FirstOrDefault(t => t.Label.Contains("CPU Package", StringComparison.OrdinalIgnoreCase)
                                             || t.Label.Contains("Tctl", StringComparison.OrdinalIgnoreCase));
        return preferred?.Id ?? TempSources.FirstOrDefault()?.Id;
    }

    private void OnFanChanged()
    {
        // Keep settings for fans that aren't present right now (e.g. GPU swapped out).
        var present = Fans.Select(f => f.ToConfig()).ToList();
        var others = _settings.Current.Fans.Where(c => !_byId.ContainsKey(c.Id));
        _settings.Current.Fans = present.Concat(others).ToList();

        _host.Fans.SetConfigs(_settings.Current.Fans);
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    [RelayCommand]
    private void RestoreAll()
    {
        foreach (var f in Fans) f.ModeIndex = 0;
    }
}

public partial class CurvePoint : ObservableObject
{
    [ObservableProperty] private double _temp;
    [ObservableProperty] private double _percent;
}

/// <summary>One controllable fan: live readings plus its Auto / Fixed / Curve settings.</summary>
public partial class FanViewModel : ObservableObject
{
    private readonly Action _changed;

    public FanViewModel(FanChannel channel, FanConfig? saved, ObservableCollection<TempSource> sources,
        string? defaultSource, Action changed)
    {
        _changed = changed;
        SuspendChanges = true;

        Id = channel.Id;
        TempSources = sources;
        var config = saved ?? new FanConfig { Id = channel.Id, TempSource = defaultSource };

        MinPercent = Math.Max(config.MinPercent, channel.MinPercent);
        ModeIndex = (int)config.Mode;
        FixedPercent = Math.Max(config.FixedPercent, MinPercent);
        var sourceId = config.TempSource ?? defaultSource;
        TempSource = sources.FirstOrDefault(t => t.Id == sourceId);

        foreach (var p in config.Points)
        {
            var point = new CurvePoint { Temp = p.Temp, Percent = p.Percent };
            point.PropertyChanged += OnPointChanged;
            Points.Add(point);
        }

        SuspendChanges = false;
    }

    /// <summary>When true, property changes don't count as user edits.</summary>
    public bool SuspendChanges { get; set; }

    public string Id { get; }
    public double MinPercent { get; }
    public ObservableCollection<TempSource> TempSources { get; }
    public ObservableCollection<CurvePoint> Points { get; } = new();

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _hardware = "";
    [ObservableProperty] private string _readout = "";
    [ObservableProperty] private double _currentTemp = double.NaN;
    [ObservableProperty] private double _currentPercent = double.NaN;
    [ObservableProperty] private string _currentTempText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFixed), nameof(IsCurve))]
    private int _modeIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FixedText))]
    private double _fixedPercent = 50;

    [ObservableProperty] private TempSource? _tempSource;

    public bool IsFixed => ModeIndex == (int)FanMode.Fixed;
    public bool IsCurve => ModeIndex == (int)FanMode.Curve;
    public string FixedText => $"{FixedPercent:0}%";

    partial void OnModeIndexChanged(int value) => Changed();
    partial void OnFixedPercentChanged(double value) => Changed();
    partial void OnTempSourceChanged(TempSource? value) => Changed();
    private void OnPointChanged(object? sender, PropertyChangedEventArgs e) => Changed();

    private void Changed()
    {
        if (!SuspendChanges) _changed();
    }

    public void Apply(FanChannel channel, IReadOnlyDictionary<string, float?> temps)
    {
        Name = channel.Name;
        Hardware = channel.Hardware;

        var rpm = channel.Rpm is float r ? $"{r:0} RPM" : "— RPM";
        var pct = channel.Percent is float p ? $"{p:0}%" : "—";
        Readout = $"{rpm} · {pct}";
        CurrentPercent = channel.Percent ?? double.NaN;

        if (TempSource is not null && temps.TryGetValue(TempSource.Id, out var t) && t is float temp)
        {
            CurrentTemp = temp;
            CurrentTempText = $"Now {Format.Temp(temp)}";
        }
        else
        {
            CurrentTemp = double.NaN;
            CurrentTempText = IsCurve ? "Sensor not reporting: fan held at 100%" : "";
        }
    }

    [RelayCommand]
    private void Preset(string name)
    {
        (double T, double P)[] curve = name switch
        {
            "silent" => [(30, 20), (55, 30), (70, 50), (80, 75), (90, 100)],
            "performance" => [(30, 40), (50, 55), (65, 75), (75, 95), (85, 100)],
            _ => [(30, 25), (50, 35), (65, 55), (80, 85), (90, 100)],
        };

        SuspendChanges = true;
        while (Points.Count < curve.Length)
        {
            var extra = new CurvePoint();
            extra.PropertyChanged += OnPointChanged;
            Points.Add(extra);
        }
        while (Points.Count > curve.Length) Points.RemoveAt(Points.Count - 1);
        for (int i = 0; i < curve.Length; i++)
        {
            Points[i].Temp = curve[i].T;
            Points[i].Percent = Math.Max(curve[i].P, MinPercent);
        }
        SuspendChanges = false;
        _changed();
    }

    public FanConfig ToConfig() => new()
    {
        Id = Id,
        Mode = (FanMode)Math.Clamp(ModeIndex, 0, 2),
        FixedPercent = FixedPercent,
        TempSource = TempSource?.Id,
        MinPercent = MinPercent,
        Points = Points.OrderBy(p => p.Temp).Select(p => new CurvePointData { Temp = p.Temp, Percent = p.Percent }).ToList(),
    };
}
