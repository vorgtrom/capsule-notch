using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using WinForms = System.Windows.Forms;

namespace Capsule
{
    public sealed class MenuItemSpec
    {
        public int Id;
        public string Text = "";
        public bool Checked;
        public bool Separator;
    }

    // An invisible window that owns popup menus, so a menu closes when you click anywhere else.
    public sealed class MenuOwner : IDisposable
    {
        readonly HwndSource source;

        public MenuOwner()
        {
            var p = new HwndSourceParameters("Capsule menu owner");
            p.WindowStyle = 0;
            p.ExtendedWindowStyle = 0x80;   // WS_EX_TOOLWINDOW: never in the taskbar
            p.PositionX = -32000;
            p.PositionY = -32000;
            p.Width = 1;
            p.Height = 1;
            source = new HwndSource(p);
        }

        public IntPtr Handle { get { return source.Handle; } }
        public void Dispose() { source.Dispose(); }
    }

    // A native popup menu at the mouse. Returns the chosen item's Id, or 0 when dismissed.
    public static class NativeMenu
    {
        const uint MF_STRING = 0, MF_CHECKED = 0x8, MF_SEPARATOR = 0x800;
        const uint TPM_RIGHTBUTTON = 0x2, TPM_NONOTIFY = 0x80, TPM_RETURNCMD = 0x100;

        [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string text);
        [DllImport("user32.dll")] static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr owner, IntPtr options);
        [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr menu);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        public static int Show(IntPtr owner, IList<MenuItemSpec> items)
        {
            IntPtr menu = CreatePopupMenu();
            try
            {
                foreach (MenuItemSpec item in items)
                {
                    if (item.Separator) AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
                    else AppendMenu(menu, MF_STRING | (item.Checked ? MF_CHECKED : 0), new UIntPtr((uint)item.Id), item.Text);
                }
                POINT at;
                Native.GetCursorPos(out at);
                SetForegroundWindow(owner);   // without this the menu would not close on a click elsewhere
                int chosen = TrackPopupMenuEx(menu, TPM_RIGHTBUTTON | TPM_NONOTIFY | TPM_RETURNCMD, at.X, at.Y, owner, IntPtr.Zero);
                PostMessage(owner, 0, IntPtr.Zero, IntPtr.Zero);   // WM_NULL: the documented companion to the line above
                return chosen;
            }
            finally
            {
                DestroyMenu(menu);
            }
        }
    }

    // The tray icon: a small ring in the colour of Claude's current session, with the numbers as its tooltip.
    public sealed class Tray : IDisposable
    {
        readonly WinForms.NotifyIcon icon = new WinForms.NotifyIcon();
        IntPtr handle = IntPtr.Zero;
        string lastKey;

        public event Action LeftClick;
        public event Action RightClick;

        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);

        public Tray()
        {
            icon.MouseUp += delegate(object sender, WinForms.MouseEventArgs e)
            {
                if (e.Button == WinForms.MouseButtons.Left && LeftClick != null) LeftClick();
                if (e.Button == WinForms.MouseButtons.Right && RightClick != null) RightClick();
            };
            Update(null, "Capsule");
            icon.Visible = true;
        }

        public void Update(double? claudeUsed, string tooltip)
        {
            string key = (claudeUsed.HasValue ? Palette.ForUsed(claudeUsed.Value) + Math.Round(claudeUsed.Value) : "none") + "|" + tooltip;
            if (key == lastKey) return;
            lastKey = key;
            using (Bitmap bitmap = DrawRing(claudeUsed, WinForms.SystemInformation.SmallIconSize.Width))
            {
                IntPtr fresh = bitmap.GetHicon();
                icon.Icon = Icon.FromHandle(fresh);
                if (handle != IntPtr.Zero) DestroyIcon(handle);
                handle = fresh;
            }
            icon.Text = tooltip.Length > 63 ? tooltip.Substring(0, 63) : tooltip;   // the limit NotifyIcon allows
        }

        public void Balloon(string title, string text) { icon.ShowBalloonTip(5000, title, text, WinForms.ToolTipIcon.None); }

        public static Bitmap DrawRing(double? used, int size)
        {
            var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                float stroke = Math.Max(2f, size / 7f);
                var ring = new RectangleF(stroke / 2 + 0.5f, stroke / 2 + 0.5f, size - stroke - 1, size - stroke - 1);
                using (var track = new Pen(Color.FromArgb(140, 142, 142, 147), stroke)) g.DrawEllipse(track, ring);
                if (used.HasValue && used.Value >= 0.5)
                {
                    using (var arc = new Pen(ColorTranslator.FromHtml(Palette.ForUsed(used.Value)), stroke))
                    {
                        arc.StartCap = LineCap.Round;
                        arc.EndCap = LineCap.Round;
                        g.DrawArc(arc, ring, -90, (float)(Math.Min(100, used.Value) / 100 * 360));
                    }
                }
            }
            return bitmap;
        }

        public void Dispose()
        {
            icon.Visible = false;
            icon.Dispose();
            if (handle != IntPtr.Zero) DestroyIcon(handle);
        }
    }
}
