using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Capsule
{
    // The full panel (spec §2), beside the capsule on the screen side. Unlike the capsule it takes keyboard focus, so
    // the Ideas box can be typed in. It slides and fades in and out over 150 ms; Esc or clicking elsewhere closes it.
    public sealed class PanelWindow : Window
    {
        public const int MotionMs = 150;
        const double SlideDips = 12;

        readonly PanelView view = new PanelView();
        readonly TranslateTransform slide = new TranslateTransform();
        readonly DispatcherTimer hideLater;
        GlassLayer glass;   // the live blur below the panel; null when the theme is solid
        IntPtr hwnd;
        RECT capsule;
        bool leftEdge;
        MonitorInfo monitor;
        bool closing;

        public PanelWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            Title = "Capsule";
            view.HorizontalAlignment = HorizontalAlignment.Left;
            view.VerticalAlignment = VerticalAlignment.Top;
            view.RenderTransform = slide;
            view.Resized += delegate
            {
                if (!IsOpen) return;
                view.Relayout();
                Place();
            };
            Content = view;
            SourceInitialized += delegate
            {
                hwnd = new WindowInteropHelper(this).Handle;
                Native.AddExStyle(hwnd, Native.WS_EX_TOOLWINDOW);   // out of Alt-Tab and the taskbar, yet it can take focus
            };
            PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key != Key.Escape) return;
                e.Handled = true;
                Dismiss();
            };
            Deactivated += delegate { Dismiss(); };
            // Alt+F4, or any other close request, only dismisses: a WPF window that has been closed can never be shown again.
            // WPF ignores Cancel when Windows is shutting down or the session is ending, so the app still exits then; but a
            // panel.Close() of our own on the way out would be cancelled here without a word.
            Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e) { e.Cancel = true; Dismiss(); };
            IsVisibleChanged += delegate
            {
                if (glass == null) return;
                if (!IsVisible)
                {
                    glass.Hide();
                    return;
                }
                glass.Show();
                Native.KeepPairOnTop(hwnd, glass.Handle);
            };
            hideLater = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(MotionMs) };
            hideLater.Tick += delegate
            {
                hideLater.Stop();
                if (!closing) return;   // opened again meanwhile: a tick that was already queued when Open stopped the timer mustn't hide it
                Hide();
                closing = false;
                view.ShowBoard();   // the settings, and a secret typed into them but not saved, don't wait hidden for the next open
            };
        }

        public PanelView View { get { return view; } }
        public IntPtr Handle { get { return hwnd; } }
        public bool IsOpen { get { return IsVisible && !closing; } }
        public bool OpenedByShortcut { get; private set; }   // the last open came from the global shortcut
        IntPtr GlassHandle { get { return glass != null ? glass.Handle : IntPtr.Zero; } }
        double Away { get { return leftEdge ? -SlideDips : SlideDips; } }   // where the panel slides in from and out to: toward the capsule

        // Live blur below the panel on or off. False when it was asked for but couldn't be made.
        public bool SetGlass(bool on)
        {
            if (on && glass == null)
            {
                glass = GlassLayer.Create();
                if (glass == null) return false;
                if (IsVisible)
                {
                    Place();
                    if (glass.Broken)   // its shape failed: no blur here, and the caller falls back to solid glass
                    {
                        glass.Dispose();
                        glass = null;
                        return false;
                    }
                    glass.Show();
                }
                else glass.Rest(0f);   // hidden like the panel, so even its first fade-in has a start to run from
            }
            if (!on && glass != null)
            {
                glass.Dispose();
                glass = null;
            }
            return true;
        }

        // Opens beside the capsule (its window rect in physical pixels), or brings an open panel to the front.
        public void Open(PanelModel model, RECT capsuleRect, bool left, MonitorInfo on, bool byShortcut)
        {
            if (on == null) return;
            bool wasClosing = closing;
            if (closing)
            {
                hideLater.Stop();
                closing = false;
            }
            capsule = capsuleRect;
            leftEdge = left;
            monitor = on;
            OpenedByShortcut = byShortcut;
            bool appearing = !IsVisible;
            new WindowInteropHelper(this).EnsureHandle();
            if (appearing || wasClosing || byShortcut) view.ShowBoard();   // it always opens on the tiles: also when turned round mid-close, and by the shortcut with the settings showing
            view.Update(model);
            Place();
            if (appearing)
            {
                Rest();   // faded out and slid toward the capsule: where the fade-in starts
                Show();
                Place();   // as with the card: showing on a monitor with other scaling can resize the window
                Animate(true);
            }
            else if (wasClosing) Animate(true);   // opened again during the close: turn round from wherever it has got to
            Activate();
            if (byShortcut) view.FocusIdeas();
        }

        // New content while open: the panel resizes to fit.
        public void Refresh(PanelModel model)
        {
            if (!IsOpen) return;
            view.Update(model);
            Place();
        }

        // The theme changed: recolour, and redraw if open.
        public void Restyle(PanelModel model)
        {
            view.Restyle();
            if (IsOpen) Refresh(model);
        }

        public void Dismiss()
        {
            if (!IsVisible || closing) return;
            closing = true;
            Animate(false);
            hideLater.Start();
        }

        // Sizes the window to the view and puts it beside the capsule, the glass right below it.
        void Place()
        {
            if (monitor == null || hwnd == IntPtr.Zero) return;
            view.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double scale = monitor.Scale;
            int width = (int)Math.Ceiling((view.DesiredSize.Width - 2 * PanelView.ShadowMargin) * scale);
            int height = (int)Math.Ceiling((view.DesiredSize.Height - 2 * PanelView.ShadowMargin) * scale);
            RECT body = Layout.PanelRect(capsule, leftEdge, monitor.Work, width, height, (int)Math.Round(8 * scale));
            RECT r = Layout.Inflate(body, (int)Math.Round(PanelView.ShadowMargin * scale));
            Native.MovePair(hwnd, GlassHandle, r.Left, r.Top, r.Width, r.Height);
            if (glass != null)
            {
                // The outline is the panel's own; in the window it sits inside the room for the shadow.
                Geometry shape = view.Outline.CloneCurrentValue();
                shape.Transform = new TranslateTransform(PanelView.ShadowMargin, PanelView.ShadowMargin);
                glass.SetShape(shape, scale, r.Width, r.Height);
            }
        }

        // Faded out and slid toward the capsule, any running animation dropped: where a fade-in from hidden starts.
        void Rest()
        {
            slide.BeginAnimation(TranslateTransform.XProperty, null);
            slide.X = Away;
            view.BeginAnimation(UIElement.OpacityProperty, null);
            view.Opacity = 0;
            if (glass != null) glass.Rest((float)(Away * monitor.Scale));
        }

        // In: from a little toward the capsule, fading up. Out: the reverse. Both only say where to end up, so each runs
        // from wherever the panel is at that moment: turning round halfway (a second click) never jumps. The blur has the
        // same ease-out, so it stays in step with the content.
        void Animate(bool appear)
        {
            double away = Away;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var move = new DoubleAnimation(appear ? 0 : away, TimeSpan.FromMilliseconds(MotionMs));
            move.EasingFunction = ease;
            Timeline.SetDesiredFrameRate(move, NotchView.AnimationFps);
            slide.BeginAnimation(TranslateTransform.XProperty, move);
            var fade = new DoubleAnimation(appear ? 1 : 0, TimeSpan.FromMilliseconds(MotionMs));
            fade.EasingFunction = ease;
            Timeline.SetDesiredFrameRate(fade, NotchView.AnimationFps);
            view.BeginAnimation(UIElement.OpacityProperty, fade);
            if (glass != null) glass.Animate(appear, (float)(away * monitor.Scale), MotionMs);
        }
    }
}
