using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using WUC = Windows.UI.Composition;
using Vec2 = System.Numerics.Vector2;
using Vec3 = System.Numerics.Vector3;

namespace Capsule
{
    // Live blur under one of Capsule's windows (spec §5). Windows blurs what is behind a window only through its
    // visual layer, which it draws over the window's own content, so the blur lives in a bare window of its own,
    // kept directly below the WPF window it serves: Windows' "host backdrop" brush, cut to the WPF window's outline,
    // with a window region of the same shape so clicks outside the outline fall through. The 2026-10-01 spike
    // (recorded in the plan) found that window regions can't cut the accent-policy blur, and that this works.
    public sealed class GlassLayer : IDisposable
    {
        const string ClassName = "CapsuleGlass";
        const int WS_POPUP = unchecked((int)0x80000000);
        const int WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOREDIRECTIONBITMAP = 0x00200000, WS_EX_NOACTIVATE = 0x08000000;
        const int WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3, SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;
        const int DWMWA_USE_HOSTBACKDROPBRUSH = 17;

        [ComImport, Guid("29E691FA-4567-4DCA-B319-D0F207EB6807"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ICompositorDesktopInterop
        {
            void CreateDesktopWindowTarget(IntPtr hwndTarget, [MarshalAs(UnmanagedType.Bool)] bool isTopmost, [MarshalAs(UnmanagedType.IInspectable)] out object result);
            void EnsureOnThread(uint threadId);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct DispatcherQueueOptions
        {
            public int Size, ThreadType, ApartmentType;
        }

        delegate IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WNDCLASSEX
        {
            public int cbSize, style;
            public WndProc lpfnWndProc;
            public int cbClsExtra, cbWndExtra;
            public IntPtr hInstance, hIcon, hCursor, hbrBackground;
            public string lpszMenuName, lpszClassName;
            public IntPtr hIconSm;
        }

        [DllImport("CoreMessaging.dll")] static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, [MarshalAs(UnmanagedType.IUnknown)] out object controller);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateWindowEx(int exStyle, string className, string title, int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
        [DllImport("user32.dll")] static extern IntPtr DefWindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int command);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr handle);
        [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(IntPtr name);

        static object dispatcherQueueController;   // the visual layer needs a dispatcher queue on the UI thread
        static WUC.Compositor compositor;
        static WndProc windowProc;                 // kept alive for as long as the window class exists
        static bool? supported;
        static bool stopped;                       // Stop has shut the visual layer down: there is no more blur in this process

        readonly IntPtr hwnd;
        readonly WUC.Desktop.DesktopWindowTarget target;
        readonly WUC.ContainerVisual root;
        readonly WUC.SpriteVisual blur;
        GeometrySource outline;                    // keeps the current outline's geometry alive
        string shapeKey = "";
        bool broken;                               // a shape or a motion couldn't be applied: the layer is hidden for good
        bool disposed;

        // Raised once for each layer that breaks after it was made, because a shape or a motion couldn't be applied. The
        // layer has hidden itself by then; its owner should fall back to solid glass (spec §8). Raised on the UI thread
        // inside the call that failed, so a handler should defer what it does.
        public static event Action Failed;

        // Whether live blur can work here: Windows 11 (the host-backdrop opt-in arrived in build 22000) and the
        // visual layer starts. First asked on the UI thread, which then owns the visual layer.
        public static bool Supported
        {
            get
            {
                if (!supported.HasValue) supported = Start();
                return supported.Value;
            }
        }

        static bool Start()
        {
            try
            {
                if (WindowsBuild() < 22000) return false;
                var options = new DispatcherQueueOptions { Size = Marshal.SizeOf(typeof(DispatcherQueueOptions)), ThreadType = 2, ApartmentType = 2 };   // this thread, STA
                int hr = CreateDispatcherQueueController(options, out dispatcherQueueController);
                if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                compositor = new WUC.Compositor();
                return true;
            }
            catch (Exception e)
            {
                Log.Error("glass: live blur unavailable (" + e.GetType().Name + ")", null);
                return false;
            }
        }

        static int WindowsBuild()
        {
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
            {
                int build;
                return key != null && int.TryParse(key.GetValue("CurrentBuildNumber") as string, out build) ? build : 0;
            }
        }

        // A hidden glass window, or null when live blur isn't available (the caller then draws solid glass).
        public static GlassLayer Create()
        {
            if (!Supported) return null;
            try { return new GlassLayer(); }
            catch (Exception e)
            {
                Log.Error("glass: window (" + e.GetType().Name + ")", null);
                return null;
            }
        }

        GlassLayer()
        {
            if (windowProc == null)
            {
                WndProc proc = WindowProc;
                var wc = new WNDCLASSEX();
                wc.cbSize = Marshal.SizeOf(typeof(WNDCLASSEX));
                wc.lpfnWndProc = proc;
                wc.hInstance = GetModuleHandle(IntPtr.Zero);
                wc.lpszClassName = ClassName;
                if (RegisterClassEx(ref wc) == 0) throw new Win32Exception();
                windowProc = proc;   // kept from here on, for as long as the class exists: a failed registration has nothing to keep
            }
            hwnd = CreateWindowEx(WS_EX_NOREDIRECTIONBITMAP | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST, ClassName, "Capsule glass",
                WS_POPUP, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, GetModuleHandle(IntPtr.Zero), IntPtr.Zero);
            if (hwnd == IntPtr.Zero) throw new Win32Exception();
            try
            {
                int on = 1;
                int hr = DwmSetWindowAttribute(hwnd, DWMWA_USE_HOSTBACKDROPBRUSH, ref on, 4);
                if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                object made;
                ((ICompositorDesktopInterop)(object)compositor).CreateDesktopWindowTarget(hwnd, false, out made);
                target = (WUC.Desktop.DesktopWindowTarget)made;
                root = compositor.CreateContainerVisual();
                target.Root = root;
                blur = compositor.CreateSpriteVisual();
                blur.Brush = compositor.CreateHostBackdropBrush();
                root.Children.InsertAtTop(blur);
            }
            catch (Exception)
            {
                DestroyWindow(hwnd);   // Create gives up on this layer, so its window must not be left behind
                throw;
            }
        }

        static IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_MOUSEACTIVATE) return new IntPtr(MA_NOACTIVATE);   // a click never activates the glass
            return DefWindowProc(hwnd, msg, wParam, lParam);
        }

        public IntPtr Handle { get { return hwnd; } }
        public bool IsVisible { get { return !disposed && IsWindowVisible(hwnd); } }
        public bool Broken { get { return broken; } }   // a shape or a motion couldn't be applied: nothing here works any more, and Failed was raised

        // Cuts the blur to an outline. outline: in DIPs from the served window's top-left; scale: its monitor's
        // DPI / 96; width and height: the window's size in pixels. Position the window with Native.MovePair.
        // False when the shape couldn't be applied (spec §8: a glass failure must never stop Capsule): the layer then
        // hides itself for good, and Failed tells its owner to fall back to solid glass.
        public bool SetShape(Geometry outlineDips, double scale, int width, int height)
        {
            if (broken || disposed) return false;
            try
            {
                List<Point[]> polygons = GlassShape.Polygons(outlineDips, scale);
                string key = GlassShape.Key(polygons, width, height);
                if (key == shapeKey || polygons.Count == 0) return true;
                // What can fail is built first, and the layer is changed after it; the key is set last. A failure must
                // not leave half a shape (an unclipped rectangle of blur) that the key then keeps from being tried again.
                var next = new GeometrySource(polygons);
                var clip = compositor.CreateGeometricClip(compositor.CreatePathGeometry(new WUC.CompositionPath(next)));
                root.Size = new Vec2(width, height);
                blur.Size = new Vec2(width, height);
                blur.Clip = clip;
                outline = next;
                IntPtr region = GlassShape.Region(polygons);   // last before the key: nothing after it can fail and leak it
                if (region != IntPtr.Zero && SetWindowRgn(hwnd, region, true) == 0) DeleteObject(region);
                shapeKey = key;
                return true;
            }
            catch (Exception e)
            {
                Break(e, "cut to its outline");
                return false;
            }
        }

        // The layer can't be trusted any more: hidden for good (Show does nothing from here on), logged once by the
        // exception's type and what was being done, and the owner told.
        void Break(Exception e, string doing)
        {
            if (broken) return;
            broken = true;
            Log.Error("glass: the blur couldn't be " + doing + " (" + e.GetType().Name + "); solid glass is used instead", null);
            Hide();
            if (Failed != null) Failed();
        }

        public void Show() { if (!broken && !disposed) ShowWindow(hwnd, SW_SHOWNOACTIVATE); }
        public void Hide() { if (!disposed) ShowWindow(hwnd, SW_HIDE); }

        // Fades the blur in (appear) or out over ms milliseconds, sliding it dx pixels as it goes, in step with the
        // panel's own fade and slide: the same ease-out, and each movement starts from wherever the blur is when this
        // is called, so turning round halfway doesn't make it jump. A compositor error breaks the layer, as in SetShape:
        // it must never stop Capsule or leave the panel shown but inactive.
        public void Animate(bool appear, float dx, int ms)
        {
            if (broken || disposed) return;
            try
            {
                var easeOut = EaseOut();
                var fade = compositor.CreateScalarKeyFrameAnimation();
                fade.InsertExpressionKeyFrame(0f, "this.StartingValue");
                fade.InsertKeyFrame(1f, appear ? 1f : 0f, easeOut);
                fade.Duration = TimeSpan.FromMilliseconds(ms);
                blur.StartAnimation("Opacity", fade);
                var slide = compositor.CreateVector3KeyFrameAnimation();
                slide.InsertExpressionKeyFrame(0f, "this.StartingValue");
                slide.InsertKeyFrame(1f, new Vec3(appear ? 0f : dx, 0f, 0f), easeOut);
                slide.Duration = TimeSpan.FromMilliseconds(ms);
                blur.StartAnimation("Offset", slide);
            }
            catch (Exception e) { Break(e, "animated"); }
        }

        // The blur as it is while its panel is hidden: faded out and dx pixels aside, any animation dropped, so that the
        // next Animate(true) fades it in from there (a blur that has never been animated starts out fully shown). A
        // compositor error breaks the layer: at start-up it must not stop Capsule.
        public void Rest(float dx)
        {
            if (broken || disposed) return;
            try
            {
                blur.StopAnimation("Opacity");
                blur.StopAnimation("Offset");
                blur.Opacity = 0f;
                blur.Offset = new Vec3(dx, 0f, 0f);
            }
            catch (Exception e) { Break(e, "rested"); }
        }

        // The card's unfold and fold (polish spec §2): grows or shrinks the blur to `size` (1 = full size) over ms
        // milliseconds about the centre Pose set, alongside Animate's fade: the same ease-out, from wherever it is. A
        // compositor error breaks the layer. Its clip is scaled with it, so the blur stays inside the card's outline.
        public void AnimateScale(float size, int ms)
        {
            if (broken || disposed) return;
            try
            {
                var grow = compositor.CreateVector3KeyFrameAnimation();
                grow.InsertExpressionKeyFrame(0f, "this.StartingValue");
                grow.InsertKeyFrame(1f, new Vec3(size, size, 1f), EaseOut());
                grow.Duration = TimeSpan.FromMilliseconds(ms);
                blur.StartAnimation("Scale", grow);
            }
            catch (Exception e) { Break(e, "animated"); }
        }

        // The blur put in a pose at once, any motion dropped: faded to opacity, at `size` about centre (pixels from the
        // window's top-left), then moved by offset (pixels). Where the card's unfold starts, and how a motion is re-aimed
        // halfway. A compositor error breaks the layer.
        public void Pose(float opacity, float size, Vec2 centre, Vec2 offset)
        {
            if (broken || disposed) return;
            try
            {
                blur.StopAnimation("Opacity");
                blur.StopAnimation("Offset");
                blur.StopAnimation("Scale");
                blur.Opacity = opacity;
                blur.CenterPoint = new Vec3(centre.X, centre.Y, 0f);
                blur.Scale = new Vec3(size, size, 1f);
                blur.Offset = new Vec3(offset.X, offset.Y, 0f);
            }
            catch (Exception e) { Break(e, "animated"); }
        }

        // The cubic ease-out curve, which is what WPF's CubicEase draws in its EaseOut mode.
        static WUC.CompositionEasingFunction EaseOut()
        {
            return compositor.CreateCubicBezierEasingFunction(new Vec2(0.215f, 0.61f), new Vec2(0.355f, 1f));
        }

        // Shuts the visual layer down for good, on the way out: after every layer has been disposed, while the UI thread's
        // dispatcher still runs (Application.Exit). Left running to the end, it made the process exit with 0xC000041D
        // (STATUS_FATAL_USER_CALLBACK_EXCEPTION, which Git Bash reports as 127) though Main returned 0. No layer is made
        // after this, and one still about breaks on its next call instead of throwing. Never throws; a second call does nothing.
        public static void Stop()
        {
            if (stopped) return;
            stopped = true;
            supported = false;
            if (compositor != null)
            {
                // Closed, not dropped: a layer still about then fails with ObjectDisposedException, which says why.
                try { compositor.Dispose(); }
                catch (Exception e) { Log.Error("glass: closing the visual layer failed (" + e.GetType().Name + ")", null); }
            }
            var queue = dispatcherQueueController as Windows.System.DispatcherQueueController;
            if (queue == null)
            {
                // Nothing to stop when the visual layer never started (no live blur on this PC); otherwise say why not.
                if (dispatcherQueueController != null) Log.Info("glass: the visual layer's dispatcher queue couldn't be read as one; it wasn't stopped");
                dispatcherQueueController = null;
                return;
            }
            dispatcherQueueController = null;
            try
            {
                // The queue stops on this thread's message loop: pump it until it has (a few milliseconds), never for long.
                Windows.Foundation.IAsyncAction stopping = queue.ShutdownQueueAsync();
                var waited = Stopwatch.StartNew();
                while (stopping.Status == Windows.Foundation.AsyncStatus.Started && waited.ElapsedMilliseconds < 1000 && !Dispatcher.CurrentDispatcher.HasShutdownStarted)
                {
                    var frame = new DispatcherFrame();
                    var tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
                    tick.Tick += delegate
                    {
                        tick.Stop();
                        frame.Continue = false;
                    };
                    tick.Start();
                    Dispatcher.PushFrame(frame);
                }
                if (stopping.Status == Windows.Foundation.AsyncStatus.Started)   // gave up waiting: the cap ran out, or the dispatcher is going
                    Log.Info("glass: the visual layer's dispatcher queue was still stopping after " + waited.ElapsedMilliseconds + " ms; it is left to finish on its own");
            }
            catch (Exception e) { Log.Error("glass: stopping the visual layer's dispatcher queue failed (" + e.GetType().Name + ")", null); }
        }

        public void Dispose()
        {
            if (disposed) return;   // the window is gone, and its handle may already belong to another window
            disposed = true;
            try
            {
                target.Root = null;
                target.Dispose();
            }
            catch (Exception e)
            {
                Log.Error("glass: releasing the visual layer failed (" + e.GetType().Name + ")", null);   // the window is destroyed all the same
            }
            DestroyWindow(hwnd);
        }
    }

    // The COM view of a Direct2D geometry the visual layer asks a geometry source for.
    [ComImport, Guid("0657AF73-53FD-47CF-84FF-C8492D2A80A3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IGeometrySource2DInterop
    {
        [PreserveSig] int GetGeometry(out IntPtr value);
        [PreserveSig] int TryGetGeometryUsingFactory(IntPtr factory, out IntPtr value);
    }

    // An outline for CompositionPath, which takes an IGeometrySource2D and reads it as a Direct2D geometry.
    // Public, so the runtime will hand it to Windows as a COM object.
    public sealed class GeometrySource : Windows.Graphics.IGeometrySource2D, IGeometrySource2DInterop
    {
        IntPtr geometry;

        public GeometrySource(List<Point[]> polygons) { geometry = Direct2D.Path(polygons); }

        public int GetGeometry(out IntPtr value)
        {
            Marshal.AddRef(geometry);
            value = geometry;
            return 0;
        }

        public int TryGetGeometryUsingFactory(IntPtr factory, out IntPtr value)
        {
            value = IntPtr.Zero;
            return unchecked((int)0x80004001);   // E_NOTIMPL
        }

        ~GeometrySource()
        {
            // The factory is multithreaded, so releasing from the finalizer thread is allowed.
            if (geometry != IntPtr.Zero) Marshal.Release(geometry);
            geometry = IntPtr.Zero;
        }
    }

    // Builds Direct2D path geometries from polygons, calling the COM methods by their vtable slots.
    static class Direct2D
    {
        [StructLayout(LayoutKind.Sequential)]
        struct PointF
        {
            public float X, Y;
            public PointF(double x, double y) { X = (float)x; Y = (float)y; }
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreatePathGeometryFn(IntPtr factory, out IntPtr path);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int OpenFn(IntPtr path, out IntPtr sink);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void SetFillModeFn(IntPtr sink, int mode);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void BeginFigureFn(IntPtr sink, PointF start, int begin);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void AddLineFn(IntPtr sink, PointF point);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void EndFigureFn(IntPtr sink, int end);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CloseFn(IntPtr sink);

        [DllImport("d2d1.dll")] static extern int D2D1CreateFactory(int type, ref Guid riid, IntPtr options, out IntPtr factory);

        // Vtable slots: ID2D1Factory::CreatePathGeometry; ID2D1PathGeometry::Open; ID2D1GeometrySink (with the
        // ID2D1SimplifiedGeometrySink methods first) SetFillMode, BeginFigure, EndFigure, Close, AddLine.
        const int CreatePathGeometrySlot = 10, OpenSlot = 17, SetFillModeSlot = 3, BeginFigureSlot = 5, EndFigureSlot = 8, CloseSlot = 9, AddLineSlot = 10;

        static IntPtr factory;

        public static IntPtr Path(List<Point[]> polygons)
        {
            if (factory == IntPtr.Zero)
            {
                Guid iid = new Guid("06152247-6f50-465a-9245-118bfd3b6007");   // ID2D1Factory
                Check(D2D1CreateFactory(1, ref iid, IntPtr.Zero, out factory));  // D2D1_FACTORY_TYPE_MULTI_THREADED
            }
            IntPtr path, sink;
            Check(Slot<CreatePathGeometryFn>(factory, CreatePathGeometrySlot)(factory, out path));
            try
            {
                Check(Slot<OpenFn>(path, OpenSlot)(path, out sink));
            }
            catch (Exception)
            {
                Marshal.Release(path);
                throw;
            }
            try
            {
                Slot<SetFillModeFn>(sink, SetFillModeSlot)(sink, 1);   // D2D1_FILL_MODE_WINDING, as the window region
                var begin = Slot<BeginFigureFn>(sink, BeginFigureSlot);
                var line = Slot<AddLineFn>(sink, AddLineSlot);
                var end = Slot<EndFigureFn>(sink, EndFigureSlot);
                foreach (Point[] polygon in polygons)
                {
                    begin(sink, new PointF(polygon[0].X, polygon[0].Y), 0);   // D2D1_FIGURE_BEGIN_FILLED
                    for (int i = 1; i < polygon.Length; i++) line(sink, new PointF(polygon[i].X, polygon[i].Y));
                    end(sink, 1);                                              // D2D1_FIGURE_END_CLOSED
                }
                Check(Slot<CloseFn>(sink, CloseSlot)(sink));
            }
            catch (Exception)
            {
                Marshal.Release(path);   // the caller never gets the path, so nobody else can release it
                throw;
            }
            finally
            {
                Marshal.Release(sink);
            }
            return path;
        }

        static T Slot<T>(IntPtr com, int index) where T : class
        {
            IntPtr vtable = Marshal.ReadIntPtr(com);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(vtable, index * IntPtr.Size), typeof(T));
        }

        static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }
    }
}
