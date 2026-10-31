namespace Ember.Services;

/// <summary>
/// Per-process numbers from the "Process V2" performance counters (instance names are "name:pid"):
///  - private working set: the memory Task Manager's Memory column shows
///  - disk: IO read + write bytes per second
/// Returns empty maps if the counters are unavailable; callers fall back gracefully.
/// </summary>
internal sealed class ProcessCounters : IDisposable
{
    private IntPtr _query;
    private IntPtr[] _counters = Array.Empty<IntPtr>();
    private bool _initialized, _available;

    public (Dictionary<int, long> PrivateMemory, Dictionary<int, double> Disk) Read()
    {
        var memory = new Dictionary<int, long>();
        var disk = new Dictionary<int, double>();
        if (!_initialized)
        {
            _initialized = true;
            _available = Pdh.TryOpen(out _query, out _counters,
                @"\Process V2(*)\Working Set - Private",
                @"\Process V2(*)\IO Read Bytes/sec",
                @"\Process V2(*)\IO Write Bytes/sec");
        }
        if (!_available || !Pdh.Collect(_query)) return (memory, disk);

        foreach (var (pid, value) in Items(_counters[0])) memory[pid] = (long)value;
        foreach (var (pid, value) in Items(_counters[1])) disk[pid] = disk.GetValueOrDefault(pid) + value;
        foreach (var (pid, value) in Items(_counters[2])) disk[pid] = disk.GetValueOrDefault(pid) + value;
        return (memory, disk);
    }

    private static IEnumerable<(int Pid, double Value)> Items(IntPtr counter)
    {
        var items = Pdh.ReadArray(counter);
        if (items is null) yield break;
        foreach (var (instance, value) in items)
        {
            var colon = instance.LastIndexOf(':');
            if (colon < 0 || !int.TryParse(instance.AsSpan(colon + 1), out var pid) || pid == 0) continue;
            yield return (pid, value);
        }
    }

    public void Dispose() => Pdh.Close(ref _query);
}
