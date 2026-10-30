using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ember.Services;

namespace Ember.ViewModels;

public sealed record ServiceFilter(string Key, string Label)
{
    public override string ToString() => Label;
}

public partial class ServicesViewModel : ViewModelBase
{
    private List<ServiceRow> _all = new();

    public ObservableCollection<ServiceRow> Items { get; } = new();

    public IReadOnlyList<ServiceFilter> Filters { get; } =
    [
        new("all", "All services"),
        new("running", "Running"),
        new("stopped", "Stopped"),
        new("thirdparty", "Not from Microsoft"),
    ];

    public ServicesViewModel() => _filter = Filters[0];

    public bool IsElevated => Environment.IsPrivilegedProcess;

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private ServiceFilter _filter;
    [ObservableProperty] private string _summary = "Loading services…";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand), nameof(RestartCommand))]
    private ServiceRow? _selected;

    partial void OnSearchChanged(string value) => ApplyFilter();
    partial void OnFilterChanged(ServiceFilter value) => ApplyFilter();

    [RelayCommand]
    public async Task Refresh()
    {
        IsBusy = true;
        try
        {
            var keep = Selected?.Name;
            var list = await Task.Run(WindowsServices.Load);
            _all = list.Select(s => new ServiceRow(s)).ToList();
            ApplyFilter();
            if (keep is not null) Selected = Items.FirstOrDefault(i => i.Name == keep);

            var running = list.Count(s => s.State == "Running");
            Summary = $"{list.Count} services · {running} running";
        }
        catch (Exception ex)
        {
            Summary = "Couldn't read services";
            Status = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyFilter()
    {
        var q = Search.Trim();
        var visible = _all.Where(r => Filter.Key switch
            {
                "running" => r.IsRunning,
                "stopped" => !r.IsRunning,
                "thirdparty" => !r.IsMicrosoft,
                _ => true,
            })
            .Where(r => q.Length == 0 ||
                        r.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        r.Description.Contains(q, StringComparison.OrdinalIgnoreCase))
            .ToList();

        Items.Clear();
        foreach (var r in visible) Items.Add(r);
    }

    private bool CanStart() => Selected is { IsRunning: false } && !IsBusy;
    private bool CanStop() => Selected is { IsRunning: true } && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task Start() => Run("Starting", WindowsServices.Start, "started");

    [RelayCommand(CanExecute = nameof(CanStop))]
    private Task Stop() => Run("Stopping", WindowsServices.Stop, "stopped");

    [RelayCommand(CanExecute = nameof(CanStop))]
    private Task Restart() => Run("Restarting", WindowsServices.Restart, "restarted");

    [RelayCommand]
    private Task SetStartup(string mode)
    {
        var label = mode switch
        {
            "auto" => "Automatic",
            "delayed" => "Automatic (delayed)",
            "disabled" => "Disabled",
            _ => "Manual",
        };
        return Run("Updating", name => WindowsServices.SetStartup(name, mode), $"set to {label}");
    }

    private async Task Run(string verb, Func<string, string> action, string done)
    {
        if (Selected is not { } row) return;
        IsBusy = true;
        Status = $"{verb} {row.DisplayName}…";
        var error = await Task.Run(() => action(row.Name));
        Status = error.Length == 0 ? $"{row.DisplayName} {done}." : $"{row.DisplayName}: {error}";
        await Refresh();
        IsBusy = false;
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        RestartCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Called as the right-click menu opens; false cancels it.</summary>
    public bool PrepareMenu() => Selected is not null;

    [RelayCommand]
    private void SearchOnline()
    {
        if (Selected is not { } row) return;
        try
        {
            Process.Start(new ProcessStartInfo(
                $"https://www.bing.com/search?q={Uri.EscapeDataString(row.Name + " " + row.DisplayName + " service")}")
            { UseShellExecute = true });
        }
        catch { }
    }

    [RelayCommand]
    private async Task CopyName()
    {
        if (Selected is { } row && App.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(row.Name);
            Status = $"Copied \"{row.Name}\".";
        }
    }

    [RelayCommand]
    private void OpenConsole()
    {
        try { Process.Start(new ProcessStartInfo("services.msc") { UseShellExecute = true }); }
        catch { }
    }
}

public sealed class ServiceRow
{
    public ServiceRow(ServiceInfo s)
    {
        Name = s.Name;
        DisplayName = s.DisplayName;
        Description = string.IsNullOrWhiteSpace(s.Description) ? s.Name : s.Description;
        State = s.State;
        IsRunning = s.State == "Running";
        IsStopped = s.State == "Stopped";
        IsMicrosoft = s.IsMicrosoft;
        Startup = s.StartMode switch
        {
            "Automatic" when s.Delayed => "Automatic (delayed)",
            "" => "Unknown",
            _ => s.StartMode,
        };
        PidText = s.Pid > 0 ? $"PID {s.Pid}" : "";
    }

    public string Name { get; }
    public string DisplayName { get; }
    public string Description { get; }
    public string State { get; }
    public bool IsRunning { get; }
    public bool IsStopped { get; }
    public bool IsMicrosoft { get; }
    public string Startup { get; }
    public string PidText { get; }
}
