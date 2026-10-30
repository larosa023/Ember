using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Ember.ViewModels;
using Ember.Views;

namespace Ember;

/// <summary>Maps each page view-model to its view. No reflection, so it stays fast.</summary>
public sealed class ViewLocator : IDataTemplate
{
    public Control? Build(object? data) => data switch
    {
        DashboardViewModel => new DashboardView(),
        ProcessesViewModel => new ProcessesView(),
        SensorsViewModel => new SensorsView(),
        FansViewModel => new FansView(),
        HistoryViewModel => new HistoryView(),
        DetailViewModel => new DetailView(),
        StorageViewModel => new StorageView(),
        NetworkViewModel => new NetworkView(),
        StartupViewModel => new StartupView(),
        SettingsViewModel => new SettingsView(),
        _ => new TextBlock { Text = "Page not found" },
    };

    public bool Match(object? data) => data is ViewModelBase;
}
