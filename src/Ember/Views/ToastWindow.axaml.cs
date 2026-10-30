using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace Ember.Views;

/// <summary>Small borderless pop-up in the bottom-right corner of the screen. Closes itself after 8 seconds.</summary>
public partial class ToastWindow : Window
{
    private readonly Action? _onClick;

    public ToastWindow() => InitializeComponent();   // designer / XAML loader

    public ToastWindow(string title, string message, bool hot, Action onClick) : this()
    {
        _onClick = onClick;
        TitleText.Text = title;
        MessageText.Text = message;

        if (hot && Application.Current?.FindResource("Hot") is IBrush hotBrush)
        {
            IconGlyph.Foreground = hotBrush;
            if (hotBrush is ISolidColorBrush solid)
                IconBadge.Background = new SolidColorBrush(solid.Color, 0.16);
        }

        CloseButton.Click += (_, e) =>
        {
            e.Handled = true;
            FadeOutAndClose();
        };
        Card.PointerPressed += (_, e) =>
        {
            if (e.Handled) return;
            _onClick?.Invoke();
            FadeOutAndClose();
        };

        Opened += (_, _) =>
        {
            PlaceBottomRight();
            Opacity = 1;
            DispatcherTimer.RunOnce(FadeOutAndClose, TimeSpan.FromSeconds(8));
        };
    }

    private void PlaceBottomRight()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) return;
        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        var margin = (int)(16 * scale);
        Position = new PixelPoint(
            area.Right - (int)(Bounds.Width * scale) - margin,
            area.Bottom - (int)(Bounds.Height * scale) - margin);
    }

    private bool _closing;

    private void FadeOutAndClose()
    {
        if (_closing) return;
        _closing = true;
        Opacity = 0;
        DispatcherTimer.RunOnce(Close, TimeSpan.FromMilliseconds(220));
    }
}
