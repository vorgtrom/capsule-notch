using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Capsule
{
    public sealed class CaptureDesktop : ICaptureDesktop
    {
        readonly Stopwatch clock = Stopwatch.StartNew();

        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT
        {
            public int dx, dy;
            public uint mouseData, flags, time;
            public UIntPtr extraInfo;
        }

        [StructLayout(LayoutKind.Explicit)]
        struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mouse; }

        [StructLayout(LayoutKind.Sequential)]
        struct INPUT { public uint type; public INPUTUNION data; }

        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT point);
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, INPUT[] input, int size);
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
        [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr desktop);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder value, int length, out int needed);

        public long NowMs { get { return clock.ElapsedMilliseconds; } }
        public bool EscapePressed { get { return Pressed(0x1B); } }
        public bool ModifiersPressed { get { return Pressed(0x10) || Pressed(0x11) || Pressed(0x12) || Pressed(0x5B) || Pressed(0x5C); } }

        static bool Pressed(int key) { return (GetAsyncKeyState(key) & 0x8000) != 0; }
        public Task WaitAsync(int milliseconds, CancellationToken cancel) { return Task.Delay(milliseconds, cancel); }

        static bool OrdinaryDesktop()
        {
            IntPtr desktop = OpenInputDesktop(0, false, 1);
            if (desktop == IntPtr.Zero) return false;
            try
            {
                var name = new StringBuilder(256);
                int needed;
                return GetUserObjectInformation(desktop, 2, name, name.Capacity * 2, out needed) && name.ToString() == "Default";
            }
            finally { CloseDesktop(desktop); }
        }

        public bool TargetValid(CaptureRequest request)
        {
            if (request == null || !OrdinaryDesktop() || !SelectionValid(request.Area)) return false;
            if (request.Mode == CaptureMode.Screenshot) return true;
            RECT bounds;
            if (!Native.GetWindowRect(request.Target, out bounds)) return false;
            return WindowMatches(request, IsWindow(request.Target), IsWindowVisible(request.Target), IsIconic(request.Target),
                Native.Foreground(), bounds, BrowserWindow(request.Target));
        }

        internal static bool WindowMatches(CaptureRequest request, bool exists, bool visible, bool minimized, IntPtr foreground, RECT bounds, bool browser)
        {
            RECT original = request.TargetBounds;
            return request.Target != IntPtr.Zero && exists && visible && !minimized && foreground == request.Target && browser &&
                bounds.Left == original.Left && bounds.Top == original.Top && bounds.Right == original.Right && bounds.Bottom == original.Bottom &&
                Contains(bounds, request.Area);
        }

        static bool BrowserWindow(IntPtr window)
        {
            uint process;
            if (GetWindowThreadProcessId(window, out process) == 0 || process == 0) return false;
            try
            {
                using (Process target = Process.GetProcessById((int)process))
                {
                    string name = target.ProcessName;
                    return String.Equals(name, "msedge", StringComparison.OrdinalIgnoreCase) || String.Equals(name, "chrome", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
            catch (Win32Exception) { return false; }
        }

        // Call after all selection overlays hide. This is the only capture helper that acquires foreground.
        public static bool TryCreateScrollingRequest(RECT area, POINT selectedPoint, out CaptureRequest request)
        {
            request = null;
            if (!OrdinaryDesktop() || !SelectionValid(area) || !Contains(area, selectedPoint)) return false;
            IntPtr window = GetAncestor(WindowFromPoint(selectedPoint), 2);
            RECT bounds;
            if (window == IntPtr.Zero || !IsWindowVisible(window) || IsIconic(window) || !BrowserWindow(window) ||
                !Native.GetWindowRect(window, out bounds) || !Contains(bounds, area) || !Native.GiveForegroundTo(window)) return false;
            var selected = new CaptureRequest { Mode = CaptureMode.Scrolling, Area = area, Target = window, TargetBounds = bounds };
            if (!new CaptureDesktop().TargetValid(selected)) return false;
            request = selected;
            return true;
        }

        public static bool SelectionValid(RECT area)
        {
            long width = (long)area.Right - area.Left, height = (long)area.Bottom - area.Top;
            if (width <= 0 || height <= 0 || height > CaptureLimits.MaxRows || width * height > CaptureLimits.MaxPixels) return false;
            foreach (MonitorInfo monitor in Native.Monitors()) if (Contains(monitor.Bounds, area)) return true;
            return false;
        }

        static bool Contains(RECT outer, RECT inner)
        {
            return inner.Left >= outer.Left && inner.Top >= outer.Top && inner.Right <= outer.Right && inner.Bottom <= outer.Bottom && inner.Width > 0 && inner.Height > 0;
        }

        static bool Contains(RECT area, POINT point)
        {
            return point.X >= area.Left && point.X < area.Right && point.Y >= area.Top && point.Y < area.Bottom;
        }

        public bool PointerInside(RECT area)
        {
            POINT point;
            return Native.GetCursorPos(out point) && Contains(area, point);
        }

        internal Action BeforeRead, AfterRead;

        public CaptureFrame ReadFrame(RECT area)
        {
            return ReadFrame(area, ReadScreen);
        }

        internal CaptureFrame ReadFrame(RECT area, Func<RECT, CaptureFrame> read)
        {
            CaptureFrame frame = null;
            try
            {
                try
                {
                    if (BeforeRead != null) BeforeRead();
                    frame = read(area);
                }
                finally { if (AfterRead != null) AfterRead(); }
                return frame;
            }
            catch
            {
                if (frame != null) frame.Dispose();
                throw;
            }
        }

        CaptureFrame ReadScreen(RECT area)
        {
            if (!OrdinaryDesktop() || !SelectionValid(area)) throw new InvalidOperationException("This screen area cannot be captured.");
            try
            {
                using (var bitmap = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppArgb))
                {
                    using (Graphics graphics = Graphics.FromImage(bitmap))
                        graphics.CopyFromScreen(area.Left, area.Top, 0, 0, bitmap.Size, CopyPixelOperation.SourceCopy);
                    BitmapData data = bitmap.LockBits(new Rectangle(0, 0, area.Width, area.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                    try
                    {
                        byte[] pixels = new byte[checked(area.Width * area.Height * 4)];
                        for (int row = 0; row < area.Height; row++)
                        {
                            Marshal.Copy(IntPtr.Add(data.Scan0, row * data.Stride), pixels, row * area.Width * 4, area.Width * 4);
                            for (int x = row * area.Width * 4 + 3; x < (row + 1) * area.Width * 4; x += 4) pixels[x] = 255;
                        }
                        return new CaptureFrame(area.Width, area.Height, pixels);
                    }
                    finally { bitmap.UnlockBits(data); }
                }
            }
            catch (Win32Exception) { throw new InvalidOperationException("The selected screen area could not be read."); }
            catch (ExternalException) { throw new InvalidOperationException("The selected screen area could not be read."); }
        }

        public bool Scroll(CaptureRequest request, int direction)
        {
            if (direction != 1 && direction != -1) throw new ArgumentOutOfRangeException("direction");
            if (request.Mode != CaptureMode.Scrolling || !TargetValid(request) || EscapePressed || ModifiersPressed || !PointerInside(request.Area)) return false;
            var input = new INPUT { type = 0, data = new INPUTUNION { mouse = new MOUSEINPUT { mouseData = unchecked((uint)(direction * 120)), flags = 0x0800 } } };
            return SendInput(1, new[] { input }, Marshal.SizeOf(typeof(INPUT))) == 1;
        }

        // Coordinates are local WPF units. Add the physical monitor origin only after scaling.
        public static POINT PhysicalPoint(MonitorInfo monitor, double localX, double localY)
        {
            if (monitor == null) throw new ArgumentNullException("monitor");
            if (Double.IsNaN(monitor.Scale) || Double.IsInfinity(monitor.Scale) || monitor.Scale <= 0) throw new ArgumentException("The monitor scale is invalid.", "monitor");
            return new POINT { X = checked(monitor.Bounds.Left + Pixel(localX * monitor.Scale)), Y = checked(monitor.Bounds.Top + Pixel(localY * monitor.Scale)) };
        }

        static int Pixel(double value)
        {
            if (Double.IsNaN(value) || Double.IsInfinity(value) || value < Int32.MinValue || value > Int32.MaxValue) throw new ArgumentOutOfRangeException("value");
            return checked((int)Math.Round(value, MidpointRounding.AwayFromZero));
        }

        public static RECT PhysicalArea(MonitorInfo monitor, double left, double top, double right, double bottom)
        {
            POINT first = PhysicalPoint(monitor, Math.Min(left, right), Math.Min(top, bottom));
            POINT last = PhysicalPoint(monitor, Math.Max(left, right), Math.Max(top, bottom));
            return new RECT { Left = Math.Max(monitor.Bounds.Left, first.X), Top = Math.Max(monitor.Bounds.Top, first.Y),
                Right = Math.Min(monitor.Bounds.Right, last.X), Bottom = Math.Min(monitor.Bounds.Bottom, last.Y) };
        }
    }
}
