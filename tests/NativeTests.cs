using System;
using System.Runtime.InteropServices;

namespace Capsule
{
    public static class NativeTests
    {
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();

        public static void Run()
        {
            OwnWindowsSayNothingAboutFullscreen();
        }

        // The fullscreen check is told which windows are Capsule's own. When one of them is in front (the panel, opened
        // by the shortcut over a fullscreen app, say) it can't tell whether that app is still behind it, so it must say
        // "don't know" (null) and let the caller keep what it knew, not "no fullscreen app" (false), which would bring the
        // capsule back for as long as the panel is open and hide it again afterwards.
        static void OwnWindowsSayNothingAboutFullscreen()
        {
            MonitorInfo monitor = Native.Primary();
            if (monitor == null)
            {
                Console.WriteLine("  fullscreen checks skipped: no monitor");
                return;
            }
            TestRunner.Check(Native.IsFullscreenOn(monitor).HasValue, "with no windows of its own named, the answer is always yes or no");
            IntPtr front = IntPtr.Zero;
            bool unknown = false;
            for (int attempt = 0; attempt < 5 && !unknown; attempt++)   // the foreground window may change between the two calls: look again
            {
                front = GetForegroundWindow();
                if (front == IntPtr.Zero) break;
                unknown = !Native.IsFullscreenOn(monitor, front).HasValue;
            }
            if (front == IntPtr.Zero)
            {
                Console.WriteLine("  own-window fullscreen check skipped: no foreground window");
                return;
            }
            TestRunner.Check(unknown, "when the window in front is one of Capsule's own, the answer is \"don't know\"");
        }
    }
}
