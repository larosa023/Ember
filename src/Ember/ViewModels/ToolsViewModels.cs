using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ember.Services;

namespace Ember.ViewModels;

/// <summary>GPU page: NVIDIA power limit slider (percent of the card's default, like Afterburner).</summary>
public partial class GpuTuningViewModel : ViewModelBase
{
    private readonly SettingsService _settings;
    private Nvml.PowerInfo? _gpu;
    private bool _loaded;

    public GpuTuningViewModel(SettingsService settings) => _settings = settings;

    [ObservableProperty] private bool _isAvailable;
    [ObservableProperty] private string _gpuName = "";
    [ObservableProperty] private double _minPercent = 50;
    [ObservableProperty] private double _maxPercent = 100;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetText))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private double _percent = 100;

    [ObservableProperty] private string _currentText = "";
    [ObservableProperty] private string _rangeText = "";
    [ObservableProperty] private string _status = "";

    public bool IsElevated => Environment.IsPrivilegedProcess;

    public string TargetText => _gpu is null ? "" : $"{Percent:0}% · {_gpu.DefaultW * Percent / 100:0} W";

    partial void OnStatusChanged(string value)
    {
        if (value.Length > 0) ClearAfter(() => Status = "", 10);
    }

    /// <summary>Reads the card once, the first time the GPU page opens.</summary>
    public async Task LoadAsync()
    {
        if (_loaded) return;
        _loaded = true;
        var gpus = await Task.Run(Nvml.ReadPower);
        _gpu = gpus.FirstOrDefault(g => g.Name == _settings.Current.GpuPowerLimitGpu) ?? gpus.FirstOrDefault();
        if (_gpu is null) return;
        Show(_gpu);
        IsAvailable = true;
    }

    private void Show(Nvml.PowerInfo gpu)
    {
        GpuName = gpu.Name;
        MinPercent = Math.Floor(gpu.MinW / gpu.DefaultW * 100);
        MaxPercent = Math.Floor(gpu.MaxW / gpu.DefaultW * 100);
        Percent = Math.Round(gpu.CurrentW / gpu.DefaultW * 100);
        CurrentText = $"Now {gpu.CurrentW:0} W · default {gpu.DefaultW:0} W";
        RangeText = $"Your card allows {gpu.MinW:0}–{gpu.MaxW:0} W ({MinPercent:0}–{MaxPercent:0}%).";
        OnPropertyChanged(nameof(TargetText));
    }

    private bool CanApply() => _gpu is not null;

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task Apply() => await SetAsync(_gpu!.DefaultW * Percent / 100, save: true);

    [RelayCommand]
    private async Task Reset()
    {
        if (_gpu is null) return;
        Percent = 100;
        await SetAsync(_gpu.DefaultW, save: false);
    }

    private async Task SetAsync(double watts, bool save)
    {
        var gpu = _gpu!;
        var error = await Task.Run(() => GpuPower.Set(gpu, watts));
        if (error is not null)
        {
            Status = error;
            return;
        }

        _settings.Current.GpuPowerLimitW = save && Math.Abs(watts - gpu.DefaultW) >= 0.5 ? watts : null;
        _settings.Current.GpuPowerLimitGpu = _settings.Current.GpuPowerLimitW is null ? null : gpu.Name;
        _settings.Save();

        var fresh = (await Task.Run(Nvml.ReadPower)).FirstOrDefault(g => g.Name == gpu.Name);
        if (fresh is not null)
        {
            _gpu = fresh;
            Show(fresh);
        }
        Status = _settings.Current.GpuPowerLimitW is null
            ? "Power limit back to default."
            : "Applied. Ember keeps this limit while it runs and restores the default when it closes.";
    }
}

/// <summary>Memory page: "Clear standby memory".</summary>
public partial class MemoryCleanupViewModel : ViewModelBase
{
    [ObservableProperty] private string _standbyText = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isBusy;

    public bool IsElevated => Environment.IsPrivilegedProcess;

    partial void OnStatusChanged(string value)
    {
        if (value.Length > 0) ClearAfter(() => Status = "", 10);
    }

    private int _tick;

    /// <summary>Called each tick while the Memory page is open; the standby size is re-read every 5 seconds.</summary>
    public void Update()
    {
        if (!IsElevated || _tick++ % 5 != 0) return;
        StandbyText = MemoryCleaner.StandbyBytes() is long b ? $"Standby (cached): {Format.Bytes(b)}" : "";
    }

    [RelayCommand]
    private async Task Clear()
    {
        IsBusy = true;
        try
        {
            var before = await Task.Run(MemoryCleaner.StandbyBytes);
            var error = await Task.Run(MemoryCleaner.ClearStandby);
            if (error is not null)
            {
                Status = error;
                return;
            }
            var after = await Task.Run(MemoryCleaner.StandbyBytes);
            Status = before is long b && after is long a && b > a
                ? $"Freed {Format.Bytes(b - a)} of cached memory."
                : "Standby memory cleared.";
            _tick = 0;
            Update();
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>Windows power plan picker (Dashboard and tray).</summary>
public partial class PowerPlanViewModel : ViewModelBase
{
    public ObservableCollection<PowerPlans.Plan> Plans { get; } = new();

    [ObservableProperty] private PowerPlans.Plan? _selected;
    [ObservableProperty] private bool _isAvailable;

    private bool _syncing;

    /// <summary>Re-reads the list and the active plan (cheap; called when the dashboard opens).</summary>
    public void Refresh()
    {
        var plans = PowerPlans.List();
        var active = PowerPlans.Active();
        _syncing = true;
        try
        {
            if (!plans.Select(p => p.Id).SequenceEqual(Plans.Select(p => p.Id)))
            {
                Plans.Clear();
                foreach (var p in plans) Plans.Add(p);
            }
            Selected = Plans.FirstOrDefault(p => p.Id == active);
            IsAvailable = Plans.Count > 1;
        }
        finally { _syncing = false; }
    }

    partial void OnSelectedChanged(PowerPlans.Plan? value)
    {
        if (_syncing || value is null) return;
        if (!PowerPlans.SetActive(value.Id)) Refresh();
    }

    [RelayCommand]
    private void Activate(PowerPlans.Plan plan) => Selected = plan;
}
