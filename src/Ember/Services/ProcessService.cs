using System.Diagnostics;
using Avalonia.Media;
using Ember.Models;

namespace Ember.Services;

/// <summary>
/// Samples running processes: per-process CPU %, memory, exe path, friendly name, icon (cached),
/// and whether the process owns a visible window (so the UI can split Apps from background work).
/// </summary>
public sealed class ProcessService
{
    private static readonly string WindowsDir =
        Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";

    private Dictionary<int, TimeSpan> _lastCpu = new();
    private long _lastTimestamp;

    // Per-PID metadata, remembered with the process name so a reused PID gets looked up again.
    private readonly Dictionary<int, Meta> _meta = new();
    private readonly Dictionary<string, (IImage? Icon, string? Description)> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly GpuProcessCounter _gpu = new();
    private IImage? _genericIcon;
    private bool _genericLoaded;

    private sealed record Meta(string Name, string? Path, IImage? Icon, string? Description, bool IsWindows);

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

        Dictionary<int, double> gpuByPid;
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

                var meta = GetMeta(pid, name);
                result.Add(new ProcessInfo(pid, name, Math.Clamp(cpu, 0, 100), memory, gpuByPid.GetValueOrDefault(pid),
                    meta.Path, meta.Icon, meta.Description, withWindows.Contains(pid), meta.IsWindows));
            }
        }

        foreach (var pid in _meta.Keys.Where(k => !alive.Contains(k)).ToList())
            _meta.Remove(pid);

        _lastCpu = cpuTimes;
        _lastTimestamp = now;
        return result;
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
