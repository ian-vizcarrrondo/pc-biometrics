using System.Windows;
using System.Windows.Media;
using Pulse.Core;

namespace Pulse.App.Controls;

/// <summary>
/// Smooth area/line chart drawn only when invalidated (once per refresh while visible).
/// Up to two series share one scale; new samples enter from the right.
/// </summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = Register<BoundedHistory<double>?>(nameof(Values), null);
    public static readonly DependencyProperty Values2Property = Register<BoundedHistory<double>?>(nameof(Values2), null);
    public static readonly DependencyProperty StrokeProperty = Register<Color>(nameof(Stroke), Colors.White);
    public static readonly DependencyProperty Stroke2Property = Register<Color>(nameof(Stroke2), Colors.Gray);
    public static readonly DependencyProperty FixedMaxProperty = Register(nameof(FixedMax), double.NaN);

    private static DependencyProperty Register<T>(string name, T def) =>
        DependencyProperty.Register(name, typeof(T), typeof(Sparkline),
            new FrameworkPropertyMetadata(def, FrameworkPropertyMetadataOptions.AffectsRender));

    public BoundedHistory<double>? Values { get => (BoundedHistory<double>?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public BoundedHistory<double>? Values2 { get => (BoundedHistory<double>?)GetValue(Values2Property); set => SetValue(Values2Property, value); }
    public Color Stroke { get => (Color)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public Color Stroke2 { get => (Color)GetValue(Stroke2Property); set => SetValue(Stroke2Property, value); }
    public double FixedMax { get => (double)GetValue(FixedMaxProperty); set => SetValue(FixedMaxProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 4 || h < 4) return;

        var grid = new Pen(new SolidColorBrush(Color.FromArgb(0x22, 255, 255, 255)), 1) { DashStyle = DashStyles.Dot };
        grid.Freeze();
        for (var i = 1; i <= 3; i++)
        {
            var y = Math.Round(h * i / 4) + 0.5;
            dc.DrawLine(grid, new Point(0, y), new Point(w, y));
        }

        var a = Values;
        var b = Values2;
        var max = double.IsNaN(FixedMax) ? NiceCeiling(Math.Max(Peak(a), Peak(b))) : FixedMax;
        if (b is not null) DrawSeries(dc, b, Stroke2, w, h, max, fill: true);
        if (a is not null) DrawSeries(dc, a, Stroke, w, h, max, fill: true);
    }

    private static double Peak(BoundedHistory<double>? s)
    {
        var m = 0.0;
        if (s is null) return m;
        foreach (var v in s) if (v > m) m = v;
        return m;
    }

    /// <summary>Rounds a peak up to 1, 2 or 5 times a power of ten so the scale does not jitter.</summary>
    internal static double NiceCeiling(double peak)
    {
        if (peak <= 0) return 1;
        var floor = Math.Max(peak, 10 * 1024);
        var mag = Math.Pow(10, Math.Floor(Math.Log10(floor)));
        foreach (var m in new[] { 1.0, 2.0, 5.0, 10.0 })
            if (floor <= m * mag) return m * mag;
        return floor;
    }

    private static void DrawSeries(DrawingContext dc, BoundedHistory<double> s, Color color, double w, double h, double max, bool fill)
    {
        var n = s.Count;
        if (n < 2 || max <= 0) return;

        var step = w / (s.Capacity - 1);
        var x0 = w - (n - 1) * step;
        const double pad = 3;
        var pts = new Point[n];
        for (var i = 0; i < n; i++)
        {
            var v = Math.Clamp(s[i] / max, 0, 1);
            pts[i] = new Point(x0 + i * step, h - pad - v * (h - 2 * pad));
        }

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(pts[0], false, false);
            for (var i = 0; i < n - 1; i++)
            {
                var p0 = pts[Math.Max(i - 1, 0)];
                var p1 = pts[i];
                var p2 = pts[i + 1];
                var p3 = pts[Math.Min(i + 2, n - 1)];
                var c1 = new Point(p1.X + (p2.X - p0.X) / 6, Math.Clamp(p1.Y + (p2.Y - p0.Y) / 6, 0, h));
                var c2 = new Point(p2.X - (p3.X - p1.X) / 6, Math.Clamp(p2.Y - (p3.Y - p1.Y) / 6, 0, h));
                ctx.BezierTo(c1, c2, p2, true, true);
            }
        }
        line.Freeze();

        if (fill)
        {
            var area = new StreamGeometry();
            using (var ctx = area.Open())
            {
                ctx.BeginFigure(new Point(pts[0].X, h), true, true);
                ctx.LineTo(pts[0], false, false);
                for (var i = 0; i < n - 1; i++)
                {
                    var p0 = pts[Math.Max(i - 1, 0)];
                    var p1 = pts[i];
                    var p2 = pts[i + 1];
                    var p3 = pts[Math.Min(i + 2, n - 1)];
                    ctx.BezierTo(
                        new Point(p1.X + (p2.X - p0.X) / 6, Math.Clamp(p1.Y + (p2.Y - p0.Y) / 6, 0, h)),
                        new Point(p2.X - (p3.X - p1.X) / 6, Math.Clamp(p2.Y - (p3.Y - p1.Y) / 6, 0, h)),
                        p2, false, true);
                }
                ctx.LineTo(new Point(pts[n - 1].X, h), false, false);
            }
            area.Freeze();
            var brush = new LinearGradientBrush(Color.FromArgb(0x55, color.R, color.G, color.B),
                Color.FromArgb(0x00, color.R, color.G, color.B), 90);
            brush.Freeze();
            dc.DrawGeometry(brush, null, area);
        }

        var pen = new Pen(new SolidColorBrush(color), 1.8) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, line);

        dc.DrawEllipse(new SolidColorBrush(color), null, pts[n - 1], 2.8, 2.8);
    }
}
