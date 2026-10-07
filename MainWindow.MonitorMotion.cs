using System.Runtime.InteropServices;
using System.Windows;

namespace DynamicIsland;

public partial class MainWindow
{
    FrameLoop? _monitorMoveLoop;
    bool _monitorDragging;
    string? _dragMonitor, _snapMonitor;
    double _monitorX, _monitorY, _monitorTargetX, _monitorTargetY;
    int _pointerOffsetX, _pointerOffsetY;

    void BeginMonitorDrag()
    {
        StopMonitorMotion();
        if (!MonitorNative.GetCursorPos(out var cursor) || !MonitorNative.GetWindowRect(_hwnd, out var rect)) return;
        _dragMonitor = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(cursor.X, cursor.Y)).DeviceName;
        _pointerOffsetX = cursor.X - rect.Left;
        _pointerOffsetY = cursor.Y - rect.Top;
        _monitorX = rect.Left;
        _monitorY = rect.Top;
    }

    bool FollowMonitorDrag()
    {
        if (_dragMonitor == null || !MonitorNative.GetCursorPos(out var cursor)) return false;
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(cursor.X, cursor.Y));
        if (!_monitorDragging && screen.DeviceName == _dragMonitor) return false;
        _monitorDragging = true;
        _pull = _leanDrag = 0;
        _monitorTargetX = cursor.X - _pointerOffsetX;
        _monitorTargetY = cursor.Y - _pointerOffsetY;
        _monitorMoveLoop ??= new FrameLoop(AdvanceMonitorMotion);
        _monitorMoveLoop.Start();
        UpdateTargets();
        return true;
    }

    bool FinishMonitorDrag()
    {
        if (!_monitorDragging) { _dragMonitor = null; return false; }
        _monitorDragging = false;
        _dragMonitor = null;
        if (!MonitorNative.GetCursorPos(out var cursor)) return false;
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(cursor.X, cursor.Y));
        Settings.TargetMonitor = _snapMonitor = screen.DeviceName;
        RefreshMonitorText();
        _monitorMoveLoop ??= new FrameLoop(AdvanceMonitorMotion);
        _monitorMoveLoop.Start();
        return true;
    }

    bool AdvanceMonitorMotion(double dt)
    {
        if (_snapMonitor != null && MonitorNative.GetWindowRect(_hwnd, out var rect))
        {
            var screen = System.Windows.Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == _snapMonitor)
                ?? System.Windows.Forms.Screen.PrimaryScreen;
            if (screen == null) return false;
            _monitorTargetX = screen.Bounds.Left + (screen.Bounds.Width - (rect.Right - rect.Left)) / 2.0;
            _monitorTargetY = screen.Bounds.Top;
        }
        double follow = 1 - Math.Exp(-dt / (_monitorDragging ? 0.055 : 0.10));
        _monitorX += (_monitorTargetX - _monitorX) * follow;
        _monitorY += (_monitorTargetY - _monitorY) * follow;
        bool moving = Math.Abs(_monitorTargetX - _monitorX) > 0.5 || Math.Abs(_monitorTargetY - _monitorY) > 0.5;
        if (!moving) { _monitorX = _monitorTargetX; _monitorY = _monitorTargetY; }
        MonitorNative.SetWindowPos(_hwnd, IntPtr.Zero, (int)Math.Round(_monitorX), (int)Math.Round(_monitorY), 0, 0, 0x0001 | 0x0004 | 0x0010);
        _glass.Place();
        if (!moving && !_monitorDragging) _snapMonitor = null;
        return moving || _monitorDragging;
    }

    void StopMonitorMotion()
    {
        _monitorMoveLoop?.Stop();
        _monitorDragging = false;
        _dragMonitor = _snapMonitor = null;
    }

    static class MonitorNative
    {
        [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetCursorPos(out Point point);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    }
}
