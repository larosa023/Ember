using System.Diagnostics;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Ember.Models;

namespace Ember.Services;

/// <summary>
/// Drives, volumes and network adapters. Runs on the monitor thread.
/// Disk list, health and drive-letter mapping come from Windows' Storage WMI (no admin needed),
/// refreshed every minute; volume space every 5 seconds; network rates every tick.
/// </summary>
public sealed class SystemInfoService
{
    private static readonly string[] VirtualHints =
        ["Hyper-V", "Virtual", "VPN", "TAP-", "WireGuard", "Wintun", "VMware", "VirtualBox", "Loopback", "Npcap", "ZeroTier", "Tailscale"];

    private DateTime _lastWmi = DateTime.MinValue;
    private DateTime _lastVolumes = DateTime.MinValue;
    private IReadOnlyList<DiskInfo> _disks = Array.Empty<DiskInfo>();
    private IReadOnlyList<VolumeInfo> _volumes = Array.Empty<VolumeInfo>();
    private Dictionary<char, int> _letterToDisk = new();
    private readonly Dictionary<string, (long Rx, long Tx, long Timestamp)> _netLast = new();

    public HardwareSnapshot Enrich(HardwareSnapshot s)
    {
        var now = DateTime.UtcNow;

        if (now - _lastWmi > TimeSpan.FromSeconds(60))
        {
            _lastWmi = now;
            try { ReadStorageWmi(); } catch { /* Storage WMI unavailable: volumes still work */ }
            _lastVolumes = DateTime.MinValue;   // re-map letters right away
        }

        if (now - _lastVolumes > TimeSpan.FromSeconds(5))
        {
            _lastVolumes = now;
            try { _volumes = ReadVolumes(); } catch { }
        }

        IReadOnlyList<NetAdapterInfo> network;
        try { network = ReadNetwork(); }
        catch { network = Array.Empty<NetAdapterInfo>(); }

        return s with { Disks = _disks, Volumes = _volumes, Network = network };
    }

    // ---------- Storage ----------

    private void ReadStorageWmi()
    {
        const string ns = @"root\Microsoft\Windows\Storage";
        var disks = new List<DiskInfo>();

        using (var searcher = new ManagementObjectSearcher(ns,
                   "SELECT DeviceId, FriendlyName, MediaType, BusType, Size, HealthStatus FROM MSFT_PhysicalDisk"))
        {
            using var results = searcher.Get();
            foreach (var o in results)
            {
                using var d = (ManagementObject)o;
                if (!int.TryParse(d["DeviceId"]?.ToString(), out var index)) continue;
                var media = ToInt(d["MediaType"]);
                var bus = ToInt(d["BusType"]);
                var kind = bus switch
                {
                    17 => "NVMe SSD",
                    7 => "USB drive",
                    _ when media == 4 => bus == 11 ? "SATA SSD" : "SSD",
                    _ when media == 3 => "Hard drive",
                    _ => "Drive",
                };
                var health = ToInt(d["HealthStatus"]) switch
                {
                    0 => "Healthy",
                    1 => "Warning",
                    2 => "Unhealthy",
                    _ => "Unknown",
                };
                long size = 0;
                try { size = Convert.ToInt64(d["Size"] ?? 0L); } catch { }
                disks.Add(new DiskInfo(index, d["FriendlyName"]?.ToString()?.Trim() ?? $"Disk {index}", kind, size, health));
            }
        }

        var map = new Dictionary<char, int>();
        using (var searcher = new ManagementObjectSearcher(ns, "SELECT DiskNumber, DriveLetter FROM MSFT_Partition"))
        {
            using var results = searcher.Get();
            foreach (var o in results)
            {
                using var p = (ManagementObject)o;
                var letter = p["DriveLetter"] switch
                {
                    char c => c,
                    ushort u => (char)u,
                    int i => (char)i,
                    string str when str.Length > 0 => str[0],
                    _ => '\0',
                };
                if (!char.IsLetter(letter)) continue;
                map[char.ToUpperInvariant(letter)] = ToInt(p["DiskNumber"]);
            }
        }

        _disks = disks.OrderBy(d => d.Index).ToList();
        _letterToDisk = map;
    }

    private IReadOnlyList<VolumeInfo> ReadVolumes()
    {
        var list = new List<VolumeInfo>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
            try
            {
                if (!drive.IsReady) continue;
                var letter = char.ToUpperInvariant(drive.Name[0]);
                list.Add(new VolumeInfo(
                    $"{letter}:",
                    string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "Local Disk" : drive.VolumeLabel,
                    drive.DriveFormat,
                    drive.TotalSize,
                    drive.TotalFreeSpace,
                    _letterToDisk.TryGetValue(letter, out var disk) ? disk : null));
            }
            catch
            {
                // Drive vanished or is locked (BitLocker): skip it.
            }
        }
        return list;
    }

    // ---------- Network ----------

    /// <summary>Adapter details that rarely change; the list is rebuilt every 15 seconds.</summary>
    private sealed record Adapter(NetworkInterface Nic, string Kind, bool IsVirtual, bool HasGateway, long Speed, string? Ipv4);

    private List<Adapter> _adapters = new();
    private DateTime _adaptersRead = DateTime.MinValue;

    private void RefreshAdapters()
    {
        var adapters = new List<Adapter>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

            string? ipv4 = null;
            bool hasGateway = false;
            try
            {
                var props = nic.GetIPProperties();
                ipv4 = props.UnicastAddresses
                    .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString();
                hasGateway = props.GatewayAddresses.Any(g =>
                    g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
            }
            catch { }

            var isVirtual = VirtualHints.Any(h =>
                nic.Description.Contains(h, StringComparison.OrdinalIgnoreCase) ||
                nic.Name.Contains(h, StringComparison.OrdinalIgnoreCase));

            var kind = nic.NetworkInterfaceType switch
            {
                NetworkInterfaceType.Wireless80211 => "Wi-Fi",
                NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
                    or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit => "Ethernet",
                _ => nic.NetworkInterfaceType.ToString(),
            };
            if (nic.Description.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)) kind = "Bluetooth";

            long speed = 0;
            try { speed = nic.Speed; } catch { }

            adapters.Add(new Adapter(nic, kind, isVirtual, hasGateway, speed, ipv4));
        }
        _adapters = adapters;
        _adaptersRead = DateTime.UtcNow;
    }

    private IReadOnlyList<NetAdapterInfo> ReadNetwork()
    {
        if (DateTime.UtcNow - _adaptersRead > TimeSpan.FromSeconds(15)) RefreshAdapters();

        var now = Stopwatch.GetTimestamp();
        var list = new List<NetAdapterInfo>(_adapters.Count);
        var seen = new HashSet<string>();

        foreach (var a in _adapters)
        {
            var nic = a.Nic;
            long rx, tx;
            try
            {
                var stats = nic.GetIPStatistics();   // live counters, cheap
                rx = stats.BytesReceived;
                tx = stats.BytesSent;
            }
            catch
            {
                _adaptersRead = DateTime.MinValue;   // adapter went away: rebuild next tick
                continue;
            }

            double down = 0, up = 0;
            if (_netLast.TryGetValue(nic.Id, out var last))
            {
                var seconds = Stopwatch.GetElapsedTime(last.Timestamp, now).TotalSeconds;
                if (seconds > 0.05)
                {
                    down = Math.Max(0, (rx - last.Rx) / seconds);
                    up = Math.Max(0, (tx - last.Tx) / seconds);
                }
            }
            _netLast[nic.Id] = (rx, tx, now);
            seen.Add(nic.Id);

            list.Add(new NetAdapterInfo(nic.Id, nic.Name, nic.Description, a.Kind,
                a.IsVirtual, a.HasGateway, a.Speed, a.Ipv4, down, up, rx, tx));
        }

        foreach (var stale in _netLast.Keys.Where(k => !seen.Contains(k)).ToList())
            _netLast.Remove(stale);

        // Primary = the physical adapter that reaches the internet (has a gateway), busiest first.
        var primary = list.Where(n => n.IsPrimary && !n.IsVirtual).OrderByDescending(n => n.TotalDown).FirstOrDefault()
                      ?? list.Where(n => n.IsPrimary).OrderByDescending(n => n.TotalDown).FirstOrDefault();

        return list
            .Select(n => n with { IsPrimary = ReferenceEquals(n, primary) })
            .OrderByDescending(n => n.IsPrimary)
            .ThenBy(n => n.IsVirtual)
            .ThenByDescending(n => n.TotalDown)
            .ToList();
    }

    private static int ToInt(object? value)
    {
        try { return value is null ? -1 : Convert.ToInt32(value); }
        catch { return -1; }
    }
}
