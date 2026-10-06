using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Capsule
{
    public static class CardMotionTests
    {
        public static void Run()
        {
            AnchorsOnTheRingsInnerEdge();
            StartsSmallAtTheAnchor();
            ReanchoringKeepsTheCardInPlace();
            ReducedMotionSkipsTheAnimation();
            GlideEndsExactlyAtTheTarget();
            ACardPinnedToTheScreenEdgeKeepsItsAnchorOnTheRing();
            PosingDropsEveryRunningAnimation();
            EachMotionHasOnlyItsEnd();
            GlideFramesAreExactlyThese();
        }

        static RECT R(int left, int top, int right, int bottom)
        {
            var r = new RECT();
            r.Left = left;
            r.Top = top;
            r.Right = right;
            r.Bottom = bottom;
            return r;
        }

        static POINT P(int x, int y)
        {
            var p = new POINT();
            p.X = x;
            p.Y = y;
            return p;
        }

        // The card window beside a ring, placed as HoverCard places it: a card of 288 x 300 DIPs (pointer included), 4 DIPs
        // from the capsule, with 16 DIPs of room for its shadow around it. The capsule is 56 DIPs wide, in the middle of a
        // 1920 x 1040 work area.
        static RECT CardWindow(bool leftEdge, double scale, out RECT cell)
        {
            var work = R(0, 0, 1920, 1040);
            int capsuleWidth = (int)Math.Round(NotchView.PillWidth * scale);
            int left = leftEdge ? work.Left : work.Right - capsuleWidth;
            cell = R(left, 500, left + capsuleWidth, 500 + (int)Math.Round(NotchView.Ring * scale));
            int width = (int)Math.Ceiling((CardView.CardWidth + CardView.PointerWidth) * scale);
            int height = (int)Math.Ceiling(300 * scale);
            int pointer;
            RECT card = Layout.CardRect(cell, leftEdge, work, width, height, (int)Math.Round(4 * scale), out pointer);
            return Layout.Inflate(card, (int)Math.Round(CardView.ShadowMargin * scale));
        }

        // The anchor is the ring's centre on the capsule's inner edge: in the card's own DIPs, 4 DIPs (the gap) beyond the
        // pointer's tip, level with the pointer. For both screen edges, at 100% and at 150%.
        static void AnchorsOnTheRingsInnerEdge()
        {
            foreach (bool leftEdge in new[] { false, true })
                foreach (double scale in new[] { 1.0, 1.5 })
                {
                    RECT cell;
                    RECT window = CardWindow(leftEdge, scale, out cell);
                    Point anchor = CardMotion.ToView(CardMotion.AnchorPx(window, cell, leftEdge), scale, CardView.ShadowMargin);
                    string where = (leftEdge ? "left" : "right") + " edge at " + (int)(scale * 100) + "%";
                    double tip = leftEdge ? 0 : CardView.CardWidth + CardView.PointerWidth;
                    TestRunner.Near(leftEdge ? tip - 4 : tip + 4, anchor.X, where + ": the anchor is on the capsule's inner edge, 4 DIPs beyond the pointer's tip");
                    TestRunner.Near(150, anchor.Y, where + ": level with the pointer, at the ring's centre");
                }
            RECT c;
            RECT w = CardWindow(false, 1.5, out c);
            Point px = CardMotion.AnchorPx(w, c, false);
            TestRunner.Check(px.X == c.Left - w.Left && px.Y == (c.Top + c.Bottom) / 2.0 - w.Top, "in the window's pixels, the same point: the ring's middle on the capsule's left side (" + px + ")");
        }

        // A card drawn by the content transform that HoverCard uses, posed as it starts: a third of its size about the anchor,
        // fully transparent.
        static void StartsSmallAtTheAnchor()
        {
            foreach (bool leftEdge in new[] { false, true })
            {
                RECT cell;
                RECT window = CardWindow(leftEdge, 1.5, out cell);
                Point anchor = CardMotion.ToView(CardMotion.AnchorPx(window, cell, leftEdge), 1.5, CardView.ShadowMargin);
                var content = new Border();
                var zoom = new ScaleTransform();
                var shift = new TranslateTransform();
                Transform drawn = CardMotion.ContentTransform(zoom, shift);
                CardMotion.Pose(content, zoom, shift, 0, CardMotion.StartScale, anchor, new Vector());
                string where = leftEdge ? "left edge" : "right edge";
                Point fixedPoint = drawn.Transform(anchor);
                TestRunner.Check(Math.Abs(fixedPoint.X - anchor.X) < 1e-9 && Math.Abs(fixedPoint.Y - anchor.Y) < 1e-9, where + ": the anchor stays where it is");
                Point corner = drawn.Transform(new Point(leftEdge ? CardView.CardWidth + CardView.PointerWidth : 0, 0));
                Point expected = new Point(anchor.X + CardMotion.StartScale * ((leftEdge ? CardView.CardWidth + CardView.PointerWidth : 0) - anchor.X), anchor.Y * (1 - CardMotion.StartScale));
                TestRunner.Check(Math.Abs(corner.X - expected.X) < 1e-9 && Math.Abs(corner.Y - expected.Y) < 1e-9, where + ": the far corner is a third of the way out from it (" + corner + ")");
                TestRunner.Check(content.Opacity == 0 && zoom.ScaleX == CardMotion.StartScale && zoom.ScaleY == CardMotion.StartScale, where + ": at a third of its size, fully transparent");
            }
            TestRunner.Near(0.33, CardMotion.StartScale, "the card starts at 0.33 of its size");
        }

        // Re-aiming a motion at another anchor halfway (the card glided to another ring) moves no point of the card.
        static void ReanchoringKeepsTheCardInPlace()
        {
            var oldCentre = new Point(292, 150);
            var newCentre = new Point(292, 96);
            var shift = new Vector(3, -5);
            const double Size = 0.6;
            Vector moved = CardMotion.Reanchor(oldCentre, newCentre, Size, shift);
            foreach (Point p in new[] { new Point(0, 0), new Point(288, 150), new Point(140, 310) })
            {
                Point before = Drawn(p, oldCentre, Size, shift);
                Point after = Drawn(p, newCentre, Size, moved);
                TestRunner.Check(Math.Abs(before.X - after.X) < 1e-9 && Math.Abs(before.Y - after.Y) < 1e-9, "re-aimed at a new anchor, " + p + " stays where it was drawn");
            }
            TestRunner.Eq(shift, CardMotion.Reanchor(oldCentre, newCentre, 1, shift), "at full size the anchor doesn't matter: the shift stays");
        }

        static Point Drawn(Point p, Point centre, double size, Vector shift)
        {
            var zoom = new ScaleTransform(size, size, centre.X, centre.Y);
            var move = new TranslateTransform(shift.X, shift.Y);
            return CardMotion.ContentTransform(zoom, move).Transform(p);
        }

        // With Windows' Animation effects off, the card is at once where its motion would end, and nothing is animated.
        static void ReducedMotionSkipsTheAnimation()
        {
            TestRunner.Eq(0, CardMotion.Duration(CardMotion.OpenMs, false), "Animation effects off: no time to unfold");
            TestRunner.Eq(200, CardMotion.Duration(CardMotion.OpenMs, true), "on: the unfold takes 200 ms");
            TestRunner.Eq(150, CardMotion.Duration(CardMotion.CloseMs, true), "and the fold 150 ms");

            var content = new Border();
            var zoom = new ScaleTransform();
            var shift = new TranslateTransform();
            CardMotion.Pose(content, zoom, shift, 0, CardMotion.StartScale, new Point(292, 150), new Vector(4, 4));
            CardMotion.MoveContent(content, zoom, shift, 1, 1, CardMotion.Duration(CardMotion.OpenMs, false));
            TestRunner.Check(content.Opacity == 1 && zoom.ScaleX == 1 && zoom.ScaleY == 1 && shift.X == 0 && shift.Y == 0, "off: the card is at once at full size, opaque, in its place");
            TestRunner.Check(!content.HasAnimatedProperties && !zoom.HasAnimatedProperties && !shift.HasAnimatedProperties, "and nothing is animated");

            var moving = new Border();
            var grow = new ScaleTransform();
            var slide = new TranslateTransform();
            CardMotion.Pose(moving, grow, slide, 0, CardMotion.StartScale, new Point(292, 150), new Vector());
            CardMotion.MoveContent(moving, grow, slide, 1, 1, CardMotion.Duration(CardMotion.OpenMs, true));
            TestRunner.Check(moving.HasAnimatedProperties && grow.HasAnimatedProperties && slide.HasAnimatedProperties, "on: the opacity, the size and the shift are animated");
            TestRunner.Check(grow.ScaleX == CardMotion.StartScale && moving.Opacity == 0, "the animations leave the values the card was posed at alone: they only take over from there");
        }

        // A card whose ring is near the screen's top or bottom is pinned to the work area, not centred on the ring: the
        // anchor then sits above or below the middle of the card, still on the ring's centre. (Ring y 40-80 and 980-1020
        // in a 1040-high work area; at 150% the same rings, scaled, in a work area scaled the same.)
        static void ACardPinnedToTheScreenEdgeKeepsItsAnchorOnTheRing()
        {
            foreach (double scale in new[] { 1.0, 1.5 })
            {
                var work = R(0, 0, (int)(1920 * scale), (int)(1040 * scale));
                int capsuleWidth = (int)Math.Round(NotchView.PillWidth * scale);
                int width = (int)Math.Ceiling((CardView.CardWidth + CardView.PointerWidth) * scale);
                int height = (int)Math.Ceiling(300 * scale);
                foreach (int ringTop in new[] { 40, 980 })
                {
                    RECT cell = R(work.Right - capsuleWidth, (int)Math.Round(ringTop * scale), work.Right, (int)Math.Round((ringTop + 40) * scale));
                    int pointer;
                    RECT card = Layout.CardRect(cell, false, work, width, height, (int)Math.Round(4 * scale), out pointer);
                    RECT window = Layout.Inflate(card, (int)Math.Round(CardView.ShadowMargin * scale));
                    Point anchor = CardMotion.ToView(CardMotion.AnchorPx(window, cell, false), scale, CardView.ShadowMargin);
                    string where = "a ring at y " + ringTop + "-" + (ringTop + 40) + " at " + (int)(scale * 100) + "%";
                    TestRunner.Near(CardView.CardWidth + CardView.PointerWidth + 4, anchor.X, where + ": the anchor is on the capsule's inner edge");
                    TestRunner.Near(ringTop == 40 ? 60 : 260, anchor.Y, where + ": the card is pinned to the work area, the anchor still on the ring's centre");
                }
            }
        }

        // Pose drops every running animation, on each of the five properties: it is how a motion is re-aimed or started afresh.
        // Each property is animated alone on a fresh card, so one that Pose forgot can't hide behind another.
        static void PosingDropsEveryRunningAnimation()
        {
            string[] names = { "opacity", "size across", "size down", "shift across", "shift down" };
            for (int which = 0; which < names.Length; which++)
            {
                var content = new Border();
                var zoom = new ScaleTransform();
                var shift = new TranslateTransform();
                var motion = CardMotion.Motion(1, CardMotion.OpenMs);
                if (which == 0) content.BeginAnimation(UIElement.OpacityProperty, motion);
                else if (which == 1) zoom.BeginAnimation(ScaleTransform.ScaleXProperty, motion);
                else if (which == 2) zoom.BeginAnimation(ScaleTransform.ScaleYProperty, motion);
                else if (which == 3) shift.BeginAnimation(TranslateTransform.XProperty, motion);
                else shift.BeginAnimation(TranslateTransform.YProperty, motion);
                TestRunner.Check(content.HasAnimatedProperties || zoom.HasAnimatedProperties || shift.HasAnimatedProperties, "the " + names[which] + " is animated");
                CardMotion.Pose(content, zoom, shift, 0.5, 0.7, new Point(292, 150), new Vector(1, 2));
                TestRunner.Check(!content.HasAnimatedProperties && !zoom.HasAnimatedProperties && !shift.HasAnimatedProperties, "posing the card drops the animation of the " + names[which]);
                TestRunner.Check(content.Opacity == 0.5 && zoom.ScaleX == 0.7 && zoom.ScaleY == 0.7 && shift.X == 1 && shift.Y == 2, "and leaves the pose it was given (" + names[which] + ")");
            }
        }

        // Each property's motion, as built: it has no From (it starts from wherever the property is, so a motion turned
        // round halfway doesn't jump), goes to the given end over the given time on the cubic ease-out, at the notch's rate.
        static void EachMotionHasOnlyItsEnd()
        {
            System.Windows.Media.Animation.DoubleAnimation motion = CardMotion.Motion(0.33, CardMotion.CloseMs);
            TestRunner.Check(!motion.From.HasValue, "no From");
            TestRunner.Check(motion.To.HasValue && motion.To.Value == 0.33, "to the end given");
            TestRunner.Eq(TimeSpan.FromMilliseconds(CardMotion.CloseMs), motion.Duration.TimeSpan, "over the time given");
            var ease = motion.EasingFunction as System.Windows.Media.Animation.CubicEase;
            TestRunner.Check(ease != null && ease.EasingMode == System.Windows.Media.Animation.EasingMode.EaseOut, "on the cubic ease-out");
            TestRunner.Eq(NotchView.AnimationFps, System.Windows.Media.Animation.Timeline.GetDesiredFrameRate(motion) ?? 0, "at the notch's frame rate");
        }

        static string Frames(List<POINT> frames)
        {
            var text = new List<string>();
            foreach (POINT p in frames) text.Add(p.X + "," + p.Y);
            return string.Join(" ", text);
        }

        // The glide's frames, as they are: their rounding, a glide up as well as down, and none at all for no time.
        static void GlideFramesAreExactlyThese()
        {
            TestRunner.Eq("1556,301 1556,323 1556,330 1556,330", Frames(CardMotion.Glide(P(1556, 254), P(1556, 330), CardMotion.GlideMs, NotchView.AnimationFps)), "down 76 pixels");
            TestRunner.Eq("1548,352 1544,397 1543,410 1543,411", Frames(CardMotion.Glide(P(1556, 254), P(1543, 411), CardMotion.GlideMs, NotchView.AnimationFps)), "down 157 and left 13");
            TestRunner.Eq("1556,283 1556,261 1556,254 1556,254", Frames(CardMotion.Glide(P(1556, 330), P(1556, 254), CardMotion.GlideMs, NotchView.AnimationFps)), "and up the same 76 pixels");
            TestRunner.Eq("1543,411", Frames(CardMotion.Glide(P(1556, 254), P(1543, 411), 0, NotchView.AnimationFps)), "no time: one frame, on the target");
        }

        // The ring switch moves the window one step per frame: four steps at 30 fps over 120 ms, the last exactly on the
        // target, never past it.
        static void GlideEndsExactlyAtTheTarget()
        {
            List<POINT> frames = CardMotion.Glide(P(1556, 254), P(1556, 330), CardMotion.GlideMs, NotchView.AnimationFps);
            TestRunner.Eq(4, frames.Count, "120 ms at 30 fps is four frames");
            TestRunner.Check(frames[frames.Count - 1].X == 1556 && frames[frames.Count - 1].Y == 330, "the last frame is the target");
            bool steady = true;
            for (int i = 0; i < frames.Count; i++)
                steady &= frames[i].X == 1556 && frames[i].Y >= (i == 0 ? 254 : frames[i - 1].Y) && frames[i].Y <= 330;
            TestRunner.Check(steady, "each frame is further on, and none goes past the target");
            TestRunner.Check(frames[0].Y - 254 >= (330 - 254) / 2, "eased out: more than half the way in the first frame (" + frames[0].Y + ")");

            List<POINT> odd = CardMotion.Glide(P(1556, 254), P(1543, 411), CardMotion.GlideMs, NotchView.AnimationFps);
            TestRunner.Check(odd[odd.Count - 1].X == 1543 && odd[odd.Count - 1].Y == 411, "an uneven distance still ends exactly on the target");
            List<POINT> still = CardMotion.Glide(P(70, 90), P(70, 90), CardMotion.GlideMs, NotchView.AnimationFps);
            bool stays = true;
            foreach (POINT p in still) stays &= p.X == 70 && p.Y == 90;
            TestRunner.Check(still.Count == 4 && stays, "a glide to where the card already is stays put");

            // Frames are picked by the clock: one every 1/30 s, the last at 120 ms, however late the timer's ticks come.
            int[] due = new int[7];
            long[] at = { 0, 20, 34, 67, 101, 119, 120 };
            for (int i = 0; i < at.Length; i++) due[i] = CardMotion.FramesDue(at[i], 4, CardMotion.GlideMs, NotchView.AnimationFps);
            TestRunner.Eq("0,0,1,2,3,3,4", string.Join(",", due), "frames due at 0, 20, 34, 67, 101, 119 and 120 ms");
            TestRunner.Eq(4, CardMotion.FramesDue(400, 4, CardMotion.GlideMs, NotchView.AnimationFps), "a timer that comes late still ends on the last frame");
        }
    }
}
