using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Capsule
{
    internal static class CaptureControls
    {
        internal static TextBlock Text(string text, double size = 13)
        {
            return new TextBlock { Text = text, FontSize = size, Foreground = Theme.Current.Brush(Theme.Current.Text),
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4) };
        }

        internal static Button Button(string text, Action action)
        {
            var button = new Button { Content = text, MinHeight = 32, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(4),
                Foreground = Theme.Current.Brush(Theme.Current.Text), Background = Theme.Current.Brush(Theme.Current.Tile),
                BorderBrush = Theme.Current.Brush(Theme.Current.TileRim), FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI") };
            var surface = new FrameworkElementFactory(typeof(Border), "CaptureButtonSurface");
            surface.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            surface.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            surface.SetValue(Border.BorderThicknessProperty, new Thickness(2));
            surface.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            surface.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
            content.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
            content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            surface.AppendChild(content);
            var template = new ControlTemplate(typeof(Button)) { VisualTree = surface };
            var focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
            focus.Setters.Add(new Setter(Border.BorderBrushProperty, Theme.Current.Brush(Theme.Current.Text), "CaptureButtonSurface"));
            template.Triggers.Add(focus);
            var pressed = new Trigger { Property = System.Windows.Controls.Button.IsPressedProperty, Value = true };
            pressed.Setters.Add(new Setter(UIElement.OpacityProperty, 0.65, "CaptureButtonSurface"));
            template.Triggers.Add(pressed);
            button.Template = template;
            AutomationProperties.SetName(button, text.Replace("_", ""));
            button.Click += delegate { action(); };
            return button;
        }

        // A capture mode in the chooser: its icon, its name and the key that picks it, on a tile that lights up under the
        // pointer.
        internal static Button ModeButton(string glyph, string label, string key, Action action)
        {
            Theme theme = Theme.Current;
            var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            content.Children.Add(new TextBlock { Text = glyph, FontFamily = new FontFamily(PanelView.IconFont), FontSize = 26,
                Foreground = theme.Brush(theme.Text), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 8) });
            content.Children.Add(new TextBlock { Text = label, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = theme.Brush(theme.Text),
                TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center });
            content.Children.Add(new Border { Child = new TextBlock { Text = key, FontSize = 10.5, Foreground = theme.Brush(theme.Secondary) },
                Background = theme.Brush(theme.Track), CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 1),
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 2) });
            Button button = Button(label, action);
            button.Content = content;
            button.MinHeight = 108;
            button.Padding = new Thickness(8, 10, 8, 10);
            button.Margin = new Thickness(4);
            Hover(button);
            return button;
        }

        // A small round button showing one glyph, such as the chooser's close.
        internal static Button IconButton(string glyph, string name, Action action)
        {
            Theme theme = Theme.Current;
            Button button = Button(name, action);
            button.Content = new TextBlock { Text = glyph, FontFamily = new FontFamily(PanelView.IconFont), FontSize = 11, Foreground = theme.Brush(theme.Secondary) };
            button.MinHeight = 28;
            button.Width = 28;
            button.Height = 28;
            button.Padding = new Thickness(0);
            button.Margin = new Thickness(0);
            button.Background = Brushes.Transparent;
            button.BorderBrush = Brushes.Transparent;
            button.ToolTip = name + " (Esc)";
            Hover(button);
            return button;
        }

        // The tile's rim brightens under the pointer.
        static void Hover(Button button)
        {
            Brush rim = button.BorderBrush;
            button.MouseEnter += delegate { button.BorderBrush = Theme.Current.Brush(Theme.Current.Secondary); };
            button.MouseLeave += delegate { button.BorderBrush = rim; };
        }

        internal static void Window(Window window, string title, double width, double height)
        {
            window.Title = title;
            window.Width = width;
            window.Height = height;
            window.Background = Theme.Current.Brush(Theme.Current.Surface);
            window.Foreground = Theme.Current.Brush(Theme.Current.Text);
            window.FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            window.ShowInTaskbar = false;
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        // A window that opens while another app is in front (from the shortcut, or after a scrolling capture left the
        // browser there): above everything when it appears, then given the keyboard. stayOnTop keeps it above other
        // windows until it closes.
        internal static void Forward(Window window, bool stayOnTop)
        {
            window.Topmost = true;
            window.ContentRendered += delegate
            {
                window.Activate();
                if (!stayOnTop) window.Topmost = false;
            };
        }
    }

    public sealed class CaptureChooser : Window
    {
        bool finished;
        public event Action<CaptureMode> Chosen;
        public bool TextOnly { get; private set; }   // Copy text: an area whose text goes to the clipboard
        public event Action Cancelled;

        // Icon-font glyphs (in both Segoe Fluent Icons and Segoe MDL2 Assets), kept as escapes: invisible otherwise.
        internal const string ScreenshotGlyph = "\uF407", ScrollGlyph = "\uEC8F", TextGlyph = "\uE8D2", CloseGlyph = "\uE711";

        // A glass card like the panel's: three modes side by side, each with its icon and key, and Cancel in the corner.
        public CaptureChooser()
        {
            CaptureControls.Window(this, "Capture", 456, 220);
            CaptureControls.Forward(this, true);
            ResizeMode = ResizeMode.NoResize;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            SizeToContent = SizeToContent.Height;   // no empty band under the modes
            Theme theme = Theme.Current;
            var header = new DockPanel { Margin = new Thickness(4, 0, 0, 10), Background = Brushes.Transparent };
            Button cancel = CaptureControls.IconButton(CloseGlyph, "Cancel", Cancel);
            DockPanel.SetDock(cancel, Dock.Right);
            header.Children.Add(cancel);
            TextBlock title = CaptureControls.Text("Capture", 16);
            title.FontWeight = FontWeights.SemiBold;
            title.VerticalAlignment = VerticalAlignment.Center;
            title.Margin = new Thickness(0);
            header.Children.Add(title);
            header.MouseLeftButtonDown += delegate { try { DragMove(); } catch (InvalidOperationException) { } };
            var modes = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3 };
            modes.Children.Add(CaptureControls.ModeButton(ScreenshotGlyph, "Screenshot", "S", delegate { Choose(CaptureMode.Screenshot); }));
            modes.Children.Add(CaptureControls.ModeButton(ScrollGlyph, "Whole page", "W", delegate { Choose(CaptureMode.Scrolling); }));
            modes.Children.Add(CaptureControls.ModeButton(TextGlyph, "Copy text", "T", ChooseText));
            var panel = new StackPanel();
            panel.Children.Add(header);
            panel.Children.Add(modes);
            Content = new Border
            {
                Child = panel,
                Padding = new Thickness(14),
                Margin = new Thickness(16),   // room for the shadow
                CornerRadius = new CornerRadius(18),
                Background = theme.Brush(Color.FromArgb(0xFA, theme.Surface.R, theme.Surface.G, theme.Surface.B)),
                BorderBrush = theme.Brush(theme.TileRim),
                BorderThickness = new Thickness(1),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.45, Color = Colors.Black },
            };
            PreviewKeyDown += delegate(object sender, KeyEventArgs e) { if (HandleKey(e.Key)) e.Handled = true; };
            Closed += delegate { Cancel(); };
        }

        void Choose(CaptureMode mode)
        {
            if (finished) return;
            finished = true;
            Close();
            if (Chosen != null) Chosen(mode);
        }

        void ChooseText()
        {
            TextOnly = true;
            Choose(CaptureMode.Screenshot);
        }

        void Cancel()
        {
            if (finished) return;
            finished = true;
            Close();
            if (Cancelled != null) Cancelled();
        }

        internal bool HandleKey(Key key)
        {
            if (key == Key.S || key == Key.D1) Choose(CaptureMode.Screenshot);
            else if (key == Key.W || key == Key.D2) Choose(CaptureMode.Scrolling);
            else if (key == Key.T || key == Key.D3) ChooseText();
            else if (key == Key.Escape) Cancel();
            else return false;
            return true;
        }
    }

    public sealed class CaptureSelection
    {
        readonly CaptureMode mode;
        readonly List<CaptureSelectionOverlay> overlays = new List<CaptureSelectionOverlay>();
        readonly TaskCompletionSource<CaptureRequest> completion = new TaskCompletionSource<CaptureRequest>();
        bool finished;
        readonly CancellationToken cancel;
        CancellationTokenRegistration registration;
        readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        internal Task<CaptureRequest> Completion { get { return completion.Task; } }
        internal bool OverlaysClosed { get; private set; }

        internal CaptureSelection(CaptureMode mode, IEnumerable<MonitorInfo> monitors)
            : this(mode, monitors, CancellationToken.None) { }

        internal CaptureSelection(CaptureMode mode, IEnumerable<MonitorInfo> monitors, CancellationToken cancel)
        {
            this.mode = mode;
            this.cancel = cancel;
            foreach (MonitorInfo monitor in monitors) overlays.Add(new CaptureSelectionOverlay(this, monitor));
            registration = cancel.Register(delegate
            {
                if (dispatcher.CheckAccess()) CancelOwned();
                else dispatcher.BeginInvoke(new Action(CancelOwned));
            });
        }

        public static Task<CaptureRequest> Open(CaptureMode mode)
        {
            return Open(mode, CancellationToken.None);
        }

        public static Task<CaptureRequest> Open(CaptureMode mode, CancellationToken cancel)
        {
            if (cancel.IsCancellationRequested)
            {
                var cancelled = new TaskCompletionSource<CaptureRequest>();
                cancelled.SetCanceled();
                return cancelled.Task;
            }
            var selection = new CaptureSelection(mode, Native.Monitors(), cancel);
            if (selection.overlays.Count == 0) selection.Cancel();
            else foreach (CaptureSelectionOverlay overlay in selection.overlays) overlay.Show();
            return selection.Completion;
        }

        internal static bool TryArea(MonitorInfo monitor, Point first, Point last, out RECT area)
        {
            area = new RECT();
            double width = monitor.Bounds.Width / monitor.Scale, height = monitor.Bounds.Height / monitor.Scale;
            if (first.X < 0 || first.Y < 0 || last.X < 0 || last.Y < 0 || first.X > width || last.X > width || first.Y > height || last.Y > height) return false;
            area = CaptureDesktop.PhysicalArea(monitor, first.X, first.Y, last.X, last.Y);
            return area.Width > 0 && area.Height > 0 && area.Height <= CaptureLimits.MaxRows && (long)area.Width * area.Height <= CaptureLimits.MaxPixels;
        }

        internal bool HandleKey(Key key)
        {
            if (key != Key.Escape) return false;
            Cancel();
            return true;
        }

        void CloseOverlays()
        {
            foreach (CaptureSelectionOverlay overlay in overlays) overlay.Close();
            overlays.Clear();
            OverlaysClosed = true;
        }

        void CancelOwned()
        {
            Cancel();
            completion.TrySetCanceled();   // also cancel a chosen area while its compositor wait is pending
        }

        internal void Cancel()
        {
            if (finished) return;
            finished = true;
            CloseOverlays();
            completion.TrySetCanceled();
            registration.Dispose();
        }

        internal async void Complete(RECT area)
        {
            if (finished) return;
            finished = true;
            CloseOverlays();
            try
            {
                // The overlays must leave the compositor before any caller reads the screen.
                await Dispatcher.CurrentDispatcher.InvokeAsync(delegate { }, DispatcherPriority.ContextIdle);
                await Task.Delay(50, cancel);
                cancel.ThrowIfCancellationRequested();
                CaptureRequest request;
                if (mode == CaptureMode.Scrolling)
                {
                    POINT inside = new POINT { X = area.Left + area.Width / 2, Y = area.Top + area.Height / 2 };
                    if (!CaptureDesktop.TryCreateScrollingRequest(area, inside, out request))
                        throw new InvalidOperationException("Select a page area inside an Edge or Chrome window.");
                }
                else request = new CaptureRequest { Mode = mode, Area = area };
                cancel.ThrowIfCancellationRequested();
                completion.TrySetResult(request);
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(); }
            catch (Exception e) { completion.TrySetException(e); }
            finally { registration.Dispose(); }
        }
    }

    internal sealed class CaptureSelectionOverlay : Window
    {
        readonly CaptureSelection selection;
        readonly MonitorInfo monitor;
        readonly Canvas canvas = new Canvas();
        readonly Rectangle outline = new Rectangle { Stroke = Brushes.White, StrokeThickness = 2, Fill = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)) };
        readonly TextBlock hint = CaptureControls.Text("Drag an area on this monitor. Escape cancels.", 16);
        Point start;
        bool dragging;

        internal CaptureSelectionOverlay(CaptureSelection selection, MonitorInfo monitor)
        {
            this.selection = selection;
            this.monitor = monitor;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = new SolidColorBrush(Color.FromArgb(70, 0, 0, 0));
            ShowInTaskbar = false;
            Topmost = true;
            Cursor = Cursors.Cross;
            Width = monitor.Bounds.Width / monitor.Scale;
            Height = monitor.Bounds.Height / monitor.Scale;
            Content = canvas;
            canvas.Children.Add(outline);
            canvas.Children.Add(hint);
            Canvas.SetLeft(hint, 20);
            Canvas.SetTop(hint, 20);
            SourceInitialized += delegate { Native.MovePair(new WindowInteropHelper(this).Handle, IntPtr.Zero, monitor.Bounds.Left, monitor.Bounds.Top, monitor.Bounds.Width, monitor.Bounds.Height); };
            Loaded += delegate { Native.MovePair(new WindowInteropHelper(this).Handle, IntPtr.Zero, monitor.Bounds.Left, monitor.Bounds.Top, monitor.Bounds.Width, monitor.Bounds.Height); };
            PreviewKeyDown += delegate(object sender, KeyEventArgs e) { if (selection.HandleKey(e.Key)) e.Handled = true; };
            Closed += delegate { selection.Cancel(); };
            MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e) { start = e.GetPosition(canvas); dragging = true; CaptureMouse(); e.Handled = true; };
            MouseMove += delegate(object sender, MouseEventArgs e)
            {
                if (!dragging) return;
                Point end = e.GetPosition(canvas);
                Canvas.SetLeft(outline, Math.Min(start.X, end.X));
                Canvas.SetTop(outline, Math.Min(start.Y, end.Y));
                outline.Width = Math.Abs(end.X - start.X);
                outline.Height = Math.Abs(end.Y - start.Y);
            };
            MouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs e)
            {
                if (!dragging) return;
                dragging = false;
                ReleaseMouseCapture();
                RECT area;
                if (CaptureSelection.TryArea(monitor, start, e.GetPosition(canvas), out area)) selection.Complete(area);
                else hint.Text = "Select a nonempty area within one monitor. Escape cancels.";
                e.Handled = true;
            };
        }
    }

    public sealed class CaptureProgressWindow : Window
    {
        readonly TextBlock status = CaptureControls.Text("Preparing capture.");
        RECT placement;
        bool placed, closed;
        public event Action StopRequested;
        public event Action CancelRequested;
        public bool OverlapsSelection { get; private set; }
        public Button StopButton { get; private set; }
        public Button CancelButton { get; private set; }
        public string StatusText { get { return status.Text; } }

        public CaptureProgressWindow()
        {
            CaptureControls.Window(this, "Capture progress", 360, 130);
            ShowActivated = false;
            Topmost = true;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            var panel = new StackPanel { Margin = new Thickness(10) };
            panel.Children.Add(status);
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            StopButton = CaptureControls.Button("_Stop", delegate { if (StopRequested != null) StopRequested(); });
            CancelButton = CaptureControls.Button("_Cancel", delegate { if (CancelRequested != null) CancelRequested(); });
            actions.Children.Add(StopButton);
            actions.Children.Add(CancelButton);
            panel.Children.Add(actions);
            Content = panel;
            SourceInitialized += delegate
            {
                IntPtr handle = new WindowInteropHelper(this).Handle;
                Native.AddExStyle(handle, Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);
                HwndSource.FromHwnd(handle).AddHook(NoActivate);
                if (placed) Native.MovePair(handle, IntPtr.Zero, placement.Left, placement.Top, placement.Width, placement.Height);
            };
            Closed += delegate { closed = true; };
        }

        internal static IntPtr NoActivate(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == 0x0021) { handled = true; return new IntPtr(3); }
            return IntPtr.Zero;
        }

        public void Place(RECT area, MonitorInfo monitor)
        {
            RECT work = monitor.Work.Width > 0 ? monitor.Work : monitor.Bounds;
            int width = Math.Min(work.Width, (int)Math.Ceiling(Width * monitor.Scale));
            int height = Math.Min(work.Height, (int)Math.Ceiling(Height * monitor.Scale));
            int left = Math.Max(work.Left, Math.Min(area.Left, work.Right - width));
            int top = area.Bottom + 8;
            if (top + height > work.Bottom) top = area.Top - height - 8;
            if (top < work.Top)
            {
                top = Math.Max(work.Top, Math.Min(area.Top, work.Bottom - height));
                left = area.Right + 8;
                if (left + width > work.Right) left = area.Left - width - 8;
                if (left < work.Left) left = work.Left;
            }
            placement = new RECT { Left = left, Top = top, Right = left + width, Bottom = top + height };
            OverlapsSelection = placement.Left < area.Right && placement.Right > area.Left && placement.Top < area.Bottom && placement.Bottom > area.Top;
            placed = true;
            IntPtr handle = new WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero) Native.MovePair(handle, IntPtr.Zero, left, top, width, height);
        }

        public void Update(CaptureProgress progress)
        {
            if (progress == null) return;
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(delegate { Update(progress); })); return; }
            if (!closed) status.Text = (progress.Paused ? "Paused. " : "") + progress.Status + "\n" + progress.FrameCount + " frames, " + progress.Rows + " rows";
        }
    }
}
