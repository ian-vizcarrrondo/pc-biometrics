using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using Pulse.Core;

namespace Pulse.App.Controls;

/// <summary>
/// Time-axis chart. Points are placed by their real timestamps between <see cref="From"/> and <see cref="To"/>, so
/// irregular sampling, pauses and gaps are visible. Missing readings break the line instead of drawing zero.
/// A hover cursor and a keyboard cursor (Left/Right/Home/End) show the value at a time.
/// </summary>
public sealed class TimeChart : FrameworkElement
{
    public static readonly DependencyProperty SeriesProperty = Register<IReadOnlyList<HistoryPoint>?>(nameof(Series), null);
    public static readonly DependencyProperty Series2Property = Register<IReadOnlyList<HistoryPoint>?>(nameof(Series2), null);
    public static readonly DependencyProperty FromProperty = Register(nameof(From), DateTime.MinValue);
    public static readonly DependencyProperty ToProperty = Register(nameof(To), DateTime.MinValue);
    public static readonly DependencyProperty StrokeProperty = Register(nameof(Stroke), Colors.White);
    public static readonly DependencyProperty Stroke2Property = Register(nameof(Stroke2), Colors.Gray);
    public static readonly DependencyProperty FixedMaxProperty = Register(nameof(FixedMax), double.NaN);
    public static readonly DependencyProperty UnitProperty = Register(nameof(Unit), "%");
    public static readonly DependencyProperty IsPlaybackProperty = Register(nameof(IsPlayback), false);

    private static DependencyProperty Register<T>(string name, T def) =>
        DependencyProperty.Register(name, typeof(T), typeof(TimeChart),
            new FrameworkPropertyMetadata(def, FrameworkPropertyMetadataOptions.AffectsRender));

    private DateTime? _cursor;
    private IReadOnlyList<ChartPoint>? _a, _b;

    public TimeChart()
    {
        Focusable = true;
        FocusVisualStyle = null;
        Cursor = Cursors.Cross;
        AutomationProperties.SetName(this, "Time chart");
    }

    public IReadOnlyList<HistoryPoint>? Series { get => (IReadOnlyList<HistoryPoint>?)GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }
    public IReadOnlyList<HistoryPoint>? Series2 { get => (IReadOnlyList<HistoryPoint>?)GetValue(Series2Property); set => SetValue(Series2Property, value); }
    public DateTime From { get => (DateTime)GetValue(FromProperty); set => SetValue(FromProperty, value); }
    public DateTime To { get => (DateTime)GetValue(ToProperty); set => SetValue(ToProperty, value); }
    public Color Stroke { get => (Color)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public Color Stroke2 { get => (Color)GetValue(Stroke2Property); set => SetValue(Stroke2Property, value); }
    public double FixedMax { get => (double)GetValue(FixedMaxProperty); set => SetValue(FixedMaxProperty, value); }
    /// <summary>"%", "°C" or "B/s"; selects value formatting.</summary>
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    public bool IsPlayback { get => (bool)GetValue(IsPlaybackProperty); set => SetValue(IsPlaybackProperty, value); }

    /// <summary>Raised when the cursor moves; arguments are the cursor time and the value of the primary series at that time.</summary>
    public event Action<DateTime?, double?>? CursorMoved;

    private double PlotWidth => Math.Max(1, ActualWidth);

    private DateTime? TimeAt(double x)
    {
        if (To <= From) return null;
        var f = Math.Clamp(x / PlotWidth, 0, 1);
        return From + TimeSpan.FromTicks((long)((To - From).Ticks * f));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        SetCursor(TimeAt(e.GetPosition(this).X));
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (!IsKeyboardFocused) SetCursor(null);
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        if (!IsMouseOver) SetCursor(null);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (To <= From) return;
        var step = TimeSpan.FromTicks((To - From).Ticks / 60);
        var current = _cursor ?? To;
        DateTime? next = e.Key switch
        {
            Key.Left => current - step,
            Key.Right => current + step,
            Key.Home => From,
            Key.End => To,
            Key.Escape => null,
            _ => current,
        };
        if (e.Key is not (Key.Left or Key.Right or Key.Home or Key.End or Key.Escape)) return;
        SetCursor(next is { } n ? (n < From ? From : n > To ? To : n) : null);
        e.Handled = true;
    }

    private void SetCursor(DateTime? t)
    {
        _cursor = t;
        var (v, _) = t is null ? (null, default(DateTime)) : Nearest(Series, t.Value);
        CursorMoved?.Invoke(t, v);
        InvalidateVisual();
    }

    private (double? Value, DateTime Time) Nearest(IReadOnlyList<HistoryPoint>? s, DateTime t)
    {
        if (s is null || s.Count == 0) return (null, t);
        var tolerance = TimeSpan.FromTicks(Math.Max((To - From).Ticks / 40, TimeSpan.TicksPerSecond * 3));
        HistoryPoint? best = null;
        var bestDist = TimeSpan.MaxValue;
        foreach (var p in s)
        {
            if (p.Value is null) continue;
            var d = (p.TimeUtc - t).Duration();
            if (d < bestDist) { bestDist = d; best = p; }
        }
        return best is { } b && bestDist <= tolerance ? (b.Value, b.TimeUtc) : (null, t);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 4 || h < 4) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        var grid = new Pen(new SolidColorBrush(Color.FromArgb(0x22, 255, 255, 255)), 1) { DashStyle = DashStyles.Dot };
        grid.Freeze();
        for (var i = 1; i <= 3; i++)
        {
            var y = Math.Round(h * i / 4) + 0.5;
            dc.DrawLine(grid, new Point(0, y), new Point(w, y));
        }

        if (To <= From) return;
        var buckets = Math.Max(8, (int)(w / 2));
        _a = Series is null ? null : SeriesMath.Downsample(Series, From, To, buckets);
        _b = Series2 is null ? null : SeriesMath.Downsample(Series2, From, To, buckets);

        var max = double.IsNaN(FixedMax) ? NiceCeiling(Math.Max(Peak(_a), Peak(_b))) : FixedMax;
        if (_b is not null) DrawSeries(dc, _b, Stroke2, w, h, max);
        if (_a is not null) DrawSeries(dc, _a, Stroke, w, h, max);

        if (_a is { Count: 0 } && (_b is null || _b.Count == 0))
            Label(dc, IsPlayback ? "No recorded data in this range" : "No valid data in this window", w / 2, h / 2 - 7, Color.FromRgb(0x71, 0x81, 0x93), true);

        if (_cursor is { } c)
        {
            var x = (c - From).TotalSeconds / (To - From).TotalSeconds * w;
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(0xAA, 255, 255, 255)), 1);
            dc.DrawLine(pen, new Point(x + 0.5, 0), new Point(x + 0.5, h));
            var (v, vt) = Nearest(Series, c);
            var time = (v is null ? c : vt).ToLocalTime().ToString(To - From <= TimeSpan.FromHours(2) ? "HH:mm:ss" : "HH:mm", CultureInfo.CurrentCulture);
            var text = v is { } val ? $"{time}  {FormatValue(val)}" : $"{time}  no data";
            Label(dc, text, Math.Clamp(x, 60, Math.Max(60, w - 60)), 2, Colors.White, true, background: true);
        }
    }

    public string FormatValue(double v) => Unit switch
    {
        "B/s" => Format.Rate(v),
        "°C" => v.ToString("0.#", CultureInfo.CurrentCulture) + " °C",
        _ => v.ToString("0.#", CultureInfo.CurrentCulture) + " %",
    };

    private void Label(DrawingContext dc, string text, double x, double y, Color color, bool centered, bool background = false)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10,
            new SolidColorBrush(color), VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var origin = new Point(centered ? x - ft.Width / 2 : x, y);
        if (background)
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0xE0, 0x0B, 0x10, 0x17)), null,
                new Rect(origin.X - 5, origin.Y - 1, ft.Width + 10, ft.Height + 2), 4, 4);
        dc.DrawText(ft, origin);
    }

    private static double Peak(IReadOnlyList<ChartPoint>? s)
    {
        var m = 0.0;
        if (s is null) return m;
        foreach (var p in s) if (p.Value > m) m = p.Value;
        return m;
    }

    internal static double NiceCeiling(double peak)
    {
        if (peak <= 0) return 1;
        var floor = Math.Max(peak, 10 * 1024);
        var mag = Math.Pow(10, Math.Floor(Math.Log10(floor)));
        foreach (var m in new[] { 1.0, 2.0, 5.0, 10.0 })
            if (floor <= m * mag) return m * mag;
        return floor;
    }

    private void DrawSeries(DrawingContext dc, IReadOnlyList<ChartPoint> s, Color color, double w, double h, double max)
    {
        if (s.Count == 0 || max <= 0) return;
        const double pad = 3;
        var span = (To - From).TotalSeconds;
        Point P(ChartPoint p) => new((p.TimeUtc - From).TotalSeconds / span * w,
            h - pad - Math.Clamp(p.Value / max, 0, 1) * (h - 2 * pad));

        var line = new StreamGeometry();
        var area = new StreamGeometry();
        var dots = new List<Point>();
        using (var lc = line.Open())
        using (var ac = area.Open())
        {
            var i = 0;
            while (i < s.Count)
            {
                var j = i + 1;
                while (j < s.Count && !s[j].StartsSegment) j++;
                var count = j - i;
                var first = P(s[i]);
                if (count == 1) dots.Add(first);
                else
                {
                    lc.BeginFigure(first, false, false);
                    ac.BeginFigure(new Point(first.X, h), true, true);
                    ac.LineTo(first, false, false);
                    for (var k = i + 1; k < j; k++)
                    {
                        var pt = P(s[k]);
                        lc.LineTo(pt, true, false);
                        ac.LineTo(pt, false, false);
                    }
                    ac.LineTo(new Point(P(s[j - 1]).X, h), false, false);
                }
                i = j;
            }
        }
        line.Freeze();
        area.Freeze();

        var brush = new LinearGradientBrush(Color.FromArgb(0x44, color.R, color.G, color.B), Color.FromArgb(0x00, color.R, color.G, color.B), 90);
        brush.Freeze();
        dc.DrawGeometry(brush, null, area);
        var pen = new Pen(new SolidColorBrush(color), 1.6) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, line);
        foreach (var d in dots) dc.DrawEllipse(new SolidColorBrush(color), null, d, 1.8, 1.8);
    }
}
