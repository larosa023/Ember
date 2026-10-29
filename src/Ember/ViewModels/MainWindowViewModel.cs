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
    [NotifyPropertyChangedFor(nameof(IsDashboard), nameof(IsProcesses), nameof(IsSensors), nameof(IsStartup), nameof(IsSettings))]
    private ViewModelBase _currentPage;

    public MainWindowViewModel(MonitorHost host, SettingsService settings, ThemeService themes, UpdateService updates)
    {
        _host = host;
        Settings = new SettingsViewModel(settings, themes, updates, host);
        _currentPage = Dashboard;
        _host.Tick += OnTick;
    }

    public DashboardViewModel Dashboard { get; } = new();
    public ProcessesViewModel Processes { get; } = new();
    public SensorsViewModel Sensors { get; } = new();
    public StartupViewModel Startup { get; } = new();
    public SettingsViewModel Settings { get; }

    public bool IsDashboard => CurrentPage == Dashboard;
    public bool IsProcesses => CurrentPage == Processes;
    public bool IsSensors => CurrentPage == Sensors;
    public bool IsStartup => CurrentPage == Startup;
    public bool IsSettings => CurrentPage == Settings;

    public bool IsElevated => Environment.IsPrivilegedProcess;
    public string VersionText => $"v{AppInfo.Version}";

    private void OnTick(HardwareSnapshot snapshot, IReadOnlyList<ProcessInfo>? processes)
    {
        Dashboard.Update(snapshot);
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
