using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ember.Models;
using Ember.Services;

namespace Ember.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly MonitorHost _host;
    private readonly SettingsService _settings;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDashboard), nameof(IsProcesses), nameof(IsSensors), nameof(IsHistory), nameof(IsFans), nameof(IsServices), nameof(IsSpecs), nameof(IsStartup), nameof(IsSettings))]
    private ViewModelBase _currentPage;

    public MainWindowViewModel(MonitorHost host, SettingsService settings, ThemeService themes, UpdateService updates,
        AlertService alerts, HistoryService history, OverlayController overlay, HotkeyService hotkeys)
    {
        Overlay = overlay;
        History = new HistoryViewModel(history);
        Processes.SetMemoryUnitRequested = key => Settings!.SetMemoryUnit(key);
        _host = host;
        _settings = settings;
        _isSidebarCollapsed = settings.Current.SidebarCollapsed;
        Settings = new SettingsViewModel(settings, themes, updates, host, alerts, overlay, hotkeys);
        Fans = new FansViewModel(settings, host);
        CpuDetail = new DetailViewModel(DetailKind.Cpu, () => CurrentPage = Dashboard);
        GpuDetail = new DetailViewModel(DetailKind.Gpu, () => CurrentPage = Dashboard);
        MemoryDetail = new DetailViewModel(DetailKind.Memory, () => CurrentPage = Dashboard);
        StorageDetail = new StorageViewModel(() => CurrentPage = Dashboard);
        NetworkDetail = new NetworkViewModel(() => CurrentPage = Dashboard);
        Dashboard.OpenRequested += page => CurrentPage = page switch
        {
            "gpu" => GpuDetail,
            "ram" => MemoryDetail,
            "storage" => StorageDetail,
            "network" => NetworkDetail,
            _ => CpuDetail,
        };
        _currentPage = Dashboard;
        _host.Tick += OnTick;
    }

    public DashboardViewModel Dashboard { get; } = new();
    public ProcessesViewModel Processes { get; } = new();
    public SensorsViewModel Sensors { get; } = new();
    public StartupViewModel Startup { get; } = new();
    public SettingsViewModel Settings { get; }
    public FansViewModel Fans { get; }
    public HistoryViewModel History { get; }
    public OverlayController Overlay { get; }
    public SpecsViewModel Specs { get; } = new();
    public ServicesViewModel Services { get; } = new();
    public DetailViewModel CpuDetail { get; }
    public DetailViewModel GpuDetail { get; }
    public DetailViewModel MemoryDetail { get; }
    public StorageViewModel StorageDetail { get; }
    public NetworkViewModel NetworkDetail { get; }

    public bool IsDashboard => CurrentPage == Dashboard || CurrentPage is DetailViewModel or StorageViewModel or NetworkViewModel;
    public bool IsProcesses => CurrentPage == Processes;
    public bool IsSensors => CurrentPage == Sensors;
    public bool IsFans => CurrentPage == Fans;
    public bool IsHistory => CurrentPage == History;
    public bool IsServices => CurrentPage == Services;
    public bool IsSpecs => CurrentPage == Specs;
    public bool IsStartup => CurrentPage == Startup;
    public bool IsSettings => CurrentPage == Settings;

    // ---------- Sidebar ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SidebarWidth))]
    private bool _isSidebarCollapsed;

    public double SidebarWidth => IsSidebarCollapsed ? 68 : 224;

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarCollapsed = !IsSidebarCollapsed;

    partial void OnIsSidebarCollapsedChanged(bool value)
    {
        _settings.Current.SidebarCollapsed = value;
        _settings.Save();
    }

    public bool IsElevated => Environment.IsPrivilegedProcess;
    public string VersionText => $"v{AppInfo.Version}";

    // ---------- Sensor driver (PawnIO) ----------

    private int _ticks;

    /// <summary>CPU temps are missing and the PawnIO driver isn't installed: offer to install it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAdminBanner))]
    private bool _showDriverBanner;

    [ObservableProperty] private bool _isInstallingDriver;
    [ObservableProperty] private string _driverStatus = "";

    public bool ShowAdminBanner => !IsElevated && !ShowDriverBanner;

    [RelayCommand]
    private async Task InstallDriver()
    {
        IsInstallingDriver = true;
        DriverStatus = "Installing… approve the Windows prompt.";
        var ok = await PawnIo.InstallAsync();
        IsInstallingDriver = false;

        if (ok)
        {
            DriverStatus = "Installed. Restarting Ember…";
            RestartAsAdmin();
        }
        else
        {
            DriverStatus = "Couldn't install automatically. The download page is open; run the installer, then restart Ember.";
        }
    }

    private void OnTick(HardwareSnapshot snapshot, IReadOnlyList<ProcessInfo>? processes)
    {
        // Give sensors a few seconds to come up before deciding the driver is missing.
        if (_ticks < 4 && ++_ticks == 4)
            ShowDriverBanner = snapshot.CpuTemp is null && !PawnIo.IsInstalled;
        else if (ShowDriverBanner && snapshot.CpuTemp is not null)
            ShowDriverBanner = false;

        Dashboard.Update(snapshot);
        CpuDetail.Update(snapshot);
        GpuDetail.Update(snapshot);
        MemoryDetail.Update(snapshot);
        StorageDetail.Update(snapshot);
        NetworkDetail.Update(snapshot);
        if (CurrentPage == History) History.RefreshIfChanged();
        Fans.Update(snapshot);
        Sensors.Update(snapshot.Sensors);
        Settings.UpdateGpuList(snapshot.GpuNames);
        Processes.UpdateTotals(snapshot);
        if (processes is not null)
        {
            Processes.SetNetworkAvailable(_host.ProcessNetworkAvailable);
            Processes.Update(processes);
        }
    }

    /// <summary>Visits every page once; used by the release build's smoke test.</summary>
    public IEnumerable<Action> SmokeTestSteps()
    {
        // Stay on Processes a few seconds so the per-app disk/network/status sampling runs.
        yield return () => Navigate("processes");
        for (int i = 0; i < 5; i++) yield return () => { };
        foreach (var page in new[] { "sensors", "history", "fans", "services", "system", "startup", "settings", "dashboard" })
            yield return () => Navigate(page);
        yield return () => CurrentPage = CpuDetail;
        yield return () => CurrentPage = GpuDetail;
        yield return () => CurrentPage = MemoryDetail;
        yield return () => CurrentPage = StorageDetail;
        yield return () => CurrentPage = NetworkDetail;
        yield return () => IsSidebarCollapsed = !IsSidebarCollapsed;
        yield return () => IsSidebarCollapsed = !IsSidebarCollapsed;
        yield return () => Settings.SendTestAlertCommand.Execute(null);
        yield return () => Overlay.Toggle();   // open the overlay window
        yield return () => Overlay.ToggleLock();
        yield return () => Overlay.ToggleLock();
        yield return () => Overlay.Toggle();   // and close it
        yield return () => Navigate("dashboard");
    }

    [RelayCommand]
    private void Navigate(string page)
    {
        CurrentPage = page switch
        {
            "processes" => Processes,
            "sensors" => Sensors,
            "fans" => Fans,
            "history" => History,
            "services" => Services,
            "system" => Specs,
            "startup" => Startup,
            "settings" => Settings,
            _ => Dashboard,
        };
    }

    partial void OnCurrentPageChanged(ViewModelBase value)
    {
        // Only pay for process sampling while the Processes page is open.
        _host.SampleProcesses = value == Processes;
        if (value == Startup && Startup.Items.Count == 0) Startup.Refresh();
        if (value == History) History.Refresh();
        if (value == Services && Services.Items.Count == 0) _ = Services.Refresh();
        if (value == Specs) _ = Specs.LoadAsync();
    }

    [RelayCommand]
    private void RestartAsAdmin()
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return;
        try
        {
            // "--restarted" tells the new copy to wait for this one to exit (single-instance lock).
            Process.Start(new ProcessStartInfo(exe, "--restarted") { UseShellExecute = true, Verb = "runas" });
            (Application.Current as App)?.Quit();
        }
        catch (Win32Exception)
        {
            // User cancelled the UAC prompt.
        }
    }
}
