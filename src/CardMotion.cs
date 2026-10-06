using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Capsule
{
    // The hover card's motion (polish spec §2), apart from its window: where it unfolds from, how it starts, how its
    // content moves, and the glide from one ring to another. Geometry and WPF transforms only, so it is tested without a
    // window. HoverCard moves the card's blur the same way, through GlassLayer.
    public static class CardMotion
    {
        public const int OpenMs = 200, CloseMs = 150, GlideMs = 120;
        public const double StartScale = 0.33;   // the card's size where it unfolds from and folds into, 1 being full size

        // The anchor, in pixels from the card window's top-left: the hovered ring's centre on the capsule's inner edge, the
        // edge facing the card. cell is the ring's rect on screen as NotchWindow.CellScreenRect gives it: it spans the
        // capsule's width, so its side away from the screen edge is the capsule's inner edge.
        public static Point AnchorPx(RECT window, RECT cell, bool leftEdge)
        {
            return new Point((leftEdge ? cell.Right : cell.Left) - window.Left, (cell.Top + cell.Bottom) / 2.0 - window.Top);
        }

        // A point in the card window's pixels, in the card view's own DIPs: the view sits `margin` DIPs inside the window,
        // the room for its shadow. scale: the monitor's DPI / 96.
        public static Point ToView(Point px, double scale, double margin)
        {
            return new Point(px.X / scale - margin, px.Y / scale - margin);
        }

        // How the card's content is drawn: scaled about the zoom's centre, then shifted. GlassLayer's blur is drawn the same
        // way (Scale about CenterPoint, then Offset), so the two stay in step.
        public static Transform ContentTransform(ScaleTransform zoom, TranslateTransform shift)
        {
            var drawn = new TransformGroup();
            drawn.Children.Add(zoom);
            drawn.Children.Add(shift);
            return drawn;
        }

        // How long a motion takes: no time at all while Windows' Animation effects are off (SystemParameters.ClientAreaAnimation).
        public static int Duration(int ms, bool animationEffects) { return animationEffects ? ms : 0; }

        // Puts the content in a pose at once, dropping any motion: its opacity, its size (1 = full) about centre (the view's
        // DIPs), and its shift.
        public static void Pose(UIElement content, ScaleTransform zoom, TranslateTransform shift, double opacity, double size, Point centre, Vector offset)
        {
            Set(content, UIElement.OpacityProperty, opacity);
            Set(zoom, ScaleTransform.ScaleXProperty, size);
            Set(zoom, ScaleTransform.ScaleYProperty, size);
            zoom.CenterX = centre.X;
            zoom.CenterY = centre.Y;
            Set(shift, TranslateTransform.XProperty, offset.X);
            Set(shift, TranslateTransform.YProperty, offset.Y);
        }

        static void Set(IAnimatable target, DependencyProperty property, double value)
        {
            target.BeginAnimation(property, null);
            ((DependencyObject)target).SetValue(property, value);
        }

        // Moves the content to `opacity` and `size`, and its shift back to none, over ms milliseconds on the panel's cubic
        // ease-out; at once when ms is 0 (Animation effects off: HoverCard.Animate passes CardMotion.Duration's result). Each
        // motion has only its end (Motion), so one turned round halfway starts from wherever the content has got to, and
        // nothing jumps.
        public static void MoveContent(UIElement content, ScaleTransform zoom, TranslateTransform shift, double opacity, double size, int ms)
        {
            if (ms <= 0)
            {
                Pose(content, zoom, shift, opacity, size, new Point(zoom.CenterX, zoom.CenterY), new Vector());
                return;
            }
            Run(content, UIElement.OpacityProperty, opacity, ms);
            Run(zoom, ScaleTransform.ScaleXProperty, size, ms);
            Run(zoom, ScaleTransform.ScaleYProperty, size, ms);
            Run(shift, TranslateTransform.XProperty, 0, ms);
            Run(shift, TranslateTransform.YProperty, 0, ms);
        }

        static void Run(IAnimatable target, DependencyProperty property, double to, int ms)
        {
            target.BeginAnimation(property, Motion(to, ms));
        }

        // One property's motion: from wherever it is (From is left unset) to `to`, over ms milliseconds on the cubic
        // ease-out, at the notch's frame rate.
        internal static DoubleAnimation Motion(double to, int ms)
        {
            var motion = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            Timeline.SetDesiredFrameRate(motion, NotchView.AnimationFps);
            return motion;
        }

        // The content is drawn at `size` about oldCentre, then shifted by `shift`. Drawn about newCentre instead, it needs
        // this shift to stay exactly where it is: so a motion can be re-aimed at another ring's anchor halfway through.
        // Shifts are animated back to none along with the size, and they vanish together at full size.
        public static Vector Reanchor(Point oldCentre, Point newCentre, double size, Vector shift)
        {
            return shift + (1 - size) * (oldCentre - newCentre);
        }

        // How many of a glide's frames are due `elapsed` milliseconds after it began: frame i (from 1) at the end of the i-th
        // 1/fps of a second, the last one at ms. Picked by the clock, not counted in timer ticks: Windows' timers fire only
        // on its 15.6 ms tick, so a 33 ms timer runs every 47 ms and four frames would take 190 ms instead of 120.
        public static int FramesDue(long elapsed, int count, int ms, int fps)
        {
            if (elapsed >= ms) return count;
            return Math.Max(0, Math.Min(count - 1, (int)(elapsed * fps / 1000)));
        }

        // The glide from one ring's place to another's: the window's top-left at the end of each frame, fps frames a second
        // over ms milliseconds, on the same cubic ease-out. The last frame is exactly `to`.
        public static List<POINT> Glide(POINT from, POINT to, int ms, int fps)
        {
            int count = Math.Max(1, (int)Math.Ceiling(ms * fps / 1000.0));
            var frames = new List<POINT>();
            for (int i = 1; i < count; i++)
            {
                double t = i * 1000.0 / fps / ms;
                double eased = 1 - Math.Pow(1 - t, 3);
                var p = new POINT();
                p.X = from.X + (int)Math.Round((to.X - from.X) * eased);
                p.Y = from.Y + (int)Math.Round((to.Y - from.Y) * eased);
                frames.Add(p);
            }
            frames.Add(to);
            return frames;
        }
    }
}
