using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Ember.Views;

/// <summary>Ctrl+F focuses a page's search box; Esc in the box clears it.</summary>
internal static class SearchKeys
{
    public static void Attach(UserControl page, TextBox box)
    {
        page.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control)
            {
                box.Focus();
                box.SelectAll();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && box.IsFocused && !string.IsNullOrEmpty(box.Text))
            {
                box.Text = "";
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
    }
}
