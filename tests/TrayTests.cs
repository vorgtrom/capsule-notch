using System;
using System.Drawing;

namespace Capsule
{
    public static class TrayTests
    {
        public static void Run()
        {
            int clicks = 0;
            var action = new BalloonAction();
            action.Replace(() => clicks += 10);
            action.Replace(() => clicks++);
            action.Take()();
            TestRunner.Eq(1, clicks, "click runs only the most recent balloon action");
            TestRunner.Check(action.Take() == null, "click clears the stored action");
            action.Replace(() => clicks++);
            action.Replace(null);
            TestRunner.Check(action.Take() == null, "closing or replacing with a plain balloon clears its action");
            TestRunner.Check(!Controller.ShouldPlayCue(Cue.Attention, true), "no attention cue while its app is in front");
            TestRunner.Check(Controller.ShouldPlayCue(Cue.Attention, false), "attention cue when the app is away");
            TestRunner.Check(Controller.ShouldPlayCue(Cue.Done, true), "done still sounds with the app in front");
            using (Bitmap bitmap = Tray.DrawRing(73, 32))
            {
                Color top = bitmap.GetPixel(16, 2);
                TestRunner.Check(top.A > 200 && top.R > 200 && top.G < 120, "the arc at the top is orange-red: " + top);
                TestRunner.Eq(0, (int)bitmap.GetPixel(16, 16).A, "the centre is transparent");
            }
            using (Bitmap bitmap = Tray.DrawRing(null, 32))
            {
                Color top = bitmap.GetPixel(16, 2);
                TestRunner.Check(top.A > 0 && Math.Abs(top.R - top.B) < 30, "unknown: a grey track only: " + top);
            }
            using (Bitmap bitmap = Tray.DrawRing(25, 32))
            {
                TestRunner.Check(bitmap.GetPixel(29, 16).A > 200, "a quarter arc reaches 3 o'clock: " + bitmap.GetPixel(29, 16));
                TestRunner.Check(bitmap.GetPixel(16, 29).A <= 140, "but not 6 o'clock, which shows only the track: " + bitmap.GetPixel(16, 29));
            }
        }
    }
}
