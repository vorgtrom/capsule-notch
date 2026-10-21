using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Capsule
{
    public enum ClipKind { Text, Link, Colour, Image }

    // One copy. Kept in memory only: never written to disk, never logged.
    public sealed class ClipItem
    {
        public ClipKind Kind;
        public string Text;          // what was copied, whole; null for an image
        public string Preview = "";  // one line: the text, a link's host and path, the colour as typed
        public string Swatch;        // a colour's #RRGGBB
        public byte[] Png;           // an image, as PNG: a screenshot is far smaller so than as pixels
        public ImageSource Thumb;    // and its thumbnail, frozen
        public string Key = "";      // the same copy again has the same key
        public long AtMs;
    }

    // The last copies, newest first: MaxItems in all, MaxImages of them images. Copying something again moves it to the
    // top. Not thread-safe: the UI thread only.
    public sealed class ClipboardHistory
    {
        public const int MaxItems = 25, MaxImages = 5, MaxTextChars = 100000, PreviewChars = 120;
        const double ThumbHeight = 36;

        static readonly Regex Hex = new Regex(@"^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$");
        static readonly Regex Rgb = new Regex(@"^rgba?\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})\s*(,\s*[\d.]+%?\s*)?\)$", RegexOptions.IgnoreCase);
        static readonly Regex Spaces = new Regex(@"\s+");

        readonly List<ClipItem> items = new List<ClipItem>();

        public IList<ClipItem> Items { get { return items.AsReadOnly(); } }

        // True when the list changed.
        public bool Add(ClipItem item)
        {
            if (item == null) return false;
            if (items.Count > 0 && items[0].Key == item.Key) return false;   // copied again: already on top
            items.RemoveAll(i => i.Key == item.Key);
            items.Insert(0, item);
            int images = 0;
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].Kind == ClipKind.Image && ++images > MaxImages) items.RemoveAt(i--);
            }
            if (items.Count > MaxItems) items.RemoveRange(MaxItems, items.Count - MaxItems);
            return true;
        }

        public void Clear() { items.Clear(); }

        public List<ClipItem> Filtered(ClipKind? kind)
        {
            return kind.HasValue ? items.FindAll(i => i.Kind == kind.Value) : new List<ClipItem>(items);
        }

        // Text as a link (http or https, nothing else on the line), a colour (#hex or rgb()), or plain text. Null when empty
        // or too long to keep.
        public static ClipItem FromText(string text, long now)
        {
            if (text == null || text.Length > MaxTextChars) return null;
            string t = text.Trim();
            if (t == "") return null;
            if (IcsLink.Normalize(t) != null) return null;   // a calendar's secret iCal address: anyone with it can read the calendar
            var item = new ClipItem { Kind = ClipKind.Text, Text = text, AtMs = now, Key = "t:" + text };
            Uri uri;
            if (t.IndexOfAny(new[] { ' ', '\t', '\r', '\n' }) < 0 && Uri.TryCreate(t, UriKind.Absolute, out uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                item.Kind = ClipKind.Link;
                string path = uri.AbsolutePath.TrimEnd('/');
                item.Preview = Shorten(uri.Host + path);   // the host and path: no query, so no tracking codes
                return item;
            }
            string swatch = Colour(t);
            if (swatch != null)
            {
                item.Kind = ClipKind.Colour;
                item.Swatch = swatch;
                item.Preview = t;
                return item;
            }
            item.Preview = Shorten(Spaces.Replace(t, " "));
            return item;
        }

        // #RGB, #RRGGBB, #RRGGBBAA or rgb()/rgba() as #RRGGBB; null for anything else.
        public static string Colour(string t)
        {
            if (Hex.IsMatch(t))
            {
                string h = t.Substring(1);
                if (h.Length == 3) h = new string(new[] { h[0], h[0], h[1], h[1], h[2], h[2] });
                return "#" + h.Substring(0, 6).ToUpperInvariant();
            }
            Match m = Rgb.Match(t);
            if (!m.Success) return null;
            var parts = new int[3];
            for (int i = 0; i < 3; i++)
            {
                parts[i] = int.Parse(m.Groups[i + 1].Value, CultureInfo.InvariantCulture);
                if (parts[i] > 255) return null;
            }
            return "#" + parts[0].ToString("X2") + parts[1].ToString("X2") + parts[2].ToString("X2");
        }

        public static ClipItem FromImage(BitmapSource image, long now)
        {
            if (image == null || image.PixelWidth == 0 || image.PixelHeight == 0) return null;
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            byte[] png;
            using (var stream = new MemoryStream())
            {
                encoder.Save(stream);
                png = stream.ToArray();
            }
            string key;
            using (var sha = SHA1.Create()) key = "i:" + Convert.ToBase64String(sha.ComputeHash(png));
            double scale = Math.Min(1, ThumbHeight * 2 / image.PixelHeight);   // twice the drawn height: sharp on a 200% screen
            var thumb = new TransformedBitmap(image, new ScaleTransform(scale, scale));
            thumb.Freeze();
            return new ClipItem
            {
                Kind = ClipKind.Image,
                Png = png,
                Thumb = thumb,
                Key = key,
                AtMs = now,
                Preview = image.PixelWidth + " × " + image.PixelHeight,
            };
        }

        public static BitmapSource Decode(byte[] png)
        {
            var decoder = new PngBitmapDecoder(new MemoryStream(png), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            BitmapSource frame = decoder.Frames[0];
            frame.Freeze();
            return frame;
        }

        // What password managers and other apps put on a copy that is theirs alone: such a copy is never kept.
        public static bool Private(ICollection<string> formats, Func<string, object> read)
        {
            if (formats.Contains("ExcludeClipboardContentFromMonitorProcessing") || formats.Contains("Clipboard Viewer Ignore")) return true;
            if (!formats.Contains("CanIncludeInClipboardHistory")) return false;
            try
            {
                var stream = read("CanIncludeInClipboardHistory") as MemoryStream;
                if (stream == null || stream.Length < 4) return true;
                return BitConverter.ToInt32(stream.ToArray(), 0) == 0;
            }
            catch (Exception) { return true; }   // can't tell: better skipped
        }

        static string Shorten(string s)
        {
            return s.Length <= PreviewChars ? s : s.Substring(0, PreviewChars - 1) + "…";
        }

        // Reads the clipboard into an item: null when there is nothing to keep, or it is private. May throw while another
        // app holds the clipboard.
        public static ClipItem Read(long now)
        {
            IDataObject data = Clipboard.GetDataObject();
            if (data == null) return null;
            var formats = new HashSet<string>(data.GetFormats(false));
            if (Private(formats, f => data.GetData(f))) return null;
            if (data.GetDataPresent(DataFormats.UnicodeText)) return FromText(data.GetData(DataFormats.UnicodeText) as string, now);
            if (data.GetDataPresent(DataFormats.Bitmap)) return FromImage(Clipboard.GetImage(), now);
            return null;   // files and other kinds: not kept
        }

        // Puts an item back on the clipboard. False while another app holds it.
        public static bool CopyBack(ClipItem item)
        {
            try
            {
                if (item.Kind == ClipKind.Image) Clipboard.SetImage(Decode(item.Png));
                else Clipboard.SetText(item.Text);
                return true;
            }
            catch (COMException) { return false; }
            catch (ExternalException) { return false; }
        }
    }

    // A message-only window Windows tells each time the clipboard changes: nothing is polled.
    public sealed class ClipboardWatcher : IDisposable
    {
        const int WM_CLIPBOARDUPDATE = 0x031D;
        static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool AddClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

        HwndSource source;

        public event Action Changed;

        public bool Start()
        {
            if (source != null) return true;
            source = new HwndSource(new HwndSourceParameters("Capsule clipboard") { ParentWindow = HWND_MESSAGE, WindowStyle = 0 });
            source.AddHook(Hook);
            if (AddClipboardFormatListener(source.Handle)) return true;
            Stop();
            return false;
        }

        public void Stop()
        {
            if (source == null) return;
            RemoveClipboardFormatListener(source.Handle);
            source.RemoveHook(Hook);
            source.Dispose();
            source = null;
        }

        IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_CLIPBOARDUPDATE && Changed != null) Changed();
            return IntPtr.Zero;
        }

        public void Dispose() { Stop(); }
    }
}
