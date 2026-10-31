using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace AVASerMonitor;

/// <summary>One line on the plot: its name, colour, whether it shows, and its points.</summary>
public sealed class PlotSeries(string name, Color colour)
{
    public string Name { get; } = name;
    public Color Colour { get; } = colour;
    public bool Visible { get; set; } = true;
    public double[] X { get; set; } = [];
    public double[] Y { get; set; } = [];
}

/// <summary>
/// The live plot: every visible series as a line, scaled to fit, with gridlines, engineering-notation tick labels, a
/// legend, and a crosshair that reads off each series under the pointer. Drawn by the control itself (lines thinned to
/// the most and least in each pixel column, so tens of thousands of points stay quick), so nothing is shared with
/// another thread while it draws.
/// </summary>
public sealed class PlotView : Control
{
    private static readonly Color[] Palette =
    [
        Color.Parse("#4EF0A0"), Color.Parse("#4CC9F0"), Color.Parse("#FFB347"), Color.Parse("#F72585"),
        Color.Parse("#B388EB"), Color.Parse("#FF6B6B"), Color.Parse("#FFE66D"), Color.Parse("#80ED99"),
    ];

    private readonly List<PlotSeries> _series = [];
    private Point? _pointer;

    private static readonly IBrush Back = new SolidColorBrush(Color.Parse("#0B1219"));
    private static readonly IPen Frame = new Pen(new SolidColorBrush(Color.Parse("#2E4A5F")), 1);
    private static readonly IPen Grid = new Pen(new SolidColorBrush(Color.Parse("#1A2A38")), 1);
    private static readonly IPen CursorPen = new Pen(new SolidColorBrush(Color.Parse("#6F8BA3")), 1, new DashStyle([3, 3], 0));
    private static readonly IBrush Label = new SolidColorBrush(Color.Parse("#8FA3B8"));
    private static readonly IBrush Readout = new SolidColorBrush(Color.Parse("#E4EDF6"));
    private static readonly IBrush Panel = new SolidColorBrush(Color.Parse("#E0101820"));
    private static readonly Typeface Face = new("Inter");

    public IReadOnlyList<PlotSeries> Series => _series;

    /// <summary>What the x axis counts: "sample" or "s".</summary>
    public string XUnit { get; set; } = "sample";

    /// <summary>A note shown in the middle when there is nothing to plot.</summary>
    public string Empty { get; set; } = "Values the device prints show here, as lines: \"12, 30\" or \"temp:21.5 humidity:40\".";

    public PlotView()
    {
        ClipToBounds = true;
        Cursor = new Avalonia.Input.Cursor(StandardCursorType.Cross);
    }

    /// <summary>The series called <paramref name="name"/>, made (with the next colour) if new.</summary>
    public PlotSeries Get(string name)
    {
        var s = _series.FirstOrDefault(x => x.Name == name);
        if (s != null) return s;
        s = new PlotSeries(name, Palette[_series.Count % Palette.Length]);
        _series.Add(s);
        return s;
    }

    public void Clear()
    {
        _series.Clear();
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _pointer = e.GetPosition(this);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _pointer = null;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Back, bounds);
        var area = new Rect(64, 12, Math.Max(10, bounds.Width - 76), Math.Max(10, bounds.Height - 40));
        var shown = _series.Where(s => s.Visible && s.X.Length > 0).ToList();
        if (shown.Count == 0)
        {
            context.DrawRectangle(null, Frame, area);
            var note = Text(Empty, 13, Label, Math.Max(100, area.Width - 40));
            context.DrawText(note, new Point(area.Center.X - note.Width / 2, area.Center.Y - note.Height / 2));
            return;
        }

        double x0 = shown.Min(s => s.X[0]), x1 = shown.Max(s => s.X[^1]);
        if (x1 <= x0) x1 = x0 + 1;
        double y0 = double.MaxValue, y1 = double.MinValue;
        foreach (var s in shown)
            foreach (var v in s.Y)
                if (double.IsFinite(v)) { y0 = Math.Min(y0, v); y1 = Math.Max(y1, v); }
        if (y0 > y1) { y0 = -1; y1 = 1; }
        if (y1 - y0 < 1e-12) { var pad = Math.Abs(y1) > 0 ? Math.Abs(y1) * 0.1 : 1; y0 -= pad; y1 += pad; }
        var margin = (y1 - y0) * 0.06;
        y0 -= margin;
        y1 += margin;
        double X(double v) => area.Left + (v - x0) / (x1 - x0) * area.Width;
        double Y(double v) => area.Bottom - (v - y0) / (y1 - y0) * area.Height;

        foreach (var t in Ticks(y0, y1))
        {
            context.DrawLine(Grid, new Point(area.Left, Y(t)), new Point(area.Right, Y(t)));
            var label = Text(Format(t), 11, Label);
            context.DrawText(label, new Point(area.Left - label.Width - 6, Y(t) - label.Height / 2));
        }
        foreach (var t in Ticks(x0, x1))
        {
            context.DrawLine(Grid, new Point(X(t), area.Top), new Point(X(t), area.Bottom));
            var label = Text(XUnit == "s" ? Format(t) + "s" : Format(t), 11, Label);
            context.DrawText(label, new Point(X(t) - label.Width / 2, area.Bottom + 4));
        }
        context.DrawRectangle(null, Frame, area);

        using (context.PushClip(area))
        {
            foreach (var s in shown) DrawLine(context, s, area, X, Y, x0, x1);
        }

        DrawLegend(context, shown, area);
        if (_pointer is { } p && area.Contains(p)) DrawCursor(context, shown, area, p, X, Y, x0, x1);
    }

    /// <summary>
    /// A series as one polyline, thinned when it has more points than pixels: each pixel column keeps its first, lowest,
    /// highest and last point, so a spike never vanishes.
    /// </summary>
    private static void DrawLine(DrawingContext context, PlotSeries s, Rect area, Func<double, double> X, Func<double, double> Y, double x0, double x1)
    {
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            var started = false;
            var column = int.MinValue;
            double first = 0, low = 0, high = 0, last = 0;
            var any = false;
            void Emit(int col)
            {
                if (!any) return;
                var px = area.Left + col;
                foreach (var v in new[] { first, low, high, last }.Distinct())
                {
                    var pt = new Point(px, Y(v));
                    if (!started) { g.BeginFigure(pt, false); started = true; }
                    else g.LineTo(pt);
                }
                any = false;
            }
            var dense = s.X.Length > area.Width * 2;
            for (var i = 0; i < s.X.Length; i++)
            {
                var y = s.Y[i];
                if (!double.IsFinite(y)) continue;
                if (!dense)
                {
                    var pt = new Point(X(s.X[i]), Y(y));
                    if (!started) { g.BeginFigure(pt, false); started = true; }
                    else g.LineTo(pt);
                    continue;
                }
                var col = (int)((s.X[i] - x0) / (x1 - x0) * area.Width);
                if (col != column) { Emit(column); column = col; first = low = high = last = y; any = true; }
                else { low = Math.Min(low, y); high = Math.Max(high, y); last = y; }
            }
            if (dense) Emit(column);
            if (started) g.EndFigure(false);
        }
        context.DrawGeometry(null, new Pen(new SolidColorBrush(s.Colour), 1.6, lineJoin: PenLineJoin.Round), geometry);
    }

    private static void DrawLegend(DrawingContext context, List<PlotSeries> shown, Rect area)
    {
        var rows = shown.Select(s => (s, Text(s.Name, 12, Readout))).ToList();
        var width = rows.Max(r => r.Item2.Width) + 34;
        var box = new Rect(area.Right - width - 8, area.Top + 8, width, rows.Count * 18 + 8);
        context.FillRectangle(Panel, box);
        context.DrawRectangle(null, Frame, box);
        for (var i = 0; i < rows.Count; i++)
        {
            var y = box.Top + 6 + i * 18;
            context.DrawLine(new Pen(new SolidColorBrush(rows[i].s.Colour), 3), new Point(box.Left + 8, y + 8), new Point(box.Left + 24, y + 8));
            context.DrawText(rows[i].Item2, new Point(box.Left + 30, y));
        }
    }

    /// <summary>A vertical line at the pointer, and each series' value nearest it.</summary>
    private void DrawCursor(DrawingContext context, List<PlotSeries> shown, Rect area, Point p, Func<double, double> X, Func<double, double> Y, double x0, double x1)
    {
        context.DrawLine(CursorPen, new Point(p.X, area.Top), new Point(p.X, area.Bottom));
        var at = x0 + (p.X - area.Left) / area.Width * (x1 - x0);
        var lines = new List<(Color, string)> { (Colors.Transparent, XUnit == "s" ? $"t = {Format(at)}s" : $"sample {at:0}") };
        foreach (var s in shown)
        {
            var i = Array.BinarySearch(s.X, at);
            if (i < 0) i = Math.Clamp(~i, 0, s.X.Length - 1);
            if (i > 0 && Math.Abs(s.X[i - 1] - at) < Math.Abs(s.X[i] - at)) i--;
            lines.Add((s.Colour, $"{s.Name}  {s.Y[i].ToString("G6", CultureInfo.InvariantCulture)}"));
            context.DrawEllipse(new SolidColorBrush(s.Colour), null, new Point(X(s.X[i]), Y(s.Y[i])), 3.5, 3.5);
        }
        var texts = lines.Select(l => (l.Item1, Text(l.Item2, 12, Readout))).ToList();
        var w = texts.Max(t => t.Item2.Width) + 26;
        var left = p.X + 12 + w > area.Right ? p.X - 12 - w : p.X + 12;
        var box = new Rect(left, area.Top + 8, w, texts.Count * 18 + 8);
        context.FillRectangle(Panel, box);
        context.DrawRectangle(null, Frame, box);
        for (var i = 0; i < texts.Count; i++)
        {
            var y = box.Top + 4 + i * 18;
            if (texts[i].Item1.A > 0) context.FillRectangle(new SolidColorBrush(texts[i].Item1), new Rect(box.Left + 7, y + 5, 8, 8));
            context.DrawText(texts[i].Item2, new Point(box.Left + 20, y));
        }
    }

    private static FormattedText Text(string text, double size, IBrush brush, double maxWidth = double.PositiveInfinity)
    {
        var t = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, brush);
        if (double.IsFinite(maxWidth)) t.MaxTextWidth = maxWidth;
        t.TextAlignment = double.IsFinite(maxWidth) ? TextAlignment.Center : TextAlignment.Left;
        return t;
    }

    /// <summary>Round tick values: steps of 1, 2 or 5 times a power of ten, about six across.</summary>
    internal static List<double> Ticks(double min, double max)
    {
        var span = max - min;
        if (!(span > 0) || !double.IsFinite(span)) return [];
        var rough = span / 6;
        var power = Math.Pow(10, Math.Floor(Math.Log10(rough)));
        var step = new[] { 1.0, 2, 5, 10 }.Select(m => m * power).First(s => s >= rough);
        var ticks = new List<double>();
        // Each tick a whole number of steps, rounded, so 0.6 is 0.6 and not 0.6000000000000001.
        for (var k = Math.Ceiling(min / step); k * step <= max + step * 1e-9 && ticks.Count < 50; k++)
            ticks.Add(k == 0 ? 0 : Math.Round(k * step, 12));
        return ticks;
    }

    /// <summary>A number in engineering notation: 1.5k, 250m, 12.</summary>
    internal static string Format(double v)
    {
        if (v == 0 || !double.IsFinite(v)) return "0";
        var a = Math.Abs(v);
        (double scale, string suffix) = a >= 1e9 ? (1e9, "G") : a >= 1e6 ? (1e6, "M") : a >= 1e3 ? (1e3, "k") : a >= 0.1 ? (1, "") : a >= 1e-3 ? (1e-3, "m") : a >= 1e-6 ? (1e-6, "µ") : (1e-9, "n");
        return (v / scale).ToString("0.###", CultureInfo.InvariantCulture) + suffix;
    }
}
