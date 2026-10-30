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
    public event Action<DetailKind>? OpenRequested;

    [RelayCommand]
    private void Open(string kind) => OpenRequested?.Invoke(kind switch
    {
        "gpu" => DetailKind.Gpu,
        "ram" => DetailKind.Memory,
        _ => DetailKind.Cpu,
    });

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
