using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Ember.Models;

namespace Ember.ViewModels;

public partial class SensorsViewModel : ViewModelBase
{
    private readonly Dictionary<string, SensorGroup> _groups = new();
    private readonly Dictionary<string, SensorRow> _rows = new();

    public ObservableCollection<SensorGroup> Groups { get; } = new();

    [ObservableProperty] private string _summary = "Reading sensors…";

    public void Update(IReadOnlyList<SensorReading> readings)
    {
        foreach (var r in readings)
        {
            if (!_groups.TryGetValue(r.HardwareId, out var group))
            {
                group = new SensorGroup(r.Hardware);
                _groups[r.HardwareId] = group;
                Groups.Add(group);
            }

            if (!_rows.TryGetValue(r.Id, out var row))
            {
                row = new SensorRow(r.Name, r.Type.ToString());
                _rows[r.Id] = row;
                group.Rows.Add(row);
            }

            row.Apply(r);
        }

        if (_rows.Count > 0)
            Summary = $"{_rows.Count} sensors across {_groups.Count} devices";
    }
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

    public void Apply(SensorReading r)
    {
        Value = Format.Sensor(r.Type, r.Value);
        Min = Format.Sensor(r.Type, r.Min);
        Max = Format.Sensor(r.Type, r.Max);
    }
}
