using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ember.Services;
using Velopack;

namespace Ember.ViewModels;

public sealed record RefreshOption(int Ms, string Label)
{
    public override string ToString() => Label;
}

public sealed record UnitOption(string Key, string Label)
{
    public override string ToString() => Label;
}

public sealed record RetentionOption(int Days, string Label)
{
    public override string ToString() => Label;
}

public partial class SettingsViewModel : ViewModelBase
{
    private const string AutoGpu = "Automatic (discrete first)";

    private readonly SettingsService _settings;
    private readonly ThemeService _themes;
    private readonly UpdateService _updates;
    private readonly MonitorHost _host;
    private UpdateInfo? _pending;
    private bool _init = true;
    private bool _taskExists;

    private readonly AlertService _alerts;
    private readonly HotkeyService _hotkeys;

    public SettingsViewModel(SettingsService settings, ThemeService themes, UpdateService updates, MonitorHost host,
        AlertService alerts, OverlayController overlay, HotkeyService hotkeys)
    {
        _alerts = alerts;
        Overlay = overlay;
        _hotkeys = hotkeys;
        foreach (var (action, label) in HotkeyActions)
            Hotkeys.Add(new HotkeyRow(action, label, settings.Current.Hotkeys.GetValueOrDefault(action) ?? "", StartCapture));
        hotkeys.Applied += failed =>
        {
            foreach (var row in Hotkeys) row.InUse = failed.Contains(row.Action);
        };
        _settings = settings;
        _alertsEnabled = settings.Current.AlertsEnabled;
        _cpuAlert = settings.Current.CpuAlertC;
        _gpuAlert = settings.Current.GpuAlertC;
        _historyToDisk = settings.Current.HistoryToDisk;
        _selectedTemperatureUnit = TemperatureUnits[settings.Current.UseFahrenheit ? 1 : 0];
        _selectedMemoryUnit = Pick(MemoryUnits, settings.Current.MemoryUnit);
        _selectedNetUnit = Pick(NetUnits, settings.Current.NetUnit);
        _selectedDiskUnit = Pick(DiskUnits, settings.Current.DiskUnit);
        _selectedRetention = RetentionOptions.FirstOrDefault(o => o.Days == settings.Current.HistoryDays) ?? RetentionOptions[1];
        _themes = themes;
        _updates = updates;
        _host = host;

        FillThemeNames();
        var current = settings.Current;
        SelectedTheme = ThemeNames.FirstOrDefault(n => string.Equals(n, current.Theme, StringComparison.OrdinalIgnoreCase)) ?? "Ember";
        SelectedRefresh = RefreshOptions.FirstOrDefault(o => o.Ms == current.RefreshMs) ?? RefreshOptions[1];
        UseFahrenheit = current.UseFahrenheit;
        AutoCheckUpdates = current.AutoCheckUpdates;
        MinimizeToTray = current.MinimizeToTray;
        CloseToTray = current.CloseToTray;

        GpuOptions.Add(AutoGpu);
        if (!string.IsNullOrEmpty(current.PreferredGpu)) GpuOptions.Add(current.PreferredGpu);
        SelectedGpu = current.PreferredGpu ?? AutoGpu;

        _init = false;
        _ = LoadStartupStateAsync();
    }

    public bool IsElevated => Environment.IsPrivilegedProcess;

    // ---------- Appearance ----------

    public ObservableCollection<string> ThemeNames { get; } = new();
    [ObservableProperty] private string? _selectedTheme;

    partial void OnSelectedThemeChanged(string? value)
    {
        if (_init || string.IsNullOrEmpty(value)) return;
        _themes.ApplyByName(value);
        _settings.Current.Theme = value;
        _settings.Save();
    }

    private void FillThemeNames()
    {
        ThemeNames.Clear();
        foreach (var t in _themes.All) ThemeNames.Add(t.Name);
    }

    [RelayCommand]
    private void ReloadThemes()
    {
        var keep = SelectedTheme;
        _init = true;
        _themes.Reload();
        FillThemeNames();
        SelectedTheme = ThemeNames.FirstOrDefault(n => n == keep) ?? "Ember";
        _init = false;
        _themes.ApplyByName(SelectedTheme);
    }

    [RelayCommand]
    private void OpenThemesFolder()
    {
        try
        {
            Directory.CreateDirectory(SettingsService.ThemesDir);
            Process.Start(new ProcessStartInfo(SettingsService.ThemesDir) { UseShellExecute = true });
        }
        catch { }
    }

    // ---------- Monitoring ----------

    public IReadOnlyList<RefreshOption> RefreshOptions { get; } =
    [
        new(500, "Every 0.5 seconds"),
        new(1000, "Every second"),
        new(2000, "Every 2 seconds"),
        new(5000, "Every 5 seconds (lightest)"),
    ];

    [ObservableProperty] private RefreshOption? _selectedRefresh;
    [ObservableProperty] private bool _useFahrenheit;

    public ObservableCollection<string> GpuOptions { get; } = new();
    [ObservableProperty] private string? _selectedGpu;

    partial void OnSelectedRefreshChanged(RefreshOption? value)
    {
        if (_init || value is null) return;
        _host.IntervalMs = value.Ms;
        _settings.Current.RefreshMs = value.Ms;
        _settings.Save();
    }

    partial void OnUseFahrenheitChanged(bool value)
    {
        if (_init) return;
        Format.UseFahrenheit = value;
        _settings.Current.UseFahrenheit = value;
        _settings.Save();
        OnPropertyChanged(nameof(CpuAlertText));
        OnPropertyChanged(nameof(GpuAlertText));
    }

    // ---------- Overlay ----------

    public OverlayController Overlay { get; }

    // ---------- Shortcuts ----------

    private static readonly (string Action, string Label)[] HotkeyActions =
    [
        ("toggle-ember", "Show or hide Ember"),
        ("toggle-overlay", "Show or hide the overlay"),
        ("lock-overlay", "Lock or unlock the overlay"),
    ];

    public System.Collections.ObjectModel.ObservableCollection<HotkeyRow> Hotkeys { get; } = new();

    /// <summary>The row waiting for a key press, or null.</summary>
    [ObservableProperty] private HotkeyRow? _capturing;

    private void StartCapture(HotkeyRow row)
    {
        if (Capturing is not null) Capturing.IsCapturing = false;
        Capturing = row;
        row.IsCapturing = true;
        row.Message = "";
    }

    /// <summary>Called by the Settings page for each key press while capturing. Returns true if handled.</summary>
    public bool CaptureKey(Avalonia.Input.Key key, Avalonia.Input.KeyModifiers mods)
    {
        if (Capturing is not { } row) return false;

        // Modifier keys on their own: keep waiting for the real key.
        if (key is Avalonia.Input.Key.LeftCtrl or Avalonia.Input.Key.RightCtrl or Avalonia.Input.Key.LeftAlt
            or Avalonia.Input.Key.RightAlt or Avalonia.Input.Key.LeftShift or Avalonia.Input.Key.RightShift
            or Avalonia.Input.Key.LWin or Avalonia.Input.Key.RWin)
            return true;

        if (key == Avalonia.Input.Key.Escape && mods == Avalonia.Input.KeyModifiers.None)
        {
            EndCapture(row);
            return true;
        }

        if (key is Avalonia.Input.Key.Back or Avalonia.Input.Key.Delete && mods == Avalonia.Input.KeyModifiers.None)
        {
            EndCapture(row);
            SaveHotkey(row, "");
            return true;
        }

        var text = Hotkey.FromKeyEvent(key, mods);
        if (text is null)
        {
            row.Message = "That key can't be used. Try a letter, number or F-key.";
            return true;
        }

        var isFunctionKey = key is >= Avalonia.Input.Key.F1 and <= Avalonia.Input.Key.F24;
        if (mods == Avalonia.Input.KeyModifiers.None && !isFunctionKey)
        {
            row.Message = "Add Ctrl, Alt, Shift or Win so it doesn't block normal typing.";
            return true;
        }

        var clash = Hotkeys.FirstOrDefault(r => r != row && string.Equals(r.Combo, text, StringComparison.OrdinalIgnoreCase));
        if (clash is not null)
        {
            row.Message = $"Already used for \"{clash.Label}\".";
            return true;
        }

        EndCapture(row);
        SaveHotkey(row, text);
        return true;
    }

    private void EndCapture(HotkeyRow row)
    {
        row.IsCapturing = false;
        Capturing = null;
    }

    private void SaveHotkey(HotkeyRow row, string combo)
    {
        row.Combo = combo;
        row.Message = "";
        _settings.Current.Hotkeys[row.Action] = combo;
        _settings.Save();
        _hotkeys.Apply(_settings.Current.Hotkeys);
    }

    [RelayCommand]
    private void ResetHotkeys()
    {
        var defaults = new Models.AppSettings().Hotkeys;
        foreach (var row in Hotkeys)
        {
            row.Combo = defaults.GetValueOrDefault(row.Action) ?? "";
            row.Message = "";
            _settings.Current.Hotkeys[row.Action] = row.Combo;
        }
        _settings.Save();
        _hotkeys.Apply(_settings.Current.Hotkeys);
    }

    // ---------- Units ----------

    public IReadOnlyList<UnitOption> TemperatureUnits { get; } =
    [
        new("c", "Celsius (°C)"),
        new("f", "Fahrenheit (°F)"),
    ];

    public IReadOnlyList<UnitOption> MemoryUnits { get; } =
    [
        new("auto", "Automatic (MB or GB)"),
        new("mb", "Always MB, like Task Manager"),
        new("gb", "Always GB"),
    ];

    public IReadOnlyList<UnitOption> NetUnits { get; } =
    [
        new("bits", "Automatic bits (Kbps, Mbps, Gbps)"),
        new("mbps", "Always Mbps, like Task Manager"),
        new("bytes", "Automatic bytes (KB/s, MB/s)"),
        new("mbs", "Always MB/s"),
    ];

    public IReadOnlyList<UnitOption> DiskUnits { get; } =
    [
        new("auto", "Automatic (KB/s, MB/s, GB/s)"),
        new("mbs", "Always MB/s, like Task Manager"),
    ];

    [ObservableProperty] private UnitOption _selectedTemperatureUnit;
    [ObservableProperty] private UnitOption _selectedMemoryUnit;
    [ObservableProperty] private UnitOption _selectedNetUnit;
    [ObservableProperty] private UnitOption _selectedDiskUnit;

    /// <summary>Lets other pages (e.g. the Memory column header) change the memory unit.</summary>
    public void SetMemoryUnit(string key) => SelectedMemoryUnit = Pick(MemoryUnits, key);

    private static UnitOption Pick(IReadOnlyList<UnitOption> options, string? key) =>
        options.FirstOrDefault(o => o.Key == key) ?? options[0];

    partial void OnSelectedTemperatureUnitChanged(UnitOption value)
    {
        if (value is not null) UseFahrenheit = value.Key == "f";
    }

    partial void OnSelectedMemoryUnitChanged(UnitOption value)
    {
        if (value is null) return;
        Format.MemoryUnit = value.Key;
        _settings.Current.MemoryUnit = value.Key;
        _settings.Save();
    }

    partial void OnSelectedNetUnitChanged(UnitOption value)
    {
        if (value is null) return;
        Format.NetUnit = value.Key;
        _settings.Current.NetUnit = value.Key;
        _settings.Save();
    }

    partial void OnSelectedDiskUnitChanged(UnitOption value)
    {
        if (value is null) return;
        Format.DiskUnit = value.Key;
        _settings.Current.DiskUnit = value.Key;
        _settings.Save();
    }

    // ---------- Alerts ----------

    [ObservableProperty] private bool _alertsEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CpuAlertText))]
    private double _cpuAlert;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GpuAlertText))]
    private double _gpuAlert;

    public string CpuAlertText => Format.Temp((float)CpuAlert);
    public string GpuAlertText => Format.Temp((float)GpuAlert);

    partial void OnAlertsEnabledChanged(bool value)
    {
        _settings.Current.AlertsEnabled = value;
        _settings.Save();
    }

    partial void OnCpuAlertChanged(double value)
    {
        _settings.Current.CpuAlertC = Math.Round(value);
        _settings.Save();
    }

    partial void OnGpuAlertChanged(double value)
    {
        _settings.Current.GpuAlertC = Math.Round(value);
        _settings.Save();
    }

    [RelayCommand]
    private void SendTestAlert() => _alerts.ShowTest();

    // ---------- History ----------

    public IReadOnlyList<RetentionOption> RetentionOptions { get; } =
    [
        new(1, "1 day"),
        new(7, "7 days"),
        new(30, "30 days"),
    ];

    [ObservableProperty] private bool _historyToDisk;
    [ObservableProperty] private RetentionOption _selectedRetention;

    partial void OnHistoryToDiskChanged(bool value)
    {
        _settings.Current.HistoryToDisk = value;
        _settings.Save();
    }

    partial void OnSelectedRetentionChanged(RetentionOption value)
    {
        if (value is null) return;
        _settings.Current.HistoryDays = value.Days;
        _settings.Save();
    }

    partial void OnSelectedGpuChanged(string? value)
    {
        if (_init || string.IsNullOrEmpty(value)) return;
        var preferred = value == AutoGpu ? null : value;
        _host.PreferredGpu = preferred;
        _settings.Current.PreferredGpu = preferred;
        _settings.Save();
    }

    /// <summary>Called every tick with the GPUs found; only rebuilds the list when it changes.</summary>
    public void UpdateGpuList(IReadOnlyList<string> names)
    {
        var current = GpuOptions.Skip(1).ToList();
        if (current.SequenceEqual(names)) return;

        var keep = SelectedGpu;
        _init = true;
        GpuOptions.Clear();
        GpuOptions.Add(AutoGpu);
        foreach (var n in names) GpuOptions.Add(n);
        SelectedGpu = keep is not null && GpuOptions.Contains(keep) ? keep : AutoGpu;
        _init = false;
    }

    // ---------- Startup & tray ----------

    [ObservableProperty] private bool _launchAtStartup;
    [ObservableProperty] private bool _startupAsAdmin;
    [ObservableProperty] private bool _minimizeToTray;
    [ObservableProperty] private bool _closeToTray;
    [ObservableProperty] private string _startupStatus = "";

    private async Task LoadStartupStateAsync()
    {
        // schtasks takes a moment; don't block the UI for it.
        var (runKey, task) = await Task.Run(() => (AutoStart.RunKeyEnabled, AutoStart.TaskExists));
        _taskExists = task;
        _init = true;
        LaunchAtStartup = runKey || task;
        StartupAsAdmin = task;
        _init = false;
    }

    partial void OnLaunchAtStartupChanged(bool value) { if (!_init) ApplyStartup(); }
    partial void OnStartupAsAdminChanged(bool value) { if (!_init) ApplyStartup(); }

    partial void OnMinimizeToTrayChanged(bool value)
    {
        if (_init) return;
        _settings.Current.MinimizeToTray = value;
        _settings.Save();
    }

    partial void OnCloseToTrayChanged(bool value)
    {
        if (_init) return;
        _settings.Current.CloseToTray = value;
        _settings.Save();
    }

    private void ApplyStartup()
    {
        try
        {
            if (!LaunchAtStartup)
            {
                AutoStart.SetRunKey(false);
                if (_taskExists)
                {
                    if (!IsElevated)
                    {
                        StartupStatus = "Ember's admin startup task can only be removed while running as admin. Use \"Restart as admin\", then turn this off.";
                        Revert(() => LaunchAtStartup = true);
                        return;
                    }
                    if (AutoStart.SetTask(false)) _taskExists = false;
                }
                StartupStatus = "Ember won't start with Windows.";
                return;
            }

            if (StartupAsAdmin)
            {
                if (!IsElevated)
                {
                    StartupStatus = "To set up admin startup, use \"Restart as admin\" once, then turn this on.";
                    Revert(() => StartupAsAdmin = false);
                    return;
                }

                if (!AutoStart.SetTask(true))
                {
                    StartupStatus = "Couldn't create the startup task.";
                    Revert(() => StartupAsAdmin = false);
                    return;
                }

                _taskExists = true;
                AutoStart.SetRunKey(false);
                StartupStatus = "Ember will start in the tray at sign-in with admin rights, so CPU temps work with no UAC prompt.";
            }
            else
            {
                AutoStart.SetRunKey(true);
                if (_taskExists && IsElevated && AutoStart.SetTask(false)) _taskExists = false;
                StartupStatus = "Ember will start in the tray when you sign in.";
            }
        }
        catch (Exception ex)
        {
            StartupStatus = $"Couldn't change startup: {ex.Message}";
        }
    }

    /// <summary>Flip a toggle back without re-running ApplyStartup.</summary>
    private void Revert(Action set) => Dispatcher.UIThread.Post(() =>
    {
        _init = true;
        set();
        _init = false;
    });

    // ---------- Updates ----------

    [ObservableProperty] private bool _autoCheckUpdates;
    [ObservableProperty] private string _updateStatus = "";
    [ObservableProperty] private double _updateProgress;
    [ObservableProperty] private bool _isDownloading;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallUpdateCommand))]
    private bool _updateReady;

    public string VersionText => $"Ember {AppInfo.Version}";

    partial void OnAutoCheckUpdatesChanged(bool value)
    {
        if (_init) return;
        _settings.Current.AutoCheckUpdates = value;
        _settings.Save();
    }

    [RelayCommand]
    private Task CheckForUpdates() => CheckAsync(userInitiated: true);

    public Task CheckForUpdatesSilentlyAsync() => CheckAsync(userInitiated: false);

    private async Task CheckAsync(bool userInitiated)
    {
        if (IsDownloading || UpdateReady) return;

        if (!_updates.IsInstalled)
        {
            if (userInitiated)
                UpdateStatus = "Updates work in installed builds (Ember-win-Setup.exe from GitHub Releases), not dev builds.";
            return;
        }

        try
        {
            if (userInitiated) UpdateStatus = "Checking for updates…";
            var info = await _updates.CheckAsync();
            if (info is null)
            {
                if (userInitiated) UpdateStatus = "You're on the latest version.";
                return;
            }

            var version = info.TargetFullRelease.Version.ToString();
            UpdateStatus = $"Downloading Ember {version}…";
            IsDownloading = true;
            await _updates.DownloadAsync(info, p => Dispatcher.UIThread.Post(() => UpdateProgress = p));

            _pending = info;
            IsDownloading = false;
            UpdateReady = true;
            UpdateStatus = $"Ember {version} is ready. Restart to install.";
        }
        catch (Exception ex)
        {
            IsDownloading = false;
            if (userInitiated) UpdateStatus = $"Update check failed: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(UpdateReady))]
    private void InstallUpdate()
    {
        if (_pending is not null) _updates.ApplyAndRestart(_pending);
    }
}

public partial class HotkeyRow : ObservableObject
{
    private readonly System.Action<HotkeyRow> _onStart;

    public HotkeyRow(string action, string label, string combo, System.Action<HotkeyRow> onStart)
    {
        Action = action;
        Label = label;
        _combo = combo;
        _onStart = onStart;
    }

    [RelayCommand]
    private void Start() => _onStart(this);

    public string Action { get; }
    public string Label { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Display))]
    private string _combo;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Display))]
    private bool _isCapturing;

    [ObservableProperty] private bool _inUse;
    [ObservableProperty] private string _message = "";

    public string Display => IsCapturing ? "Press keys… (Esc cancels)"
        : string.IsNullOrEmpty(Combo) ? "Not set" : Combo.Replace("+", " + ");
}
