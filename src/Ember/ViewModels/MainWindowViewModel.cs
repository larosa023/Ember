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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDashboard), nameof(IsProcesses), nameof(IsSensors), nameof(IsFans), nameof(IsStartup), nameof(IsSettings))]
    private ViewModelBase _currentPage;

    public MainWindowViewModel(MonitorHost host, SettingsService settings, ThemeService themes, UpdateService updates)
    {
        _host = host;
        Settings = new SettingsViewModel(settings, themes, updates, host);
        Fans = new FansViewModel(settings, host);
        CpuDetail = new DetailViewModel(DetailKind.Cpu, () => CurrentPage = Dashboard);
        GpuDetail = new DetailViewModel(DetailKind.Gpu, () => CurrentPage = Dashboard);
        MemoryDetail = new DetailViewModel(DetailKind.Memory, () => CurrentPage = Dashboard);
        Dashboard.OpenRequested += kind => CurrentPage = kind switch
        {
            DetailKind.Gpu => GpuDetail,
            DetailKind.Memory => MemoryDetail,
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
    public DetailViewModel CpuDetail { get; }
    public DetailViewModel GpuDetail { get; }
    public DetailViewModel MemoryDetail { get; }

    public bool IsDashboard => CurrentPage == Dashboard || CurrentPage is DetailViewModel;
    public bool IsProcesses => CurrentPage == Processes;
    public bool IsSensors => CurrentPage == Sensors;
    public bool IsFans => CurrentPage == Fans;
    public bool IsStartup => CurrentPage == Startup;
    public bool IsSettings => CurrentPage == Settings;

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
        Fans.Update(snapshot);
        Sensors.Update(snapshot.Sensors);
        Settings.UpdateGpuList(snapshot.GpuNames);
        if (processes is not null) Processes.Update(processes);
    }

    [RelayCommand]
    private void Navigate(string page)
    {
        CurrentPage = page switch
        {
            "processes" => Processes,
            "sensors" => Sensors,
            "fans" => Fans,
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
