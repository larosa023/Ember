using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Ember.ViewModels;

namespace Ember.Views;

public partial class ProcessesView : UserControl
{
    public ProcessesView()
    {
        InitializeComponent();
        SearchKeys.Attach(this, SearchBox);

        // Double-click an app with several processes to expand or collapse it.
        ProcessList.DoubleTapped += (_, e) =>
        {
            if ((e.Source as Control)?.DataContext is ProcessGroup { HasChildren: true } group)
                group.ToggleExpandedCommand.Execute(null);
        };
    }

    /// <summary>Reads the clicked process's priority / efficiency state, or cancels on headers and empty space.</summary>
    private void OnMenuOpening(object? sender, CancelEventArgs e)
    {
        if (DataContext is not ProcessesViewModel vm || !vm.PrepareMenu())
            e.Cancel = true;
    }
}
