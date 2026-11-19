using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Capsule
{
    // News from the capsule itself: a small pill that unfolds from a ring for a few seconds ("Copied 4 lines of text",
    // "Mic muted", the song that just started) and folds back into it, in place of a Windows notification. It never takes
    // focus or clicks, and never shows over a request: the Controller asks only while the card and panel are closed.
    // Windows notifications stay for alerts.
    public sealed class NewsPill : Window
    {
        public const int ShowMs = 2600;
        const double Gap = 8, ShadowMargin = 18, PillHeight = 44;
        readonly Border surface = new Border();
        readonly TextBlock glyph = new TextBlock(), title = new TextBlock(), detail = new TextBlock();
        readonly ScaleTransform zoom = new ScaleTransform(1, 1);
        readonly DispatcherTimer fold;
        IntPtr hwnd;

        public NewsPill()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            IsHitTestVisible = false;
            Title = "Capsule news";
            glyph.FontFamily = new FontFamily(PanelView.IconFont);
            glyph.FontSize = 15;
            glyph.VerticalAlignment = VerticalAlignment.Center;
            glyph.Margin = new Thickness(0, 0, 10, 0);
            title.FontSize = 13;
            title.FontWeight = FontWeights.SemiBold;
            detail.FontSize = 11.5;
            foreach (TextBlock t in new[] { title, detail })
            {
                t.FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
                t.TextTrimming = TextTrimming.CharacterEllipsis;
                t.MaxWidth = 260;
            }
            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            words.Children.Add(title);
            words.Children.Add(detail);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(glyph);
            row.Children.Add(words);
            surface.Child = row;
            surface.MinHeight = PillHeight;
            surface.Padding = new Thickness(14, 7, 18, 7);
            surface.CornerRadius = new CornerRadius(PillHeight / 2);
            surface.BorderThickness = new Thickness(1);
            surface.Margin = new Thickness(ShadowMargin);
            surface.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.4, Color = Colors.Black };
            surface.RenderTransform = zoom;
            surface.Opacity = 0;
            Content = surface;
            fold = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ShowMs) };
            fold.Tick += delegate { fold.Stop(); Fold(); };
            SourceInitialized += delegate
            {
                hwnd = new WindowInteropHelper(this).Handle;
                Native.AddExStyle(hwnd, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT);
            };
        }

        // Unfolds beside `cell` (a ring's rect on screen, as NotchWindow.CellScreenRect gives it), toward the screen's middle.
        public void Show(string icon, string heading, string more, RECT cell, bool leftEdge, MonitorInfo monitor)
        {
            if (monitor == null) return;
            Fill(icon, heading, more);
            fold.Stop();
            new WindowInteropHelper(this).EnsureHandle();
            surface.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double scale = monitor.Scale;
            int width = (int)Math.Round((surface.DesiredSize.Width + 2 * ShadowMargin) * scale);
            int height = (int)Math.Round((surface.DesiredSize.Height + 2 * ShadowMargin) * scale);
            int shadow = (int)Math.Round(ShadowMargin * scale), gap = (int)Math.Round(Gap * scale);
            int x = leftEdge ? cell.Right + gap - shadow : cell.Left - gap - width + shadow;
            int y = (cell.Top + cell.Bottom) / 2 - height / 2;
            surface.RenderTransformOrigin = new Point(leftEdge ? 0 : 1, 0.5);   // it grows out of the capsule's side
            if (!IsVisible) Show();
            Native.MovePair(hwnd, IntPtr.Zero, x, y, width, height);
            Animate(1, CardMotion.OpenMs);
            fold.Start();
        }

        // What it says, in the theme's colours. Separate from Show so the previews can draw it without a window.
        internal FrameworkElement Fill(string icon, string heading, string more)
        {
            Theme theme = Theme.Current;
            surface.Background = theme.Brush(Color.FromArgb(0xF5, theme.Surface.R, theme.Surface.G, theme.Surface.B));
            surface.BorderBrush = theme.Brush(theme.TileRim);
            glyph.Foreground = title.Foreground = theme.Brush(theme.Text);
            detail.Foreground = theme.Brush(theme.Secondary);
            glyph.Text = icon;
            title.Text = heading;
            detail.Text = more ?? "";
            detail.Visibility = string.IsNullOrEmpty(more) ? Visibility.Collapsed : Visibility.Visible;
            return surface;
        }

        void Fold()
        {
            Animate(0, CardMotion.CloseMs);
            var hide = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(CardMotion.CloseMs + 20) };
            hide.Tick += delegate { hide.Stop(); if (!fold.IsEnabled) Hide(); };
            hide.Start();
        }

        // Grows from a third of its size, fading in; or the other way. At once with Windows' animation effects off.
        void Animate(double to, int ms)
        {
            ms = CardMotion.Duration(ms, SystemParameters.ClientAreaAnimation);
            double size = to > 0 ? 1 : CardMotion.StartScale;
            if (ms == 0)
            {
                surface.Opacity = to;
                zoom.ScaleX = zoom.ScaleY = size;
                return;
            }
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var time = TimeSpan.FromMilliseconds(ms);
            if (to > 0 && surface.Opacity < 0.01) { zoom.ScaleX = zoom.ScaleY = CardMotion.StartScale; }
            surface.BeginAnimation(OpacityProperty, new DoubleAnimation(to, time) { EasingFunction = ease });
            zoom.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(size, time) { EasingFunction = ease });
            zoom.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(size, time) { EasingFunction = ease });
        }

        // Folds away at once: the card or the panel is opening over it.
        public void Dismiss()
        {
            fold.Stop();
            surface.BeginAnimation(OpacityProperty, null);
            surface.Opacity = 0;
            if (IsVisible) Hide();
        }
    }
}
