using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using Vec2 = System.Numerics.Vector2;

namespace Capsule
{
    public static class GlassShapeTests
    {
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr handle);

        public static void Run()
        {
            RoundedRectangleBecomesOneSmoothPolygon();
            CapsuleOutlineKeepsItsShoulders();
            OutlineTransformsAreApplied();
            KeysFollowTheOutline();
            RegionsAreMade();
            AFailingShapeIsContained();
            ALiveLayerTakesItsMotions();
            AStoppedVisualLayerIsContained();   // last: it stops the visual layer for the rest of this test run
        }

        static Rect Bounds(Point[] polygon)
        {
            double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
            foreach (Point p in polygon)
            {
                left = Math.Min(left, p.X);
                top = Math.Min(top, p.Y);
                right = Math.Max(right, p.X);
                bottom = Math.Max(bottom, p.Y);
            }
            return new Rect(left, top, right - left, bottom - top);
        }

        static void RoundedRectangleBecomesOneSmoothPolygon()
        {
            var shape = new RectangleGeometry(new Rect(0, 0, 100, 50), 10, 10);
            List<Point[]> polygons = GlassShape.Polygons(shape, 1.5);
            TestRunner.Eq(1, polygons.Count, "one polygon");
            Rect b = Bounds(polygons[0]);
            TestRunner.Check(Math.Abs(b.Left) < 0.5 && Math.Abs(b.Top) < 0.5, "starts at the window's corner: " + b);
            TestRunner.Check(Math.Abs(b.Right - 150) < 0.5 && Math.Abs(b.Bottom - 75) < 0.5, "scaled to pixels: " + b);
            TestRunner.Check(polygons[0].Length > 16, "the corners are curved, not cut: " + polygons[0].Length + " points");
            TestRunner.Eq(0, GlassShape.Polygons(null, 1).Count, "no outline, no polygons");
        }

        static void CapsuleOutlineKeepsItsShoulders()
        {
            Geometry right = NotchView.PillGeometry(2, false);
            Point[] r = GlassShape.Polygons(right, 1)[0];
            Rect b = Bounds(r);
            TestRunner.Check(Math.Abs(b.Right - NotchView.PillWidth) < 0.5 && Math.Abs(b.Left) < 0.5, "right-edge capsule spans the pill width: " + b);
            TestRunner.Check(Math.Abs(b.Height - NotchView.HeightFor(2)) < 0.5, "and its full height: " + b);
            bool topShoulder = false, bottomShoulder = false;
            foreach (Point p in r)
            {
                if (Math.Abs(p.X - NotchView.PillWidth) < 0.01 && Math.Abs(p.Y) < 0.01) topShoulder = true;
                if (Math.Abs(p.X - NotchView.PillWidth) < 0.01 && Math.Abs(p.Y - NotchView.HeightFor(2)) < 0.01) bottomShoulder = true;
            }
            TestRunner.Check(topShoulder && bottomShoulder, "the shoulders meet the screen edge at the top and bottom corners");
            Point[] l = GlassShape.Polygons(NotchView.PillGeometry(2, true), 2)[0];
            Rect lb = Bounds(l);
            TestRunner.Check(Math.Abs(lb.Left) < 0.5 && Math.Abs(lb.Right - 2 * NotchView.PillWidth) < 0.5, "the left-edge capsule is mirrored into the window, at 2x: " + lb);
            bool leftShoulder = false;
            foreach (Point p in l) if (Math.Abs(p.X) < 0.01 && Math.Abs(p.Y) < 0.01) leftShoulder = true;
            TestRunner.Check(leftShoulder, "its top shoulder meets the left screen edge");
        }

        // The card and the panel cut their blur from an outline moved inside the room for their shadow, by a Transform on
        // the geometry: it has to be applied before the outline is scaled to pixels.
        static void OutlineTransformsAreApplied()
        {
            var shape = new RectangleGeometry(new Rect(0, 0, 100, 50), 10, 10);
            shape.Transform = new TranslateTransform(5, 7);
            List<Point[]> polygons = GlassShape.Polygons(shape, 2);
            TestRunner.Eq(1, polygons.Count, "one polygon");
            Rect b = Bounds(polygons[0]);
            TestRunner.Check(Math.Abs(b.Left - 10) < 0.5 && Math.Abs(b.Top - 14) < 0.5, "an outline moved by (5, 7) DIPs starts at (10, 14) pixels at 2x: " + b);
            TestRunner.Check(Math.Abs(b.Right - 210) < 0.5 && Math.Abs(b.Bottom - 114) < 0.5, "and ends where its moved edges do: " + b);
        }

        static void KeysFollowTheOutline()
        {
            List<Point[]> a = GlassShape.Polygons(NotchView.PillGeometry(2, false), 1.25);
            List<Point[]> same = GlassShape.Polygons(NotchView.PillGeometry(2, false), 1.25);
            List<Point[]> other = GlassShape.Polygons(NotchView.PillGeometry(1, false), 1.25);
            TestRunner.Eq(GlassShape.Key(a, 70, 287), GlassShape.Key(same, 70, 287), "an unchanged outline keeps its key");
            TestRunner.Check(GlassShape.Key(a, 70, 287) != GlassShape.Key(other, 70, 192), "another outline gets another key");
            TestRunner.Check(GlassShape.Key(a, 70, 287) != GlassShape.Key(a, 70, 288), "so does another window size");
        }

        static void RegionsAreMade()
        {
            IntPtr region = GlassShape.Region(GlassShape.Polygons(NotchView.PillGeometry(2, false), 1));
            TestRunner.Check(region != IntPtr.Zero, "a window region for the capsule outline");
            if (region != IntPtr.Zero) DeleteObject(region);
            TestRunner.Eq(IntPtr.Zero, GlassShape.Region(new List<Point[]>()), "no polygons, no region");
        }

        // Spec §8: a glass failure must never stop Capsule. A shape that can't be applied is contained: SetShape reports
        // it, the layer hides itself for good and tells its owner once. The shape that can't be applied here is a geometry
        // made on another thread, which WPF refuses to read. This uses the real blur window, which stays hidden, and is
        // skipped on a PC that can't make one. Whether it can is GlassLayer.Supported's to say: it reads the Windows build
        // number from the registry (HKLM, CurrentBuildNumber), read-only, and writes nothing there.
        static void AFailingShapeIsContained()
        {
            GlassLayer layer = GlassLayer.Create();
            if (layer == null)
            {
                Console.WriteLine("  glass layer checks skipped: live blur isn't available on this PC");
                return;
            }
            int failures = 0;
            Action count = delegate { failures++; };
            GlassLayer.Failed += count;
            try
            {
                Geometry foreign = null;
                var maker = new Thread(delegate() { foreign = new RectangleGeometry(new Rect(0, 0, 100, 50), 10, 10); });
                maker.SetApartmentState(ApartmentState.STA);
                maker.Start();
                maker.Join();
                Geometry good = new RectangleGeometry(new Rect(0, 0, 100, 50), 10, 10);
                TestRunner.Check(layer.SetShape(good, 1, 100, 50) && !layer.Broken && failures == 0, "an outline that can be applied is applied");

                bool applied = true;
                Exception thrown = null;
                try { applied = layer.SetShape(foreign, 1, 100, 51); }
                catch (Exception e) { thrown = e; }
                TestRunner.Check(thrown == null, "an outline that can't be applied doesn't throw out of SetShape" + (thrown != null ? " (it threw " + thrown.GetType().Name + ")" : ""));
                TestRunner.Check(!applied && layer.Broken, "it is reported, and the layer is broken");
                TestRunner.Eq(1, failures, "and the owner is told once");
                if (layer.Broken)
                {
                    layer.Show();   // only on a broken layer, where it must not put a window on screen
                    TestRunner.Check(!layer.IsVisible, "a broken layer never shows");
                }
                TestRunner.Check(!layer.SetShape(good, 1, 100, 50) && failures == 1, "a broken layer stays broken: nothing more is tried, and nobody is told again");
                string log = Files.ReadText(Paths.LogFile) ?? "";
                TestRunner.Check(log.Contains("the blur couldn't be cut to its outline (InvalidOperationException)"), "the failure is logged, by its type");
            }
            finally
            {
                GlassLayer.Failed -= count;
                layer.Dispose();
            }
            Exception again = null;
            try { layer.Dispose(); }
            catch (Exception e) { again = e; }
            TestRunner.Check(again == null, "a second Dispose does nothing");
        }

        // The motions the card and the panel ask of a live layer (hidden here) all go through: none breaks it, and its owner
        // is told of nothing. Before the stopped-layer checks, which break the layers made after them.
        static void ALiveLayerTakesItsMotions()
        {
            GlassLayer layer = GlassLayer.Create();
            if (layer == null)
            {
                Console.WriteLine("  live layer motion checks skipped: live blur isn't available on this PC");
                return;
            }
            int failures = 0;
            Action count = delegate { failures++; };
            GlassLayer.Failed += count;
            try
            {
                layer.SetShape(new RectangleGeometry(new Rect(0, 0, 100, 50), 10, 10), 1, 100, 50);
                layer.Pose(0f, 0.33f, new Vec2(10f, 20f), new Vec2());
                TestRunner.Check(!layer.Broken, "posing a live layer leaves it whole");
                layer.AnimateScale(1f, 200);
                TestRunner.Check(!layer.Broken, "so does the scale animation");
                layer.Animate(true, 0f, 200);
                TestRunner.Check(!layer.Broken, "so does the fade");
                layer.Pose(1f, 1f, new Vec2(10f, 20f), new Vec2(3f, 4f));
                layer.Animate(false, 12f, 150);
                layer.Rest(12f);
                TestRunner.Check(!layer.Broken && failures == 0, "and posing over a running motion, folding and resting do too: nobody is told of a failure");
            }
            finally
            {
                GlassLayer.Failed -= count;
                layer.Dispose();
            }
        }

        // Capsule stops the visual layer on its way out, while WPF's dispatcher still runs (polish spec §4.7). A layer that
        // is still about after that fails on every compositor call (the compositor is closed): the call breaks the layer
        // instead of throwing, as a failing shape does (§4.1), so a compositor error never stops Capsule or leaves the panel
        // shown but inactive. Hidden windows only. After this, the test run has no visual layer, so it comes last.
        static void AStoppedVisualLayerIsContained()
        {
            GlassLayer resting = GlassLayer.Create(), moving = GlassLayer.Create(), posed = GlassLayer.Create(), scaled = GlassLayer.Create();
            if (resting == null || moving == null || posed == null || scaled == null)
            {
                Console.WriteLine("  stopped visual layer checks skipped: live blur isn't available on this PC");
                return;
            }
            var shape = new RectangleGeometry(new Rect(0, 0, 100, 50), 10, 10);
            foreach (GlassLayer layer in new[] { resting, moving, posed, scaled }) layer.SetShape(shape, 1, 100, 50);
            int failures = 0;
            Action count = delegate { failures++; };
            GlassLayer.Failed += count;
            try
            {
                Exception thrown = null;
                try { GlassLayer.Stop(); }
                catch (Exception e) { thrown = e; }
                TestRunner.Check(thrown == null, "stopping the visual layer doesn't throw" + (thrown != null ? " (it threw " + thrown.GetType().Name + ")" : ""));
                TestRunner.Check(!GlassLayer.Supported && GlassLayer.Create() == null, "and no layer is made after it");

                thrown = null;
                try { resting.Rest(0f); }
                catch (Exception e) { thrown = e; }
                TestRunner.Check(thrown == null && resting.Broken, "Rest on a layer that outlived it breaks the layer instead of throwing" + (thrown != null ? " (it threw " + thrown.GetType().Name + ")" : ""));
                thrown = null;
                try { moving.Animate(true, 12f, 150); }
                catch (Exception e) { thrown = e; }
                TestRunner.Check(thrown == null && moving.Broken, "and so does Animate" + (thrown != null ? " (it threw " + thrown.GetType().Name + ")" : ""));
                thrown = null;
                try { posed.Pose(0f, 0.33f, new Vec2(10f, 20f), new Vec2()); }
                catch (Exception e) { thrown = e; }
                TestRunner.Check(thrown == null && posed.Broken, "and so does posing it, as the card's unfold starts" + (thrown != null ? " (it threw " + thrown.GetType().Name + ")" : ""));
                thrown = null;
                try { scaled.AnimateScale(1f, 200); }
                catch (Exception e) { thrown = e; }
                TestRunner.Check(thrown == null && scaled.Broken, "and so does the scale animation, as the card unfolds" + (thrown != null ? " (it threw " + thrown.GetType().Name + ")" : ""));
                TestRunner.Eq(4, failures, "each layer's owner is told once");
                string log = Files.ReadText(Paths.LogFile) ?? "";
                TestRunner.Check(log.Contains("the blur couldn't be animated (ObjectDisposedException)"), "the failure is logged, by its type");
                TestRunner.Check(log.Contains("the blur couldn't be rested (ObjectDisposedException)"), "and a failing Rest says it was resting, not animating");

                thrown = null;
                try { GlassLayer.Stop(); }
                catch (Exception e) { thrown = e; }
                TestRunner.Check(thrown == null, "a second Stop does nothing");
            }
            finally
            {
                GlassLayer.Failed -= count;
                foreach (GlassLayer layer in new[] { resting, moving, posed, scaled }) layer.Dispose();
            }
        }
    }
}
