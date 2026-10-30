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
    private ToastWindow? _toast;

    public AlertService(SettingsService settings, Action openEmber)
    {
        _settings = settings;
        _openEmber = openEmber;
    }

    public void Check(HardwareSnapshot s)
    {
        var cfg = _settings.Current;
        if (!cfg.AlertsEnabled) return;
        Evaluate(_cpu, s.CpuTemp, cfg.CpuAlertC, s.CpuName);
        Evaluate(_gpu, s.GpuTemp, cfg.GpuAlertC, s.GpuName);
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
