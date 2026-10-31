using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ember.Services;

namespace Ember.ViewModels;

public partial class SpecsViewModel : ViewModelBase
{
    private List<SpecSection> _sections = new();

    public ObservableCollection<SpecSection> Sections { get; } = new();

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _status = "";

    public async Task LoadAsync()
    {
        if (IsLoading || Sections.Count > 0) return;
        IsLoading = true;
        try
        {
            _sections = await Task.Run(SystemSpecs.Read);
            foreach (var s in _sections) Sections.Add(s);
        }
        catch (Exception ex)
        {
            Status = $"Couldn't read system info: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task Refresh()
    {
        Sections.Clear();
        Status = "";
        await LoadAsync();
    }

    [RelayCommand]
    private async Task Copy()
    {
        if (_sections.Count == 0 || App.Clipboard is not { } clipboard) return;
        await clipboard.SetTextAsync(SystemSpecs.ToText(_sections));
        Status = "Copied. Paste it anywhere (no serial numbers or personal info included).";
    }
}
