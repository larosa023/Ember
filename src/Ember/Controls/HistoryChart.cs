using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Ember.Controls;

/// <summary>One line on a <see cref="HistoryChart"/>. NaN values leave a gap.</summary>
public sealed record ChartLine(string Name, double[] Values);

/// <summary>Everything a chart draws. Replace the whole object to redraw.</summary>
public sealed record ChartData(DateTime[] Times, IReadOnlyList<ChartLine> Lines, double Min, double Max, Func<double, string> Label);

/// <summary>
/// Time-series chart for the History page: up to three lines, y-axis labels, time labels,
/// and a hover readout showing the time and every line's value under the pointer.
/// </summary>
public class HistoryChart : Control
{
    public static readonly StyledProperty<ChartData?> DataProperty =
        AvaloniaProperty.Register<HistoryChart, ChartData?>(nameof(Data));

    public static readonly StyledProperty<IBrush?> Brush1Property =
        AvaloniaProperty.Register<HistoryChart, IBrush?>(nameof(Brush1));
    public static readonly StyledProperty<IBrush?> Brush2Property =
        AvaloniaProperty.Register<HistoryChart, IBrush?>(nameof(Brush2));
    public static readonly StyledProperty<IBrush?> Brush3Property =
        AvaloniaProperty.Register<HistoryChart, IBrush?>(nameof(Brush3));
    public static readonly StyledProperty<IBrush?> FillBrushProperty =
        AvaloniaProperty.Register<HistoryChart, IBrush?>(nameof(FillBrush));
    public static readonly StyledProperty<IBrush?> GridBrushProperty =
        AvaloniaProperty.Register<HistoryChart, IBrush?>(nameof(GridBrush));
    public static readonly StyledProperty<IBrush?> LabelBrushProperty =
        AvaloniaProperty.Register<HistoryChart, IBrush?>(nameof(LabelBrush));
    public static readonly StyledProperty<IBrush?> PanelBrushProperty =
        AvaloniaProperty.Register<HistoryChart, IBrush?>(nameof(PanelBrush));
    public static readonly StyledProperty<IBrush?> TextBrushProperty =
        AvaloniaProperty.Register<HistoryChart, IBrush?>(nameof(TextBrush));

    static HistoryChart()
    {
        AffectsRender<HistoryChart>(DataProperty, Brush1Property, Brush2Property, Brush3Property,
            FillBrushProperty, GridBrushProperty, LabelBrushProperty, PanelBrushProperty, TextBrushProperty);
    }

    public ChartData? Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public IBrush? Brush1 { get => GetValue(Brush1Property); set => SetValue(Brush1Property, value); }
    public IBrush? Brush2 { get => GetValue(Brush2Property); set => SetValue(Brush2Property, value); }
    public IBrush? Brush3 { get => GetValue(Brush3Property); set => SetValue(Brush3Property, value); }
    public IBrush? FillBrush { get => GetValue(FillBrushProperty); set => SetValue(FillBrushProperty, value); }
    public IBrush? GridBrush { get => GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public IBrush? LabelBrush { get => GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }
    public IBrush? PanelBrush { get => GetValue(PanelBrushProperty); set => SetValue(PanelBrushProperty, value); }
    public IBrush? TextBrush { get => GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }

    private const double PadLeft = 44, PadRight = 8, PadTop = 8, PadBottom = 22;
    private int _hover = -1;
    private readonly Typeface _font = new(FontFamily.Default);

    private IBrush? BrushFor(int i) => i switch { 0 => Brush1, 1 => Brush2, _ => Brush3 };

    public override void Render(DrawingContext ctx)
    {
        // Transparent fill so the whole chart (not just the lines) receives hover events.
        ctx.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));

        var data = Data;
        var w = Bounds.Width - PadLeft - PadRight;
        var h = Bounds.Height - PadTop - PadBottom;
        if (data is null || w < 20 || h < 20) return;

        var n = data.Times.Length;
        var range = data.Max - data.Min <= 0 ? 1 : data.Max - data.Min;
        double X(int i) => PadLeft + (n <= 1 ? 0 : i * w / (n - 1));
        double Y(double v) => PadTop + (1 - Math.Clamp((v - data.Min) / range, 0, 1)) * h;

        // Grid + y labels
        var grid = GridBrush is { } gb ? new Pen(gb, 1) : null;
        for (int k = 0; k <= 4; k++)
        {
            var v = data.Min + range * k / 4;
            var y = Y(v);
            if (grid is not null) ctx.DrawLine(grid, new Point(PadLeft, y), new Point(PadLeft + w, y));
            Text(ctx, data.Label(v), new Point(0, y - 7), 11, LabelBrush, alignRightAt: PadLeft - 8);
        }

        // Time labels
        if (n > 1)
        {
            foreach (var i in new[] { 0, n / 3, 2 * n / 3, n - 1 })
            {
                var t = data.Times[i].ToString("HH:mm", CultureInfo.InvariantCulture);
                var x = Math.Clamp(X(i) - 14, PadLeft, PadLeft + w - 30);
                Text(ctx, t, new Point(x, PadTop + h + 6), 11, LabelBrush);
            }
        }

        // Lines (first one also gets a soft fill)
        for (int s = 0; s < data.Lines.Count; s++)
        {
            var brush = BrushFor(s);
            if (brush is null) continue;
            var values = data.Lines[s].Values;

            var line = new StreamGeometry();
            StreamGeometry? area = s == 0 && FillBrush is not null ? new StreamGeometry() : null;
            using (var lg = line.Open())
            using (var ag = area?.Open())
            {
                bool open = false;
                int segStart = 0;
                for (int i = 0; i < Math.Min(n, values.Length); i++)
                {
                    var v = values[i];
                    if (double.IsNaN(v))
                    {
                        if (open)
                        {
                            lg.EndFigure(false);
                            if (ag is not null) { ag.LineTo(new Point(X(i - 1), Y(data.Min))); ag.LineTo(new Point(X(segStart), Y(data.Min))); ag.EndFigure(true); }
                        }
                        open = false;
                        continue;
                    }
                    var p = new Point(X(i), Y(v));
                    if (!open)
                    {
                        lg.BeginFigure(p, false);
                        ag?.BeginFigure(p, true);
                        segStart = i;
                        open = true;
                    }
                    else
                    {
                        lg.LineTo(p);
                        ag?.LineTo(p);
                    }
                }
                if (open)
                {
                    lg.EndFigure(false);
                    var last = Math.Min(n, values.Length) - 1;
                    if (ag is not null) { ag.LineTo(new Point(X(last), Y(data.Min))); ag.LineTo(new Point(X(segStart), Y(data.Min))); ag.EndFigure(true); }
                }
            }
            if (area is not null) ctx.DrawGeometry(FillBrush, null, area);
            ctx.DrawGeometry(null, new Pen(brush, s == 0 ? 2 : 1.6, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), line);
        }

        // Hover readout
        if (_hover >= 0 && _hover < n)
        {
            var x = X(_hover);
            if (LabelBrush is { } lb)
                ctx.DrawLine(new Pen(lb, 1, new DashStyle(new double[] { 3, 3 }, 0)), new Point(x, PadTop), new Point(x, PadTop + h));

            var rows = new List<(string Text, IBrush? Brush)>
            {
                (data.Times[_hover].ToString("ddd HH:mm:ss", CultureInfo.InvariantCulture), null),
            };
            for (int s = 0; s < data.Lines.Count; s++)
            {
                var v = _hover < data.Lines[s].Values.Length ? data.Lines[s].Values[_hover] : double.NaN;
                if (!double.IsNaN(v) && BrushFor(s) is { } b) ctx.DrawEllipse(b, null, new Point(x, Y(v)), 4, 4);
                rows.Add(($"{data.Lines[s].Name}  {(double.IsNaN(v) ? "—" : data.Label(v))}", BrushFor(s)));
            }

            const double lineH = 17, boxW = 150;
            var boxH = rows.Count * lineH + 10;
            var bx = x + 12 + boxW > PadLeft + w ? x - 12 - boxW : x + 12;
            var box = new Rect(bx, PadTop + 4, boxW, boxH);
            ctx.DrawRectangle(PanelBrush, GridBrush is { } g2 ? new Pen(g2, 1) : null, new RoundedRect(box, 8));
            for (int r = 0; r < rows.Count; r++)
            {
                var y = box.Y + 5 + r * lineH;
                if (rows[r].Brush is { } dot) ctx.DrawEllipse(dot, null, new Point(box.X + 12, y + 8), 3.5, 3.5);
                Text(ctx, rows[r].Text, new Point(box.X + (rows[r].Brush is null ? 10 : 22), y), 12,
                     r == 0 ? LabelBrush : TextBrush, bold: r > 0);
            }
        }
    }

    private void Text(DrawingContext ctx, string text, Point at, double size, IBrush? brush, double? alignRightAt = null, bool bold = false)
    {
        if (brush is null) return;
        var face = bold ? new Typeface(_font.FontFamily, FontStyle.Normal, FontWeight.SemiBold) : _font;
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, brush);
        var p = alignRightAt is double right ? new Point(right - ft.Width, at.Y) : at;
        ctx.DrawText(ft, p);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var data = Data;
        if (data is null || data.Times.Length < 2) return;
        var w = Bounds.Width - PadLeft - PadRight;
        var x = e.GetPosition(this).X - PadLeft;
        var index = (int)Math.Round(Math.Clamp(x / w, 0, 1) * (data.Times.Length - 1));
        if (index != _hover)
        {
            _hover = index;
            InvalidateVisual();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = -1;
        InvalidateVisual();
    }
}
