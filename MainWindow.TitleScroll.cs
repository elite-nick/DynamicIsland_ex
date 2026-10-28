using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DynamicIsland;

public partial class MainWindow
{
    const double TitleScrollSpeed = 46;
    const double TitleScrollMinRun = 2.0;
    const double TitleScrollPause = 1.25;
    const double TitleScrollTolerance = 1.0;
    const double TitleEndPadding = 12.0;
    const double ToastTitleStartPause = 0.35;
    const double ToastTitleEndPause = 1.15;
    const double ToastTitleSpeed = 78.0;
    const double ToastTitleMinRun = 1.2;

    readonly Dictionary<TextBlock, (string Text, double Width, bool Active)> _titleScrollState = new();

    void SongTitleViewport_SizeChanged(object sender, SizeChangedEventArgs e) => RefreshSongTitleScroll();

    void RefreshSongTitleScroll()
    {
        RefreshTitle(TitleBigViewport, TitleBig, TitleBigMarquee, TitleBigShift, _view == View.MediaBig);
        RefreshTitle(ToastTitleViewport, ToastTitle, ToastTitleMarquee, ToastTitleShift, _view == View.Toast);
    }

    static double TitleViewportWidth(FrameworkElement viewport) =>
        viewport.ActualWidth > 0 ? viewport.ActualWidth : viewport.Width;

    static double MeasureFullTitle(TextBlock text, string title)
    {
        var face = new Typeface(text.FontFamily, text.FontStyle,
            text.FontWeight, text.FontStretch);
        var measured = new FormattedText(title, CultureInfo.CurrentUICulture,
            text.FlowDirection, face, text.FontSize, text.Foreground,
            VisualTreeHelper.GetDpi(text).PixelsPerDip);
        return measured.WidthIncludingTrailingWhitespace;
    }

    double ToastTitleDisplaySeconds()
    {
        if (!Settings.SongTitleAutoScroll || string.IsNullOrWhiteSpace(ToastTitle.Text))
            return ToastSeconds;

        double viewportWidth = TitleViewportWidth(ToastTitleViewport);
        if (viewportWidth <= 0 || !double.IsFinite(viewportWidth)) return ToastSeconds;
        double overflow = MeasureFullTitle(ToastTitleMarquee, ToastTitle.Text) - viewportWidth;
        if (!double.IsFinite(overflow) || overflow <= TitleScrollTolerance) return ToastSeconds;
        double travel = overflow + TitleEndPadding;
        return Math.Max(ToastSeconds, ToastTitleStartPause
            + Math.Max(ToastTitleMinRun, travel / ToastTitleSpeed)
            + ToastTitleEndPause + 0.75);
    }

    void RefreshTitle(Canvas viewport, TextBlock fallback, TextBlock scrolling, TranslateTransform shift, bool active)
    {
        bool enabled = active && Settings.SongTitleAutoScroll;
        double viewportWidth = TitleViewportWidth(viewport);
        var state = (fallback.Text, viewportWidth, enabled);
        if (_titleScrollState.TryGetValue(scrolling, out var previous) && previous == state) return;
        _titleScrollState[scrolling] = state;

        shift.BeginAnimation(TranslateTransform.XProperty, null);
        shift.X = 0;
        scrolling.Text = fallback.Text;
        scrolling.ClearValue(FrameworkElement.WidthProperty);
        scrolling.Visibility = Visibility.Collapsed;
        fallback.Visibility = Visibility.Visible;
        if (!enabled || string.IsNullOrWhiteSpace(fallback.Text)
            || viewportWidth <= 0 || !double.IsFinite(viewportWidth)) return;

        scrolling.Visibility = Visibility.Hidden;
        scrolling.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double textWidth = scrolling.DesiredSize.Width;
        double overflow = textWidth - viewportWidth;
        if (!double.IsFinite(overflow) || overflow <= TitleScrollTolerance)
        {
            scrolling.Visibility = Visibility.Collapsed;
            return;
        }

        double travel = overflow + TitleEndPadding;
        scrolling.Visibility = Visibility.Visible;
        fallback.Visibility = Visibility.Collapsed;
        shift.BeginAnimation(TranslateTransform.XProperty, active && _view == View.Toast
            ? CreateToastTitleScroll(travel)
            : CreateTitleScroll(travel));
    }

    static DoubleAnimationUsingKeyFrames CreateToastTitleScroll(double travel)
    {
        double run = Math.Max(ToastTitleMinRun, travel / ToastTitleSpeed);
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        var animation = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.HoldEnd };
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(0,
            KeyTime.FromTimeSpan(TimeSpan.FromSeconds(ToastTitleStartPause))));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(-travel,
            KeyTime.FromTimeSpan(TimeSpan.FromSeconds(ToastTitleStartPause + run)), ease));
        return animation;
    }

    static DoubleAnimationUsingKeyFrames CreateTitleScroll(double overflow, double start = 0)
    {
        double run = Math.Max(TitleScrollMinRun, overflow / TitleScrollSpeed);
        TimeSpan t0 = TimeSpan.Zero;
        TimeSpan t1 = TimeSpan.FromSeconds(TitleScrollPause);
        TimeSpan t2 = TimeSpan.FromSeconds(TitleScrollPause + run);
        TimeSpan t3 = TimeSpan.FromSeconds(2 * TitleScrollPause + run);
        TimeSpan t4 = TimeSpan.FromSeconds(2 * TitleScrollPause + 2 * run);
        TimeSpan t5 = TimeSpan.FromSeconds(3 * TitleScrollPause + 2 * run);
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        var animation = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(start, KeyTime.FromTimeSpan(t0)));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(start, KeyTime.FromTimeSpan(t1)));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(start - overflow, KeyTime.FromTimeSpan(t2), ease));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(start - overflow, KeyTime.FromTimeSpan(t3)));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(start, KeyTime.FromTimeSpan(t4), ease));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(start, KeyTime.FromTimeSpan(t5)));
        return animation;
    }
}
