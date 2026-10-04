using System.Windows;
using System.Windows.Media;

namespace DynamicIsland;

/// <summary>
/// Waveform bars driven by the live output spectrum: bass in the middle, treble towards the edges. The same row
/// serves the pill and the player: closed up it stands with a few bars, and opened out more come in at its ends.
/// </summary>
public sealed class Equalizer : FrameworkElement
{
    const double BarWidth = 3;
    const double Attack = 0.03, Release = 0.17; // seconds

    // each bar is measured against its own slowly moving baseline, so quiet and loud tracks both swing fully
    const double RangeDb = 10, Center = 0.5;    // a bar at its usual level sits half-way, +5 dB fills it
    const double Rise = 0.8, Sink = 2.5;        // seconds for a baseline to follow a louder / quieter passage
    const double SpreadDb = 12;                 // bands further than this under the loudest one stay small
    const double FloorDb = -70;                 // baselines never sink below this, so noise stays flat

    static readonly double[] F1 = { 7.1, 9.3, 6.2, 10.4, 8.0, 5.6, 9.9, 7.7 };
    static readonly double[] F2 = { 2.3, 3.1, 1.7, 2.9, 3.7, 2.1, 1.3, 3.3 };

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(Equalizer),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    // the same row as a board of dots: each bar is a column of them, lit from the bottom up as far as it stands
    const double DotPitch = 4.2;                // a dot and the gap over it, about: the rows share the height out evenly
    const double DotOff = 0.16, DotLit = 0.72;  // how bright a dot is unlit and lit; the topmost lit one is brighter still
    const double DotHead = 0.5;                 // ...and this much of it is white

    int _bars = 5, _few = 5;
    double _open;
    bool _dots;
    double[] _levels = null!, _targets = null!;
    int[] _rank = null!, _slot = null!; // band shown by each bar, and the bar showing each band
    // the spectrum shared out among the few bars and among all of them: both are kept listening, so the row has
    // its heights ready whichever way it turns
    Split _closed = null!, _opened = null!;

    public Equalizer() => Lay();

    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    /// <summary>The bars of the row opened out.</summary>
    public int Bars
    {
        get => _bars;
        set
        {
            _bars = Math.Max(2, value);
            Lay();
        }
    }

    /// <summary>How many of them stand while it is closed up: the lowest bands, which are the ones in the middle.</summary>
    public int Few
    {
        get => Math.Min(_few, _bars);
        set
        {
            _few = Math.Max(2, value);
            Lay();
        }
    }

    /// <summary>How far the row is opened out, 0 → 1.</summary>
    public double Open
    {
        get => _open;
        set
        {
            value = Math.Clamp(value, 0, 1);
            if (value == _open) return;
            _open = value;
            InvalidateVisual();
        }
    }

    /// <summary>Drawn as columns of dots instead of bars.</summary>
    public bool Dots
    {
        get => _dots;
        set
        {
            if (value == _dots) return;
            _dots = value;
            InvalidateVisual();
        }
    }

    void Lay()
    {
        _levels = new double[_bars];
        _targets = new double[_bars];
        _closed = new Split(Few);
        _opened = new Split(_bars);

        // the lowest band sits in the middle, higher ones alternate outwards
        _rank = new int[_bars];
        _slot = new int[_bars];
        int center = (_bars - 1) / 2;
        for (int k = 0; k < _bars; k++)
        {
            _slot[k] = center + (k % 2 == 1 ? (k + 1) / 2 : -k / 2);
            _rank[_slot[k]] = k;
        }
    }

    /// <summary>Current height (0..1) of the bar that shows the given band, 0 being the lowest.</summary>
    public double Level(int band) => _levels[_slot[band]];

    /// <summary>
    /// Advances one frame. <paramref name="spectrum"/> holds band powers from bass to treble; pass null to fall
    /// back to a wobble scaled by the output <paramref name="peak"/>. Returns false once the bars are at rest.
    /// </summary>
    public bool Tick(float[]? spectrum, double peak, bool playing, double t, double dt)
    {
        if (!playing) Array.Clear(_targets);
        else if (spectrum != null) Level(spectrum, dt);
        else Wobble(peak, t);

        double rise = 1 - Math.Exp(-dt / Attack), fall = 1 - Math.Exp(-dt / Release);
        bool moved = false;
        for (int i = 0; i < _bars; i++)
        {
            // fast attack, slow decay
            double next = _levels[i] + (_targets[i] - _levels[i]) * (_targets[i] > _levels[i] ? rise : fall);
            if (Math.Abs(next - _levels[i]) > 0.002) moved = true;
            _levels[i] = next;
        }

        if (moved) InvalidateVisual();
        return moved;
    }

    void Level(float[] spectrum, double dt)
    {
        _closed.Level(spectrum, dt);
        _opened.Level(spectrum, dt);
        for (int i = 0; i < _bars; i++)
        {
            int band = _rank[i];
            double all = _opened.Heights[band];
            // a bar that stands either way shows a wider slice of the spectrum closed up than opened out: on the way
            // between the two it is somewhere between their heights
            _targets[i] = band < _closed.Heights.Length ? _closed.Heights[band] + (all - _closed.Heights[band]) * _open : all;
        }
    }

    void Wobble(double peak, double t)
    {
        double level = Math.Pow(Math.Clamp(peak * 1.8, 0, 1), 0.6);
        double mid = (_bars - 1) / 2.0;
        for (int i = 0; i < _bars; i++)
        {
            double noise = 0.5 + 0.5 * Math.Sin(t * F1[i % 8] + i * 1.9) * Math.Cos(t * F2[i % 8] + i * 0.7);
            double envelope = 1 - 0.3 * Math.Abs(i - mid) / mid;
            _targets[i] = level * envelope * (0.3 + 0.7 * noise);
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        // the bars that stand, a part of one counted as that part: the row is shared out among them
        double standing = Few + (_bars - Few) * _open;
        double gap = (w - standing * BarWidth) / Math.Max(standing - 1, 1), x = 0;
        for (int i = 0; i < _bars; i++)
        {
            // the bars past the few come in one after another, each growing out of nothing in its place
            double there = Math.Clamp(standing - _rank[i], 0, 1);
            if (there > 0.001 && _dots) Column(dc, x, h, _levels[i], there);
            else if (there > 0.001)
            {
                double bw = BarWidth * there, bh = bw + _levels[i] * (h - bw) * there;
                var rect = new Rect(x, (h - bh) / 2, bw, bh);
                // quiet bars sit back a little, loud ones come forward
                dc.PushOpacity((0.55 + 0.45 * _levels[i]) * there);
                dc.DrawRoundedRectangle(Fill, null, rect, bw / 2, bw / 2);
                dc.Pop();
            }
            x += there * (BarWidth + gap);
        }
    }

    /// <summary>One bar as a column of dots, <paramref name="there"/> of it standing.</summary>
    void Column(DrawingContext dc, double x, double h, double level, double there)
    {
        // as many rows as fit; between two views of the music the height is on its way, and the row it makes room
        // for grows out of nothing at the top, the way a bar does at the end of the row
        double fit = h / DotPitch, whole = Math.Floor(fit), part = Math.Clamp((fit - whole - 0.3) / 0.4, 0, 1);
        double rows = Math.Max(whole + part * part * (3 - 2 * part), 1);
        double gap = (h - rows * BarWidth) / Math.Max(rows - 1, 1), y = h;
        // the bottom dot is lit even in silence, as a bar at rest is a dot
        double lit = 1 + level * (rows - 1);
        for (int r = 0; r < rows; r++)
        {
            double row = Math.Min(rows - r, 1), size = BarWidth * there * row;
            double on = Math.Clamp(lit - r, 0, 1), head = on * (1 - Math.Clamp(lit - r - 1, 0, 1));
            var centre = new Point(x + BarWidth * there / 2, y - size / 2);
            dc.PushOpacity((DotOff + (DotLit - DotOff) * on + (1 - DotLit) * head) * there * row);
            dc.DrawEllipse(Fill, null, centre, size / 2, size / 2);
            dc.Pop();
            if (head > 0.01)
            {
                dc.PushOpacity(DotHead * head * there * row);
                dc.DrawEllipse(Brushes.White, null, centre, size / 2, size / 2);
                dc.Pop();
            }
            y -= row * (BarWidth + gap);
        }
    }

    /// <summary>The spectrum shared out among a number of bars, lowest band first, and how high each of them stands for it.</summary>
    sealed class Split
    {
        readonly double[] _db, _base;

        public Split(int bars)
        {
            Heights = new double[bars];
            _db = new double[bars];
            _base = new double[bars];
            Array.Fill(_base, FloorDb);
        }

        public double[] Heights { get; }

        public void Level(float[] spectrum, double dt)
        {
            int bars = Heights.Length;
            double headroom = RangeDb * (1 - Center), top = FloorDb;
            for (int i = 0; i < bars; i++)
            {
                // mean power of the slice of the spectrum that belongs to this bar
                int from = i * spectrum.Length / bars, to = Math.Max((i + 1) * spectrum.Length / bars, from + 1);
                double power = 0;
                for (int b = from; b < to; b++) power += spectrum[b];
                double db = _db[i] = 10 * Math.Log10(power / (to - from) + 1e-14);

                // silence leaves the baseline alone, so a gap between tracks doesn't reset it
                if (db > FloorDb)
                {
                    double b = _base[i];
                    b += (db - b) * (1 - Math.Exp(-dt / (db > b ? Rise : Sink)));
                    _base[i] = Math.Max(b, db - headroom);
                }
                top = Math.Max(top, _base[i]);
            }

            for (int i = 0; i < bars; i++)
            {
                double reference = Math.Max(_base[i], top - SpreadDb);
                Heights[i] = Math.Clamp(Center + (_db[i] - reference) / RangeDb, 0, 1);
            }
        }
    }
}
