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
    IReadOnlyList<SensorReading> Sensors)
{
    public static readonly HardwareSnapshot Empty = new(
        "Processor", null, null, null, null,
        "Graphics", null, null, null, null,
        null, null,
        null, null, null,
        Array.Empty<string>(),
        Array.Empty<SensorReading>());
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
