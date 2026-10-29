using Ember.Models;
using LibreHardwareMonitor.Hardware;

namespace Ember.Services;

/// <summary>
/// Applies fan settings through LibreHardwareMonitor's control sensors (motherboard Super I/O
/// headers and GPU coolers). Runs on the monitor thread once per tick.
///
/// Safety rules:
///  - Auto hands the fan back to the BIOS / GPU driver.
///  - A curve whose temperature sensor disappears runs the fan at 100%.
///  - Speeds never go below the fan's floor (default 20%) or the hardware minimum.
///  - Speed ramps up quickly and down slowly, so fans don't pulse.
///  - Every fan Ember touched is restored to BIOS control on exit, crash or update.
/// </summary>
public sealed class FanController
{
    private const double MaxStepUp = 15;     // % per tick
    private const double MaxStepDown = 4;    // % per tick

    private readonly object _gate = new();
    private Dictionary<string, FanConfig> _configs = new();
    private readonly Dictionary<string, ISensor> _controls = new();
    private readonly Dictionary<string, double> _applied = new();

    public FanController()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => RestoreAll();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => RestoreAll();
    }

    /// <summary>Thread-safe: called from the UI whenever a fan setting changes.</summary>
    public void SetConfigs(IEnumerable<FanConfig> configs)
    {
        var copy = new Dictionary<string, FanConfig>();
        foreach (var c in configs) copy[c.Id] = c.Clone();
        lock (_gate) _configs = copy;
    }

    public List<FanChannel> Update(IEnumerable<IHardware> hardware, IReadOnlyDictionary<string, float?> temps)
    {
        var channels = new List<FanChannel>();
        lock (_gate)
        {
            foreach (var hw in hardware) Visit(hw, temps, channels);
        }
        return channels;
    }

    private void Visit(IHardware hw, IReadOnlyDictionary<string, float?> temps, List<FanChannel> channels)
    {
        foreach (var sensor in hw.Sensors)
        {
            if (sensor.SensorType != SensorType.Control || sensor.Control is null) continue;

            var id = sensor.Identifier.ToString();
            _controls[id] = sensor;

            // The RPM sensor for a header shares its index with the control sensor.
            var rpm = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Fan && s.Index == sensor.Index);
            var isGpu = hw.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;

            channels.Add(new FanChannel(id, rpm?.Name ?? sensor.Name, hw.Name, sensor.Value, rpm?.Value,
                sensor.Control.MinSoftwareValue, sensor.Control.MaxSoftwareValue, isGpu));

            try { Drive(id, sensor, temps); }
            catch { /* a single misbehaving header shouldn't stop the others */ }
        }

        foreach (var sub in hw.SubHardware) Visit(sub, temps, channels);
    }

    private void Drive(string id, ISensor sensor, IReadOnlyDictionary<string, float?> temps)
    {
        var control = sensor.Control!;
        _configs.TryGetValue(id, out var config);

        if (config is null || config.Mode == FanMode.Auto)
        {
            if (_applied.Remove(id)) control.SetDefault();
            return;
        }

        double target = config.Mode == FanMode.Fixed ? config.FixedPercent : Evaluate(config, temps);
        double floor = Math.Max(config.MinPercent, control.MinSoftwareValue);
        double ceiling = control.MaxSoftwareValue > floor ? control.MaxSoftwareValue : 100;
        target = Math.Clamp(target, floor, ceiling);

        double next = target;
        if (config.Mode == FanMode.Curve)
        {
            double last = _applied.TryGetValue(id, out var l) ? l : sensor.Value ?? target;
            next = target > last ? Math.Min(target, last + MaxStepUp) : Math.Max(target, last - MaxStepDown);
        }

        if (!_applied.TryGetValue(id, out var previous) || Math.Abs(previous - next) >= 0.5)
            control.SetSoftware((float)next);
        _applied[id] = next;
    }

    private static double Evaluate(FanConfig config, IReadOnlyDictionary<string, float?> temps)
    {
        // Failsafe: no sensor, or it stopped reporting → full speed.
        if (config.TempSource is null || !temps.TryGetValue(config.TempSource, out var reading) || reading is not float temp)
            return 100;

        var points = config.Points.OrderBy(p => p.Temp).ToList();
        if (points.Count == 0) return 100;
        if (temp <= points[0].Temp) return points[0].Percent;
        if (temp >= points[^1].Temp) return points[^1].Percent;

        for (int i = 1; i < points.Count; i++)
        {
            var a = points[i - 1];
            var b = points[i];
            if (temp <= b.Temp)
            {
                var t = b.Temp - a.Temp <= 0 ? 1 : (temp - a.Temp) / (b.Temp - a.Temp);
                return a.Percent + (b.Percent - a.Percent) * t;
            }
        }
        return points[^1].Percent;
    }

    /// <summary>Give every fan Ember touched back to the BIOS / driver.</summary>
    public void RestoreAll()
    {
        lock (_gate)
        {
            foreach (var id in _applied.Keys.ToList())
            {
                try { if (_controls.TryGetValue(id, out var s)) s.Control?.SetDefault(); } catch { }
            }
            _applied.Clear();
        }
    }
}
