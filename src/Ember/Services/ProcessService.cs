using System.Diagnostics;
using Avalonia.Media;
using Ember.Models;

namespace Ember.Services;

/// <summary>Samples running processes, computes per-process CPU %, and resolves exe paths and icons (cached).</summary>
public sealed class ProcessService
{
    private Dictionary<int, TimeSpan> _lastCpu = new();
    private long _lastTimestamp;

    // Path + icon per PID, remembered with the process name so a reused PID gets looked up again.
    private readonly Dictionary<int, (string Name, string? Path, IImage? Icon)> _meta = new();
    private readonly Dictionary<string, IImage?> _iconsByPath = new(StringComparer.OrdinalIgnoreCase);
    private IImage? _genericIcon;
    private bool _genericLoaded;

    /// <summary>Forget the previous CPU sample (used when the Processes page is hidden).</summary>
    public void Reset()
    {
        if (_lastTimestamp == 0) return;
        _lastCpu = new Dictionary<int, TimeSpan>();
        _lastTimestamp = 0;
    }

    public IReadOnlyList<ProcessInfo> Sample()
    {
        var now = Stopwatch.GetTimestamp();
        var elapsedMs = _lastTimestamp == 0 ? 0 : Stopwatch.GetElapsedTime(_lastTimestamp, now).TotalMilliseconds;
        var cores = Environment.ProcessorCount;

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
                try { memory = p.WorkingSet64; } catch { }

                double cpu = 0;
                try
                {
                    var total = p.TotalProcessorTime;
                    cpuTimes[pid] = total;
                    if (elapsedMs > 0 && _lastCpu.TryGetValue(pid, out var previous))
                        cpu = (total - previous).TotalMilliseconds / (elapsedMs * cores) * 100;
                }
                catch { /* protected process: no access without admin */ }

                var (path, icon) = Meta(pid, name);
                result.Add(new ProcessInfo(pid, name, Math.Clamp(cpu, 0, 100), memory, path, icon));
            }
        }

        foreach (var pid in _meta.Keys.Where(k => !alive.Contains(k)).ToList())
            _meta.Remove(pid);

        _lastCpu = cpuTimes;
        _lastTimestamp = now;
        return result;
    }

    private (string? Path, IImage? Icon) Meta(int pid, string name)
    {
        if (_meta.TryGetValue(pid, out var known) && known.Name == name)
            return (known.Path, known.Icon);

        string? path = null;
        try { path = Native.GetProcessPath(pid); } catch { }

        IImage? icon = null;
        if (path is not null && !_iconsByPath.TryGetValue(path, out icon))
        {
            try { icon = Native.ExtractIcon(path); } catch { icon = null; }
            _iconsByPath[path] = icon;
        }
        icon ??= GenericIcon();

        _meta[pid] = (name, path, icon);
        return (path, icon);
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
