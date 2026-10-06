using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace Capsule
{
    // The frameless, always-on-top window that holds the notch. It is placed in physical pixels with Win32,
    // so it lands exactly on a monitor's edge whatever that monitor's scaling.
    public sealed class NotchWindow : Window
    {
        readonly NotchView view = new NotchView();
        IList<CellModel> models = new List<CellModel>();
        GlassLayer glass;   // the live blur below the notch; null when the theme is solid
        IntPtr hwnd;
        string edge = "right";
        string device = "";
        double position = 0.5;
        MonitorInfo monitor;
        POINT pressAt;
        RECT pressRect;
        bool pressed, dragging;
        int hoverCell = -1;

        public event Action Clicked;                         // a click, not a drag, anywhere on the notch
        public event Action<int> HoverChanged;               // a cell index, or -1 when the mouse leaves the cells
        public event Action RightClicked;
        public event Action<string, string, double> Moved;   // edge, monitor device name, position along the edge

        public NotchWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            Title = "Capsule";
            view.HorizontalAlignment = HorizontalAlignment.Left;
            view.VerticalAlignment = VerticalAlignment.Top;
            Content = view;
            SourceInitialized += delegate
            {
                hwnd = new WindowInteropHelper(this).Handle;
                Native.AddExStyle(hwnd, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
            };
            // Not mid-drag: a notch dragged onto a monitor with other scaling must follow the cursor, and the drop
            // places it. Checked when the queued call runs, since a drag can start in between.
            DpiChanged += delegate { Dispatcher.BeginInvoke(new Action(delegate { if (!dragging) Reposition(); })); };
            MouseLeftButtonDown += OnPress;
            MouseMove += OnMouseMove;
            MouseLeftButtonUp += OnRelease;
            // Capture can go without a button-up (Alt-Tab, the lock screen, a menu opening): end the press there,
            // and put a half-dragged notch back where it was.
            LostMouseCapture += delegate
            {
                bool wasDragging = dragging;
                pressed = false;
                dragging = false;
                if (wasDragging) Reposition();
            };
            MouseRightButtonUp += delegate(object sender, MouseButtonEventArgs e)
            {
                e.Handled = true;
                if (RightClicked != null) RightClicked();
            };
            MouseLeave += delegate { if (!pressed) SetHover(-1); };
            IsVisibleChanged += delegate { ShowGlass(); };
        }

        public IntPtr Handle { get { return hwnd; } }
        public MonitorInfo Monitor { get { return monitor; } }
        public bool LeftEdge { get { return edge == "left"; } }
        IntPtr GlassHandle { get { return glass != null ? glass.Handle : IntPtr.Zero; } }

        // Live blur below the notch on or off. False when it was asked for but couldn't be made.
        public bool SetGlass(bool on)
        {
            if (on && glass == null)
            {
                glass = GlassLayer.Create();
                if (glass == null) return false;
                Reposition();   // cuts the blur to the capsule's outline
                if (glass.Broken)   // that first shape failed: there is no blur here, and the caller falls back to solid glass
                {
                    glass.Dispose();
                    glass = null;
                    return false;
                }
                ShowGlass();
            }
            if (!on && glass != null)
            {
                glass.Dispose();
                glass = null;
            }
            return true;
        }

        void ShowGlass()
        {
            if (glass == null) return;
            if (!IsVisible)
            {
                glass.Hide();
                return;
            }
            glass.Show();
            Native.KeepPairOnTop(hwnd, glass.Handle);
        }

        // Every 2 s from the Controller: stay above other topmost windows, the glass right below.
        public void KeepOnTop() { Native.KeepPairOnTop(hwnd, GlassHandle); }

        // Creates the window handle without showing the window, so it can be placed before it first appears.
        public void Create() { new WindowInteropHelper(this).EnsureHandle(); }

        // The theme changed: redraw in its colours.
        public void Restyle()
        {
            view.Restyle();
            view.Update(models, LeftEdge);
        }

        public void SetCells(IList<CellModel> cells)
        {
            bool resized = cells.Count != models.Count;
            models = cells;
            view.Update(models, LeftEdge);
            if (resized) Reposition();
        }

        public void Place(string edgeName, string monitorDevice, double y)
        {
            edge = edgeName == "left" ? "left" : "right";
            device = monitorDevice ?? "";
            position = Math.Max(0, Math.Min(1, y));
            view.Update(models, LeftEdge);
            Reposition();
        }

        public void Reposition()
        {
            if (hwnd == IntPtr.Zero) return;
            monitor = FindMonitor(device);
            if (monitor == null) return;
            Size size = view.ViewSize;
            int width = (int)Math.Round(size.Width * monitor.Scale);
            int height = (int)Math.Round(size.Height * monitor.Scale);
            RECT r = Layout.NotchRect(monitor.Work, LeftEdge, position, width, height);
            Native.MovePair(hwnd, GlassHandle, r.Left, r.Top, r.Width, r.Height);
            if (glass != null) glass.SetShape(NotchView.PillGeometry(models.Count, LeftEdge), monitor.Scale, r.Width, r.Height);
        }

        // A cell's ring in physical screen pixels, for placing the card beside it.
        public RECT CellScreenRect(int index)
        {
            RECT window;
            Native.GetWindowRect(hwnd, out window);
            double scale = monitor != null ? monitor.Scale : 1;
            Point center = NotchView.RingCenter(index);
            var r = new RECT();
            r.Left = window.Left;
            r.Right = window.Right;
            r.Top = window.Top + (int)Math.Round((center.Y - NotchView.Ring / 2) * scale);
            r.Bottom = window.Top + (int)Math.Round((center.Y + NotchView.Ring / 2) * scale);
            return r;
        }

        // The saved monitor if it is still attached, otherwise the primary one.
        static MonitorInfo FindMonitor(string device)
        {
            List<MonitorInfo> all = Native.Monitors();
            foreach (MonitorInfo m in all) if (device != "" && m.Device == device) return m;
            foreach (MonitorInfo m in all) if (m.Primary) return m;
            return all.Count > 0 ? all[0] : Native.Primary();
        }

        void OnPress(object sender, MouseButtonEventArgs e)
        {
            pressed = true;
            dragging = false;
            Native.GetCursorPos(out pressAt);
            Native.GetWindowRect(hwnd, out pressRect);
            CaptureMouse();
            e.Handled = true;
        }

        void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (!pressed)
            {
                SetHover(view.CellAt(e.GetPosition(view)));
                return;
            }
            POINT now;
            Native.GetCursorPos(out now);
            int dx = now.X - pressAt.X, dy = now.Y - pressAt.Y;
            if (!dragging && (Math.Abs(dx) > 4 || Math.Abs(dy) > 4))
            {
                dragging = true;
                SetHover(-1);
            }
            if (dragging) Native.MoveToPair(hwnd, GlassHandle, pressRect.Left + dx, pressRect.Top + dy);
        }

        void OnRelease(object sender, MouseButtonEventArgs e)
        {
            if (!pressed) return;
            bool wasDragging = dragging;
            pressed = false;
            dragging = false;   // cleared before releasing capture, so LostMouseCapture finds nothing to undo
            ReleaseMouseCapture();
            e.Handled = true;
            if (!wasDragging)
            {
                if (Clicked != null) Clicked();
                return;
            }
            RECT window;
            Native.GetWindowRect(hwnd, out window);
            POINT cursor;
            Native.GetCursorPos(out cursor);
            MonitorInfo target = Native.MonitorAt(cursor) ?? monitor;
            if (target == null) return;
            Place(Layout.SnapLeft(window, target.Work) ? "left" : "right", target.Device, Layout.SnapY(window, target.Work));
            if (Moved != null) Moved(edge, device, position);
        }

        void SetHover(int cell)
        {
            if (cell == hoverCell) return;
            hoverCell = cell;
            if (HoverChanged != null) HoverChanged(cell);
        }
    }
}
