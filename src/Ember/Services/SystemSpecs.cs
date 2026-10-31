using System.Globalization;
using System.Management;
using System.Net.NetworkInformation;
using System.Text;
using Microsoft.Win32;

namespace Ember.Services;

public sealed record SpecRow(string Label, string Value);
public sealed record SpecSection(string Title, string Icon, IReadOnlyList<SpecRow> Rows, IReadOnlyList<string> Summary);

/// <summary>
/// Reads the PC's specs once (WMI + registry). No serial numbers, MAC addresses or computer name,
/// so the copied text is safe to paste on a forum.
/// </summary>
public static class SystemSpecs
{
    public static List<SpecSection> Read()
    {
        var sections = new List<SpecSection>();
        Try(sections, Windows);
        Try(sections, Processor);
        Try(sections, Motherboard);
        Try(sections, Memory);
        Try(sections, Graphics);
        Try(sections, Storage);
        Try(sections, Network);
        return sections;
    }

    /// <summary>Plain-text summary for "Copy specs".</summary>
    public static string ToText(IEnumerable<SpecSection> sections)
    {
        var sb = new StringBuilder();
        foreach (var s in sections)
            foreach (var line in s.Summary)
                sb.AppendLine(line);
        return sb.ToString().TrimEnd();
    }

    private static void Try(List<SpecSection> into, Func<SpecSection?> read)
    {
        try { if (read() is { } s) into.Add(s); }
        catch { /* one failing section shouldn't hide the rest */ }
    }

    // ---------- Sections ----------

    private static SpecSection? Windows()
    {
        var os = First("SELECT Caption, InstallDate, LastBootUpTime, OSArchitecture FROM Win32_OperatingSystem");
        using var cv = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var caption = Str(os, "Caption")?.Replace("Microsoft ", "") ?? "Windows";
        var display = cv?.GetValue("DisplayVersion")?.ToString();
        var build = cv?.GetValue("CurrentBuild")?.ToString();
        var ubr = cv?.GetValue("UBR")?.ToString();
        var buildText = build is null ? null : ubr is null ? build : $"{build}.{ubr}";

        var rows = new List<SpecRow> { new("Edition", caption) };
        if (display is not null) rows.Add(new("Version", display));
        if (buildText is not null) rows.Add(new("Build", buildText));
        if (Str(os, "OSArchitecture") is { } arch) rows.Add(new("Architecture", arch));
        if (Date(os, "InstallDate") is { } installed) rows.Add(new("Installed", installed.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)));
        if (Date(os, "LastBootUpTime") is { } boot)
            rows.Add(new("Last restart", $"{boot.ToString("MMM d, h:mm tt", CultureInfo.InvariantCulture)} ({Format.Uptime(DateTime.Now - boot)} ago)"));

        var summary = $"OS: {caption}{(display is null ? "" : $" {display}")}{(buildText is null ? "" : $" (build {buildText})")}";
        return new SpecSection("Windows", "", rows, [summary]);
    }

    private static SpecSection? Processor()
    {
        var cpu = First("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, L2CacheSize, L3CacheSize, SocketDesignation FROM Win32_Processor");
        if (cpu is null) return null;
        var name = Str(cpu, "Name")?.Trim() ?? "Processor";
        var cores = Int(cpu, "NumberOfCores");
        var threads = Int(cpu, "NumberOfLogicalProcessors");

        var rows = new List<SpecRow> { new("Model", name) };
        if (cores > 0) rows.Add(new("Cores / threads", $"{cores} / {threads}"));
        if (Int(cpu, "MaxClockSpeed") is var mhz and > 0) rows.Add(new("Base speed", $"{mhz / 1000.0:0.00} GHz"));
        if (Int(cpu, "L2CacheSize") is var l2 and > 0) rows.Add(new("L2 cache", Format.Bytes(l2 * 1024L)));
        if (Int(cpu, "L3CacheSize") is var l3 and > 0) rows.Add(new("L3 cache", Format.Bytes(l3 * 1024L)));
        if (Str(cpu, "SocketDesignation") is { Length: > 0 } socket) rows.Add(new("Socket", socket));

        return new SpecSection("Processor", "", rows,
            [$"CPU: {name}{(cores > 0 ? $" ({cores} cores / {threads} threads)" : "")}"]);
    }

    private static SpecSection? Motherboard()
    {
        var board = First("SELECT Manufacturer, Product, Version FROM Win32_BaseBoard");
        var bios = First("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS");
        var maker = Str(board, "Manufacturer")?.Trim() ?? "";
        var product = Str(board, "Product")?.Trim() ?? "";

        var rows = new List<SpecRow>();
        if (maker.Length > 0) rows.Add(new("Manufacturer", maker));
        if (product.Length > 0) rows.Add(new("Model", product));
        var biosVersion = Str(bios, "SMBIOSBIOSVersion")?.Trim();
        var biosDate = Date(bios, "ReleaseDate")?.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);
        if (biosVersion is not null) rows.Add(new("BIOS", $"{Str(bios, "Manufacturer")?.Trim()} {biosVersion}".Trim()));
        if (biosDate is not null) rows.Add(new("BIOS date", biosDate));
        if (rows.Count == 0) return null;

        return new SpecSection("Motherboard", "", rows,
            [$"Motherboard: {maker} {product}{(biosVersion is null ? "" : $" (BIOS {biosVersion}{(biosDate is null ? "" : $", {biosDate}")})")}".Trim()]);
    }

    private static SpecSection? Memory()
    {
        var sticks = All("SELECT DeviceLocator, Capacity, Speed, ConfiguredClockSpeed, Manufacturer, PartNumber, SMBIOSMemoryType FROM Win32_PhysicalMemory");
        var slots = All("SELECT MemoryDevices FROM Win32_PhysicalMemoryArray").Sum(a => Int(a, "MemoryDevices"));
        if (sticks.Count == 0) return null;

        long total = sticks.Sum(s => Long(s, "Capacity"));
        var type = Int(sticks[0], "SMBIOSMemoryType") switch { 34 => "DDR5", 26 => "DDR4", 24 => "DDR3", _ => "" };
        var speed = sticks.Max(s => Math.Max(Int(s, "ConfiguredClockSpeed"), 0));
        if (speed == 0) speed = sticks.Max(s => Int(s, "Speed"));

        var rows = new List<SpecRow>
        {
            new("Installed", $"{Format.Bytes(total)}{(type.Length > 0 ? $" {type}" : "")}"),
        };
        if (speed > 0) rows.Add(new("Speed", $"{speed} MT/s"));
        rows.Add(new("Slots used", slots > 0 ? $"{sticks.Count} of {slots}" : sticks.Count.ToString()));
        foreach (var s in sticks)
        {
            var part = $"{Str(s, "Manufacturer")?.Trim()} {Str(s, "PartNumber")?.Trim()}".Trim();
            rows.Add(new(Str(s, "DeviceLocator")?.Trim() ?? "Module",
                $"{Format.Bytes(Long(s, "Capacity"))}{(part.Length > 0 && part != "Unknown" ? $" · {part}" : "")}"));
        }

        var sizes = sticks.GroupBy(s => Long(s, "Capacity")).Select(g => $"{g.Count()} × {Format.Bytes(g.Key)}");
        return new SpecSection("Memory", "", rows,
            [$"RAM: {Format.Bytes(total)}{(type.Length > 0 ? $" {type}" : "")}{(speed > 0 ? $" @ {speed} MT/s" : "")} ({string.Join(", ", sizes)})"]);
    }

    private static SpecSection? Graphics()
    {
        var gpus = All("SELECT Name, DriverVersion, DriverDate, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate FROM Win32_VideoController")
            .Where(g => Str(g, "Name") is { } n && !n.Contains("Basic Display", StringComparison.OrdinalIgnoreCase)
                        && !n.Contains("Remote", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (gpus.Count == 0) return null;

        var vram = ReadVram();
        var rows = new List<SpecRow>();
        var summary = new List<string>();
        foreach (var g in gpus)
        {
            var name = Str(g, "Name")!.Trim();
            var driver = Str(g, "DriverVersion");
            var date = Date(g, "DriverDate")?.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);
            var memory = vram.GetValueOrDefault(name);
            rows.Add(new("Model", name));
            if (memory > 0) rows.Add(new("Video memory", Format.Bytes(memory)));
            if (driver is not null) rows.Add(new("Driver", date is null ? driver : $"{driver} ({date})"));
            var w = Int(g, "CurrentHorizontalResolution");
            if (w > 0) rows.Add(new("Display", $"{w} × {Int(g, "CurrentVerticalResolution")} @ {Int(g, "CurrentRefreshRate")} Hz"));
            summary.Add($"GPU: {name}{(memory > 0 ? $" ({Format.Bytes(memory)})" : "")}{(driver is null ? "" : $", driver {driver}")}");
        }
        return new SpecSection("Graphics", "", rows, summary);
    }

    private static SpecSection? Storage()
    {
        var disks = All("SELECT FriendlyName, MediaType, BusType, Size FROM MSFT_PhysicalDisk", @"root\Microsoft\Windows\Storage");
        if (disks.Count == 0) return null;
        var rows = new List<SpecRow>();
        var summary = new List<string>();
        foreach (var d in disks)
        {
            var kind = Int(d, "BusType") switch
            {
                17 => "NVMe SSD",
                7 => "USB drive",
                _ when Int(d, "MediaType") == 4 => "SSD",
                _ when Int(d, "MediaType") == 3 => "Hard drive",
                _ => "Drive",
            };
            var name = Str(d, "FriendlyName")?.Trim() ?? "Disk";
            var size = Format.Bytes(Long(d, "Size"));
            rows.Add(new(kind, $"{name} · {size}"));
            summary.Add($"Storage: {name} ({size} {kind})");
        }
        return new SpecSection("Storage", "", rows, summary);
    }

    private static SpecSection? Network()
    {
        var rows = new List<SpecRow>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            if (nic.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
                nic.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)) continue;
            var kind = nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : "Ethernet";
            rows.Add(new(kind, $"{nic.Description}{(nic.Speed > 0 ? $" · {Format.LinkSpeed(nic.Speed)}" : "")}"));
        }
        return rows.Count == 0 ? null
            : new SpecSection("Network", "", rows, rows.Select(r => $"Network: {r.Value}").ToList());
    }

    // ---------- Helpers ----------

    /// <summary>VRAM per adapter name from the display driver's registry keys (WMI's value caps at 4 GB).</summary>
    private static Dictionary<string, long> ReadVram()
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
        if (cls is null) return result;
        foreach (var sub in cls.GetSubKeyNames().Where(n => n.All(char.IsDigit)))
        {
            try
            {
                using var key = cls.OpenSubKey(sub);
                if (key?.GetValue("DriverDesc") is not string desc) continue;
                long size = key.GetValue("HardwareInformation.qwMemorySize") switch
                {
                    long l => l,
                    byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0),
                    _ => key.GetValue("HardwareInformation.MemorySize") switch
                    {
                        int i => (uint)i,
                        byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b, 0),
                        _ => 0,
                    },
                };
                if (size > 0) result[desc.Trim()] = size;
            }
            catch { }
        }
        return result;
    }

    private static List<ManagementObject> All(string query, string scope = @"root\cimv2")
    {
        using var searcher = new ManagementObjectSearcher(scope, query);
        return searcher.Get().Cast<ManagementObject>().ToList();
    }

    private static ManagementObject? First(string query) => All(query).FirstOrDefault();

    private static string? Str(ManagementObject? o, string p)
    {
        try { return o?[p]?.ToString(); } catch { return null; }
    }

    private static int Int(ManagementObject? o, string p)
    {
        try { return o?[p] is { } v ? Convert.ToInt32(v) : 0; } catch { return 0; }
    }

    private static long Long(ManagementObject? o, string p)
    {
        try { return o?[p] is { } v ? Convert.ToInt64(v) : 0; } catch { return 0; }
    }

    private static DateTime? Date(ManagementObject? o, string p)
    {
        try { return Str(o, p) is { Length: > 0 } s ? ManagementDateTimeConverter.ToDateTime(s) : null; }
        catch { return null; }
    }
}
