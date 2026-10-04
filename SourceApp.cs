using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DynamicIsland;

/// <summary>
/// The app a media session belongs to (Spotify, the browser...): brings it to the front, names it and tells
/// its processes from the others.
/// </summary>
/// <remarks>
/// A session names its app in one of three ways: by its exe ("Spotify.exe"), by the id of its Start menu entry
/// ("Chrome", Firefox's "308046B0AF4A39CB") or, for a Store app, by its package ("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify").
/// </remarks>
static class SourceApp
{
    const uint GW_OWNER = 4;
    const int SW_RESTORE = 9;
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    delegate bool WindowProc(IntPtr hwnd, IntPtr param);

    [DllImport("user32.dll")] static extern bool EnumWindows(WindowProc proc, IntPtr param);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint relation);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint process);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint process);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref int size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int GetApplicationUserModelId(IntPtr process, ref int length, StringBuilder id);

    /// <param name="title">The track: among several windows of the app, the one that has it in its title is the one playing.</param>
    public static void Show(string appId, string title)
    {
        if (appId.Length == 0) return;

        string exe = Target(appId);
        IntPtr found = IntPtr.Zero;
        var text = new StringBuilder(512);
        EnumWindows((hwnd, _) =>
        {
            // only what Alt-Tab would list
            if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return true;
            if ((Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64() & Native.WS_EX_TOOLWINDOW) != 0) return true;
            if (GetWindowText(hwnd, text, text.Capacity) == 0) return true;
            GetWindowThreadProcessId(hwnd, out uint process);
            if (!Runs(process, appId, exe)) return true;

            // windows come front to back: without a title to go by, the first is the one used last
            bool named = title.Length > 0 && text.ToString().Contains(title, StringComparison.OrdinalIgnoreCase);
            if (named || found == IntPtr.Zero) found = hwnd;
            return !named;
        }, IntPtr.Zero);

        if (found == IntPtr.Zero)
        {
            Launch(appId);
            return;
        }
        if (IsIconic(found)) ShowWindow(found, SW_RESTORE);
        SetForegroundWindow(found);
    }

    /// <summary>What the app is called in the Start menu, or its exe without the ending; empty when the id says neither.</summary>
    public static string Name(string appId)
    {
        // asked on every change of what plays, for the same app
        if (appId != _nameOf) (_nameOf, _name) = (appId, Named(appId));
        return _name;
    }

    static string Named(string appId)
    {
        if (appId.Length == 0) return "";
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
            if (shell.NameSpace("shell:AppsFolder")?.ParseName(appId)?.Name is string name && name.Length > 0) return name;
        }
        catch
        {
            // no such entry
        }
        return appId.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Path.GetFileNameWithoutExtension(appId) : "";
    }

    static string _nameOf = "", _name = "";
    static string _targetOf = "", _target = "";
    static readonly object Gate = new();

    /// <summary>Whether a process is one of the app's: a player or a browser sounds through more than the one that has its window.</summary>
    public static bool Owns(string appId, uint process)
    {
        if (appId.Length == 0) return false;
        // asked on every notch of the wheel, for the same app
        string target;
        lock (Gate) // the player bar asks from a thread of its own
        {
            if (appId != _targetOf) (_targetOf, _target) = (appId, Target(appId));
            target = _target;
        }
        return Runs(process, appId, target);
    }

    /// <summary>The app's top-level windows, on show or not.</summary>
    public static List<IntPtr> Windows(string appId)
    {
        var found = new List<IntPtr>();
        if (appId.Length == 0) return found;
        EnumWindows((hwnd, _) =>
        {
            if (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return true;
            GetWindowThreadProcessId(hwnd, out uint process);
            if (Owns(appId, process)) found.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>The exe that the Start menu entry with this id starts; empty when there is no such entry.</summary>
    static string Target(string appId)
    {
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
            return shell.NameSpace("shell:AppsFolder")?.ParseName(appId)?.ExtendedProperty("System.Link.TargetParsingPath") as string ?? "";
        }
        catch
        {
            return "";
        }
    }

    static bool Runs(uint process, string appId, string exe)
    {
        IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, process);
        if (handle == IntPtr.Zero) return false;
        try
        {
            var text = new StringBuilder(1024);
            int size = text.Capacity;
            if (QueryFullProcessImageName(handle, 0, text, ref size))
            {
                string path = text.ToString();
                if (Same(path, exe) || Same(Path.GetFileName(path), appId)) return true;
            }
            size = text.Capacity;
            return GetApplicationUserModelId(handle, ref size, text.Clear()) == 0 && Same(text.ToString(), appId);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // no window to show: the app sits in the tray, or is a Store app behind a frame of the system's.
    // Started once more, such an app brings up the window it already has
    static void Launch(string appId)
    {
        try
        {
            Process.Start(new ProcessStartInfo(@"shell:AppsFolder\" + appId) { UseShellExecute = true })?.Dispose();
            return;
        }
        catch (Win32Exception)
        {
            // not a Start menu id: it is the name of the exe
        }

        try
        {
            foreach (Process running in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(appId)))
            {
                if (running.MainModule?.FileName is not { } path) continue;
                Process.Start(path).Dispose();
                return;
            }
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }
}
