using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Ember.ViewModels;

namespace Ember.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();

        // While a shortcut box is waiting, grab key presses before buttons or scrolling see them.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (DataContext is SettingsViewModel vm && vm.CaptureKey(e.Key, e.KeyModifiers))
                e.Handled = true;
        }, RoutingStrategies.Tunnel);
    }
}
