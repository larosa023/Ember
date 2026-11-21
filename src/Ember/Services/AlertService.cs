using Ember.Models;
using Ember.Views;

namespace Ember.Services;

/// <summary>
/// Watches CPU and GPU temperatures and shows a toast when one stays above its threshold.
/// Rules: must stay hot for 10 seconds (ignores brief spikes), re-arms only after cooling
/// 5°C below the threshold, and won't repeat for the same part within 5 minutes.
/// Runs on the UI thread (called from the monitor tick).
/// </summary>
public sealed class AlertService
{
    private static readonly TimeSpan Sustain = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(5);
    private const double Rearm = 5;

    private readonly SettingsService _settings;
    private readonly Action _openEmber;
    private readonly Watch _cpu = new("CPU");
    private readonly Watch _gpu = new("GPU");
    private readonly Watch _ram = new("Memory");
    private readonly Dictionary<string, DateTime> _diskAlerted = new();   // drive letter → last alert
    private readonly HashSet<string> _diskArmed = new();
    private int _diskTick;
    private ToastWindow? _toast;

    public AlertService(SettingsService settings, Action openEmber)
    {
        _settings = settings;
        _openEmber = openEmber;
    }

    public void Check(HardwareSnapshot s)
    {
        var cfg = _settings.Current;
        if (cfg.AlertsEnabled)
        {
            Evaluate(_cpu, s.CpuTemp, cfg.CpuAlertC, s.CpuName);
            Evaluate(_gpu, s.GpuTemp, cfg.GpuAlertC, s.GpuName);
        }
        if (cfg.RamAlertEnabled) EvaluateRam(s, cfg.RamAlertPercent);
        if (cfg.DiskAlertEnabled && _diskTick++ % 30 == 0) EvaluateDisks(s, cfg.DiskAlertFreePercent);
    }

    private void EvaluateRam(HardwareSnapshot s, double threshold)
    {
        if (s.RamLoad is not float load) return;
        var now = DateTime.UtcNow;
        if (load < threshold - Rearm) _ram.Armed = true;
        if (load < threshold)
        {
            _ram.HotSince = null;
            return;
        }
        _ram.HotSince ??= now;
        if (!_ram.Armed || now - _ram.HotSince < Sustain || now - _ram.LastAlert < Cooldown) return;
        _ram.Armed = false;
        _ram.LastAlert = now;
        Show("Memory is almost full",
             $"{load:0}% of your RAM has been in use for over 10 seconds. Open Processes to see which apps are using it.",
             hot: true);
    }

    /// <summary>Checked every 30 ticks: a drive below its free-space limit alerts once, then re-arms after it recovers.</summary>
    private void EvaluateDisks(HardwareSnapshot s, double freePercent)
    {
        foreach (var v in s.Volumes)
        {
            if (v.Total <= 0) continue;
            var free = v.Free * 100.0 / v.Total;
            if (free > freePercent + 2)
            {
                _diskArmed.Add(v.Letter);
                continue;
            }
            if (free > freePercent) continue;
            var first = !_diskAlerted.ContainsKey(v.Letter);
            if (!first && !_diskArmed.Contains(v.Letter)) continue;
            _diskArmed.Remove(v.Letter);
            _diskAlerted[v.Letter] = DateTime.UtcNow;
            Show($"Drive {v.Letter} is nearly full",
                 $"Only {Format.Bytes(v.Free)} ({free:0.#}%) left. Windows and games slow down when a drive gets this full.",
                 hot: true);
            return;   // one toast at a time
        }
    }

    private void Evaluate(Watch w, float? reading, double threshold, string device)
    {
        if (reading is not float temp) return;
        var now = DateTime.UtcNow;

        if (temp < threshold - Rearm) w.Armed = true;
        if (temp < threshold)
        {
            w.HotSince = null;
            return;
        }

        w.HotSince ??= now;
        if (!w.Armed || now - w.HotSince < Sustain || now - w.LastAlert < Cooldown) return;

        w.Armed = false;
        w.LastAlert = now;
        Show($"{w.Label} is running hot",
             $"{device} has been at {Format.Temp(temp)} for over 10 seconds (alert at {Format.Temp((float)threshold)}).",
             hot: true);
    }

    /// <summary>A neutral notification (e.g. "update ready").</summary>
    public void ShowInfo(string title, string message) => Show(title, message, hot: false);

    public void ShowTest() =>
        Show("Test alert", "This is how Ember lets you know when your CPU or GPU gets too hot.", hot: false);

    private void Show(string title, string message, bool hot)
    {
        try
        {
            _toast?.Close();
            _toast = new ToastWindow(title, message, hot, _openEmber);
            _toast.Closed += (_, _) => _toast = null;
            _toast.Show();
        }
        catch
        {
            // A notification must never take the app down.
        }
    }

    private sealed class Watch(string label)
    {
        public string Label { get; } = label;
        public bool Armed { get; set; } = true;
        public DateTime? HotSince { get; set; }
        public DateTime LastAlert { get; set; } = DateTime.MinValue;
    }
}
