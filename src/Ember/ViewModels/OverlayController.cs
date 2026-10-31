using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ember.Models;
using Ember.Services;
using Ember.Views;

namespace Ember.ViewModels;

/// <summary>
/// Owns the mini overlay window and its settings. Shared by the sidebar button, tray menu,
/// hotkeys and Settings page.
/// </summary>
public partial class OverlayController : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly Action _openEmber;
    private readonly DispatcherTimer _saveTimer;
    private OverlayWindow? _window;
    private bool _loading = true;

    public OverlayController(SettingsService settings, Action openEmber)
    {
        _settings = settings;
        _openEmber = openEmber;
        var c = settings.Current;
        IsLocked = c.OverlayLocked;
        Opacity = Math.Clamp(c.OverlayOpacity, 0.3, 1);
        ShowCpu = c.OverlayShowCpu;
        ShowGpu = c.OverlayShowGpu;
        ShowRam = c.OverlayShowRam;
        ShowNet = c.OverlayShowNet;
        _loading = false;

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            _settings.Save();
        };
    }

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private bool _isLocked;
    [ObservableProperty] private double _opacity;
    [ObservableProperty] private bool _showCpu;
    [ObservableProperty] private bool _showGpu;
    [ObservableProperty] private bool _showRam;
    [ObservableProperty] private bool _showNet;

    [ObservableProperty] private string _cpuText = Format.Dash;
    [ObservableProperty] private string _gpuText = Format.Dash;
    [ObservableProperty] private string _ramText = Format.Dash;
    [ObservableProperty] private string _netText = Format.Dash;

    public string OpacityText => $"{Opacity * 100:0}%";

    /// <summary>Re-open the overlay at startup if it was open last time.</summary>
    public void RestoreOnStartup()
    {
        if (_settings.Current.OverlayVisible) Show();
    }

    [RelayCommand]
    public void Toggle()
    {
        if (IsOpen) Hide();
        else Show();
    }

    [RelayCommand]
    public void ToggleLock() => IsLocked = !IsLocked;

    [RelayCommand]
    private void OpenEmber() => _openEmber();

    private void Show()
    {
        if (_window is null)
        {
            _window = new OverlayWindow { DataContext = this };
            _window.Closed += (_, _) =>
            {
                _window = null;
                IsOpen = false;
            };
            _window.PositionChanged += (_, _) =>
            {
                if (_window is null) return;
                _settings.Current.OverlayX = _window.Position.X;
                _settings.Current.OverlayY = _window.Position.Y;
                QueueSave();
            };
            _window.Opened += (_, _) => Place(_window);
        }
        _window.Show();
        _window.ApplyExStyle(IsLocked);
        IsOpen = true;
    }

    private void Hide()
    {
        _window?.Close();
        _window = null;
        IsOpen = false;
    }

    /// <summary>Saved position if it's still on a screen, otherwise the top-right corner.</summary>
    private void Place(OverlayWindow w)
    {
        var screens = w.Screens.All;
        if (_settings.Current.OverlayX is double x && _settings.Current.OverlayY is double y)
        {
            var p = new PixelPoint((int)x, (int)y);
            if (screens.Any(s => s.Bounds.Contains(p)))
            {
                w.Position = p;
                return;
            }
        }
        var screen = w.Screens.Primary ?? screens.FirstOrDefault();
        if (screen is null) return;
        var area = screen.WorkingArea;
        var margin = (int)(24 * screen.Scaling);
        w.Position = new PixelPoint(area.Right - (int)(w.Bounds.Width * screen.Scaling) - margin, area.Y + margin);
    }

    public void Update(HardwareSnapshot s)
    {
        if (!IsOpen) return;
        CpuText = Join(Format.Percent(s.CpuLoad), s.CpuTemp is null ? null : Format.Temp(s.CpuTemp));
        GpuText = Join(Format.Percent(s.GpuLoad), s.GpuTemp is null ? null : Format.Temp(s.GpuTemp));
        RamText = s.RamUsedGb is float u ? $"{Format.Percent(s.RamLoad)} · {u:0.0} GB" : Format.Percent(s.RamLoad);
        var primary = s.Network.FirstOrDefault(n => n.IsPrimary);
        NetText = primary is null ? Format.Dash : $"↓ {Format.NetRate(primary.DownBps)}  ↑ {Format.NetRate(primary.UpBps)}";
    }

    private static string Join(string a, string? b) => b is null ? a : $"{a} · {b}";

    partial void OnIsOpenChanged(bool value)
    {
        _settings.Current.OverlayVisible = value;
        QueueSave();
    }

    partial void OnIsLockedChanged(bool value)
    {
        _window?.ApplyExStyle(value);
        if (_loading) return;
        _settings.Current.OverlayLocked = value;
        QueueSave();
    }

    partial void OnOpacityChanged(double value)
    {
        OnPropertyChanged(nameof(OpacityText));
        if (_loading) return;
        _settings.Current.OverlayOpacity = value;
        QueueSave();
    }

    partial void OnShowCpuChanged(bool value) { if (!_loading) { _settings.Current.OverlayShowCpu = value; QueueSave(); } }
    partial void OnShowGpuChanged(bool value) { if (!_loading) { _settings.Current.OverlayShowGpu = value; QueueSave(); } }
    partial void OnShowRamChanged(bool value) { if (!_loading) { _settings.Current.OverlayShowRam = value; QueueSave(); } }
    partial void OnShowNetChanged(bool value) { if (!_loading) { _settings.Current.OverlayShowNet = value; QueueSave(); } }

    private void QueueSave()
    {
        if (_saveTimer is null) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void Close() => Hide();
}
