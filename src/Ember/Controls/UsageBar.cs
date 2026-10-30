using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Ember.Controls;

/// <summary>A thin rounded usage bar (0–Maximum) that always fills exactly its own width.</summary>
public class UsageBar : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<UsageBar, double>(nameof(Value));

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<UsageBar, double>(nameof(Maximum), 100);

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<UsageBar, IBrush?>(nameof(TrackBrush));

    public static readonly StyledProperty<IBrush?> FillBrushProperty =
        AvaloniaProperty.Register<UsageBar, IBrush?>(nameof(FillBrush));

    static UsageBar()
    {
        AffectsRender<UsageBar>(ValueProperty, MaximumProperty, TrackBrushProperty, FillBrushProperty);
    }

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public IBrush? FillBrush { get => GetValue(FillBrushProperty); set => SetValue(FillBrushProperty, value); }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;

        var radius = h / 2;
        if (TrackBrush is { } track)
            context.DrawRectangle(track, null, new RoundedRect(new Rect(0, 0, w, h), radius));

        var fraction = Maximum <= 0 ? 0 : Math.Clamp(Value / Maximum, 0, 1);
        var fillWidth = Math.Max(fraction * w, fraction > 0 ? h : 0);   // keep tiny values visibly round
        if (fillWidth > 0 && FillBrush is { } fill)
            context.DrawRectangle(fill, null, new RoundedRect(new Rect(0, 0, Math.Min(fillWidth, w), h), radius));
    }
}
