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

    private Dictionary<int, TimeSpan> _lastCpu = new();
    private long _lastTimestamp;

    // Per-PID metadata, remembered with the process name so a reused PID gets looked up again.
    private readonly Dictionary<int, Meta> _meta = new();
    private readonly Dictionary<string, (IImage? Icon, string? Description)> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly GpuProcessCounter _gpu = new();
    private readonly ProcessCounters _counters = new();
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
        _lastCpu = new Dictionary<int, TimeSpan>();
        _lastTimestamp = 0;
    }

    public IReadOnlyList<ProcessInfo> Sample()
    {
        var now = Stopwatch.GetTimestamp();
        var elapsedMs = _lastTimestamp == 0 ? 0 : Stopwatch.GetElapsedTime(_lastTimestamp, now).TotalMilliseconds;
        var cores = Environment.ProcessorCount;

        Dictionary<int, double> gpuByPid;
        Dictionary<int, long> privateByPid;
        Dictionary<int, double> diskByPid;
        try { (privateByPid, diskByPid) = _counters.Read(); }
        catch { (privateByPid, diskByPid) = (new Dictionary<int, long>(), new Dictionary<int, double>()); }

        if (WantNetwork) _network.EnsureStarted(); else _network.Stop();
        var netBytes = _network.IsRunning ? _network.Drain() : null;
        var seconds = elapsedMs > 0 ? elapsedMs / 1000 : 1;

        HashSet<int> suspended;
        try { suspended = WantStatus ? SuspendedProcesses.Find() : new HashSet<int>(); }
        catch { suspended = new HashSet<int>(); }

        try { gpuByPid = _gpu.Read(); }
        catch { gpuByPid = new Dictionary<int, double>(); }

        HashSet<int> withWindows;
        try { withWindows = Native.GetPidsWithWindows(); }
        catch { withWindows = new HashSet<int>(); }

        var processes = Process.GetProcesses();
        var result = new List<ProcessInfo>(processes.Length);
        var cpuTimes = new Dictionary<int, TimeSpan>(processes.Length);
        var alive = new HashSet<int>();

        foreach (var p in processes)
        {
            using (p)
            {
                int pid;
                string name;
                try
                {
                    pid = p.Id;
                    if (pid == 0) continue; // System Idle Process
                    name = p.ProcessName;
                }
                catch { continue; }

                alive.Add(pid);

                long memory = 0;
                // Task Manager's number (private working set); full working set if that's unavailable.
                if (!privateByPid.TryGetValue(pid, out memory))
                {
                    try { memory = p.WorkingSet64; } catch { }
                }

                double cpu = 0;
                try
                {
                    var total = p.TotalProcessorTime;
                    cpuTimes[pid] = total;
                    if (elapsedMs > 0 && _lastCpu.TryGetValue(pid, out var previous))
                        cpu = (total - previous).TotalMilliseconds / (elapsedMs * cores) * 100;
                }
                catch { /* protected process: no access without admin */ }

                var meta = GetMeta(pid, name);
                var net = netBytes is null ? double.NaN
                    : netBytes.TryGetValue(pid, out var b) ? (b.Sent + b.Received) / seconds : 0;
                var efficient = WantStatus && Native.IsEfficiencyMode(pid) == true;

                result.Add(new ProcessInfo(pid, name, Math.Clamp(cpu, 0, 100), memory, gpuByPid.GetValueOrDefault(pid),
                    diskByPid.GetValueOrDefault(pid), net, efficient, suspended.Contains(pid),
                    meta.Path, meta.Icon, meta.Description, withWindows.Contains(pid), meta.IsWindows));
            }
        }

        foreach (var pid in _meta.Keys.Where(k => !alive.Contains(k)).ToList())
            _meta.Remove(pid);

        _lastCpu = cpuTimes;
        _lastTimestamp = now;
        return result;
    }

    public void Dispose()
    {
        _network.Dispose();
        _counters.Dispose();
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
