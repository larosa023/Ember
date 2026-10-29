using Avalonia.Media;
using LibreHardwareMonitor.Hardware;

namespace Ember.Models;

public sealed record SensorReading(
    string Id,
    string Hardware,
    string HardwareId,
    string Name,
    SensorType Type,
    float? Value,
    float? Min,
    float? Max);

public sealed record HardwareSnapshot(
    string CpuName, float? CpuLoad, float? CpuTemp, float? CpuClock, float? CpuPower,
    string GpuName, float? GpuLoad, float? GpuTemp, float? GpuClock, float? GpuPower,
    float? VramUsedMb, float? VramTotalMb,
    float? RamUsedGb, float? RamFreeGb, float? RamLoad,
    IReadOnlyList<string> GpuNames,
    IReadOnlyList<SensorReading> Sensors,
    IReadOnlyList<FanChannel> FanChannels)
{
    public static readonly HardwareSnapshot Empty = new(
        "Processor", null, null, null, null,
        "Graphics", null, null, null, null,
        null, null,
        null, null, null,
        Array.Empty<string>(),
        Array.Empty<SensorReading>(),
        Array.Empty<FanChannel>());
}

public sealed record ProcessInfo(
    int Pid, string Name, double Cpu, long Memory,
    string? Path, IImage? Icon, string? Description,
    bool HasWindow, bool IsWindows);

public sealed class AppSettings
{
    public string Theme { get; set; } = "Ember";
    public int RefreshMs { get; set; } = 1000;
    public bool UseFahrenheit { get; set; }
    public bool AutoCheckUpdates { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    public bool CloseToTray { get; set; }

    /// <summary>GPU name to show on the dashboard; null picks automatically (discrete first).</summary>
    public string? PreferredGpu { get; set; }

    /// <summary>Process list column widths (name, pid, cpu, mem), set by dragging the header dividers.</summary>
    public Dictionary<string, double> ProcessColumns { get; set; } = new();

    /// <summary>Per-fan control settings (Auto / Fixed / Curve).</summary>
    public List<FanConfig> Fans { get; set; } = new();
}

/// <summary>A color theme. Built-ins live in ThemeService; users can drop JSON files in %AppData%\Ember\themes.</summary>
public sealed class ThemeDefinition
{
    public string Name { get; set; } = "Custom";
    public bool Dark { get; set; } = true;
    public string Background { get; set; } = "#0F0F12";
    public string Surface { get; set; } = "#17171C";
    public string SurfaceAlt { get; set; } = "#1F1F26";
    public string Stroke { get; set; } = "#26262E";
    public string Text { get; set; } = "#F2F2F5";
    public string TextMuted { get; set; } = "#8A8A96";
    public string Accent { get; set; } = "#FF6A2B";
    public string OnAccent { get; set; } = "#FFFFFF";
    public string Good { get; set; } = "#3DDC97";
    public string Warn { get; set; } = "#FFB020";
    public string Hot { get; set; } = "#FF4D4F";
}

/// <summary>A controllable fan found by LibreHardwareMonitor (motherboard header or GPU).</summary>
public sealed record FanChannel(
    string Id, string Name, string Hardware,
    float? Percent, float? Rpm,
    float MinPercent, float MaxPercent, bool IsGpu);

public enum FanMode { Auto, Fixed, Curve }

public sealed class CurvePointData
{
    public double Temp { get; set; }
    public double Percent { get; set; }
}

public sealed class FanConfig
{
    public string Id { get; set; } = "";
    public FanMode Mode { get; set; } = FanMode.Auto;
    public double FixedPercent { get; set; } = 50;
    public string? TempSource { get; set; }
    public List<CurvePointData> Points { get; set; } = DefaultCurve();

    /// <summary>Floor so a fan can never be driven to a stall by accident.</summary>
    public double MinPercent { get; set; } = 20;

    public static List<CurvePointData> DefaultCurve() =>
    [
        new() { Temp = 30, Percent = 25 },
        new() { Temp = 50, Percent = 35 },
        new() { Temp = 65, Percent = 55 },
        new() { Temp = 80, Percent = 85 },
        new() { Temp = 90, Percent = 100 },
    ];

    public FanConfig Clone() => new()
    {
        Id = Id,
        Mode = Mode,
        FixedPercent = FixedPercent,
        TempSource = TempSource,
        MinPercent = MinPercent,
        Points = Points.Select(p => new CurvePointData { Temp = p.Temp, Percent = p.Percent }).ToList(),
    };
}
