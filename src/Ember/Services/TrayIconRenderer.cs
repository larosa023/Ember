using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Ember.Models;

namespace Ember.Services;

/// <summary>
/// Draws the tray icon as a live number (CPU temp, GPU temp or CPU %) on the theme's accent color.
/// The icon is only redrawn when the number on it actually changes.
/// </summary>
public sealed class TrayIconRenderer
{
    private const int Size = 32;
    private WindowIcon? _logo;
    private string? _lastText;
    private Color _lastColor;

    public WindowIcon Logo => _logo ??= new WindowIcon(AssetLoader.Open(new Uri("avares://Ember/Assets/ember.ico")));

    /// <summary>The icon to show now, or null when nothing changed since last time.</summary>
    public WindowIcon? Next(string mode, HardwareSnapshot s)
    {
        string? text = mode switch
        {
            "cpu-temp" => Number(s.CpuTemp is float c ? (Format.UseFahrenheit ? c * 9 / 5 + 32 : c) : null),
            "gpu-temp" => Number(s.GpuTemp is float g ? (Format.UseFahrenheit ? g * 9 / 5 + 32 : g) : null),
            "cpu-load" => Number(s.CpuLoad),
            _ => null,
        };

        var color = Accent();
        if (text == _lastText && color == _lastColor) return null;
        _lastText = text;
        _lastColor = color;
        return text is null ? Logo : Draw(text, color);
    }

    /// <summary>Forget the last drawn value so the next tick redraws (theme or mode changed).</summary>
    public void Invalidate() => _lastText = "\0";

    private static string? Number(float? v) =>
        v is float f ? Math.Min(Math.Round(f), 999).ToString("0", CultureInfo.InvariantCulture) : null;

    private static Color Accent() =>
        Application.Current?.Resources.TryGetResource("Accent", null, out var r) == true && r is ISolidColorBrush b
            ? b.Color
            : Color.Parse("#FF6A2B");

    private static WindowIcon Draw(string text, Color accent)
    {
        var bitmap = new RenderTargetBitmap(new PixelSize(Size, Size), new Vector(96, 96));
        using (var ctx = bitmap.CreateDrawingContext())
        {
            ctx.DrawRectangle(new SolidColorBrush(accent), null, new RoundedRect(new Rect(0, 0, Size, Size), 7));

            var white = (accent.R * 0.299 + accent.G * 0.587 + accent.B * 0.114) < 170;
            var fontSize = text.Length >= 3 ? 15 : 21;
            var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold), fontSize,
                white ? Brushes.White : Brushes.Black);
            ctx.DrawText(formatted, new Point((Size - formatted.Width) / 2, (Size - formatted.Height) / 2));
        }
        return new WindowIcon(bitmap);
    }
}
