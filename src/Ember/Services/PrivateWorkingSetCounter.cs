namespace Ember.Services;

/// <summary>
/// Per-process "private working set": the memory a process uses that isn't shared with others.
/// This is the number Task Manager's Memory column shows. Read from the "Process V2" performance
/// counters (instance names are "name:pid"). Returns an empty map if unavailable, and callers
/// fall back to the full working set.
/// </summary>
internal sealed class PrivateWorkingSetCounter : IDisposable
{
    private IntPtr _query;
    private IntPtr[] _counters = Array.Empty<IntPtr>();
    private bool _initialized, _available;

    public Dictionary<int, long> Read()
    {
        var result = new Dictionary<int, long>();
        if (!_initialized)
        {
            _initialized = true;
            _available = Pdh.TryOpen(out _query, out _counters, @"\Process V2(*)\Working Set - Private");
        }
        if (!_available || !Pdh.Collect(_query)) return result;

        var items = Pdh.ReadArray(_counters[0]);
        if (items is null) return result;

        foreach (var (instance, value) in items)
        {
            var colon = instance.LastIndexOf(':');
            if (colon < 0 || !int.TryParse(instance.AsSpan(colon + 1), out var pid) || pid == 0) continue;
            result[pid] = (long)value;
        }
        return result;
    }

    public void Dispose() => Pdh.Close(ref _query);
}
