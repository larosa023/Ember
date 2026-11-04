using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using Ember.Models;
using Ember.Services;
using Ember.ViewModels;
using Ember.Views;

namespace Ember;

public partial class App : Application
{
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private SettingsService? _settings;
    private MonitorHost? _host;
    private MainWindowViewModel? _vm;
    private MainWindow? _window;
    private TrayIcon? _tray;
    private OverlayController? _overlay;
    private HotkeyService? _hotkeys;
    private NativeMenuItem? _overlayMenuItem;
    private WindowState _restoreState = WindowState.Normal;
    private bool _quitting;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <summary>The main window (null while Ember is only in the tray).</summary>
    public static Window? MainWindowInstance =>
        (Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    /// <summary>The main window's clipboard (null while Ember is only in the tray).</summary>
    public static Avalonia.Input.Platform.IClipboard? Clipboard =>
        (Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow?.Clipboard;

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            // Hiding the window to the tray must not end the app; we quit explicitly.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            _settings = SettingsService.Load();
            var themes = new ThemeService();
            themes.ApplyByName(_settings.Current.Theme);
            Format.UseFahrenheit = _settings.Current.UseFahrenheit;
            if (string.IsNullOrEmpty(_settings.Current.MemoryUnit))
                _settings.Current.MemoryUnit = _settings.Current.ProcessMemoryInMb ? "mb" : "auto";
            Format.MemoryUnit = _settings.Current.MemoryUnit;
            Format.NetUnit = _settings.Current.NetUnit;
            Format.DiskUnit = _settings.Current.DiskUnit;
            ColumnLayout.Instance.Attach(_settings);

            var history = new HistoryService(_settings);
            _host = new MonitorHost(new SensorService(), new ProcessService())
            {
                IntervalMs = _settings.Current.RefreshMs,
                PreferredGpu = _settings.Current.PreferredGpu,
                History = history,
            };

            var alerts = new AlertService(_settings, ShowWindow);
            _overlay = new OverlayController(_settings, ShowWindow);
            _hotkeys = new HotkeyService();
            _vm = new MainWindowViewModel(_host, _settings, themes, new UpdateService(), alerts, history, _overlay, _hotkeys);
            if (!Program.SmokeTest) _vm.RestoreLastPage();
            _host.Tick += UpdateTrayTooltip;
            _host.Tick += (snapshot, _) => alerts.Check(snapshot);
            _host.Tick += (snapshot, _) => _overlay.Update(snapshot);

            _hotkeys.Pressed += OnHotkey;
            if (!Program.SmokeTest) _hotkeys.Apply(_settings.Current.Hotkeys);

            CreateTray();

            if (!Program.StartMinimized)
            {
                _window = CreateWindow();
                desktop.MainWindow = _window;
            }

            desktop.Exit += (_, _) =>
            {
                if (_window is not null) SavePlacement(_window);
                _host.Dispose();
                history.Dispose();
                _hotkeys?.Dispose();
                _overlay?.Close();
                _tray?.Dispose();
            };

            SingleInstance.ListenForShow(() => Dispatcher.UIThread.Post(ShowWindow));

            // First run of a new version: refresh Windows' icon cache so the taskbar shows the current icon.
            if (!Program.SmokeTest && _settings.Current.LastRunVersion != AppInfo.Version)
            {
                _settings.Current.LastRunVersion = AppInfo.Version;
                _settings.Save();
                _ = Task.Run(Native.RefreshIconCache);
            }
            AutoStart.RefreshPath();

            if (!Program.SmokeTest) _overlay.RestoreOnStartup();

            _host.Start();

            if (Program.SmokeTest)
            {
                RunSmokeTest();
            }
            else
            {
                // Ember often lives in the tray for days, so check on launch and every 4 hours.
                _vm.Settings.UpdateDownloaded += version => alerts.ShowInfo(
                    "Ember update ready", $"Version {version} is downloaded. Click \"Update ready\" in Ember to restart and install it.");
                if (_settings.Current.AutoCheckUpdates) _ = _vm.Settings.CheckForUpdatesSilentlyAsync();
                var updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(4) };
                updateTimer.Tick += (_, _) =>
                {
                    if (_settings.Current.AutoCheckUpdates) _ = _vm.Settings.CheckForUpdatesSilentlyAsync();
                };
                updateTimer.Start();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Release-build check: open every page (so every view and binding is created), let a few
    /// sensor ticks run, then exit 0. Exceptions on the UI thread fail the run with exit code 4.
    /// </summary>
    private void RunSmokeTest()
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            CrashLog.Write(e.Exception);
            Environment.Exit(4);
        };

        var steps = new Queue<Action>(_vm!.SmokeTestSteps());
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        timer.Tick += (_, _) =>
        {
            if (steps.Count > 0)
            {
                steps.Dequeue()();
                return;
            }
            timer.Stop();
            _desktop?.Shutdown(0);
        };
        // Give the sensor loop a couple of seconds to start delivering data first.
        DispatcherTimer.RunOnce(timer.Start, TimeSpan.FromSeconds(3));
    }

    /// <summary>Exit Ember for real (tray menu, restart as admin, etc.).</summary>
    public void Quit()
    {
        if (_quitting) return;
        _quitting = true;
        _desktop?.Shutdown();
    }

    private MainWindow CreateWindow()
    {
        var window = new MainWindow { DataContext = _vm };
        RestorePlacement(window);

        window.Closing += (_, e) =>
        {
            SavePlacement(window);
            if (!_quitting && _settings?.Current.CloseToTray == true)
            {
                e.Cancel = true;
                window.Hide();
            }
        };

        window.Closed += (_, _) =>
        {
            _window = null;
            Quit();
        };

        window.PropertyChanged += (_, e) =>
        {
            if (e.Property != Window.WindowStateProperty) return;
            if (window.WindowState == WindowState.Minimized)
            {
                if (_settings?.Current.MinimizeToTray == true)
                {
                    SavePlacement(window);
                    window.Hide();
                }
            }
            else
            {
                _restoreState = window.WindowState;
            }
        };

        return window;
    }

    /// <summary>Put the window back where it was last time, if that spot is still on a screen.</summary>
    private void RestorePlacement(Window window)
    {
        var c = _settings?.Current;
        if (c is null) return;
        if (c.WindowWidth is double w && c.WindowHeight is double h && w >= window.MinWidth && h >= window.MinHeight)
        {
            window.Width = w;
            window.Height = h;
        }
        if (c.WindowX is double x && c.WindowY is double y)
        {
            var point = new PixelPoint((int)x, (int)y);
            window.Opened += (_, _) =>
            {
                if (window.Screens.All.Any(s => s.WorkingArea.Contains(point + new PixelPoint(40, 40))))
                {
                    window.Position = point;
                }
            };
            window.WindowStartupLocation = WindowStartupLocation.Manual;
        }
        if (c.WindowMaximized) window.WindowState = WindowState.Maximized;
    }

    private void SavePlacement(Window window)
    {
        var c = _settings?.Current;
        if (c is null || !window.IsVisible) return;
        c.WindowMaximized = window.WindowState == WindowState.Maximized;
        if (window.WindowState == WindowState.Normal)
        {
            c.WindowX = window.Position.X;
            c.WindowY = window.Position.Y;
            c.WindowWidth = window.Width;
            c.WindowHeight = window.Height;
        }
        _settings!.Save();
    }

    private void ShowWindow()
    {
        if (_quitting) return;
        if (_window is null)
        {
            _window = CreateWindow();
            if (_desktop is not null) _desktop.MainWindow = _window;
        }

        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = _restoreState;
        _window.Activate();
    }

    private void CreateTray()
    {
        var open = new NativeMenuItem("Open Ember");
        open.Click += (_, _) => ShowWindow();
        _overlayMenuItem = new NativeMenuItem("Show overlay") { ToggleType = NativeMenuItemToggleType.CheckBox };
        _overlayMenuItem.Click += (_, _) => _overlay?.Toggle();
        var quit = new NativeMenuItem("Quit Ember");
        quit.Click += (_, _) => Quit();

        var menu = new NativeMenu();
        menu.Items.Add(open);
        menu.Items.Add(_overlayMenuItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(quit);

        if (_overlay is not null)
        {
            _overlay.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(OverlayController.IsOpen) && _overlayMenuItem is not null)
                    _overlayMenuItem.IsChecked = _overlay.IsOpen;
            };
        }

        _tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Ember/Assets/ember.ico"))),
            ToolTipText = "Ember",
            Menu = menu,
            IsVisible = true,
        };
        _tray.Clicked += (_, _) => ShowWindow();

        TrayIcon.SetIcons(this, new TrayIcons { _tray });
    }

    private void OnHotkey(string action)
    {
        switch (action)
        {
            case "toggle-ember":
                if (_window is { IsVisible: true, IsActive: true }) _window.Hide();
                else ShowWindow();
                break;
            case "toggle-overlay":
                _overlay?.Toggle();
                break;
            case "lock-overlay":
                _overlay?.ToggleLock();
                break;
        }
    }

    private void UpdateTrayTooltip(HardwareSnapshot s, IReadOnlyList<ProcessInfo>? _)
    {
        if (_tray is null) return;
        // Windows caps tray tooltips at ~127 characters.
        var text = $"Ember\nCPU {Format.Percent(s.CpuLoad)} · {Format.Temp(s.CpuTemp)}\nGPU {Format.Percent(s.GpuLoad)} · {Format.Temp(s.GpuTemp)}";
        _tray.ToolTipText = text.Length > 127 ? text[..127] : text;
    }
}
