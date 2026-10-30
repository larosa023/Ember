using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ember.Models;

namespace Ember.ViewModels;

public partial class DashboardViewModel : ViewModelBase
{
    private readonly History _cpuHistory = new(60);
    private readonly History _gpuHistory = new(60);
    private readonly History _ramHistory = new(60);

    // CPU
    [ObservableProperty] private string _cpuName = "Processor";
    [ObservableProperty] private double _cpuLoad;
    [ObservableProperty] private string _cpuLoadText = Format.Dash;
    [ObservableProperty] private string _cpuTempText = Format.Dash;
    [ObservableProperty] private string _cpuClockText = Format.Dash;
    [ObservableProperty] private string _cpuPowerText = Format.Dash;
    [ObservableProperty] private double[] _cpuHistoryValues = Array.Empty<double>();

    // GPU
    [ObservableProperty] private string _gpuName = "Graphics";
    [ObservableProperty] private double _gpuLoad;
    [ObservableProperty] private string _gpuLoadText = Format.Dash;
    [ObservableProperty] private string _gpuTempText = Format.Dash;
    [ObservableProperty] private string _gpuClockText = Format.Dash;
    [ObservableProperty] private string _vramText = Format.Dash;
    [ObservableProperty] private double[] _gpuHistoryValues = Array.Empty<double>();

    // Memory
    [ObservableProperty] private double _ramLoad;
    [ObservableProperty] private string _ramLoadText = Format.Dash;
    [ObservableProperty] private string _ramUsedText = Format.Dash;
    [ObservableProperty] private string _ramFreeText = Format.Dash;
    [ObservableProperty] private string _ramTotalText = Format.Dash;
    [ObservableProperty] private double[] _ramHistoryValues = Array.Empty<double>();

    [ObservableProperty] private string _uptimeText = "";

    /// <summary>Raised when a CPU / GPU / Memory card is clicked.</summary>
    public event Action<string>? OpenRequested;

    /// <summary>"cpu", "gpu", "ram", "storage" or "network".</summary>
    [RelayCommand]
    private void Open(string page) => OpenRequested?.Invoke(page);

    // ---------- Storage card ----------

    private readonly Dictionary<string, VolumeRow> _volumeRows = new();
    public ObservableCollection<VolumeRow> Volumes { get; } = new();
    [ObservableProperty] private string _storageSummary = "";

    // ---------- Network card ----------

    private readonly History _downHistory = new(60);
    private readonly History _upHistory = new(60);
    [ObservableProperty] private string _netName = "No connection";
    [ObservableProperty] private string _netDownText = Format.Dash;
    [ObservableProperty] private string _netUpText = Format.Dash;
    [ObservableProperty] private double[] _netDownHistory = Array.Empty<double>();
    [ObservableProperty] private double[] _netUpHistory = Array.Empty<double>();
    [ObservableProperty] private double _netMax = 1;

    private void UpdateStorageAndNetwork(HardwareSnapshot s)
    {
        foreach (var v in s.Volumes)
        {
            if (!_volumeRows.TryGetValue(v.Letter, out var row))
            {
                row = new VolumeRow(v.Letter);
                _volumeRows[v.Letter] = row;
                var index = Volumes.Count(r => string.CompareOrdinal(r.Letter, v.Letter) < 0);
                Volumes.Insert(index, row);
            }
            row.Apply(v);
        }
        foreach (var gone in _volumeRows.Keys.Where(k => s.Volumes.All(v => v.Letter != k)).ToList())
        {
            Volumes.Remove(_volumeRows[gone]);
            _volumeRows.Remove(gone);
        }

        var hottest = s.Sensors
            .Where(r => r.HardwareType == LibreHardwareMonitor.Hardware.HardwareType.Storage &&
                        r.Type == LibreHardwareMonitor.Hardware.SensorType.Temperature &&
                        r.Name.StartsWith("Temperature", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Value).Max();
        var free = s.Volumes.Sum(v => v.Free);
        StorageSummary = s.Volumes.Count == 0 ? ""
            : hottest is float t ? $"{Format.Bytes(free)} free · hottest drive {Format.Temp(t)}"
            : $"{Format.Bytes(free)} free";

        var primary = s.Network.FirstOrDefault(n => n.IsPrimary) ?? s.Network.FirstOrDefault();
        NetName = primary is null ? "No connection" : $"{primary.Kind} · {primary.Name}";
        NetDownText = primary is null ? Format.Dash : Format.NetRate(primary.DownBps);
        NetUpText = primary is null ? Format.Dash : Format.NetRate(primary.UpBps);
        NetDownHistory = _downHistory.Push(primary?.DownBps ?? 0);
        NetUpHistory = _upHistory.Push(primary?.UpBps ?? 0);
        NetMax = Math.Max(NetDownHistory.Concat(NetUpHistory).Max() * 1.15, 64 * 1024);
    }

    public void Update(HardwareSnapshot s)
    {
        CpuName = s.CpuName;
        CpuLoad = s.CpuLoad ?? 0;
        CpuLoadText = Format.Percent(s.CpuLoad);
        CpuTempText = Format.Temp(s.CpuTemp);
        CpuClockText = Format.Clock(s.CpuClock);
        CpuPowerText = Format.Watts(s.CpuPower);
        CpuHistoryValues = _cpuHistory.Push(s.CpuLoad ?? 0);

        GpuName = s.GpuName;
        GpuLoad = s.GpuLoad ?? 0;
        GpuLoadText = Format.Percent(s.GpuLoad);
        GpuTempText = Format.Temp(s.GpuTemp);
        GpuClockText = Format.Clock(s.GpuClock);
        VramText = s.VramUsedMb is not float used ? Format.Dash
            : s.VramTotalMb is float total && total > 0 ? $"{used / 1024:0.0} / {total / 1024:0} GB"
            : $"{used / 1024:0.0} GB";
        GpuHistoryValues = _gpuHistory.Push(s.GpuLoad ?? 0);

        RamLoad = s.RamLoad ?? 0;
        RamLoadText = Format.Percent(s.RamLoad);
        RamUsedText = s.RamUsedGb is float u ? $"{u:0.0} GB" : Format.Dash;
        RamFreeText = s.RamFreeGb is float f ? $"{f:0.0} GB" : Format.Dash;
        RamTotalText = s.RamUsedGb is float u2 && s.RamFreeGb is float f2 ? $"{u2 + f2:0} GB" : Format.Dash;
        RamHistoryValues = _ramHistory.Push(s.RamLoad ?? 0);

        UpdateStorageAndNetwork(s);

        UptimeText = $"System up {Format.Uptime(TimeSpan.FromMilliseconds(Environment.TickCount64))}";
    }

    /// <summary>Fixed-size rolling history; returns a fresh array so bindings redraw.</summary>
    private sealed class History
    {
        private readonly double[] _buffer;
        public History(int size) => _buffer = new double[size];

        public double[] Push(double value)
        {
            Array.Copy(_buffer, 1, _buffer, 0, _buffer.Length - 1);
            _buffer[^1] = value;
            return (double[])_buffer.Clone();
        }
    }
}

/// <summary>One drive letter with a usage bar (dashboard card and Storage page).</summary>
public partial class VolumeRow : ObservableObject
{
    public VolumeRow(string letter) => Letter = letter;

    public string Letter { get; }
    [ObservableProperty] private string _label = "";
    [ObservableProperty] private string _usedText = "";
    [ObservableProperty] private string _freeText = "";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private double _percent;
    [ObservableProperty] private bool _isNearlyFull;

    public void Apply(VolumeInfo v)
    {
        var used = Math.Max(0, v.Total - v.Free);
        Label = v.Label;
        Percent = v.Total > 0 ? used * 100.0 / v.Total : 0;
        IsNearlyFull = Percent >= 90;
        UsedText = $"{Format.Bytes(used)} of {Format.Bytes(v.Total)}";
        FreeText = $"{Format.Bytes(v.Free)} free";
        Detail = $"{v.Format} · {Percent:0}% used";
    }
}
