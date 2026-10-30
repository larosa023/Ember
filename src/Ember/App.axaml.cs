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
            Format.ProcessMemoryInMb = _settings.Current.ProcessMemoryInMb;
            ColumnLayout.Instance.Attach(_settings);

            var history = new HistoryService(_settings);
            _host = new MonitorHost(new SensorService(), new ProcessService())
            {
                IntervalMs = _settings.Current.RefreshMs,
                PreferredGpu = _settings.Current.PreferredGpu,
                History = history,
            };

            var alerts = new AlertService(_settings, ShowWindow);
            _vm = new MainWindowViewModel(_host, _settings, themes, new UpdateService(), alerts, history);
            _host.Tick += UpdateTrayTooltip;
            _host.Tick += (snapshot, _) => alerts.Check(snapshot);

            CreateTray();

            if (!Program.StartMinimized)
            {
                _window = CreateWindow();
                desktop.MainWindow = _window;
            }

            desktop.Exit += (_, _) =>
            {
                _host.Dispose();
                history.Dispose();
                _tray?.Dispose();
            };

            SingleInstance.ListenForShow(() => Dispatcher.UIThread.Post(ShowWindow));
            AutoStart.RefreshPath();

            _host.Start();

            if (Program.SmokeTest)
                RunSmokeTest();
            else if (_settings.Current.AutoCheckUpdates)
                _ = _vm.Settings.CheckForUpdatesSilentlyAsync();
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

        window.Closing += (_, e) =>
        {
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
                if (_settings?.Current.MinimizeToTray == true) window.Hide();
            }
            else
            {
                _restoreState = window.WindowState;
            }
        };

        return window;
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
        var quit = new NativeMenuItem("Quit Ember");
        quit.Click += (_, _) => Quit();

        var menu = new NativeMenu();
        menu.Items.Add(open);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(quit);

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

    private void UpdateTrayTooltip(HardwareSnapshot s, IReadOnlyList<ProcessInfo>? _)
    {
        if (_tray is null) return;
        // Windows caps tray tooltips at ~127 characters.
        var text = $"Ember\nCPU {Format.Percent(s.CpuLoad)} · {Format.Temp(s.CpuTemp)}\nGPU {Format.Percent(s.GpuLoad)} · {Format.Temp(s.GpuTemp)}";
        _tray.ToolTipText = text.Length > 127 ? text[..127] : text;
    }
}
