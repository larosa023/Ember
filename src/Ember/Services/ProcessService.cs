using System.Diagnostics;
using Avalonia.Media;
using Ember.Models;

namespace Ember.Services;

/// <summary>
/// Samples running processes: per-process CPU %, memory, exe path, friendly name, icon (cached),
/// and whether the process owns a visible window (so the UI can split Apps from background work).
/// </summary>
public sealed class ProcessService : IDisposable
{
    private static readonly string WindowsDir =
        Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";

    private readonly ProcessTable _table = new();
    private Dictionary<int, (long Cpu, long Io)> _last = new();
    private readonly Dictionary<int, (bool On, long Sample)> _efficiency = new();
    private long _lastTimestamp;
    private long _sampleCount;

    // Per-PID metadata, remembered with the process name so a reused PID gets looked up again.
    private readonly Dictionary<int, Meta> _meta = new();
    private readonly Dictionary<string, (IImage? Icon, string? Description)> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly GpuProcessCounter _gpu = new();
    private readonly NetworkMonitor _network = new();

    /// <summary>Set by the UI: only pay for these when their column is visible.</summary>
    public static volatile bool WantNetwork = true;
    public static volatile bool WantStatus = true;

    /// <summary>True when per-process network numbers are available (needs admin).</summary>
    public bool NetworkAvailable => _network.IsRunning;
    private IImage? _genericIcon;
    private bool _genericLoaded;

    private sealed record Meta(string Name, string? Path, IImage? Icon, string? Description, bool IsWindows);

    /// <summary>Forget the previous CPU sample (used when the Processes page is hidden).</summary>
    public void Reset()
    {
        _network.Stop();   // stop tracing while the Processes page is closed
        if (_lastTimestamp == 0) return;
        _last = new Dictionary<int, (long, long)>();
        _lastTimestamp = 0;
    }

    public IReadOnlyList<ProcessInfo> Sample()
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = _lastTimestamp == 0 ? 0 : Stopwatch.GetElapsedTime(_lastTimestamp, now).TotalSeconds;
        var cores = Environment.ProcessorCount;
        _sampleCount++;

        List<RawProcess> raw;
        try { raw = _table.Read(); }
        catch { raw = new List<RawProcess>(); }

        Dictionary<int, double> gpuByPid;
        try { gpuByPid = _gpu.Read(); }
        catch { gpuByPid = new Dictionary<int, double>(); }

        if (WantNetwork) _network.EnsureStarted(); else _network.Stop();
        var netBytes = _network.IsRunning ? _network.Drain() : null;

        HashSet<int> withWindows;
        try { withWindows = Native.GetPidsWithWindows(); }
        catch { withWindows = new HashSet<int>(); }

        var result = new List<ProcessInfo>(raw.Count);
        var counters = new Dictionary<int, (long Cpu, long Io)>(raw.Count);

        foreach (var p in raw)
        {
            counters[p.Pid] = (p.CpuTime, p.IoBytes);

            double cpu = 0, disk = 0;
            if (elapsed > 0 && _last.TryGetValue(p.Pid, out var prev))
            {
                // CPU time is in 100 ns units.
                cpu = Math.Max(0, p.CpuTime - prev.Cpu) / (elapsed * 1e7 * cores) * 100;
                disk = Math.Max(0, p.IoBytes - prev.Io) / elapsed;
            }

            var net = netBytes is null ? double.NaN
                : netBytes.TryGetValue(p.Pid, out var b) ? (b.Sent + b.Received) / Math.Max(elapsed, 0.25) : 0;

            var meta = GetMeta(p.Pid, p.Name);
            result.Add(new ProcessInfo(p.Pid, p.Name, Math.Clamp(cpu, 0, 100), p.PrivateWorkingSet,
                gpuByPid.GetValueOrDefault(p.Pid), disk, net, WantStatus && IsEfficient(p.Pid), p.Suspended,
                meta.Path, meta.Icon, meta.Description, withWindows.Contains(p.Pid), meta.IsWindows));
        }

        // Forget processes that exited.
        foreach (var pid in _meta.Keys.Where(k => !counters.ContainsKey(k)).ToList())
        {
            _meta.Remove(pid);
            _efficiency.Remove(pid);
        }

        _last = counters;
        _lastTimestamp = now;
        return result;
    }

    /// <summary>Efficiency mode needs a handle per process, so it's re-checked every 5th sample only.</summary>
    private bool IsEfficient(int pid)
    {
        if (_efficiency.TryGetValue(pid, out var cached) && _sampleCount - cached.Sample < 5)
            return cached.On;
        var on = Native.IsEfficiencyMode(pid) == true;
        _efficiency[pid] = (on, _sampleCount);
        return on;
    }

    public void Dispose()
    {
        _network.Dispose();
        _table.Dispose();
        _gpu.Dispose();
    }

    private Meta GetMeta(int pid, string name)
    {
        if (_meta.TryGetValue(pid, out var known) && known.Name == name) return known;

        string? path = null;
        try { path = Native.GetProcessPath(pid); } catch { }

        IImage? icon = null;
        string? description = null;
        if (path is not null)
        {
            if (!_byPath.TryGetValue(path, out var cached))
            {
                try { cached.Icon = Native.ExtractIcon(path); } catch { }
                try
                {
                    var d = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
                    cached.Description = string.IsNullOrEmpty(d) ? null : d;
                }
                catch { }
                _byPath[path] = cached;
            }
            (icon, description) = cached;
        }
        icon ??= GenericIcon();

        // No readable path (System, Registry, protected services) or lives in C:\Windows → Windows process.
        var isWindows = path is null || path.StartsWith(WindowsDir, StringComparison.OrdinalIgnoreCase);

        var meta = new Meta(name, path, icon, description, isWindows);
        _meta[pid] = meta;
        return meta;
    }

    private IImage? GenericIcon()
    {
        if (!_genericLoaded)
        {
            _genericLoaded = true;
            try { _genericIcon = Native.GenericAppIcon(); } catch { }
        }
        return _genericIcon;
    }
}
