using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using static DynamicIsland.Motion;

namespace DynamicIsland;

public sealed class Toggle : FrameworkElement
{
    const double KnobInset = 2;

    static readonly Color OffColor = Color.FromRgb(0x39, 0x39, 0x3D);
    static readonly Color OnColor = Color.FromRgb(0x30, 0xD1, 0x58);

    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(Toggle),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public void Set(bool on, bool animate) =>
        BeginAnimation(ProgressProperty, new DoubleAnimation(on ? 1 : 0, Ms(animate ? 220 : 0))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        double share = Math.Clamp(Progress, 0, 1);
        var track = new SolidColorBrush(Color.FromRgb(
            Mix(OffColor.R, OnColor.R, share), Mix(OffColor.G, OnColor.G, share), Mix(OffColor.B, OnColor.B, share)));
        dc.DrawRoundedRectangle(track, null, new Rect(0, 0, w, h), h / 2, h / 2);
        dc.DrawEllipse(Brushes.White, null, new Point(h / 2 + (w - h) * share, h / 2), h / 2 - KnobInset, h / 2 - KnobInset);
    }

    static byte Mix(byte from, byte to, double share) => (byte)Math.Round(from + (to - from) * share);
}
