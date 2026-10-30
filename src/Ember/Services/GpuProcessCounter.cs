namespace Ember.Services;

/// <summary>
/// Per-process GPU usage from the "GPU Engine" performance counters, computed like Task Manager:
/// utilization is summed per engine type (3D, Copy, Video Decode, ...) and the busiest engine type wins.
/// Works for NVIDIA, AMD and Intel without admin.
/// </summary>
internal sealed class GpuProcessCounter : IDisposable
{
    private IntPtr _query;
    private IntPtr[] _counters = Array.Empty<IntPtr>();
    private bool _initialized, _available;

    public Dictionary<int, double> Read()
    {
        var result = new Dictionary<int, double>();
        if (!_initialized)
        {
            _initialized = true;
            _available = Pdh.TryOpen(out _query, out _counters, @"\GPU Engine(*)\Utilization Percentage");
        }
        if (!_available || !Pdh.Collect(_query)) return result;

        var items = Pdh.ReadArray(_counters[0]);
        if (items is null) return result;

        // Instance names look like: pid_1234_luid_0x0_0x1A2B_phys_0_eng_3_engtype_3D
        var perEngine = new Dictionary<(int Pid, string Engine), double>();
        foreach (var (instance, value) in items)
        {
            if (value <= 0 || !TryParse(instance, out var pid, out var engine)) continue;
            perEngine[(pid, engine)] = perEngine.GetValueOrDefault((pid, engine)) + value;
        }

        foreach (var ((pid, _), value) in perEngine)
            result[pid] = Math.Min(100, Math.Max(result.GetValueOrDefault(pid), value));

        return result;
    }

    private static bool TryParse(string instance, out int pid, out string engine)
    {
        pid = 0;
        engine = "";
        if (!instance.StartsWith("pid_", StringComparison.Ordinal)) return false;

        var end = instance.IndexOf('_', 4);
        if (end < 0 || !int.TryParse(instance.AsSpan(4, end - 4), out pid)) return false;

        var e = instance.LastIndexOf("engtype_", StringComparison.Ordinal);
        engine = e >= 0 ? instance[(e + 8)..] : "";
        return true;
    }

    public void Dispose() => Pdh.Close(ref _query);
}
