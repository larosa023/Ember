using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Ember.Services;

/// <summary>
/// Backup GPU reader using Windows' own performance counters (the same numbers Task Manager shows).
/// Used when LibreHardwareMonitor can't see the GPU or gives no load reading. Works with any
/// GPU (NVIDIA, AMD, Intel) and without admin, but has no temperature or clock data.
/// </summary>
internal sealed class GpuFallback : IDisposable
{
    private const uint PdhFmtDouble = 0x00000200;
    private const uint PdhFmtNoCap100 = 0x00008000;
    private const uint PdhMoreData = 0x800007D2;

    private IntPtr _query, _engineCounter, _memoryCounter;
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
        if (!_initialized) Initialize();
        if (!_available) return (null, null);

        if (PdhCollectQueryData(_query) != 0) return (null, null);

        // Sum of all 3D-engine utilization (per process instances), like Task Manager's "3D" graph.
        var engine = ReadArray(_engineCounter);
        float? load = engine is null ? null : (float)Math.Clamp(engine.Sum(), 0, 100);

        // Largest adapter's dedicated memory in use, in bytes.
        var memory = ReadArray(_memoryCounter);
        float? usedMb = memory is null || memory.Count == 0 ? null : (float)(memory.Max() / (1024 * 1024));

        return (load, usedMb);
    }

    private void Initialize()
    {
        _initialized = true;
        try
        {
            if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0) return;
            if (PdhAddEnglishCounter(_query, @"\GPU Engine(*engtype_3D)\Utilization Percentage", IntPtr.Zero, out _engineCounter) != 0) return;
            PdhAddEnglishCounter(_query, @"\GPU Adapter Memory(*)\Dedicated Usage", IntPtr.Zero, out _memoryCounter);
            PdhCollectQueryData(_query); // rate counters need a first sample
            _available = true;
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    private static List<double>? ReadArray(IntPtr counter)
    {
        if (counter == IntPtr.Zero) return null;

        uint size = 0;
        var status = PdhGetFormattedCounterArray(counter, PdhFmtDouble | PdhFmtNoCap100, ref size, out _, IntPtr.Zero);
        if (status != PdhMoreData || size == 0) return null;

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            status = PdhGetFormattedCounterArray(counter, PdhFmtDouble | PdhFmtNoCap100, ref size, out var count, buffer);
            if (status != 0) return null;

            var values = new List<double>((int)count);
            int stride = Marshal.SizeOf<PdhFmtCounterValueItem>();
            for (int i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<PdhFmtCounterValueItem>(buffer + i * stride);
                if (item.CStatus == 0) values.Add(item.Value);
            }
            return values;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
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

    public void Dispose()
    {
        if (_query != IntPtr.Zero)
        {
            try { PdhCloseQuery(_query); } catch { }
            _query = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtCounterValueItem
    {
        public IntPtr Name;
        public uint CStatus;
        public double Value;  // union member used with PDH_FMT_DOUBLE
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhOpenQueryW")]
    private static extern uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW")]
    private static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhGetFormattedCounterArrayW")]
    private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr itemBuffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);
}
