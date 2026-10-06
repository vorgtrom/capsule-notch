namespace Capsule
{
    public static class LayoutTests
    {
        static RECT R(int left, int top, int right, int bottom)
        {
            var r = new RECT();
            r.Left = left;
            r.Top = top;
            r.Right = right;
            r.Bottom = bottom;
            return r;
        }

        public static void Run()
        {
            RECT work = R(0, 0, 1920, 1040);
            RECT right = Layout.NotchRect(work, false, 0.5, 84, 312);
            TestRunner.Eq(1836, right.Left, "flush with the right side");
            TestRunner.Eq(364, right.Top, "centred");
            RECT left = Layout.NotchRect(work, true, 0, 84, 312);
            TestRunner.Eq(0, left.Left, "flush with the left side");
            TestRunner.Eq(0, left.Top, "clamped to the top");
            TestRunner.Eq(1040 - 312, Layout.NotchRect(work, false, 1, 84, 312).Top, "clamped to the bottom");
            TestRunner.Eq(4480 - 84, Layout.NotchRect(R(1920, 0, 4480, 1400), false, 0.5, 84, 312).Left, "second monitor");

            TestRunner.Check(Layout.SnapLeft(R(100, 400, 184, 712), work), "dropped left of centre snaps left");
            TestRunner.Check(!Layout.SnapLeft(R(1500, 400, 1584, 712), work), "right of centre snaps right");
            TestRunner.Near(0.5, Layout.SnapY(R(0, 364, 84, 676), work), "centre y");
            TestRunner.Near(0, Layout.SnapY(R(0, -500, 84, -188), work), "y clamped");

            int pointer;
            RECT card = Layout.CardRect(R(1836, 400, 1920, 460), false, work, 432, 300, 6, out pointer);
            TestRunner.Eq(1836 - 6 - 432, card.Left, "the card sits left of a right-edge notch");
            TestRunner.Eq(280, card.Top, "centred on the cell");
            TestRunner.Eq(150, pointer, "pointer at the cell's middle");
            RECT low = Layout.CardRect(R(1836, 1000, 1920, 1040), false, work, 432, 300, 6, out pointer);
            TestRunner.Eq(740, low.Top, "kept on screen");
            TestRunner.Eq(280, pointer, "the pointer follows the cell");
            RECT leftCard = Layout.CardRect(R(0, 400, 84, 460), true, work, 432, 300, 6, out pointer);
            TestRunner.Eq(90, leftCard.Left, "the card sits right of a left-edge notch");

            // Work areas that don't start at 0: a top taskbar, or a monitor below or to the side of another.
            RECT offset = R(1920, 40, 3840, 1080);
            TestRunner.Eq(404, Layout.NotchRect(offset, false, 0.5, 84, 312).Top, "centred within an offset work area");
            TestRunner.Eq(40, Layout.NotchRect(offset, true, 0, 84, 312).Top, "clamped to an offset top");
            TestRunner.Eq(1080 - 312, Layout.NotchRect(offset, true, 1, 84, 312).Top, "clamped to an offset bottom");
            TestRunner.Near(0.5, Layout.SnapY(R(3756, 404, 3840, 716), offset), "centre y in an offset work area");
            TestRunner.Near(1, Layout.SnapY(R(3756, 2000, 3840, 2312), offset), "y clamped at the bottom");
            RECT negative = R(-1920, -200, 0, 880);
            TestRunner.Eq(-1920, Layout.NotchRect(negative, true, 0.5, 84, 312).Left, "a monitor left of the primary");
            TestRunner.Check(Layout.SnapLeft(R(-1900, 0, -1816, 312), negative), "snaps left on a negative-origin monitor");
            RECT high = Layout.CardRect(R(3756, 40, 3840, 80), false, offset, 432, 300, 6, out pointer);
            TestRunner.Eq(40, high.Top, "a card near the top stays on screen");
            TestRunner.Eq(20, pointer, "its pointer still aims at the cell");
            TestRunner.Eq(1080 - 300, Layout.CardRect(R(3756, 1040, 3840, 1080), false, offset, 432, 300, 6, out pointer).Top, "a card near an offset bottom stays on screen");

            // The panel beside the capsule, centred on it and kept on screen.
            RECT panel = Layout.PanelRect(R(1864, 400, 1920, 687), false, work, 400, 500, 8);
            TestRunner.Eq(1864 - 8 - 400, panel.Left, "the panel sits left of a right-edge capsule");
            TestRunner.Eq((400 + 687) / 2 - 250, panel.Top, "centred on it");
            TestRunner.Eq(64, Layout.PanelRect(R(0, 400, 56, 687), true, work, 400, 500, 8).Left, "and right of a left-edge capsule");
            TestRunner.Eq(0, Layout.PanelRect(R(1864, 0, 1920, 100), false, work, 400, 500, 8).Top, "kept on screen at the top");
            TestRunner.Eq(1040 - 500, Layout.PanelRect(R(1864, 1000, 1920, 1040), false, work, 400, 500, 8).Top, "and at the bottom");
            TestRunner.Eq(0, Layout.PanelRect(R(1864, 400, 1920, 687), false, work, 400, 1200, 8).Top, "a panel taller than the screen starts at its top");
            TestRunner.Eq(1920 - 400, Layout.PanelRect(R(1900, 400, 1956, 687), true, work, 400, 500, 8).Left, "never off the side of the screen");

            RECT grown = Layout.Inflate(R(100, 200, 400, 500), 20);
            TestRunner.Check(grown.Left == 80 && grown.Top == 180 && grown.Right == 420 && grown.Bottom == 520, "room for a shadow on every side");

            // Switching windows puts up full-screen overlays that must not hide the notch; real apps and the lock screen still do.
            TestRunner.Check(Native.IsShellOverlay("XamlExplorerHostIslandWindow"), "Windows 11 Alt-Tab / Task View is not a fullscreen app");
            TestRunner.Check(Native.IsShellOverlay("MultitaskingViewFrame"), "Windows 10 Alt-Tab is not a fullscreen app");
            TestRunner.Check(!Native.IsShellOverlay("Chrome_WidgetWin_1") && !Native.IsShellOverlay("Windows.UI.Core.CoreWindow"), "browsers and the lock screen still count");
        }
    }
}
