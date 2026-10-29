using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Ember.Controls;

/// <summary>A 270° ring gauge, drawn directly (no templates) so it is cheap to redraw.</summary>
public class RingGauge : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<RingGauge, double>(nameof(Value));

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<RingGauge, double>(nameof(Maximum), 100);

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<RingGauge, double>(nameof(StrokeThickness), 12);

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<RingGauge, IBrush?>(nameof(TrackBrush));

    public static readonly StyledProperty<IBrush?> FillBrushProperty =
        AvaloniaProperty.Register<RingGauge, IBrush?>(nameof(FillBrush));

    static RingGauge()
    {
        AffectsRender<RingGauge>(ValueProperty, MaximumProperty, StrokeThicknessProperty,
            TrackBrushProperty, FillBrushProperty);
    }

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double StrokeThickness { get => GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
    public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public IBrush? FillBrush { get => GetValue(FillBrushProperty); set => SetValue(FillBrushProperty, value); }

    private const double StartAngle = 135;
    private const double SweepAngle = 270;

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        var t = StrokeThickness;
        if (size <= t) return;

        var radius = (size - t) / 2;
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);

        if (TrackBrush is { } track)
            DrawArc(context, center, radius, StartAngle, SweepAngle, new Pen(track, t, lineCap: PenLineCap.Round));

        var fraction = Maximum <= 0 ? 0 : Math.Clamp(Value / Maximum, 0, 1);
        if (fraction > 0.002 && FillBrush is { } fill)
            DrawArc(context, center, radius, StartAngle, SweepAngle * fraction, new Pen(fill, t, lineCap: PenLineCap.Round));
    }

    private static void DrawArc(DrawingContext ctx, Point c, double r, double startDeg, double sweepDeg, IPen pen)
    {
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(OnCircle(c, r, startDeg), false);
            g.ArcTo(OnCircle(c, r, startDeg + sweepDeg), new Size(r, r), 0, sweepDeg > 180, SweepDirection.Clockwise);
            g.EndFigure(false);
        }
        ctx.DrawGeometry(null, pen, geometry);
    }

    private static Point OnCircle(Point c, double r, double deg)
    {
        var rad = deg * Math.PI / 180;
        return new Point(c.X + r * Math.Cos(rad), c.Y + r * Math.Sin(rad));
    }
}
