using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Ember.ViewModels;

namespace Ember.Controls;

/// <summary>
/// Fan curve graph: temperature (x) → fan speed % (y). Drag the points to shape the curve.
/// Also draws the fan's live operating point.
/// </summary>
public class CurveEditor : Control
{
    public static readonly StyledProperty<IList<CurvePoint>?> PointsProperty =
        AvaloniaProperty.Register<CurveEditor, IList<CurvePoint>?>(nameof(Points));

    public static readonly StyledProperty<double> CurrentTempProperty =
        AvaloniaProperty.Register<CurveEditor, double>(nameof(CurrentTemp), double.NaN);

    public static readonly StyledProperty<double> CurrentPercentProperty =
        AvaloniaProperty.Register<CurveEditor, double>(nameof(CurrentPercent), double.NaN);

    public static readonly StyledProperty<IBrush?> LineBrushProperty =
        AvaloniaProperty.Register<CurveEditor, IBrush?>(nameof(LineBrush));

    public static readonly StyledProperty<IBrush?> FillBrushProperty =
        AvaloniaProperty.Register<CurveEditor, IBrush?>(nameof(FillBrush));

    public static readonly StyledProperty<IBrush?> GridBrushProperty =
        AvaloniaProperty.Register<CurveEditor, IBrush?>(nameof(GridBrush));

    public static readonly StyledProperty<IBrush?> LabelBrushProperty =
        AvaloniaProperty.Register<CurveEditor, IBrush?>(nameof(LabelBrush));

    public static readonly StyledProperty<IBrush?> MarkerBrushProperty =
        AvaloniaProperty.Register<CurveEditor, IBrush?>(nameof(MarkerBrush));

    static CurveEditor()
    {
        AffectsRender<CurveEditor>(PointsProperty, CurrentTempProperty, CurrentPercentProperty,
            LineBrushProperty, FillBrushProperty, GridBrushProperty, LabelBrushProperty, MarkerBrushProperty);
    }

    public IList<CurvePoint>? Points { get => GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    public double CurrentTemp { get => GetValue(CurrentTempProperty); set => SetValue(CurrentTempProperty, value); }
    public double CurrentPercent { get => GetValue(CurrentPercentProperty); set => SetValue(CurrentPercentProperty, value); }
    public IBrush? LineBrush { get => GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }
    public IBrush? FillBrush { get => GetValue(FillBrushProperty); set => SetValue(FillBrushProperty, value); }
    public IBrush? GridBrush { get => GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public IBrush? LabelBrush { get => GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }
    public IBrush? MarkerBrush { get => GetValue(MarkerBrushProperty); set => SetValue(MarkerBrushProperty, value); }

    private const double MinTemp = 20, MaxTemp = 100;
    private const double PadLeft = 38, PadRight = 12, PadTop = 10, PadBottom = 24;
    private const double PointRadius = 6, HitRadius = 14;

    private int _dragIndex = -1;
    private int _hoverIndex = -1;

    private double PlotWidth => Math.Max(1, Bounds.Width - PadLeft - PadRight);
    private double PlotHeight => Math.Max(1, Bounds.Height - PadTop - PadBottom);
    private double X(double temp) => PadLeft + (temp - MinTemp) / (MaxTemp - MinTemp) * PlotWidth;
    private double Y(double pct) => PadTop + (1 - pct / 100) * PlotHeight;
    private double TempAt(double x) => MinTemp + (x - PadLeft) / PlotWidth * (MaxTemp - MinTemp);
    private double PercentAt(double y) => (1 - (y - PadTop) / PlotHeight) * 100;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != PointsProperty) return;

        if (change.OldValue is IList<CurvePoint> oldList)
        {
            foreach (var p in oldList) p.PropertyChanged -= OnPointChanged;
            if (oldList is INotifyCollectionChanged oldNcc) oldNcc.CollectionChanged -= OnCollectionChanged;
        }
        if (change.NewValue is IList<CurvePoint> newList)
        {
            foreach (var p in newList) p.PropertyChanged += OnPointChanged;
            if (newList is INotifyCollectionChanged newNcc) newNcc.CollectionChanged += OnCollectionChanged;
        }
    }

    private void OnPointChanged(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null) foreach (CurvePoint p in e.OldItems) p.PropertyChanged -= OnPointChanged;
        if (e.NewItems is not null) foreach (CurvePoint p in e.NewItems) p.PropertyChanged += OnPointChanged;
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        if (Bounds.Width < 60 || Bounds.Height < 60) return;

        var typeface = new Typeface(FontFamily.Default);
        var gridPen = GridBrush is { } g ? new Pen(g, 1) : null;

        // Grid + labels
        for (int pct = 0; pct <= 100; pct += 25)
        {
            var y = Y(pct);
            if (gridPen is not null) ctx.DrawLine(gridPen, new Point(PadLeft, y), new Point(PadLeft + PlotWidth, y));
            DrawLabel(ctx, typeface, $"{pct}%", new Point(4, y - 7));
        }
        for (int t = 20; t <= 100; t += 20)
        {
            var x = X(t);
            if (gridPen is not null) ctx.DrawLine(gridPen, new Point(x, PadTop), new Point(x, PadTop + PlotHeight));
            DrawLabel(ctx, typeface, Format.UseFahrenheit ? $"{t * 9 / 5 + 32}°" : $"{t}°", new Point(x - 10, PadTop + PlotHeight + 6));
        }

        var points = Points?.OrderBy(p => p.Temp).ToList();
        if (points is null || points.Count == 0) return;

        // Curve: flat before the first point and after the last, like the controller evaluates it.
        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var lg = line.Open())
        using (var ag = area.Open())
        {
            var start = new Point(X(MinTemp), Y(points[0].Percent));
            lg.BeginFigure(start, false);
            ag.BeginFigure(new Point(X(MinTemp), Y(0)), true);
            ag.LineTo(start);
            foreach (var p in points)
            {
                var pt = new Point(X(p.Temp), Y(p.Percent));
                lg.LineTo(pt);
                ag.LineTo(pt);
            }
            var end = new Point(X(MaxTemp), Y(points[^1].Percent));
            lg.LineTo(end);
            ag.LineTo(end);
            ag.LineTo(new Point(X(MaxTemp), Y(0)));
            lg.EndFigure(false);
            ag.EndFigure(true);
        }
        if (FillBrush is { } fill) ctx.DrawGeometry(fill, null, area);
        if (LineBrush is { } stroke) ctx.DrawGeometry(null, new Pen(stroke, 2.5, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), line);

        // Live operating point
        if (!double.IsNaN(CurrentTemp) && MarkerBrush is { } marker)
        {
            var t = Math.Clamp(CurrentTemp, MinTemp, MaxTemp);
            var x = X(t);
            ctx.DrawLine(new Pen(marker, 1, new DashStyle(new double[] { 3, 3 }, 0)), new Point(x, PadTop), new Point(x, PadTop + PlotHeight));
            if (!double.IsNaN(CurrentPercent))
                ctx.DrawEllipse(marker, null, new Point(x, Y(Math.Clamp(CurrentPercent, 0, 100))), 4, 4);
        }

        // Draggable points (index into the unsorted list so drags stay stable)
        var list = Points!;
        for (int i = 0; i < list.Count; i++)
        {
            var c = new Point(X(list[i].Temp), Y(list[i].Percent));
            var r = i == _dragIndex || i == _hoverIndex ? PointRadius + 2 : PointRadius;
            ctx.DrawEllipse(LineBrush, new Pen(GridBrush ?? Brushes.Transparent, 2), c, r, r);
        }

        if (_dragIndex >= 0 && _dragIndex < list.Count)
        {
            var p = list[_dragIndex];
            var text = $"{(Format.UseFahrenheit ? p.Temp * 9 / 5 + 32 : p.Temp):0}° → {p.Percent:0}%";
            DrawLabel(ctx, typeface, text, new Point(Math.Min(X(p.Temp) + 10, Bounds.Width - 90), Math.Max(Y(p.Percent) - 24, 0)), bold: true);
        }
    }

    private void DrawLabel(DrawingContext ctx, Typeface typeface, string text, Point at, bool bold = false)
    {
        if (LabelBrush is null) return;
        var tf = bold ? new Typeface(typeface.FontFamily, FontStyle.Normal, FontWeight.SemiBold) : typeface;
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, bold ? 12 : 11, LabelBrush);
        ctx.DrawText(ft, at);
    }

    private int HitTest(Point pos)
    {
        var list = Points;
        if (list is null) return -1;
        int best = -1;
        double bestDist = HitRadius;
        for (int i = 0; i < list.Count; i++)
        {
            var dx = X(list[i].Temp) - pos.X;
            var dy = Y(list[i].Percent) - pos.Y;
            var d = Math.Sqrt(dx * dx + dy * dy);
            if (d <= bestDist) { best = i; bestDist = d; }
        }
        return best;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragIndex = HitTest(e.GetPosition(this));
        if (_dragIndex < 0) return;
        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pos = e.GetPosition(this);
        var list = Points;

        if (_dragIndex >= 0 && list is not null && _dragIndex < list.Count)
        {
            // Keep points in temperature order: a point can't pass its neighbours.
            var sorted = list.OrderBy(p => p.Temp).ToList();
            var point = list[_dragIndex];
            var order = sorted.IndexOf(point);
            var lo = order > 0 ? sorted[order - 1].Temp + 1 : MinTemp;
            var hi = order < sorted.Count - 1 ? sorted[order + 1].Temp - 1 : MaxTemp;

            point.Temp = Math.Round(Math.Clamp(TempAt(pos.X), lo, hi));
            point.Percent = Math.Round(Math.Clamp(PercentAt(pos.Y), 0, 100));
            e.Handled = true;
            return;
        }

        var hover = HitTest(pos);
        if (hover != _hoverIndex)
        {
            _hoverIndex = hover;
            Cursor = hover >= 0 ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragIndex < 0) return;
        _dragIndex = -1;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragIndex = -1;
        InvalidateVisual();
    }
}
