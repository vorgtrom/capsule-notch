using System;

namespace Capsule
{
    // Pixel arithmetic for placing the notch and the card. Pure, so it is tested without windows.
    public static class Layout
    {
        // The notch sits flush with the work area's left or right side, centred at fraction y of its height,
        // and never runs off the top or bottom.
        public static RECT NotchRect(RECT work, bool leftEdge, double y, int width, int height)
        {
            var r = new RECT();
            r.Left = leftEdge ? work.Left : work.Right - width;
            r.Right = r.Left + width;
            int centre = work.Top + (int)Math.Round(Math.Max(0, Math.Min(1, y)) * work.Height);
            r.Top = Math.Max(work.Top, Math.Min(work.Bottom - height, centre - height / 2));
            r.Bottom = r.Top + height;
            return r;
        }

        // After a drag: whether the notch's centre is nearer the left side.
        public static bool SnapLeft(RECT window, RECT work)
        {
            return (window.Left + window.Right) / 2 < (work.Left + work.Right) / 2;
        }

        // After a drag: the notch's centre as a fraction of the work area's height.
        public static double SnapY(RECT window, RECT work)
        {
            if (work.Height <= 0) return 0.5;
            double y = ((window.Top + window.Bottom) / 2.0 - work.Top) / work.Height;
            return Math.Max(0, Math.Min(1, y));
        }

        // The panel beside the capsule, on the screen side, vertically centred on it and kept fully on screen.
        public static RECT PanelRect(RECT capsule, bool leftEdge, RECT work, int width, int height, int gap)
        {
            var r = new RECT();
            int left = leftEdge ? capsule.Right + gap : capsule.Left - gap - width;
            r.Left = Math.Max(work.Left, Math.Min(work.Right - width, left));
            r.Right = r.Left + width;
            int middle = (capsule.Top + capsule.Bottom) / 2;
            r.Top = Math.Max(work.Top, Math.Min(work.Bottom - height, middle - height / 2));
            r.Bottom = r.Top + height;
            return r;
        }

        // r grown by n pixels on every side: a window with room around its content for a shadow.
        public static RECT Inflate(RECT r, int n)
        {
            var grown = new RECT();
            grown.Left = r.Left - n;
            grown.Top = r.Top - n;
            grown.Right = r.Right + n;
            grown.Bottom = r.Bottom + n;
            return grown;
        }

        // The card beside a cell, on the screen side, vertically centred on it and kept on screen.
        // pointerY: where the card's pointer should sit, in pixels from the card's top.
        public static RECT CardRect(RECT cell, bool leftEdge, RECT work, int width, int height, int gap, out int pointerY)
        {
            var r = new RECT();
            r.Left = leftEdge ? cell.Right + gap : cell.Left - gap - width;
            r.Right = r.Left + width;
            int middle = (cell.Top + cell.Bottom) / 2;
            r.Top = Math.Max(work.Top, Math.Min(work.Bottom - height, middle - height / 2));
            r.Bottom = r.Top + height;
            pointerY = middle - r.Top;
            return r;
        }
    }
}
