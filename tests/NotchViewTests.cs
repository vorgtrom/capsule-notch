using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace Capsule
{
    public static class NotchViewTests
    {
        public static void Run()
        {
            LogosParse();
            SizesFollowTheCellCount();
            FindsTheCellUnderAPoint();
            PillIsMirroredForTheLeftEdge();
            ArcsCoverTheRightFraction();
            CellsDescribeReadings();
            RingPartsShareOneCircle();
        }

        static void LogosParse()
        {
            TestRunner.Check(Logos.Claude.Bounds.Width > 20 && Logos.Claude.Bounds.Width <= 24.01, "the Claude mark fills its 24-unit box");
            TestRunner.Check(Logos.OpenAI.Bounds.Height > 20, "the OpenAI mark parses");
            TestRunner.Check(Logos.For("codex") == Logos.OpenAI && Logos.For("claude") == Logos.Claude, "marks by provider");
        }

        static void SizesFollowTheCellCount()
        {
            TestRunner.Near(2 * 76 + 2 * 10 + 2 * 18, NotchView.HeightFor(2), "two cells");
            TestRunner.Near(76 + 20 + 36, NotchView.HeightFor(1), "one cell");
        }

        static void FindsTheCellUnderAPoint()
        {
            var view = new NotchView();
            view.Update(new List<CellModel> { new CellModel { Provider = "claude" }, new CellModel { Provider = "codex" } }, false);
            TestRunner.Eq(-1, view.CellAt(new Point(28, 5)), "a shoulder is not a cell");
            TestRunner.Eq(0, view.CellAt(new Point(28, 18 + 10 + 30)), "first cell");
            TestRunner.Eq(1, view.CellAt(new Point(28, 18 + 10 + 76 + 30)), "second cell");
            TestRunner.Eq(-1, view.CellAt(new Point(28, 18 + 10 + 2 * 76 + 5)), "below the cells");
            TestRunner.Near(NotchView.HeightFor(2), view.ViewSize.Height, "view size");
        }

        static void PillIsMirroredForTheLeftEdge()
        {
            Geometry right = NotchView.PillGeometry(2, false);
            Geometry left = NotchView.PillGeometry(2, true);
            TestRunner.Near(0, right.Bounds.Left, "the right-edge pill starts at 0");
            TestRunner.Near(56, right.Bounds.Right, "and reaches the screen edge");
            TestRunner.Near(NotchView.HeightFor(2), right.Bounds.Height, "full height, shoulders included");
            // Both pills have the same bounds, so check the side the top shoulder is on: it meets the screen edge.
            TestRunner.Check(right.FillContains(new Point(55.5, 8)) && !right.FillContains(new Point(0.5, 8)), "the right-edge pill's shoulder is on the right");
            TestRunner.Check(left.FillContains(new Point(0.5, 8)) && !left.FillContains(new Point(55.5, 8)), "the left-edge pill's shoulder is on the left");
        }

        static void ArcsCoverTheRightFraction()
        {
            var center = new Point(28, 48);
            Rect half = NotchView.ArcGeometry(center, 18, 0.5).Bounds;
            TestRunner.Near(28, half.Left, "a half arc covers the right side only");
            TestRunner.Near(46, half.Right, "and reaches the circle's right edge");
            TestRunner.Check(NotchView.ArcGeometry(center, 18, 1.0) is EllipseGeometry, "a full circle at 100%");
        }

        static void CellsDescribeReadings()
        {
            long now = 1790690940000;
            var r = new Reading { Provider = "claude", Status = "ok", HeadlineId = "five_hour", DataAtMs = now };
            r.Windows.Add(new LimitWindow { Id = "five_hour", Used = 73 });
            CellModel c = NotchView.CellFor(r, States.Working, now);
            TestRunner.Eq("73%", c.Text, "text");
            TestRunner.Eq(Palette.Red, c.ArcColor, "colour");
            TestRunner.Check(c.ShowArc, "arc shown");
            TestRunner.Eq(States.Working, c.Activity, "activity kept for Claude");
            Reading codex = r.Clone();
            codex.Provider = "codex";
            TestRunner.Eq(States.Working, NotchView.CellFor(codex, States.Working, now).Activity, "Codex's ring spins while Codex works");
            TestRunner.Eq(null, NotchView.CellFor(codex, States.Waiting, now).Activity, "but never pulses: Codex's logs say nothing of waiting on you");
            CellModel signIn = NotchView.CellFor(new Reading { Provider = "claude", Status = "signin" }, null, now);
            TestRunner.Check(signIn.SignIn && signIn.Text == "Sign in" && !signIn.ShowArc, "sign in");
            r.Windows[0].Used = 21;
            TestRunner.Eq(Palette.Green, NotchView.CellFor(r, null, now).ArcColor, "green under 50%");
            r.Windows[0].Used = 55;
            TestRunner.Eq(Palette.Yellow, NotchView.CellFor(r, null, now).ArcColor, "yellow from 50%");
            Reading stale = r.Clone();
            stale.LastCheckFailed = true;
            stale.DataAtMs = now - 11 * 60 * 1000;
            TestRunner.Check(NotchView.CellFor(stale, null, now).Dimmed, "kept numbers dim after 10 minutes");
            stale.FromLogs = true;
            TestRunner.Check(!NotchView.CellFor(stale, null, now).Dimmed, "numbers from logs are never dimmed");
            CellModel error = NotchView.CellFor(new Reading { Provider = "claude", Status = "error" }, null, now);
            TestRunner.Check(error.Text == "–" && !error.ShowArc, "no numbers: a dash and no arc");
        }

        static void RingPartsShareOneCircle()
        {
            var view = new NotchView { Animate = false };
            view.Update(new List<CellModel> { new CellModel { Provider = "claude", ShowArc = true, Used = 50, ArcColor = Palette.Green, Activity = States.Waiting } }, false);
            int arcAt = -1, pulseAt = -1, ellipses = 0;
            for (int i = 0; i < view.Children.Count; i++)
            {
                var shape = view.Children[i] as System.Windows.Shapes.Shape;
                if (shape == null || shape.StrokeThickness != NotchView.RingStroke) continue;
                if (shape is System.Windows.Shapes.Ellipse)
                {
                    ellipses++;
                    // An Ellipse strokes inside its bounds, so it must span the ring's full size to match the arc.
                    TestRunner.Near(NotchView.Ring, shape.Width, "the track and pulse span the whole ring");
                    if (((SolidColorBrush)shape.Stroke).Color == NotchView.Brush(Palette.Amber).Color) pulseAt = i;
                }
                else arcAt = i;
            }
            TestRunner.Eq(2, ellipses, "a track and a pulse");
            TestRunner.Check(arcAt >= 0 && pulseAt > arcAt, "the waiting pulse is drawn over the arc");
        }
    }
}
