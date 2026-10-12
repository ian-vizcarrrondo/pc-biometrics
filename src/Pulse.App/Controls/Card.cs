using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Pulse.App.Controls;

/// <summary>Glass-style card with a title, accent dot and a soft static glow. No effects or animation.</summary>
public sealed class Card : HeaderedContentControl
{
    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Color), typeof(Card), new PropertyMetadata(Colors.White, OnAccentChanged));

    public static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(
        nameof(AccentBrush), typeof(Brush), typeof(Card));

    public static readonly DependencyProperty GlowBrushProperty = DependencyProperty.Register(
        nameof(GlowBrush), typeof(Brush), typeof(Card));

    static Card() => DefaultStyleKeyProperty.OverrideMetadata(typeof(Card), new FrameworkPropertyMetadata(typeof(Card)));

    public Card() => Rebuild(Colors.White);

    public Color Accent { get => (Color)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public Brush AccentBrush { get => (Brush)GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    public Brush GlowBrush { get => (Brush)GetValue(GlowBrushProperty); set => SetValue(GlowBrushProperty, value); }

    private static void OnAccentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((Card)d).Rebuild((Color)e.NewValue);

    private void Rebuild(Color c)
    {
        var solid = new SolidColorBrush(c);
        solid.Freeze();
        var glow = new RadialGradientBrush
        {
            Center = new Point(1, 0),
            GradientOrigin = new Point(1, 0),
            RadiusX = 0.9,
            RadiusY = 0.9,
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x30, c.R, c.G, c.B), 0),
                new GradientStop(Color.FromArgb(0x00, c.R, c.G, c.B), 1),
            }
        };
        glow.Freeze();
        AccentBrush = solid;
        GlowBrush = glow;
    }
}
