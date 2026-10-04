using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DynamicIsland;

/// <summary>
/// The island's black body. The pill and the bubble that splits off it are one piece of liquid: while their
/// round ends are close a neck joins them, thinning as they part until it snaps. Its light edge is also how the
/// island says things without words: it glows on the bass of the music, and a glint runs round it when something
/// happens.
/// </summary>
public sealed class Goo : FrameworkElement
{
    const double Rim = 1;          // the light edge around the body
    const double Tear = 7.5;       // gap between the two round ends at which the neck snaps
    const double Hold = 0.5;       // how far round each end the neck reaches while it is thick
    const double Handle = 2.4;     // how long the neck's curves keep to the direction they leave an end in
    const double Tolerance = 0.02; // of the merged outline

    static readonly Color Plain = Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF);
    const byte Tinted = 0x8C;      // alpha of the edge while it takes a colour: one hue needs more than white to show

    // light the edge gives off, on a beat or where the glint is: a haze to either side of it, laid on in strokes
    // each reaching a px further than the one before, so it thins out away from the edge
    static readonly double[] Reach = [2, 3, 4];
    const double BeatHaze = 0.14;  // how bright each of those strokes is on a beat, next to the edge itself

    const double Tail = 0.3;       // length of the glint, as a share of the outline
    const int Samples = 48;        // points the glint is drawn through
    const int Layers = 10;         // strokes it is laid on in, each starting nearer its head: together they brighten towards it
    const double Spark = 0.2, Flare = 0.025; // how bright each of them is: along the edge itself, and in the haze around it
    const double Arrive = 0.1, Depart = 0.18; // shares of its lap over which the glint comes up and dies away
    static readonly TimeSpan Once = TimeSpan.FromSeconds(1.5), Round = TimeSpan.FromSeconds(1.7); // a single lap; each of many
    static readonly TimeSpan Wait = TimeSpan.FromMilliseconds(160); // for the island to take the shape of what it has to say

    /// <summary>How far round the outline the glint has got, in laps: from the middle of the top edge, clockwise.</summary>
    public static readonly DependencyProperty LapProperty = DependencyProperty.Register(
        nameof(Lap), typeof(double), typeof(Goo),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    readonly SolidColorBrush _rim = new(Plain);
    readonly SolidColorBrush _beat = new(Colors.White) { Opacity = 0 }, _beatHaze = new(Colors.White) { Opacity = 0 };
    readonly SolidColorBrush _spark = new(Colors.White) { Opacity = Spark }, _flare = new(Colors.White) { Opacity = Flare };
    readonly Pen _edge, _beatEdge, _sparkEdge;
    readonly Pen[] _beatHazes, _flares;

    Rect _pill = Rect.Empty, _bubble = Rect.Empty;
    double _radius;
    bool _again; // the glint goes round and round

    public Goo()
    {
        _edge = new Pen(_rim, 2 * Rim) { LineJoin = PenLineJoin.Round };
        _beatEdge = new Pen(_beat, 2 * Rim) { LineJoin = PenLineJoin.Round };
        _beatHazes = Reach.Select(reach => new Pen(_beatHaze, 2 * reach) { LineJoin = PenLineJoin.Round }).ToArray();
        _sparkEdge = Trail(_spark, Rim);
        _flares = Reach.Select(reach => Trail(_flare, reach)).ToArray();

        static Pen Trail(Brush brush, double reach) => new(brush, 2 * reach)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
    }

    public double Lap
    {
        get => (double)GetValue(LapProperty);
        set => SetValue(LapProperty, value);
    }

    /// <summary>Turns the light edge to a colour, or back to its own faint white with null.</summary>
    public void Tint(Color? colour, Duration time)
    {
        Color to = colour is { } c ? Color.FromArgb(Tinted, c.R, c.G, c.B) : Plain;
        // the brushes are already in the picture, so nothing is drawn again
        _rim.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(to, time));
        // the beat lights the edge in the colour it has
        var lit = new ColorAnimation(colour ?? Colors.White, time);
        _beat.BeginAnimation(SolidColorBrush.ColorProperty, lit);
        _beatHaze.BeginAnimation(SolidColorBrush.ColorProperty, lit);
    }

    /// <summary>Lights the edge up by this much, 0 → 1: asked on every frame of the music, with the weight of its bass.</summary>
    public void Beat(double level)
    {
        level = Math.Clamp(level, 0, 1);
        _beat.Opacity = level;
        _beatHaze.Opacity = level * BeatHaze;
    }

    /// <summary>Sends a glint of light round the edge: once, or round and round until it is told to <see cref="Still"/>.</summary>
    public void Glint(Color colour, bool again = false)
    {
        _again = again;
        // the edge itself burns nearly white, the haze around it keeps the colour
        _spark.Color = Color.FromRgb(Whiter(colour.R), Whiter(colour.G), Whiter(colour.B));
        _flare.Color = colour;

        // the value falls back to none once it is over, and with it the glint is gone
        var run = new DoubleAnimation(0, 1, Once) { BeginTime = Wait, FillBehavior = FillBehavior.Stop };
        if (again)
        {
            run.Duration = Round;
            run.IsCumulative = true;
            run.RepeatBehavior = RepeatBehavior.Forever;
        }
        else run.EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut };
        BeginAnimation(LapProperty, run);

        static byte Whiter(byte part) => (byte)(part + (255 - part) * 0.6);
    }

    /// <summary>Puts out a glint that goes round and round.</summary>
    public void Still()
    {
        if (_again) BeginAnimation(LapProperty, null);
    }

    /// <summary>Where the two are, in this element's own coordinates. An empty bubble is one tucked away out of sight.</summary>
    public void Shape(Rect pill, double radius, Rect bubble)
    {
        if (pill == _pill && radius == _radius && bubble == _bubble) return;
        _pill = pill;
        _radius = radius;
        _bubble = bubble;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_pill.IsEmpty) return;
        Geometry pill = Outline(_pill, _radius);
        if (_bubble.IsEmpty)
        {
            Draw(dc, pill);
            Shine(dc, pill);
            return;
        }

        Geometry bubble = Outline(_bubble, _bubble.Height / 2);
        Geometry? neck = Neck();
        if (neck == null && !_pill.IntersectsWith(_bubble))
        {
            Draw(dc, pill);
            Draw(dc, bubble);
            Shine(dc, pill);
            return;
        }

        // one outline for the lot, so the rim runs round the whole shape instead of across the joint
        Geometry body = Geometry.Combine(pill, bubble, GeometryCombineMode.Union, null, Tolerance, ToleranceType.Absolute);
        if (neck != null) body = Geometry.Combine(body, neck, GeometryCombineMode.Union, null, Tolerance, ToleranceType.Absolute);
        Draw(dc, body);
        Shine(dc, pill);
    }

    // the rim is the outer half of a line along the edge: it lightens what is behind the island, not its black
    void Draw(DrawingContext dc, Geometry body)
    {
        Rect around = body.Bounds;
        around.Inflate(2 * Rim, 2 * Rim);
        var outside = new GeometryGroup { FillRule = FillRule.EvenOdd };
        outside.Children.Add(new RectangleGeometry(around));
        outside.Children.Add(body);

        dc.PushClip(outside);
        dc.DrawGeometry(null, _edge, body);
        dc.DrawGeometry(null, _beatEdge, body);
        dc.Pop();
        dc.DrawGeometry(Brushes.Black, null, body);
        // the haze of a beat lies on the black as much as around it: over a light window that is where it shows
        foreach (Pen haze in _beatHazes) dc.DrawGeometry(null, haze, body);
    }

    /// <summary>The glint: a streak along the pill's edge, bright at its head and trailing off behind it.</summary>
    void Shine(DrawingContext dc, Geometry pill)
    {
        double lap = Lap;
        if (lap <= 0) return;
        // one lap comes up out of nothing and dies away before it is back; of many, only the first comes up
        double strength = Math.Min(lap / Arrive, 1);
        if (!_again) strength *= Math.Clamp((1 - lap) / Depart, 0, 1);
        if (strength <= 0.004) return;

        PathGeometry path = pill.GetFlattenedPathGeometry(0.05, ToleranceType.Absolute);
        var points = new Point[Samples + 1];
        for (int i = 0; i <= Samples; i++)
        {
            double at = lap - Tail * (Samples - i) / Samples;
            path.GetPointAtFractionLength(at - Math.Floor(at), out points[i], out _);
        }

        dc.PushOpacity(strength * strength * (3 - 2 * strength));
        for (int layer = 0; layer < Layers; layer++)
        {
            // the starts crowd towards the head, so the tail is long and faint
            double behind = 1 - (double)layer / Layers;
            int from = Math.Min((int)Math.Round(Samples * (1 - behind * behind)), Samples - 1);
            var trail = new StreamGeometry();
            using (StreamGeometryContext g = trail.Open())
            {
                g.BeginFigure(points[from], false, false);
                g.PolyLineTo(points[(from + 1)..], true, true);
            }
            trail.Freeze();
            foreach (Pen flare in _flares) dc.DrawGeometry(null, flare, trail);
            dc.DrawGeometry(null, _sparkEdge, trail);
        }
        dc.Pop();
    }

    static Geometry Outline(Rect rect, double radius)
    {
        rect.Inflate(-Math.Min(Rim, rect.Width / 2), -Math.Min(Rim, rect.Height / 2));
        return Squircle.Of(rect, Math.Max(radius - Rim, 0));
    }

    /// <summary>The bridge between the pill's top right corner and the bubble's left end, each taken as a circle.</summary>
    Geometry? Neck()
    {
        double r1 = _radius - Rim, r2 = _bubble.Height / 2 - Rim;
        var c1 = new Point(_pill.Right - _radius, _pill.Top + _radius);
        var c2 = new Point(_bubble.Left + _bubble.Height / 2, _bubble.Top + _bubble.Height / 2);
        Vector between = c2 - c1;
        double d = between.Length, gap = d - r1 - r2;
        // still tucked behind the pill, one end inside the other, or pulled clear
        if (r1 <= 0 || r2 <= 0 || between.X <= 0 || d <= Math.Abs(r1 - r2) || gap >= Tear) return null;

        // where the circles cross, as an angle off the line between their centres; none once they have parted
        double u1 = 0, u2 = 0;
        if (gap < 0)
        {
            u1 = Math.Acos(Math.Clamp((r1 * r1 + d * d - r2 * r2) / (2 * r1 * d), -1, 1));
            u2 = Math.Acos(Math.Clamp((r2 * r2 + d * d - r1 * r1) / (2 * r2 * d), -1, 1));
        }

        // the neck lets go of the ends as the gap opens, and is down to nothing at the tear
        double hold = Hold * (1 - Math.Clamp(gap / Tear, 0, 1));
        double axis = Math.Atan2(between.Y, between.X), wide = Math.Acos((r1 - r2) / d);
        double a1 = axis + u1 + (wide - u1) * hold, a2 = axis - u1 - (wide - u1) * hold;
        double a3 = axis + Math.PI - u2 - (Math.PI - u2 - wide) * hold, a4 = axis - Math.PI + u2 + (Math.PI - u2 - wide) * hold;
        Point p1 = On(c1, a1, r1), p2 = On(c1, a2, r1), p3 = On(c2, a3, r2), p4 = On(c2, a4, r2);

        double reach = Math.Min(hold * Handle, (p1 - p3).Length / (r1 + r2)) * Math.Min(1, 2 * d / (r1 + r2));
        const double Quarter = Math.PI / 2;
        var neck = new StreamGeometry();
        using (StreamGeometryContext g = neck.Open())
        {
            g.BeginFigure(p1, true, true);
            g.BezierTo(On(p1, a1 - Quarter, r1 * reach), On(p3, a3 + Quarter, r2 * reach), p3, true, true);
            g.LineTo(p4, true, true);
            g.BezierTo(On(p4, a4 - Quarter, r2 * reach), On(p2, a2 + Quarter, r1 * reach), p2, true, true);
        }
        return neck;
    }

    static Point On(Point centre, double angle, double radius) =>
        new(centre.X + radius * Math.Cos(angle), centre.Y + radius * Math.Sin(angle));
}
