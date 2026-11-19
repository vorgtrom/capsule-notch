using System;
using System.Text;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace Capsule
{
    // The text in a capture, read by Windows' own offline OCR in the user's languages. The text is never logged.
    public static class ScreenText
    {
        // Whether Windows has an OCR language for this user.
        public static bool Available { get { return OcrEngine.TryCreateFromUserProfileLanguages() != null; } }

        // The capture's lines, one per line; "" when it has no text. Null when Windows has no OCR language installed.
        public static async Task<string> ReadAsync(CaptureFrame frame)
        {
            OcrEngine engine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine == null) return null;
            int max = (int)OcrEngine.MaxImageDimension;
            int sourceWidth, sourceHeight;
            byte[] source = Enlarged(frame, max, out sourceWidth, out sourceHeight);   // kept: the frame may be released while this awaits
            int width = Math.Min(sourceWidth, max);
            var text = new StringBuilder();
            // ponytail: a page taller than the OCR limit is read in strips, so a line lying across a strip edge can be
            // lost; overlap the strips if that ever shows up.
            for (int top = 0; top < sourceHeight; top += max)
            {
                int height = Math.Min(max, sourceHeight - top);
                var pixels = new byte[width * height * 4];
                for (int y = 0; y < height; y++)
                    System.Buffer.BlockCopy(source, ((top + y) * sourceWidth) * 4, pixels, y * width * 4, width * 4);
                var writer = new DataWriter();
                writer.WriteBytes(pixels);
                using (SoftwareBitmap bitmap = SoftwareBitmap.CreateCopyFromBuffer(writer.DetachBuffer(), BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied))
                {
                    OcrResult result = await Finish(engine.RecognizeAsync(bitmap));
                    foreach (OcrLine line in result.Lines)
                    {
                        if (text.Length > 0) text.Append("\r\n");
                        AppendLine(text, line);
                    }
                }
            }
            return text.ToString();
        }

        // Windows' OCR misreads small screen text ("Custgm" for "Custom"), so a capture up to about a screen's size is read
        // at twice its size. Bigger ones (a scrolled page) are read as they are, to keep the copies small.
        static byte[] Enlarged(CaptureFrame frame, int max, out int width, out int height)
        {
            width = frame.Width;
            height = frame.Height;
            if (width * 2 > max || height * 2 > max || (long)width * height > 2500000) return frame.Pixels;
            var original = System.Windows.Media.Imaging.BitmapSource.Create(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, frame.Pixels, width * 4);
            var large = new System.Windows.Media.Imaging.TransformedBitmap(original, new System.Windows.Media.ScaleTransform(2, 2));
            width = large.PixelWidth;
            height = large.PixelHeight;
            var pixels = new byte[width * height * 4];
            large.CopyPixels(pixels, width * 4, 0);
            return pixels;
        }

        // The compiler shipped with Windows can't await WinRT operations directly (see WindowsMusicSource.Finish).
        static Task<T> Finish<T>(IAsyncOperation<T> operation)
        {
            var completion = new TaskCompletionSource<T>();
            operation.Completed = delegate(IAsyncOperation<T> result, AsyncStatus status)
            {
                try
                {
                    if (status == AsyncStatus.Completed) completion.TrySetResult(result.GetResults());
                    else if (status == AsyncStatus.Canceled) completion.TrySetCanceled();
                    else completion.TrySetException(result.ErrorCode ?? new InvalidOperationException("Text recognition failed."));
                }
                catch (Exception e) { completion.TrySetException(e); }
            };
            return completion.Task;
        }

        // Words joined by spaces, except between two CJK characters, which Chinese and Japanese write without one.
        static void AppendLine(StringBuilder text, OcrLine line)
        {
            string previous = null;
            foreach (OcrWord word in line.Words)
            {
                if (previous != null && !(Cjk(previous[previous.Length - 1]) && Cjk(word.Text[0]))) text.Append(' ');
                text.Append(word.Text);
                previous = word.Text;
            }
        }

        internal static bool Cjk(char c) { return c >= '⺀' && c <= '鿿' || c >= '豈' && c <= '﫿' || c >= '＀' && c <= '￯'; }
    }
}
