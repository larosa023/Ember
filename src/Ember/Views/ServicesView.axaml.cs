using System.ComponentModel;
using Avalonia.Controls;
using Ember.ViewModels;

namespace Ember.Views;

public partial class ServicesView : UserControl
{
    public ServicesView() => InitializeComponent();

    private void OnMenuOpening(object? sender, CancelEventArgs e)
    {
        if (DataContext is not ServicesViewModel vm || !vm.PrepareMenu())
            e.Cancel = true;
    }
}
