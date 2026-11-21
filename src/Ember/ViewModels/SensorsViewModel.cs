using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ember.Models;
using LibreHardwareMonitor.Hardware;

namespace Ember.ViewModels;

/// <summary>
/// Sensors page: one collapsible card per device, readings grouped by type
/// (Temperatures, Loads, Clocks, ...), with an optional text filter.
/// </summary>
public partial class SensorsViewModel : ViewModelBase
{
    private readonly Dictionary<string, HardwareCard> _cards = new();
    private IReadOnlyList<SensorReading> _last = Array.Empty<SensorReading>();

    public ObservableCollection<HardwareCard> Cards { get; } = new();

    [ObservableProperty] private string _summary = "Reading sensors…";
    [ObservableProperty] private string _search = "";

    partial void OnSearchChanged(string value)
    {
        foreach (var card in Cards) card.ClearRows();
        Update(_last);
    }

    [RelayCommand]
    private void ExpandAll()
    {
        foreach (var c in Cards) c.IsExpanded = true;
    }

    [RelayCommand]
    private void CollapseAll()
    {
        foreach (var c in Cards) c.IsExpanded = false;
    }

    public void Update(IReadOnlyList<SensorReading> readings)
    {
        _last = readings;
        var query = Search.Trim();
        int shown = 0;

        foreach (var r in readings)
        {
            if (!_cards.TryGetValue(r.HardwareId, out var card))
            {
                card = new HardwareCard(r.Hardware);
                _cards[r.HardwareId] = card;
                Cards.Add(card);
            }

            if (query.Length > 0 &&
                !r.Name.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !r.Hardware.Contains(query, StringComparison.OrdinalIgnoreCase))
                continue;

            card.Apply(r);
            shown++;
        }

        foreach (var card in Cards) card.Refresh();

        if (readings.Count > 0)
            Summary = query.Length > 0
                ? $"{shown} of {readings.Count} sensors match"
                : $"{readings.Count} sensors across {Cards.Count} devices";
    }
}

/// <summary>One device (CPU, GPU, motherboard chip, drive...) on the Sensors page.</summary>
public partial class HardwareCard : ObservableObject
{
    private readonly Dictionary<SensorType, SensorGroup> _sections = new();
    private readonly Dictionary<string, SensorRow> _rows = new();

    public HardwareCard(string name) => Name = name;

    public string Name { get; }
    public ObservableCollection<SensorGroup> Sections { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Chevron))]
    private bool _isExpanded = true;

    [ObservableProperty] private string _countText = "";
    [ObservableProperty] private bool _hasRows;   // false while a filter matches nothing on this device

    public string Chevron => IsExpanded ? "" : "";

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;

    public void Apply(SensorReading r)
    {
        if (!_sections.TryGetValue(r.Type, out var section))
        {
            section = new SensorGroup(SensorTypes.Name(r.Type));
            _sections[r.Type] = section;
            var order = SensorTypes.Order(r.Type);
            var index = Sections.Count(s => SensorTypes.Order(TypeOf(s)) < order);
            Sections.Insert(index, section);
        }

        if (!_rows.TryGetValue(r.Id, out var row))
        {
            row = new SensorRow(r.Name, "");
            _rows[r.Id] = row;
            section.Rows.Add(row);
        }
        row.Apply(r);
    }

    public void Refresh()
    {
        CountText = _rows.Count == 1 ? "1 sensor" : $"{_rows.Count} sensors";
        HasRows = _rows.Count > 0;
    }

    /// <summary>Drop rows so a new filter can rebuild them.</summary>
    public void ClearRows()
    {
        _rows.Clear();
        _sections.Clear();
        Sections.Clear();
    }

    private SensorType TypeOf(SensorGroup g) => _sections.First(kv => ReferenceEquals(kv.Value, g)).Key;
}

public static class SensorTypes
{
    private static readonly SensorType[] Ordered =
    [
        SensorType.Temperature, SensorType.Load, SensorType.Clock, SensorType.Power, SensorType.Voltage,
        SensorType.Current, SensorType.Fan, SensorType.Control, SensorType.Data, SensorType.SmallData,
        SensorType.Throughput, SensorType.Level, SensorType.Factor, SensorType.Flow, SensorType.Frequency,
        SensorType.Energy, SensorType.Noise, SensorType.Humidity, SensorType.TimeSpan,
    ];

    public static int Order(SensorType t)
    {
        var i = Array.IndexOf(Ordered, t);
        return i < 0 ? 99 : i;
    }

    public static string Name(SensorType t) => t switch
    {
        SensorType.Temperature => "Temperatures",
        SensorType.Load => "Loads",
        SensorType.Clock => "Clocks",
        SensorType.Power => "Power",
        SensorType.Voltage => "Voltages",
        SensorType.Current => "Currents",
        SensorType.Fan => "Fans",
        SensorType.Control => "Fan control",
        SensorType.Data => "Data",
        SensorType.SmallData => "Memory",
        SensorType.Throughput => "Throughput",
        SensorType.Level => "Levels",
        _ => t.ToString(),
    };
}

public sealed class SensorGroup
{
    public SensorGroup(string name) => Name = name;
    public string Name { get; }
    public ObservableCollection<SensorRow> Rows { get; } = new();
}

public partial class SensorRow : ObservableObject
{
    public SensorRow(string name, string type)
    {
        Name = name;
        Type = type;
    }

    public string Name { get; }
    public string Type { get; }

    [ObservableProperty] private string _value = Format.Dash;
    [ObservableProperty] private string _min = Format.Dash;
    [ObservableProperty] private string _max = Format.Dash;

    private float? _v, _min, _max;
    private bool _formatted;
    private (bool, string, string, string) _units;

    /// <summary>Only re-formats the numbers that actually changed.</summary>
    public void Apply(SensorReading r)
    {
        var units = (Format.UseFahrenheit, Format.NetUnit, Format.DiskUnit, Format.MemoryUnit);
        var unitChanged = units != _units;
        _units = units;
        if (!_formatted || unitChanged || r.Value != _v) Value = Format.Sensor(r.Type, _v = r.Value);
        if (!_formatted || unitChanged || r.Min != _min) Min = Format.Sensor(r.Type, _min = r.Min);
        if (!_formatted || unitChanged || r.Max != _max) Max = Format.Sensor(r.Type, _max = r.Max);
        _formatted = true;
    }
}
