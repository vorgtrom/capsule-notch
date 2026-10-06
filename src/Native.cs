using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Capsule
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width { get { return Right - Left; } }
        public int Height { get { return Bottom - Top; } }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X, Y;
    }

    // One monitor, in physical pixels. Scale is its DPI / 96.
    public sealed class MonitorInfo
    {
        public IntPtr Handle;
        public string Device = "";
        public RECT Bounds;
        public RECT Work;
        public bool Primary;
        public double Scale = 1;
    }

    // The Win32 calls the windows need, and nothing more.
    public static class Native
    {
        public const int WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
        const int GWL_STYLE = -16, GWL_EXSTYLE = -20, WS_CAPTION = 0x00C00000;
        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
        const uint MONITOR_DEFAULTTOPRIMARY = 1, MONITOR_DEFAULTTONEAREST = 2, MONITORINFOF_PRIMARY = 1;
        const int DWMWA_EXTENDED_FRAME_BOUNDS = 9, MDT_EFFECTIVE_DPI = 0;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
        }

        delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref RECT clip, IntPtr data);

        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hWnd, int index, int value);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT point, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);
        [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
        [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out RECT value, int size);
        [DllImport("user32.dll")] static extern IntPtr BeginDeferWindowPos(int count);
        [DllImport("user32.dll")] static extern IntPtr DeferWindowPos(IntPtr info, IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern bool EndDeferWindowPos(IntPtr info);

        // The window that has the keyboard now (zero when none).
        public static IntPtr Foreground() { return GetForegroundWindow(); }

        // Gives the keyboard back to a window that had it; false when that window is gone or Windows refused.
        public static bool GiveForegroundTo(IntPtr window) { return window != IntPtr.Zero && IsWindow(window) && SetForegroundWindow(window); }

        public static void AddExStyle(IntPtr hwnd, int bits) { SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | bits); }
        public static void RemoveExStyle(IntPtr hwnd, int bits) { SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) & ~bits); }
        public static bool HasExStyle(IntPtr hwnd, int bits) { return (GetWindowLong(hwnd, GWL_EXSTYLE) & bits) == bits; }

        // Moves and sizes a window and its glass (glass: GlassLayer.Handle, or IntPtr.Zero for none) in physical pixels,
        // keeping them topmost and never activating them, in one step and with the glass directly below the window, so
        // the blur never lags behind it or shows above it.
        public static void MovePair(IntPtr top, IntPtr glass, int x, int y, int width, int height) { PlacePair(top, glass, x, y, width, height, SWP_NOACTIVATE); }
        public static void MoveToPair(IntPtr top, IntPtr glass, int x, int y) { PlacePair(top, glass, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE); }
        // As MoveToPair, but the windows keep their place in the stacking order (and so the glass its place right below the
        // window): for a glide, whose frames mustn't lift the card over the capsule's inner edge.
        public static void MoveToPairKeepingOrder(IntPtr top, IntPtr glass, int x, int y) { PlacePair(top, glass, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE); }
        public static void KeepPairOnTop(IntPtr top, IntPtr glass) { PlacePair(top, glass, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE); }

        static void PlacePair(IntPtr top, IntPtr glass, int x, int y, int width, int height, uint flags)
        {
            if (glass != IntPtr.Zero)
            {
                IntPtr info = BeginDeferWindowPos(2);
                if (info != IntPtr.Zero) info = DeferWindowPos(info, top, HWND_TOPMOST, x, y, width, height, flags);
                if (info != IntPtr.Zero) info = DeferWindowPos(info, glass, top, x, y, width, height, flags);
                if (info != IntPtr.Zero && EndDeferWindowPos(info)) return;
            }
            SetWindowPos(top, HWND_TOPMOST, x, y, width, height, flags);
            if (glass != IntPtr.Zero) SetWindowPos(glass, top, x, y, width, height, flags);
        }

        public static List<MonitorInfo> Monitors()
        {
            var list = new List<MonitorInfo>();
            MonitorEnumProc callback = delegate(IntPtr monitor, IntPtr hdc, ref RECT clip, IntPtr data)
            {
                MonitorInfo info = Describe(monitor);
                if (info != null) list.Add(info);
                return true;
            };
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
            GC.KeepAlive(callback);
            return list;
        }

        public static MonitorInfo Primary() { return Describe(MonitorFromPoint(new POINT(), MONITOR_DEFAULTTOPRIMARY)); }
        public static MonitorInfo MonitorAt(POINT point) { return Describe(MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST)); }

        static MonitorInfo Describe(IntPtr handle)
        {
            var raw = new MONITORINFOEX();
            raw.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
            if (handle == IntPtr.Zero || !GetMonitorInfo(handle, ref raw)) return null;
            var info = new MonitorInfo();
            info.Handle = handle;
            info.Device = raw.szDevice ?? "";
            info.Bounds = raw.rcMonitor;
            info.Work = raw.rcWork;
            info.Primary = (raw.dwFlags & MONITORINFOF_PRIMARY) != 0;
            uint dpiX, dpiY;
            if (GetDpiForMonitor(handle, MDT_EFFECTIVE_DPI, out dpiX, out dpiY) == 0 && dpiX > 0) info.Scale = dpiX / 96.0;
            return info;
        }

        // Windows shell overlays that cover the whole screen without a caption while you switch windows: Windows 11's
        // Alt-Tab, Task View and Snap Assist (XamlExplorerHostIslandWindow), Windows 10's Alt-Tab (MultitaskingViewFrame)
        // and the brief handover window of a switch (ForegroundStaging). They are not fullscreen apps.
        public static bool IsShellOverlay(string className)
        {
            return className == "XamlExplorerHostIslandWindow" || className == "MultitaskingViewFrame" || className == "ForegroundStaging";
        }

        // True when the foreground window covers the whole of this monitor and has no caption bar: a fullscreen
        // video, game or slideshow. Maximised windows keep their caption, so they don't count. Null when the foreground
        // window is one of `own`, Capsule's own windows: with the panel in front it can't be told whether a fullscreen
        // app is still behind it, so the caller keeps what it knew.
        public static bool? IsFullscreenOn(MonitorInfo monitor, params IntPtr[] own)
        {
            if (monitor == null) return false;
            IntPtr window = GetForegroundWindow();
            if (window == IntPtr.Zero) return false;
            foreach (IntPtr mine in own) if (mine == window) return null;
            var name = new StringBuilder(256);
            GetClassName(window, name, name.Capacity);
            string cls = name.ToString();
            if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd") return false;
            if (IsShellOverlay(cls)) return false;
            if (MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST) != monitor.Handle) return false;
            RECT r;
            if (DwmGetWindowAttribute(window, DWMWA_EXTENDED_FRAME_BOUNDS, out r, Marshal.SizeOf(typeof(RECT))) != 0) GetWindowRect(window, out r);
            RECT b = monitor.Bounds;
            bool covers = r.Left <= b.Left && r.Top <= b.Top && r.Right >= b.Right && r.Bottom >= b.Bottom;
            return covers && (GetWindowLong(window, GWL_STYLE) & WS_CAPTION) != WS_CAPTION;
        }

        // The foreground window's class and bounds, for the log. Never its title, which can hold private text.
        public static string ForegroundDescription()
        {
            IntPtr window = GetForegroundWindow();
            var name = new StringBuilder(256);
            GetClassName(window, name, name.Capacity);
            RECT r;
            if (DwmGetWindowAttribute(window, DWMWA_EXTENDED_FRAME_BOUNDS, out r, Marshal.SizeOf(typeof(RECT))) != 0) GetWindowRect(window, out r);
            return name + " (" + r.Left + "," + r.Top + "," + r.Right + "," + r.Bottom + ")";
        }
    }
}
