using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WEllipse = System.Windows.Shapes.Ellipse;
using WPath = System.Windows.Shapes.Path;

namespace Capsule
{
    // What one cell of the notch shows.
    public sealed class CellModel
    {
        public string Provider = "";
        public string Glyph;              // an icon-font glyph in place of the provider's mark (the calendar's), or null
        public string Text = "–";         // "73%", "–", "Sign in"; the calendar's "2:30", "now" or "—"
        public bool ShowArc;
        public double Used;               // percent; sets the arc length
        public string ArcColor = Palette.Green;
        public bool Dimmed;
        public bool SignIn;
        public string Activity;           // null, States.Working or States.Waiting (Codex: only Working)
    }

    // The glass pill and its cells, laid out in DIPs for the right screen edge and mirrored for the left.
    // NotchWindow shows it on screen; Preview renders it to PNG.
    public sealed class NotchView : Canvas
    {
        public const double PillWidth = 56, CellHeight = 76, Pad = 10, Corner = 22, Shoulder = 18;
        public const double Ring = 40, RingStroke = 4, LogoSize = 18, TextSize = 13, SpinnerStroke = 2;
        public const int AnimationFps = 30;   // WPF defaults to 60; the slow spin and pulse look the same at half the CPU

        readonly GlassSurface glass = new GlassSurface();
        readonly List<CellVisual> cells = new List<CellVisual>();
        bool leftEdge;

        // False for previews: draw a still frame instead of starting animations.
        public bool Animate = true;

        public NotchView()
        {
            Children.Add(glass);
        }

        public static double HeightFor(int count) { return Math.Max(count, 1) * CellHeight + 2 * Pad + 2 * Shoulder; }
        public Size ViewSize { get { return new Size(PillWidth, HeightFor(cells.Count)); } }
        public static double CellTop(int index) { return Shoulder + Pad + index * CellHeight; }
        public static Point RingCenter(int index) { return new Point(PillWidth / 2, CellTop(index) + 8 + Ring / 2); }

        public static CellModel CellFor(Reading r, string activity, long now)
        {
            var c = new CellModel();
            c.Provider = r.Provider;
            // Claude's ring spins or pulses with its sessions. Codex's only spins, while a Codex turn runs: Codex writes no
            // "waiting on you" into its logs (polish spec §3), so its ring never pulses.
            c.Activity = r.Provider == "codex" && activity == States.Waiting ? null : activity;
            if (r.Status == "signin")
            {
                c.SignIn = true;
                c.Text = "Sign in";
                return c;
            }
            LimitWindow headline = r.DisplayWindow;
            c.Text = Format.Percent(headline);
            if (headline != null)
            {
                c.ShowArc = true;
                c.Used = headline.Used;
                c.ArcColor = Palette.ForUsed(headline.Used);
            }
            c.Dimmed = r.IsDimmed(now);
            return c;
        }

        public void Update(IList<CellModel> models, bool left)
        {
            if (models.Count != cells.Count || left != leftEdge || glass.Outline == null)
            {
                foreach (CellVisual v in cells) v.RemoveFrom(this);
                cells.Clear();
                leftEdge = left;
                for (int i = 0; i < models.Count; i++) cells.Add(new CellVisual(this, i));
                Width = PillWidth;
                Height = HeightFor(models.Count);
                glass.Apply(PillGeometry(models.Count, left), RimGeometry(models.Count, left), 0);
            }
            for (int i = 0; i < models.Count; i++) cells[i].Apply(models[i], Animate);
        }

        // The theme changed: the next Update rebuilds the pill and its cells in the new colours.
        public void Restyle()
        {
            foreach (CellVisual v in cells) v.RemoveFrom(this);
            cells.Clear();
            glass.Apply(null, null, 0);
        }

        // The cell under a point in view coordinates, or -1.
        public int CellAt(Point p)
        {
            if (p.X < 0 || p.X > PillWidth) return -1;
            double y = p.Y - Shoulder - Pad;
            if (y < 0) return -1;
            int index = (int)(y / CellHeight);
            return index < cells.Count ? index : -1;
        }

        // The pill for the right edge: a body rounded on the screen side, with concave "shoulders" that meet the
        // screen edge (x = PillWidth) smoothly above and below it. Mirrored for the left edge.
        public static Geometry PillGeometry(int count, bool left) { return Outline(count, left, true); }

        // The pill's outline without the side that lies on the screen edge: where its rim is drawn.
        public static Geometry RimGeometry(int count, bool left) { return Outline(count, left, false); }

        static Geometry Outline(int count, bool left, bool closed)
        {
            double w = PillWidth, s = Shoulder, r = Corner, h = Math.Max(count, 1) * CellHeight + 2 * Pad;
            var g = new StreamGeometry();
            using (StreamGeometryContext ctx = g.Open())
            {
                ctx.BeginFigure(new Point(w, 0), closed, closed);
                ctx.ArcTo(new Point(w - s, s), new Size(s, s), 0, false, SweepDirection.Clockwise, true, false);
                ctx.LineTo(new Point(r, s), true, false);
                ctx.ArcTo(new Point(0, s + r), new Size(r, r), 0, false, SweepDirection.Counterclockwise, true, false);
                ctx.LineTo(new Point(0, s + h - r), true, false);
                ctx.ArcTo(new Point(r, s + h), new Size(r, r), 0, false, SweepDirection.Counterclockwise, true, false);
                ctx.LineTo(new Point(w - s, s + h), true, false);
                ctx.ArcTo(new Point(w, h + 2 * s), new Size(s, s), 0, false, SweepDirection.Clockwise, true, false);
            }
            if (left) g.Transform = new ScaleTransform(-1, 1, w / 2, 0);
            g.Freeze();
            return g;
        }

        // An arc starting at the top of a circle and running clockwise over a fraction (0-1) of it.
        public static Geometry ArcGeometry(Point center, double radius, double fraction)
        {
            if (fraction >= 0.999)
            {
                var full = new EllipseGeometry(center, radius, radius);
                full.Freeze();
                return full;
            }
            double angle = (-90 + 360 * Math.Max(0, fraction)) * Math.PI / 180;
            var start = new Point(center.X, center.Y - radius);
            var end = new Point(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle));
            var g = new StreamGeometry();
            using (StreamGeometryContext ctx = g.Open())
            {
                ctx.BeginFigure(start, false, false);
                ctx.ArcTo(end, new Size(radius, radius), 0, fraction > 0.5, SweepDirection.Clockwise, true, false);
            }
            g.Freeze();
            return g;
        }

        // An accent ("#30D158") or a theme token (Palette.Text...) in the current theme's colours.
        public static SolidColorBrush Brush(string color) { return Theme.Current.Brush(color); }

        // The shapes that draw one cell: created once per layout, then updated in place.
        sealed class CellVisual
        {
            readonly int index;
            readonly WEllipse track = new WEllipse();
            readonly WEllipse pulse = new WEllipse();
            readonly WPath arc = new WPath();
            readonly WPath spinner = new WPath();
            readonly WPath logo = new WPath();
            readonly Border glyphBox = new Border();       // holds the glyph of a cell that has one (the calendar's), centred in the ring
            readonly TextBlock glyph = new TextBlock();
            readonly TextBlock text = new TextBlock();
            readonly RotateTransform spin = new RotateTransform();
            string activity = "";   // "" = never applied, so the first Apply always sets it up

            public CellVisual(NotchView view, int index)
            {
                this.index = index;
                Point c = RingCenter(index);
                double d = Ring - RingStroke;   // the diameter of the ring stroke's centre line
                // An Ellipse draws its stroke inside its bounds, so the track and pulse get the ring's full size. That
                // puts their stroke on the same circle as the arc, a Path stroked on its centre line at radius d / 2.
                Place(track, c, Ring);
                track.Stroke = Brush(Palette.Track);
                track.StrokeThickness = RingStroke;
                Place(pulse, c, Ring);
                pulse.Stroke = Brush(Palette.Amber);
                pulse.StrokeThickness = RingStroke;
                pulse.Visibility = Visibility.Collapsed;
                arc.StrokeThickness = RingStroke;
                arc.StrokeStartLineCap = PenLineCap.Round;
                arc.StrokeEndLineCap = PenLineCap.Round;
                spinner.Data = ArcGeometry(c, d / 2 - RingStroke / 2 - 4, 0.25);   // inside the ring, clear of the logo
                Color ink = Theme.Current.Text;
                spinner.Stroke = Theme.Current.Brush(Color.FromArgb(0xD9, ink.R, ink.G, ink.B));
                spinner.StrokeThickness = SpinnerStroke;
                spinner.StrokeStartLineCap = PenLineCap.Round;
                spinner.StrokeEndLineCap = PenLineCap.Round;
                spin.CenterX = c.X;
                spin.CenterY = c.Y;
                spinner.RenderTransform = spin;
                spinner.Visibility = Visibility.Collapsed;
                logo.Fill = Brush(Palette.Text);
                logo.Stretch = Stretch.Uniform;
                Place(logo, c, LogoSize);
                glyph.FontFamily = new FontFamily(PanelView.IconFont);
                glyph.FontSize = LogoSize - 2;   // the icon fonts draw a glyph a little larger than its size
                glyph.Foreground = Brush(Palette.Text);
                glyph.HorizontalAlignment = HorizontalAlignment.Center;
                glyph.VerticalAlignment = VerticalAlignment.Center;
                glyphBox.Child = glyph;
                glyphBox.Visibility = Visibility.Collapsed;
                Place(glyphBox, c, Ring);
                text.Width = PillWidth;
                text.TextAlignment = TextAlignment.Center;
                text.Foreground = Brush(Palette.Text);
                text.FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");
                text.FontWeight = FontWeights.SemiBold;
                Canvas.SetLeft(text, 0);
                Canvas.SetTop(text, CellTop(index) + 8 + Ring + 3);
                foreach (UIElement e in Parts()) view.Children.Add(e);
            }

            IEnumerable<UIElement> Parts()
            {
                yield return track;
                yield return arc;
                yield return pulse;   // over the arc: while waiting, the whole ring pulses amber
                yield return spinner;
                yield return logo;
                yield return glyphBox;
                yield return text;
            }

            public void RemoveFrom(NotchView view)
            {
                foreach (UIElement e in Parts()) view.Children.Remove(e);
            }

            static void Place(FrameworkElement e, Point center, double size)
            {
                e.Width = size;
                e.Height = size;
                Canvas.SetLeft(e, center.X - size / 2);
                Canvas.SetTop(e, center.Y - size / 2);
            }

            public void Apply(CellModel m, bool animate)
            {
                bool hasGlyph = m.Glyph != null;
                logo.Visibility = hasGlyph ? Visibility.Collapsed : Visibility.Visible;
                glyphBox.Visibility = hasGlyph ? Visibility.Visible : Visibility.Collapsed;
                if (hasGlyph) glyph.Text = m.Glyph;
                else logo.Data = Logos.For(m.Provider);
                bool showArc = m.ShowArc && m.Used >= 0.5;
                arc.Visibility = showArc ? Visibility.Visible : Visibility.Collapsed;
                if (showArc)
                {
                    arc.Data = ArcGeometry(RingCenter(index), (Ring - RingStroke) / 2, Math.Min(m.Used, 100) / 100);
                    arc.Stroke = Brush(m.ArcColor);
                }
                text.Text = m.Text;
                text.FontSize = m.SignIn ? 11 : TextSize;
                double opacity = m.Dimmed ? 0.45 : 1;
                track.Opacity = opacity;
                arc.Opacity = opacity;
                text.Opacity = opacity;
                if (m.Activity != activity)
                {
                    activity = m.Activity;
                    SetActivity(animate);
                }
            }

            void SetActivity(bool animate)
            {
                spin.BeginAnimation(RotateTransform.AngleProperty, null);
                pulse.BeginAnimation(UIElement.OpacityProperty, null);
                spinner.Visibility = activity == States.Working ? Visibility.Visible : Visibility.Collapsed;
                pulse.Visibility = activity == States.Waiting ? Visibility.Visible : Visibility.Collapsed;
                if (activity == States.Working)
                {
                    if (animate)
                    {
                        var turn = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.2));
                        turn.RepeatBehavior = RepeatBehavior.Forever;
                        Timeline.SetDesiredFrameRate(turn, AnimationFps);
                        spin.BeginAnimation(RotateTransform.AngleProperty, turn);
                    }
                    else spin.Angle = 45;
                }
                if (activity == States.Waiting && animate)
                {
                    var breathe = new DoubleAnimation(0.35, 1, TimeSpan.FromSeconds(0.6));
                    breathe.AutoReverse = true;
                    breathe.RepeatBehavior = RepeatBehavior.Forever;
                    Timeline.SetDesiredFrameRate(breathe, AnimationFps);
                    pulse.BeginAnimation(UIElement.OpacityProperty, breathe);
                }
            }
        }
    }
}
