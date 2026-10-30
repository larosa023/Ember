using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ember.Models;
using LibreHardwareMonitor.Hardware;

namespace Ember.ViewModels;

/// <summary>Storage page: one card per physical disk with health, temperature, activity and its volumes.</summary>
public partial class StorageViewModel : ViewModelBase
{
    private readonly Action _back;
    private readonly Dictionary<int, DiskCard> _cards = new();
    private readonly Dictionary<string, VolumeRow> _otherRows = new();

    public StorageViewModel(Action back) => _back = back;

    public ObservableCollection<DiskCard> Disks { get; } = new();
    public ObservableCollection<VolumeRow> OtherVolumes { get; } = new();

    [ObservableProperty] private string _summary = "Reading drives…";
    [ObservableProperty] private bool _hasOtherVolumes;

    public bool ShowAdminNote => !Environment.IsPrivilegedProcess;

    [RelayCommand]
    private void Back() => _back();

    public void Update(HardwareSnapshot s)
    {
        foreach (var disk in s.Disks)
        {
            if (!_cards.TryGetValue(disk.Index, out var card))
            {
                card = new DiskCard(disk.Index);
                _cards[disk.Index] = card;
                var index = Disks.Count(c => c.Index < disk.Index);
                Disks.Insert(index, card);
            }

            var sensors = s.Sensors.Where(r => r.HardwareType == HardwareType.Storage && DiskNumber(r.HardwareId) == disk.Index).ToList();
            card.Apply(disk, sensors, s.Volumes.Where(v => v.DiskIndex == disk.Index).ToList());
        }

        // Volumes we couldn't tie to a disk (Storage WMI unavailable, virtual disks, ...).
        var known = s.Disks.Select(d => d.Index).ToHashSet();
        var others = s.Volumes.Where(v => v.DiskIndex is not int i || !known.Contains(i)).ToList();
        Sync(OtherVolumes, _otherRows, others);
        HasOtherVolumes = OtherVolumes.Count > 0;

        var total = s.Volumes.Sum(v => v.Total);
        var free = s.Volumes.Sum(v => v.Free);
        Summary = s.Disks.Count == 0 && s.Volumes.Count == 0 ? "Reading drives…"
            : $"{s.Disks.Count} {(s.Disks.Count == 1 ? "drive" : "drives")} · {Format.Bytes(free)} free of {Format.Bytes(total)}";
    }

    /// <summary>LHM storage ids look like "/nvme/0"; the number is the Windows disk number.</summary>
    private static int DiskNumber(string hardwareId)
    {
        var last = hardwareId.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return int.TryParse(last, out var n) ? n : -1;
    }

    internal static void Sync(ObservableCollection<VolumeRow> target, Dictionary<string, VolumeRow> rows, List<VolumeInfo> volumes)
    {
        foreach (var v in volumes)
        {
            if (!rows.TryGetValue(v.Letter, out var row))
            {
                row = new VolumeRow(v.Letter);
                rows[v.Letter] = row;
                target.Insert(target.Count(r => string.CompareOrdinal(r.Letter, v.Letter) < 0), row);
            }
            row.Apply(v);
        }
        foreach (var gone in rows.Keys.Where(k => volumes.All(v => v.Letter != k)).ToList())
        {
            target.Remove(rows[gone]);
            rows.Remove(gone);
        }
    }
}

public partial class DiskCard : ObservableObject
{
    private readonly Dictionary<string, VolumeRow> _rows = new();

    public DiskCard(int index) => Index = index;

    public int Index { get; }
    public ObservableCollection<VolumeRow> Volumes { get; } = new();
    public ObservableCollection<StatTile> Stats { get; } = new();
    private readonly Dictionary<string, StatTile> _stats = new();

    [ObservableProperty] private string _model = "";
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private string _health = "";
    [ObservableProperty] private bool _isHealthy = true;
    [ObservableProperty] private double _activity;
    [ObservableProperty] private string _activityText = Format.Dash;
    [ObservableProperty] private bool _hasVolumes;

    public void Apply(DiskInfo disk, List<SensorReading> sensors, List<VolumeInfo> volumes)
    {
        Model = disk.Model;
        Subtitle = $"{disk.Kind} · {Format.Bytes(disk.Size)} · Disk {disk.Index}";

        float? life = Find(sensors, SensorType.Level, "Life", "Remaining Life");
        if (life is null && Find(sensors, SensorType.Level, "Percentage Used") is float usedPct)
            life = Math.Max(0, 100 - usedPct);

        // Windows' own verdict first; a worn-out SSD (under 10% life) also counts as a warning.
        Health = disk.Health == "Healthy" && life is float l && l < 10 ? "Warning" : disk.Health;
        IsHealthy = Health is "Healthy" or "Unknown";

        var temp = sensors.FirstOrDefault(r => r.Type == SensorType.Temperature && r.Value is not null &&
                                               (r.Name == "Temperature" || r.Name.StartsWith("Temperature #", StringComparison.Ordinal)))?.Value;
        var activity = Find(sensors, SensorType.Load, "Total Activity");
        Activity = activity ?? 0;
        ActivityText = activity is float a ? $"{a:0}% active" : "";

        Stat("temp", "Temperature", Format.Temp(temp));
        Stat("life", "Life remaining", life is float lf ? $"{lf:0}%" : Format.Dash);
        Stat("read", "Read", Find(sensors, SensorType.Throughput, "Read Rate") is float rr ? Format.DiskRate(rr) : Format.Dash);
        Stat("write", "Write", Find(sensors, SensorType.Throughput, "Write Rate") is float wr ? Format.DiskRate(wr) : Format.Dash);
        Stat("written", "Total written", Find(sensors, SensorType.Data, "Data Written") is float dw ? Format.Bytes((long)(dw * 1024L * 1024 * 1024)) : Format.Dash);
        Stat("hours", "Powered on", Find(sensors, SensorType.Factor, "Power On Hours") is float h ? $"{h:N0} h" : Format.Dash);

        StorageViewModel.Sync(Volumes, _rows, volumes);
        HasVolumes = Volumes.Count > 0;
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

    private static float? Find(List<SensorReading> sensors, SensorType type, params string[] names)
    {
        foreach (var n in names)
        {
            var hit = sensors.FirstOrDefault(r => r.Type == type && r.Value is not null &&
                                                  string.Equals(r.Name, n, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit.Value;
        }
        return null;
    }
}
