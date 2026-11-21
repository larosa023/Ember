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

        // Instance names look like: pid_1234_luid_0x0_0x1A2B_phys_0_eng_3_engtype_3D
        // Parsed straight from PDH's buffer: no strings are created for the thousands of engine instances.
        var perEngine = _perEngine;
        perEngine.Clear();
        if (!Pdh.ForEachPositive(_counters[0], ref _buffer, ref _capacity, (instance, value) =>
            {
                if (!TryParse(instance, out var pid, out var engine)) return;
                perEngine[(pid, engine)] = perEngine.GetValueOrDefault((pid, engine)) + value;
            }))
            return result;

        foreach (var ((pid, _), value) in perEngine)
            result[pid] = Math.Min(100, Math.Max(result.GetValueOrDefault(pid), value));

        return result;
    }

    private readonly Dictionary<(int Pid, int Engine), double> _perEngine = new();
    private IntPtr _buffer;
    private int _capacity;

    private static bool TryParse(ReadOnlySpan<char> instance, out int pid, out int engine)
    {
        pid = 0;
        engine = 0;
        if (!instance.StartsWith("pid_")) return false;

        var rest = instance[4..];
        var end = rest.IndexOf('_');
        if (end < 0 || !int.TryParse(rest[..end], out pid)) return false;

        var e = instance.LastIndexOf("engtype_");
        engine = e >= 0 ? string.GetHashCode(instance[(e + 8)..]) : 0;
        return true;
    }

    public void Dispose()
    {
        Pdh.Close(ref _query);
        if (_buffer != IntPtr.Zero)
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
        }
    }
}
