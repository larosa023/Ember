using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ember.Models;
using LibreHardwareMonitor.Hardware;

namespace Ember.ViewModels;

public enum DetailKind { Cpu, Gpu, Memory }

/// <summary>
/// "Click into" page for the CPU, GPU or memory: a large 2-minute graph, key stats,
/// per-core usage (CPU) and every sensor for that part. Updated every tick so the graph
/// is already full when you open it.
/// </summary>
public partial class DetailViewModel : ViewModelBase
{
    private const int HistoryLength = 120;

    private static readonly SensorType[] GroupOrder =
    [
        SensorType.Temperature, SensorType.Load, SensorType.Clock, SensorType.Power,
        SensorType.Voltage, SensorType.Fan, SensorType.Control, SensorType.Data, SensorType.SmallData,
        SensorType.Throughput,
    ];

    private readonly double[] _history = new double[HistoryLength];
    private readonly Action _back;
    private readonly Dictionary<string, StatTile> _stats = new();
    private readonly Dictionary<string, CoreTile> _cores = new();
    private readonly Dictionary<SensorType, SensorGroup> _groups = new();
    private readonly Dictionary<string, SensorRow> _rows = new();

    public DetailViewModel(DetailKind kind, Action back)
    {
        Kind = kind;
        _back = back;
        Title = kind switch
        {
            DetailKind.Cpu => "PROCESSOR",
            DetailKind.Gpu => "GRAPHICS",
            _ => "MEMORY",
        };
    }

    public DetailKind Kind { get; }
    public string Title { get; }
    public bool ShowCores => Kind == DetailKind.Cpu;
    public string GraphLabel => Kind == DetailKind.Memory ? "Memory in use · last 2 minutes" : "Usage · last 2 minutes";

    public ObservableCollection<StatTile> Stats { get; } = new();
    public ObservableCollection<CoreTile> Cores { get; } = new();
    public ObservableCollection<SensorGroup> Groups { get; } = new();

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _headline = Format.Dash;
    [ObservableProperty] private double[] _history = Array.Empty<double>();
    [ObservableProperty] private string _coresSummary = "";

    [RelayCommand]
    private void Back() => _back();

    public void Update(HardwareSnapshot s)
    {
        var readings = Kind switch
        {
            DetailKind.Cpu => s.Sensors.Where(r => r.HardwareType == HardwareType.Cpu).ToList(),
            DetailKind.Gpu => s.Sensors.Where(r => r.Hardware == s.GpuName &&
                r.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel).ToList(),
            _ => s.Sensors.Where(r => r.HardwareType == HardwareType.Memory).ToList(),
        };

        double main = Kind switch
        {
            DetailKind.Cpu => s.CpuLoad ?? 0,
            DetailKind.Gpu => s.GpuLoad ?? 0,
            _ => s.RamLoad ?? 0,
        };
        Array.Copy(_history, 1, _history, 0, HistoryLength - 1);
        _history[^1] = main;
        History = (double[])_history.Clone();

        switch (Kind)
        {
            case DetailKind.Cpu: UpdateCpu(s, readings); break;
            case DetailKind.Gpu: UpdateGpu(s, readings); break;
            default: UpdateMemory(s, readings); break;
        }

        UpdateGroups(readings);
    }

    private void UpdateCpu(HardwareSnapshot s, List<SensorReading> r)
    {
        Name = s.CpuName;
        Headline = Format.Percent(s.CpuLoad);

        var coreLoads = r.Where(x => x.Type == SensorType.Load &&
                                     x.Name.StartsWith("CPU Core #", StringComparison.OrdinalIgnoreCase)).ToList();
        var physical = coreLoads.Select(x => CoreNumber(x.Name)).Where(n => n > 0).Distinct().Count();

        Stat("temp", "Temperature", Format.Temp(s.CpuTemp));
        Stat("clock", "Average clock", Format.Clock(s.CpuClock));
        Stat("power", "Package power", Format.Watts(s.CpuPower));
        Stat("max", "Fastest core", Format.Clock(Max(r, SensorType.Clock, "Core #")));
        Stat("cores", "Cores / threads", physical > 0 ? $"{physical} / {coreLoads.Count}" : Format.Dash);
        Stat("uptime", "Uptime", Format.Uptime(TimeSpan.FromMilliseconds(Environment.TickCount64)));

        foreach (var load in coreLoads)
        {
            if (!_cores.TryGetValue(load.Id, out var tile))
            {
                tile = new CoreTile(ShortCoreName(load.Name));
                _cores[load.Id] = tile;
                Cores.Add(tile);
            }
            var core = CoreNumber(load.Name);
            var clock = r.FirstOrDefault(x => x.Type == SensorType.Clock && x.Name == $"Core #{core}");
            tile.Apply(load.Value, clock?.Value);
        }
        CoresSummary = coreLoads.Count > 0 ? $"{coreLoads.Count} logical processors" : "Per-core data needs admin and the sensor driver.";
    }

    private void UpdateGpu(HardwareSnapshot s, List<SensorReading> r)
    {
        Name = s.GpuName;
        Headline = Format.Percent(s.GpuLoad);

        Stat("temp", "Core temperature", Format.Temp(s.GpuTemp));
        Stat("hotspot", "Hot spot", Format.Temp(Find(r, SensorType.Temperature, "GPU Hot Spot")));
        Stat("memtemp", "Memory junction", Format.Temp(Find(r, SensorType.Temperature, "GPU Memory Junction", "GPU Memory")));
        Stat("clock", "Core clock", Format.Clock(s.GpuClock));
        Stat("memclock", "Memory clock", Format.Clock(Find(r, SensorType.Clock, "GPU Memory")));
        Stat("power", "Power", Format.Watts(s.GpuPower));
        Stat("vram", "VRAM", s.VramUsedMb is float used
            ? s.VramTotalMb is float total && total > 0 ? $"{used / 1024:0.0} / {total / 1024:0} GB" : $"{used / 1024:0.0} GB"
            : Format.Dash);
        var fan = r.FirstOrDefault(x => x.Type == SensorType.Fan)?.Value;
        Stat("fan", "Fan", fan is float f ? $"{f:0} RPM" : Format.Dash);
    }

    private void UpdateMemory(HardwareSnapshot s, List<SensorReading> r)
    {
        var total = s.RamUsedGb is float u && s.RamFreeGb is float f ? u + f : (float?)null;
        Name = total is float t ? $"{t:0} GB installed" : "Memory";
        Headline = Format.Percent(s.RamLoad);

        var isVirtual = (SensorReading x) => x.Hardware.Contains("Virtual", StringComparison.OrdinalIgnoreCase);
        var virtualUsed = r.FirstOrDefault(x => isVirtual(x) && x.Type == SensorType.Data && x.Name.EndsWith("Used", StringComparison.OrdinalIgnoreCase))?.Value;
        var virtualFree = r.FirstOrDefault(x => isVirtual(x) && x.Type == SensorType.Data && x.Name.EndsWith("Available", StringComparison.OrdinalIgnoreCase))?.Value;

        Stat("used", "In use", s.RamUsedGb is float used ? $"{used:0.0} GB" : Format.Dash);
        Stat("free", "Available", s.RamFreeGb is float free ? $"{free:0.0} GB" : Format.Dash);
        Stat("total", "Total", total is float tt ? $"{tt:0.0} GB" : Format.Dash);
        Stat("commit", "Committed (incl. page file)", virtualUsed is float vu && virtualFree is float vf
            ? $"{vu:0.0} / {vu + vf:0.0} GB" : Format.Dash);
        Stat("dimm", "Hottest DIMM", Format.Temp(r.Where(x => x.Type == SensorType.Temperature).Select(x => x.Value).Max()));
    }

    private void Stat(string key, string label, string value)
    {
        if (!_stats.TryGetValue(key, out var tile))
        {
            tile = new StatTile(label);
            _stats[key] = tile;
            Stats.Add(tile);
        }
        tile.Value = value;
    }

    private void UpdateGroups(List<SensorReading> readings)
    {
        foreach (var type in GroupOrder)
        {
            // Per-core loads already have their own grid on the CPU page.
            var ofType = readings.Where(x => x.Type == type &&
                !(Kind == DetailKind.Cpu && type == SensorType.Load && x.Name.StartsWith("CPU Core #", StringComparison.OrdinalIgnoreCase))).ToList();
            if (ofType.Count == 0) continue;

            if (!_groups.TryGetValue(type, out var group))
            {
                group = new SensorGroup(GroupName(type));
                _groups[type] = group;
                // Insert in canonical order regardless of when the group first appears.
                var index = Groups.Count(g => Array.IndexOf(GroupOrder, KeyOf(g)) < Array.IndexOf(GroupOrder, type));
                Groups.Insert(index, group);
            }

            foreach (var reading in ofType)
            {
                if (!_rows.TryGetValue(reading.Id, out var row))
                {
                    var label = Kind == DetailKind.Memory && readings.Select(x => x.Hardware).Distinct().Count() > 1
                        ? $"{reading.Hardware} · {reading.Name}" : reading.Name;
                    row = new SensorRow(label, "");
                    _rows[reading.Id] = row;
                    group.Rows.Add(row);
                }
                row.Apply(reading);
            }
        }
    }

    private SensorType KeyOf(SensorGroup g) => _groups.First(kv => ReferenceEquals(kv.Value, g)).Key;

    private static string GroupName(SensorType t) => t switch
    {
        SensorType.Temperature => "Temperatures",
        SensorType.Load => "Loads",
        SensorType.Clock => "Clocks",
        SensorType.Power => "Power",
        SensorType.Voltage => "Voltages",
        SensorType.Fan => "Fans",
        SensorType.Control => "Fan control",
        SensorType.Data or SensorType.SmallData => "Memory",
        SensorType.Throughput => "Throughput",
        _ => t.ToString(),
    };

    private static float? Find(List<SensorReading> r, SensorType type, params string[] names)
    {
        foreach (var n in names)
        {
            var hit = r.FirstOrDefault(x => x.Type == type && x.Value is not null && string.Equals(x.Name, n, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit.Value;
        }
        return null;
    }

    private static float? Max(List<SensorReading> r, SensorType type, string prefix) =>
        r.Where(x => x.Type == type && x.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
         .Select(x => x.Value).Max();

    private static int CoreNumber(string name)
    {
        var m = Regex.Match(name, @"Core #(\d+)");
        return m.Success ? int.Parse(m.Groups[1].Value) : 0;
    }

    /// <summary>"CPU Core #3 Thread #2" → "Core 3 · T2", "CPU Core #3" → "Core 3".</summary>
    private static string ShortCoreName(string name)
    {
        var m = Regex.Match(name, @"Core #(\d+)(?:.*Thread #(\d+))?");
        if (!m.Success) return name;
        return m.Groups[2].Success ? $"Core {m.Groups[1].Value} · T{m.Groups[2].Value}" : $"Core {m.Groups[1].Value}";
    }
}

public partial class StatTile : ObservableObject
{
    public StatTile(string label) => Label = label;
    public string Label { get; }
    [ObservableProperty] private string _value = Format.Dash;
}

public partial class CoreTile : ObservableObject
{
    public CoreTile(string name) => Name = name;
    public string Name { get; }

    [ObservableProperty] private double _load;
    [ObservableProperty] private string _loadText = Format.Dash;
    [ObservableProperty] private string _clockText = "";

    public void Apply(float? load, float? clockMhz)
    {
        Load = load ?? 0;
        LoadText = Format.Percent(load);
        ClockText = clockMhz is float c ? Format.Clock(c) : "";
    }
}
