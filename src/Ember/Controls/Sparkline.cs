using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Ember.Controls;

/// <summary>A tiny history graph (line + soft fill). Assign a new array to redraw.</summary>
public class Sparkline : Control
{
    public static readonly StyledProperty<double[]?> ValuesProperty =
        AvaloniaProperty.Register<Sparkline, double[]?>(nameof(Values));

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<Sparkline, double>(nameof(Maximum), 100);

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<Sparkline, IBrush?>(nameof(Stroke));

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<Sparkline, IBrush?>(nameof(Fill));

    /// <summary>Optional second series (drawn as a line only), e.g. upload next to download.</summary>
    public static readonly StyledProperty<double[]?> Values2Property =
        AvaloniaProperty.Register<Sparkline, double[]?>(nameof(Values2));

    public static readonly StyledProperty<IBrush?> Stroke2Property =
        AvaloniaProperty.Register<Sparkline, IBrush?>(nameof(Stroke2));

    static Sparkline()
    {
        AffectsRender<Sparkline>(ValuesProperty, MaximumProperty, StrokeProperty, FillProperty, Values2Property, Stroke2Property);
    }

    public double[]? Values { get => GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public IBrush? Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public double[]? Values2 { get => GetValue(Values2Property); set => SetValue(Values2Property, value); }
    public IBrush? Stroke2 { get => GetValue(Stroke2Property); set => SetValue(Stroke2Property, value); }

    public override void Render(DrawingContext context)
    {
        var values = Values;
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (values is null || values.Length < 2 || w <= 0 || h <= 0) return;

        var step = w / (values.Length - 1);
        double Y(double v) => h - 1 - Math.Clamp(v / Maximum, 0, 1) * (h - 2);

        if (Fill is { } fill)
        {
            var area = new StreamGeometry();
            using (var g = area.Open())
            {
                g.BeginFigure(new Point(0, h), true);
                for (int i = 0; i < values.Length; i++) g.LineTo(new Point(i * step, Y(values[i])));
                g.LineTo(new Point(w, h));
                g.EndFigure(true);
            }
            context.DrawGeometry(fill, null, area);
        }

        if (Stroke is { } stroke)
        {
            var line = new StreamGeometry();
            using (var g = line.Open())
            {
                g.BeginFigure(new Point(0, Y(values[0])), false);
                for (int i = 1; i < values.Length; i++) g.LineTo(new Point(i * step, Y(values[i])));
                g.EndFigure(false);
            }
            context.DrawGeometry(null, new Pen(stroke, 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), line);
        }

        var second = Values2;
        if (second is { Length: > 1 } && Stroke2 is { } stroke2)
        {
            var step2 = w / (second.Length - 1);
            var line2 = new StreamGeometry();
            using (var g = line2.Open())
            {
                g.BeginFigure(new Point(0, Y(second[0])), false);
                for (int i = 1; i < second.Length; i++) g.LineTo(new Point(i * step2, Y(second[i])));
                g.EndFigure(false);
            }
            context.DrawGeometry(null, new Pen(stroke2, 1.6, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), line2);
        }
    }
}
