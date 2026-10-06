using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Vec2 = System.Numerics.Vector2;

namespace Capsule
{
    // The card window shown beside a notch cell. Like the notch, it never takes focus. It unfolds from the ring it belongs
    // to and folds back into it (polish spec §2), the panel's way: the window sits at its final size and place, and only
    // its content and its blur move. Asked to show another ring's card while open, it glides there instead. With Windows'
    // Animation effects off it appears and disappears at once.
    public sealed class HoverCard : Window
    {
        readonly CardView view = new CardView();
        readonly ScaleTransform zoom = new ScaleTransform();            // the unfold: about the anchor, StartScale to 1
        readonly TranslateTransform shift = new TranslateTransform();   // makes up for an anchor moved halfway (CardMotion.Reanchor)
        readonly DispatcherTimer hideLater;   // hides the card once it has folded away
        readonly DispatcherTimer glide;       // moves the window to the glide's frame that is due
        readonly Stopwatch glideClock = new Stopwatch();
        GlassLayer glass;   // the live blur below the card; null when the theme is solid
        IntPtr hwnd;
        Action<bool> lastBuild;   // draws the content last shown, the usage or a request, pointing right or left
        bool focusable;           // the Other… box has the keyboard: the card may be activated meanwhile
        IntPtr cameFrom;          // the window that had the keyboard before the card took it
        RECT lastCell;
        bool lastLeftEdge;
        MonitorInfo lastMonitor;
        RECT target;          // where the window belongs: its rect once any glide is over
        List<POINT> frames;   // the glide's frames, while one runs
        int framesShown;
        bool closing;         // folding away: hidden when hideLater ticks, unless turned round first

        public HoverCard()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            Title = "Capsule card";
            view.HorizontalAlignment = HorizontalAlignment.Left;
            view.VerticalAlignment = VerticalAlignment.Top;
            view.RenderTransform = CardMotion.ContentTransform(zoom, shift);
            Content = view;
            hideLater = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(CardMotion.CloseMs) };
            hideLater.Tick += delegate
            {
                hideLater.Stop();
                if (closing) HideNow();   // turned round meanwhile: a tick that was already queued mustn't hide it
            };
            // Windows' timer tick (15.6 ms): each tick moves the window to the frame that is due by the clock, if it isn't there yet.
            glide = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
            glide.Tick += delegate { Step(); };
            SourceInitialized += delegate
            {
                hwnd = new WindowInteropHelper(this).Handle;
                Native.AddExStyle(hwnd, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
            };
            DpiChanged += delegate
            {
                if (lastBuild != null && IsVisible && !closing)
                    Dispatcher.BeginInvoke(new Action(delegate { Present(lastBuild, lastCell, lastLeftEdge, lastMonitor); }));
            };
            IsVisibleChanged += delegate
            {
                if (glass == null) return;
                if (IsVisible) glass.Show();
                else glass.Hide();
            };
        }

        public IntPtr Handle { get { return hwnd; } }
        public CardView View { get { return view; } }
        public bool RequestsHeld;   // Capsule is holding requests: the request view keeps its text only while there are any
        public bool IsTyping { get { return view.Prompt.OtherBox.IsKeyboardFocused; } }   // in the Other… box
        public bool IsOpen { get { return IsVisible && !closing; } }      // showing, and not folding away
        public bool IsClosing { get { return IsVisible && closing; } }    // folding back into its ring
        IntPtr GlassHandle { get { return glass != null ? glass.Handle : IntPtr.Zero; } }

        // Live blur below the card on or off. False when it was asked for but couldn't be made.
        public bool SetGlass(bool on)
        {
            if (on && glass == null)
            {
                glass = GlassLayer.Create();
                if (glass == null) return false;
                if (IsVisible && lastBuild != null)
                {
                    // A card that is showing when the blur comes on gets its glass at once, not at its next show.
                    RECT now;
                    Native.GetWindowRect(hwnd, out now);
                    Place(now);
                    if (glass.Broken)   // its shape failed: no blur here, and the caller falls back to solid glass
                    {
                        glass.Dispose();
                        glass = null;
                        return false;
                    }
                    // A new layer starts out fully shown and unscaled: pose it where the motion now running ends (not at
                    // the content's present values, which mid-unfold would freeze a half pose), or a fold would shrink it
                    // toward the window's top-left.
                    Point anchor = AnchorPx;
                    glass.Pose(closing ? 0f : 1f, closing ? (float)CardMotion.StartScale : 1f, new Vec2((float)anchor.X, (float)anchor.Y), new Vec2());
                    glass.Show();
                }
            }
            if (!on && glass != null)
            {
                glass.Dispose();
                glass = null;
            }
            return true;
        }

        // cell: the ring's rectangle in physical pixels. The card goes on the screen side of it, pointer aimed at its middle.
        // Hidden, it unfolds from the ring. Folding away, it turns round from wherever it has got to. Open beside another
        // ring, it takes the new content and size where it is, then glides beside this ring. Otherwise its content is
        // refreshed in place.
        public void ShowBeside(CardModel model, RECT cell, bool leftEdge, MonitorInfo monitor)
        {
            GiveUpFocus();
            Present(delegate(bool pointRight) { view.Show(model, pointRight); }, cell, leftEdge, monitor);
        }

        // The calendar's card (calendar spec §2), shown the same way.
        public void ShowCalendarBeside(CalendarCardModel model, RECT cell, bool leftEdge, MonitorInfo monitor)
        {
            GiveUpFocus();
            Present(delegate(bool pointRight) { view.ShowCalendar(model, pointRight); }, cell, leftEdge, monitor);
        }

        // A held request in place of the usage (act-from-notch spec §3), shown the same way. The card keeps the keyboard
        // only while the Other… box is open, and gets the caret back into it after a redraw.
        public void ShowPromptBeside(PromptCardModel model, RECT cell, bool leftEdge, MonitorInfo monitor)
        {
            bool typing = IsTyping;
            if (!model.OtherOpen) GiveUpFocus();
            Present(delegate(bool pointRight) { view.ShowPrompt(model, pointRight); }, cell, leftEdge, monitor);
            if (model.OtherOpen && typing) view.Prompt.FocusOther();
        }

        // The Other… box was opened: the card takes the keyboard. A card that never activates can't, so it may now, until
        // the box closes or the card goes (GiveUpFocus). The click that opened the box came to Capsule, which lets
        // Windows bring the card to the front.
        public void TakeFocus()
        {
            if (hwnd == IntPtr.Zero || !IsVisible || !view.ShowingPrompt) return;
            if (!focusable)
            {
                Native.RemoveExStyle(hwnd, Native.WS_EX_NOACTIVATE);
                focusable = true;
            }
            // Whoever had the keyboard before the card is given it back when the box closes.
            IntPtr before = Native.Foreground();
            if (before != hwnd && before != IntPtr.Zero) cameFrom = before;
            Activate();
            view.Prompt.FocusOther();
        }

        // Back to a card that never takes the keyboard or the front. If the card still has the keyboard, it goes back to
        // the window that had it before the box opened: without that the card would keep it, with nothing to type in.
        void GiveUpFocus()
        {
            if (!focusable) return;
            focusable = false;
            IntPtr back = cameFrom;
            cameFrom = IntPtr.Zero;
            if (back != IntPtr.Zero && Native.Foreground() == hwnd) Native.GiveForegroundTo(back);
            Native.AddExStyle(hwnd, Native.WS_EX_NOACTIVATE);
        }

        void Present(Action<bool> build, RECT cell, bool leftEdge, MonitorInfo monitor)
        {
            if (monitor == null) return;
            bool appearing = !IsVisible;
            bool otherRing = !appearing && !Same(cell, lastCell);
            lastBuild = build;
            lastCell = cell;
            lastLeftEdge = leftEdge;
            lastMonitor = monitor;
            new WindowInteropHelper(this).EnsureHandle();
            build(!leftEdge);
            view.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            // The card itself, without the room for its shadow, is what goes beside the cell.
            double cardWidth = view.DesiredSize.Width - 2 * CardView.ShadowMargin;
            double cardHeight = view.DesiredSize.Height - 2 * CardView.ShadowMargin;
            double scale = monitor.Scale;
            int width = (int)Math.Ceiling(cardWidth * scale);
            int height = (int)Math.Ceiling(cardHeight * scale);
            int pointerPx;
            RECT card = Layout.CardRect(cell, leftEdge, monitor.Work, width, height, (int)Math.Round(4 * scale), out pointerPx);
            double lowest = CardView.Radius + CardView.PointerHeight / 2;
            double highest = cardHeight - CardView.Radius - CardView.PointerHeight / 2;
            view.SetPointer(Math.Max(lowest, Math.Min(highest, pointerPx / scale)));
            target = Layout.Inflate(card, (int)Math.Round(CardView.ShadowMargin * scale));
            bool motion = CardMotion.Duration(CardMotion.OpenMs, SystemParameters.ClientAreaAnimation) > 0;
            if (appearing)
            {
                StopGlide();
                closing = false;
                hideLater.Stop();
                Place(target);
                // Small and transparent at the ring, to unfold from there; with Animation effects off, there at once.
                PoseAt(motion ? 0 : 1, motion ? CardMotion.StartScale : 1, new Vector());
                Show();
                // Showing on a monitor whose scaling differs from the window's last one can apply Windows' suggested
                // rect for the new DPI, and DpiChanged skips hidden windows: put the card back where it belongs.
                Native.MovePair(hwnd, GlassHandle, target.Left, target.Top, target.Width, target.Height);
                Animate(true);   // at once, when Animation effects are off
                return;
            }
            if (otherRing && motion)
            {
                RECT now;
                Native.GetWindowRect(hwnd, out now);
                var resized = new RECT { Left = now.Left, Top = now.Top, Right = now.Left + target.Width, Bottom = now.Top + target.Height };
                Place(resized);   // the new content's size where the card is now, then a glide beside the new ring
                StartGlide(now.Left, now.Top);
            }
            else
            {
                StopGlide();
                Place(target);
            }
            if (closing)
            {
                closing = false;
                hideLater.Stop();
                Animate(true);   // at once, when Animation effects are off
            }
        }

        // Folds back into its ring, then hides; at once while Animation effects are off.
        public void Fold()
        {
            if (!IsVisible || closing) return;
            if (CardMotion.Duration(CardMotion.CloseMs, SystemParameters.ClientAreaAnimation) == 0)
            {
                HideNow();
                return;
            }
            closing = true;
            Animate(false);
            hideLater.Stop();
            hideLater.Start();
        }

        // Gone at once: the panel opens, the capsule moves or hides, its menu opens.
        public void HideNow()
        {
            if (!RequestsHeld) view.Prompt.Clear();
            GiveUpFocus();
            StopGlide();
            hideLater.Stop();
            closing = false;
            Hide();
        }

        static bool Same(RECT a, RECT b) { return a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom; }

        // The window at r (pixels), its blur right below it, cut to the card's outline.
        void Place(RECT r)
        {
            Native.MovePair(hwnd, GlassHandle, r.Left, r.Top, r.Width, r.Height);
            if (glass != null)
            {
                // The outline is the card's own; in the window it sits inside the room for the shadow.
                Geometry shape = view.Outline.CloneCurrentValue();
                shape.Transform = new TranslateTransform(CardView.ShadowMargin, CardView.ShadowMargin);
                glass.SetShape(shape, lastMonitor.Scale, r.Width, r.Height);
            }
        }

        // The anchor of the place the card belongs: its ring's centre on the capsule's inner edge, in window pixels.
        Point AnchorPx { get { return CardMotion.AnchorPx(target, lastCell, lastLeftEdge); } }

        // Content and blur in a pose at once, scaled about the anchor: opacity, size, and the content's shift in DIPs.
        void PoseAt(double opacity, double size, Vector offset)
        {
            double scale = lastMonitor.Scale;
            Point anchor = AnchorPx;
            CardMotion.Pose(view, zoom, shift, opacity, size, CardMotion.ToView(anchor, scale, CardView.ShadowMargin), offset);
            if (glass != null) glass.Pose((float)opacity, (float)size, new Vec2((float)anchor.X, (float)anchor.Y), new Vec2((float)(offset.X * scale), (float)(offset.Y * scale)));
        }

        // Unfolds (open) or folds the content and its blur, aimed at the anchor of the place the card belongs. When that has
        // moved since the motion began (the card glided to another ring), the motion is re-aimed where the card stands, so
        // nothing jumps.
        void Animate(bool open)
        {
            Point centre = CardMotion.ToView(AnchorPx, lastMonitor.Scale, CardView.ShadowMargin);
            if (Math.Abs(zoom.CenterX - centre.X) > 0.01 || Math.Abs(zoom.CenterY - centre.Y) > 0.01)
            {
                double size = zoom.ScaleX;
                Vector offset = CardMotion.Reanchor(new Point(zoom.CenterX, zoom.CenterY), centre, size, new Vector(shift.X, shift.Y));
                PoseAt(view.Opacity, size, offset);
            }
            // No time at all while Animation effects are off: the content is then at its end pose at once (MoveContent), and so is the blur.
            int ms = CardMotion.Duration(open ? CardMotion.OpenMs : CardMotion.CloseMs, SystemParameters.ClientAreaAnimation);
            CardMotion.MoveContent(view, zoom, shift, open ? 1 : 0, open ? 1 : CardMotion.StartScale, ms);
            if (glass == null) return;
            if (ms <= 0)
            {
                Point anchor = AnchorPx;
                glass.Pose(open ? 1f : 0f, open ? 1f : (float)CardMotion.StartScale, new Vec2((float)anchor.X, (float)anchor.Y), new Vec2());
                return;
            }
            glass.Animate(open, 0f, ms);
            glass.AnimateScale(open ? 1f : (float)CardMotion.StartScale, ms);
        }

        void StartGlide(int x, int y)
        {
            var from = new POINT { X = x, Y = y };
            var to = new POINT { X = target.Left, Y = target.Top };
            frames = CardMotion.Glide(from, to, CardMotion.GlideMs, NotchView.AnimationFps);
            framesShown = 0;
            glideClock.Restart();
            glide.Stop();
            glide.Start();
        }

        // The glide's frame that is due: the window and its blur move together in one step, never resized.
        void Step()
        {
            if (frames == null)
            {
                StopGlide();
                return;
            }
            int due = CardMotion.FramesDue(glideClock.ElapsedMilliseconds, frames.Count, CardMotion.GlideMs, NotchView.AnimationFps);
            if (due > framesShown)
            {
                framesShown = due;
                POINT p = frames[due - 1];
                Native.MoveToPairKeepingOrder(hwnd, GlassHandle, p.X, p.Y);   // not lifted over the capsule's inner edge
            }
            if (framesShown >= frames.Count) StopGlide();
        }

        void StopGlide()
        {
            glide.Stop();
            frames = null;
        }
    }
}
