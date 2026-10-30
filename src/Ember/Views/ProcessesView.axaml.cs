using System.ComponentModel;
using Avalonia.Controls;
using Ember.ViewModels;

namespace Ember.Views;

public partial class ProcessesView : UserControl
{
    public ProcessesView() => InitializeComponent();

    /// <summary>Reads the clicked process's priority / efficiency state, or cancels on headers and empty space.</summary>
    private void OnMenuOpening(object? sender, CancelEventArgs e)
    {
        if (DataContext is not ProcessesViewModel vm || !vm.PrepareMenu())
            e.Cancel = true;
    }
}
