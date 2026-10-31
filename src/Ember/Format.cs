using LibreHardwareMonitor.Hardware;

namespace Ember;

/// <summary>Display formatting shared by every page.</summary>
public static class Format
{
    public const string Dash = "—";

    public static bool UseFahrenheit { get; set; }

    /// <summary>Process memory: "auto" (MB or GB), "mb" (always MB, like Task Manager) or "gb".</summary>
    public static string MemoryUnit { get; set; } = "auto";

    /// <summary>Network speed: "bits" (Kbps/Mbps/Gbps), "mbps", "bytes" (KB/s/MB/s) or "mbs".</summary>
    public static string NetUnit { get; set; } = "bits";

    /// <summary>Disk speed: "auto" (KB/s/MB/s/GB/s) or "mbs" (always MB/s, like Task Manager).</summary>
    public static string DiskUnit { get; set; } = "auto";

    public static string ProcessMemory(long bytes) => MemoryUnit switch
    {
        "mb" => $"{bytes / 1048576.0:N1} MB",
        "gb" => $"{bytes / 1073741824.0:N2} GB",
        _ => Bytes(bytes),
    };

    public static string Temp(float? celsius)
    {
        if (celsius is not float c) return Dash;
        return UseFahrenheit ? $"{c * 9 / 5 + 32:0}°F" : $"{c:0}°C";
    }

    public static string Percent(float? v) => v is float f ? $"{f:0}%" : Dash;

    public static string Clock(float? mhz)
    {
        if (mhz is not float f) return Dash;
        return f >= 1000 ? $"{f / 1000:0.00} GHz" : $"{f:0} MHz";
    }

    public static string Watts(float? w) => w is float f ? $"{f:0} W" : Dash;

    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i <= 1 ? $"{v:0} {units[i]}" : $"{v:0.0} {units[i]}";
    }

    /// <summary>Network rate in bits per second, the way ISPs quote it (Mbps).</summary>
    public static string NetRate(double bytesPerSecond)
    {
        var bits = bytesPerSecond * 8;
        return NetUnit switch
        {
            "mbps" => $"{bits / 1e6:N1} Mbps",
            "mbs" => $"{bytesPerSecond / 1048576:N2} MB/s",
            "bytes" => bytesPerSecond >= 1073741824 ? $"{bytesPerSecond / 1073741824:0.00} GB/s"
                : bytesPerSecond >= 1048576 ? $"{bytesPerSecond / 1048576:0.0} MB/s"
                : $"{bytesPerSecond / 1024:0} KB/s",
            _ => bits >= 1e9 ? $"{bits / 1e9:0.00} Gbps"
                : bits >= 1e6 ? $"{bits / 1e6:0.0} Mbps"
                : bits >= 1e3 ? $"{bits / 1e3:0} Kbps"
                : $"{bits:0} bps",
        };
    }

    /// <summary>Per-process network column: "—" when unavailable, zero shown in the chosen unit.</summary>
    public static string ProcessNet(double bytesPerSecond)
    {
        if (double.IsNaN(bytesPerSecond)) return Dash;
        if (bytesPerSecond < 125)
            return NetUnit is "bytes" or "mbs" ? "0 MB/s" : "0 Mbps";
        return NetRate(bytesPerSecond);
    }

    public static string LinkSpeed(long bps) =>
        bps <= 0 ? Dash : bps >= 1_000_000_000 ? $"{bps / 1e9:0.#} Gbps" : $"{bps / 1e6:0} Mbps";

    /// <summary>Disk transfer rate (bytes per second).</summary>
    public static string DiskRate(double bytesPerSecond) =>
        DiskUnit == "mbs" ? $"{bytesPerSecond / 1048576:N1} MB/s"
        : bytesPerSecond < 1024 ? "0 MB/s"
        : Bytes((long)bytesPerSecond) + "/s";

    public static string Uptime(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h {t.Minutes}m"
        : t.TotalHours >= 1 ? $"{t.Hours}h {t.Minutes}m"
        : $"{t.Minutes}m";

    public static string Sensor(SensorType type, float? value)
    {
        if (value is not float v) return Dash;
        return type switch
        {
            SensorType.Temperature => Temp(v),
            SensorType.Load or SensorType.Control or SensorType.Level => $"{v:0.0}%",
            SensorType.Clock => $"{v:0} MHz",
            SensorType.Power => $"{v:0.0} W",
            SensorType.Voltage => $"{v:0.000} V",
            SensorType.Fan => $"{v:0} RPM",
            SensorType.Flow => $"{v:0} L/h",
            SensorType.Data => $"{v:0.0} GB",
            SensorType.SmallData => $"{v:0} MB",
            SensorType.Throughput => Bytes((long)v) + "/s",
            SensorType.Frequency => $"{v:0} Hz",
            _ => $"{v:0.##}",
        };
    }
}
