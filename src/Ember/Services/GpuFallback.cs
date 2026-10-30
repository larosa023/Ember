using Microsoft.Win32;

namespace Ember.Services;

/// <summary>
/// Backup GPU reader using Windows' own performance counters (the same numbers Task Manager shows).
/// Used when LibreHardwareMonitor can't see the GPU or gives no load reading. Works with any
/// GPU (NVIDIA, AMD, Intel) and without admin, but has no temperature or clock data.
/// </summary>
internal sealed class GpuFallback : IDisposable
{
    private IntPtr _query;
    private IntPtr[] _counters = Array.Empty<IntPtr>();
    private bool _initialized, _available;
    private string? _name;
    private bool _nameRead;

    public string? Name
    {
        get
        {
            if (!_nameRead)
            {
                _nameRead = true;
                _name = ReadAdapterName();
            }
            return _name;
        }
    }

    public (float? Load, float? DedicatedUsedMb) Read()
    {
        if (!_initialized)
        {
            _initialized = true;
            _available = Pdh.TryOpen(out _query, out _counters,
                @"\GPU Engine(*engtype_3D)\Utilization Percentage",
                @"\GPU Adapter Memory(*)\Dedicated Usage");
        }
        if (!_available || !Pdh.Collect(_query)) return (null, null);

        // Sum of all 3D-engine utilization (per process instances), like Task Manager's "3D" graph.
        var engine = Pdh.ReadArray(_counters[0]);
        float? load = engine is null ? null : (float)Math.Clamp(engine.Sum(e => e.Value), 0, 100);

        // Largest adapter's dedicated memory in use, in bytes.
        var memory = Pdh.ReadArray(_counters[1]);
        float? usedMb = memory is null || memory.Count == 0 ? null : (float)(memory.Max(m => m.Value) / (1024 * 1024));

        return (load, usedMb);
    }

    /// <summary>Display adapter name from the registry, preferring a discrete GPU.</summary>
    private static string? ReadAdapterName()
    {
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls is null) return null;

            var names = new List<string>();
            foreach (var sub in cls.GetSubKeyNames())
            {
                if (!sub.All(char.IsDigit)) continue;
                try
                {
                    using var key = cls.OpenSubKey(sub);
                    if (key?.GetValue("DriverDesc") is not string desc) continue;
                    if (desc.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) ||
                        desc.Contains("Remote", StringComparison.OrdinalIgnoreCase) ||
                        desc.Contains("Virtual", StringComparison.OrdinalIgnoreCase)) continue;
                    names.Add(desc);
                }
                catch { }
            }

            string[] discrete = ["NVIDIA", "GeForce", "RTX", "Radeon RX", "AMD Radeon", "Arc"];
            return names.FirstOrDefault(n => discrete.Any(d => n.Contains(d, StringComparison.OrdinalIgnoreCase)))
                ?? names.FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    public void Dispose() => Pdh.Close(ref _query);
}
