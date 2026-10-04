using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DynamicIsland;

/// <summary>
/// The two triangles of "next track", or of "previous track" turned the other way. A press sends them on by one
/// place: the one in front shrinks away into its tip, the one behind moves up to where it was, and a new one grows
/// out of the back to stand where that one was. They end as they began, having gone the way the playlist does.
/// </summary>
public sealed class Skip : FrameworkElement
{
    const double Wide = 23, High = 15; // the pair, the line around it included
    const double Step = 11;            // from one triangle to the next
    const double Tail = 1, Tip = 11;   // the ends of the one behind: where one grows out of, where one shrinks into
    const double Run = 420;            // ms they take to move on by a place

    static readonly Geometry Triangle = Frozen(Geometry.Parse("M1,1 L11,7.5 L1,14 Z"));
    static readonly Pen Line = Frozen(new Pen(Brushes.White, 2) { LineJoin = PenLineJoin.Round });

    public static readonly DependencyProperty TurnProperty = DependencyProperty.Register(
        nameof(Turn), typeof(double), typeof(Skip),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    int _goal; // places they are to have moved on by, counting from where they stood at rest

    public Skip()
    {
        Width = Wide;
        Height = High;
    }

    /// <summary>They point back: "previous track".</summary>
    public bool Back { get; set; }

    /// <summary>How many places the triangles have moved on by. Every whole number is the same picture.</summary>
    public double Turn
    {
        get => (double)GetValue(TurnProperty);
        set => SetValue(TurnProperty, value);
    }

    /// <summary>Sends them on by one place. Asked again on the way, they go one further from where they are.</summary>
    public void Play()
    {
        // out of sight there is nobody to move for
        if (!IsVisible) return;
        int goal = ++_goal;
        var run = new DoubleAnimation(goal, TimeSpan.FromMilliseconds(Run)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        run.Completed += (_, _) =>
        {
            // another press has taken over
            if (goal != _goal) return;
            // back to none: the picture is the same, and the count starts over
            _goal = 0;
            BeginAnimation(TurnProperty, null);
        };
        BeginAnimation(TurnProperty, run);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double turn = Turn - Math.Floor(Turn);
        if (Back) dc.PushTransform(new ScaleTransform(-1, 1, Wide / 2, 0));
        Draw(dc, 0, turn, Tail);
        Draw(dc, Step * turn, 1, Tail);
        Draw(dc, Step, 1 - turn, Tip);
        if (Back) dc.Pop();
    }

    /// <summary>One triangle, moved on by <paramref name="x"/> from the place behind, at this share of its size about that point of its length.</summary>
    static void Draw(DrawingContext dc, double x, double size, double about)
    {
        if (size < 0.02) return;
        dc.PushTransform(new TranslateTransform(x, 0));
        dc.PushTransform(new ScaleTransform(size, size, about, High / 2));
        // small, it is faint too: it comes out of nothing and goes into nothing
        dc.PushOpacity(Math.Min(size * 2, 1));
        dc.DrawGeometry(Brushes.White, Line, Triangle);
        dc.Pop();
        dc.Pop();
        dc.Pop();
    }

    static T Frozen<T>(T made) where T : Freezable
    {
        if (made.CanFreeze) made.Freeze();
        return made;
    }
}
