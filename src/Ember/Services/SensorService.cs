using Ember.Models;
using LibreHardwareMonitor.Hardware;

namespace Ember.Services;

/// <summary>
/// Reads temperatures, loads, clocks, fans and power through LibreHardwareMonitorLib,
/// with a Windows performance-counter fallback for the GPU. Call only from the monitor thread.
/// </summary>
public sealed class SensorService : IDisposable
{
    private readonly Computer _computer = new()
    {
        IsCpuEnabled = true,
        IsGpuEnabled = true,
        IsMemoryEnabled = true,
        IsMotherboardEnabled = true,
        IsStorageEnabled = true,
        IsControllerEnabled = true,
        IsNetworkEnabled = false,
    };

    private readonly UpdateVisitor _visitor = new();
    private readonly GpuFallback _gpuFallback = new();

    /// <summary>Drives fans per the user's Auto / Fixed / Curve settings, once per tick.</summary>
    public FanController Fans { get; } = new();
    private volatile string? _preferredGpu;
    private bool _opened;

    /// <summary>GPU name to report on the dashboard; null = automatic (discrete GPU first).</summary>
    public string? PreferredGpu
    {
        get => _preferredGpu;
        set => _preferredGpu = value;
    }

    public HardwareSnapshot Read()
    {
        if (!_opened)
        {
            _opened = true;       // only try once; Open() is slow
            _computer.Open();
        }

        _computer.Accept(_visitor);

        var all = new List<SensorReading>(256);
        var gpus = new List<IHardware>();
        IHardware? cpu = null, memory = null;

        foreach (var hw in _computer.Hardware)
        {
            Collect(hw, all);
            switch (hw.HardwareType)
            {
                case HardwareType.Cpu:
                    cpu ??= hw;
                    break;
                case HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel:
                    gpus.Add(hw);
                    break;
                case HardwareType.Memory:
                    // Newer LHM reports "Total Memory" and "Virtual Memory" separately; we want physical RAM.
                    if (hw.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase)) break;
                    if (memory is null && Find(hw, SensorType.Data, "Memory Used") is not null) memory = hw;
                    break;
            }
        }

        var gpu = PickGpu(gpus);

        string gpuName = gpu?.Name ?? "";
        float? gpuLoad = null, gpuTemp = null, gpuClock = null, gpuPower = null, vramUsed = null, vramTotal = null;
        if (gpu is not null)
        {
            gpuLoad = Find(gpu, SensorType.Load, "GPU Core") ?? MaxLoad(gpu, "D3D");
            gpuTemp = Find(gpu, SensorType.Temperature, "GPU Core", "GPU Hot Spot") ?? First(gpu, SensorType.Temperature);
            gpuClock = Find(gpu, SensorType.Clock, "GPU Core");
            gpuPower = Find(gpu, SensorType.Power, "GPU Package", "GPU Power", "GPU Core");
            vramUsed = Find(gpu, SensorType.SmallData, "GPU Memory Used", "D3D Dedicated Memory Used");
            vramTotal = Find(gpu, SensorType.SmallData, "GPU Memory Total");
        }

        // LibreHardwareMonitor missed the GPU (or has no load for it): ask Windows directly.
        if (gpu is null || gpuLoad is null)
        {
            var (load, usedMb) = _gpuFallback.Read();
            gpuLoad ??= load;
            vramUsed ??= usedMb;
            if (gpu is null) gpuName = _gpuFallback.Name ?? "No GPU detected";
        }

        var temps = new Dictionary<string, float?>();
        foreach (var r in all)
            if (r.Type == SensorType.Temperature) temps[r.Id] = r.Value;

        List<FanChannel> fanChannels;
        try { fanChannels = Fans.Update(_computer.Hardware, temps); }
        catch { fanChannels = new List<FanChannel>(); }

        var gpuNames = gpus.Select(g => g.Name).ToList();
        if (gpuNames.Count == 0 && _gpuFallback.Name is { } fallbackName) gpuNames.Add(fallbackName);

        return new HardwareSnapshot(
            CpuName: cpu?.Name ?? "Processor",
            CpuLoad: cpu is null ? null : Find(cpu, SensorType.Load, "CPU Total"),
            CpuTemp: cpu is null ? null
                : Find(cpu, SensorType.Temperature, "CPU Package", "Core (Tctl/Tdie)", "Core (Tctl)", "Tctl/Tdie", "Core Average", "Core Max")
                  ?? First(cpu, SensorType.Temperature),
            CpuClock: cpu is null ? null : AverageCoreClock(cpu),
            CpuPower: cpu is null ? null : Find(cpu, SensorType.Power, "CPU Package", "Package"),

            GpuName: gpuName,
            GpuLoad: gpuLoad,
            GpuTemp: gpuTemp,
            GpuClock: gpuClock,
            GpuPower: gpuPower,
            VramUsedMb: vramUsed,
            VramTotalMb: vramTotal,

            RamUsedGb: memory is null ? null : Find(memory, SensorType.Data, "Memory Used"),
            RamFreeGb: memory is null ? null : Find(memory, SensorType.Data, "Memory Available"),
            RamLoad: memory is null ? null : Find(memory, SensorType.Load, "Memory"),

            GpuNames: gpuNames,
            Sensors: all,
            FanChannels: fanChannels);
    }

    private IHardware? PickGpu(List<IHardware> gpus)
    {
        if (gpus.Count == 0) return null;

        var preferred = _preferredGpu;
        if (!string.IsNullOrEmpty(preferred))
        {
            var match = gpus.FirstOrDefault(g => string.Equals(g.Name, preferred, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }

        // Automatic: a discrete card beats integrated graphics.
        return gpus.FirstOrDefault(g => g.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd) ?? gpus[0];
    }

    private static void Collect(IHardware hw, List<SensorReading> into)
    {
        var hwId = hw.Identifier.ToString();
        foreach (var s in hw.Sensors)
            into.Add(new SensorReading(s.Identifier.ToString(), hw.Name, hwId, hw.HardwareType, s.Name, s.SensorType, s.Value, s.Min, s.Max));
        foreach (var sub in hw.SubHardware)
            Collect(sub, into);
    }

    private static float? Find(IHardware hw, SensorType type, params string[] names)
    {
        foreach (var name in names)
            foreach (var s in hw.Sensors)
                if (s.SensorType == type && s.Value is not null &&
                    string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
                    return s.Value;
        return null;
    }

    private static float? First(IHardware hw, SensorType type)
    {
        foreach (var s in hw.Sensors)
            if (s.SensorType == type && s.Value is not null) return s.Value;
        return null;
    }

    private static float? MaxLoad(IHardware hw, string prefix)
    {
        float? max = null;
        foreach (var s in hw.Sensors)
            if (s.SensorType == SensorType.Load && s.Value is float v &&
                s.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                max = max is null ? v : Math.Max(max.Value, v);
        return max;
    }

    private static float? AverageCoreClock(IHardware hw)
    {
        float sum = 0;
        int n = 0;
        foreach (var s in hw.Sensors)
        {
            if (s.SensorType == SensorType.Clock && s.Value is float v && v > 0 &&
                s.Name.StartsWith("Core #", StringComparison.Ordinal))
            {
                sum += v;
                n++;
            }
        }
        return n > 0 ? sum / n : null;
    }

    public void Dispose()
    {
        Fans.RestoreAll();   // hand every fan back to the BIOS before we let go of the hardware
        _gpuFallback.Dispose();
        if (_opened)
        {
            try { _computer.Close(); } catch { /* shutting down anyway */ }
        }
    }

    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);

        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (var sub in hardware.SubHardware) sub.Accept(this);
        }

        public void VisitSensor(ISensor sensor) { }
        public void VisitParameter(IParameter parameter) { }
    }
}
