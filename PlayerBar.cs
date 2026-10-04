using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;

namespace DynamicIsland;

/// <summary>
/// The bar Telegram shows at the top of its window while it plays, read through UI Automation: the app tells the
/// system neither the track's times nor, for a file with no tags, its name, and the bar has them.
/// </summary>
sealed class PlayerBar
{
    /// <param name="Text">The bar's line: "Artist – Song", or the name of the file.</param>
    /// <param name="At">The time its label shows.</param>
    /// <param name="Percent">How far its slider is, in hundredths; -1 when it does not say.</param>
    public readonly record struct Reading(string Text, TimeSpan At, int Percent);

    static readonly TimeSpan First = TimeSpan.FromSeconds(2), Longest = TimeSpan.FromSeconds(30);

    const uint WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_MOUSELEAVE = 0x02A3;
    const int MK_LBUTTON = 1;

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr hwnd, ref POINT point);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    AutomationElement? _text, _time, _slider;
    IntPtr _window;
    string _app = "";
    DateTime _next;
    TimeSpan _wait = First;

    /// <summary>
    /// Whether the app is Telegram or a client built from it (materialgram, AyuGram, 64Gram...): no other is asked,
    /// as being asked makes a browser build the whole tree of its page.
    /// </summary>
    public static bool Has(string appId) =>
        appId.Contains("gram", StringComparison.OrdinalIgnoreCase) || SourceApp.Name(appId).Contains("gram", StringComparison.OrdinalIgnoreCase);

    /// <summary>The app has started to play: its bar is looked for at once again.</summary>
    public void Forget()
    {
        _next = default;
        _wait = First;
    }

    /// <summary>Call off the UI thread: it waits for the app to answer.</summary>
    /// <returns>Null when the app has no such bar, or it is not on show.</returns>
    public Reading? Read(string appId)
    {
        if (appId != _app)
        {
            _app = appId;
            _text = null;
            Forget();
        }

        try
        {
            if (_text == null && !Find(appId)) return null;
            string value = ((ValuePattern)_slider!.GetCurrentPattern(ValuePattern.Pattern)).Current.Value;
            return new Reading(_text!.Current.Name, Time(_time!.Current.Name), int.TryParse(value.TrimEnd('%'), out int percent) ? percent : -1);
        }
        catch
        {
            // the bar is gone with its player
            _text = null;
            return null;
        }
    }

    /// <summary>
    /// Plays from another place: the bar's slider takes no value, so it is clicked, by messages put to the app's
    /// window, which neither move the pointer nor bring the window up. Call off the UI thread.
    /// </summary>
    /// <returns>False when the bar is not on show to be clicked.</returns>
    public bool Seek(double fraction)
    {
        try
        {
            if (_text == null || _slider == null) return false;
            Rect slider = _slider.Current.BoundingRectangle;
            if (slider.IsEmpty || slider.Width < 1 || _slider.Current.IsOffscreen) return false;

            var at = new POINT
            {
                X = (int)Math.Round(slider.X + slider.Width * Math.Clamp(fraction, 0, 1)),
                Y = (int)(slider.Y + slider.Height / 2),
            };
            if (!ScreenToClient(_window, ref at)) return false;

            IntPtr where = (at.Y << 16) | (at.X & 0xFFFF);
            PostMessage(_window, WM_MOUSEMOVE, IntPtr.Zero, where);
            PostMessage(_window, WM_LBUTTONDOWN, MK_LBUTTON, where);
            PostMessage(_window, WM_LBUTTONUP, IntPtr.Zero, where);
            // or the slider stays lit as if the pointer were on it
            PostMessage(_window, WM_MOUSELEAVE, IntPtr.Zero, IntPtr.Zero);
            return true;
        }
        catch
        {
            return false;
        }
    }

    bool Find(string appId)
    {
        if (DateTime.UtcNow < _next) return false;

        foreach (IntPtr window in SourceApp.Windows(appId))
        {
            AutomationElement? bar = AutomationElement.FromHandle(window).FindFirst(TreeScope.Descendants, Class("class Media::Player::Widget"));
            if (bar == null) continue;

            _text = bar.FindFirst(TreeScope.Children, Class("class Ui::FlatLabel"));
            _time = bar.FindFirst(TreeScope.Descendants, Class("class Ui::LabelSimple"));
            _slider = bar.FindFirst(TreeScope.Children, Class("class Ui::FilledSlider"));
            if (_text != null && _time != null && _slider != null)
            {
                _window = window;
                _wait = First;
                return true;
            }
        }

        _text = null;
        // looking through the whole window is work for the app: the longer the bar is not there, the more seldom
        _next = DateTime.UtcNow + _wait;
        _wait = _wait + _wait < Longest ? _wait + _wait : Longest;
        return false;
    }

    static PropertyCondition Class(string name) => new(AutomationElement.ClassNameProperty, name);

    /// <summary>"m:ss" or "h:mm:ss"; zero when the label reads otherwise.</summary>
    static TimeSpan Time(string label)
    {
        int seconds = 0;
        foreach (string part in label.Split(':'))
        {
            if (!int.TryParse(part, out int n) || n < 0) return TimeSpan.Zero;
            seconds = seconds * 60 + n;
        }
        return TimeSpan.FromSeconds(seconds);
    }
}
