using System;
using System.Threading;
using System.Threading.Tasks;

namespace Capsule
{
    public enum CaptureMode { Screenshot, Scrolling }

    public sealed class CaptureRequest
    {
        public CaptureMode Mode;
        public RECT Area;
        public IntPtr Target;
        public RECT TargetBounds;
    }

    public sealed class CaptureResult
    {
        public CaptureFrame Image;
        public bool Partial;
        public string Notice;
    }

    public sealed class CaptureProgress
    {
        public int FrameCount;
        public int Rows;
        public bool Paused;
        public string Status;
    }

    public interface ICaptureDesktop
    {
        long NowMs { get; }
        bool EscapePressed { get; }
        bool ModifiersPressed { get; }
        bool TargetValid(CaptureRequest request);
        bool PointerInside(RECT area);
        CaptureFrame ReadFrame(RECT area);
        bool Scroll(CaptureRequest request, int direction);
        Task WaitAsync(int milliseconds, CancellationToken cancel);
    }

    public sealed class CaptureSession
    {
        readonly ICaptureDesktop desktop;
        CaptureRequest request;
        CancellationToken cancel;
        long started;
        int running, stopped, frameCount, rows;
        string notice;

        public CaptureSession(ICaptureDesktop desktop)
        {
            if (desktop == null) throw new ArgumentNullException("desktop");
            this.desktop = desktop;
        }

        // Continuations can run off the UI thread. UI subscribers must dispatch their updates.
        public event Action<CaptureProgress> Progress;
        public void Stop() { Interlocked.Exchange(ref stopped, 1); }

        public async Task<CaptureResult> RunAsync(CaptureRequest request, CancellationToken cancel)
        {
            if (request == null) throw new ArgumentNullException("request");
            long width = (long)request.Area.Right - request.Area.Left;
            long height = (long)request.Area.Bottom - request.Area.Top;
            if (width <= 0 || height <= 0 || height > CaptureLimits.MaxRows || width * height > CaptureLimits.MaxPixels ||
                (request.Mode != CaptureMode.Screenshot && request.Mode != CaptureMode.Scrolling))
                throw new ArgumentException("The selected area exceeds the capture limits.", "request");
            if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
                throw new InvalidOperationException("A capture is already running.");
            this.request = new CaptureRequest { Mode = request.Mode, Area = request.Area, Target = request.Target, TargetBounds = request.TargetBounds };
            this.cancel = cancel;
            started = desktop.NowMs;
            stopped = frameCount = rows = 0;
            notice = "";
            CaptureFrame current = null;
            CaptureStitcher stitcher = null;
            try
            {
                CheckCancellation();
                if (request.Mode == CaptureMode.Screenshot)
                {
                    current = ReadChecked();
                    CheckCancellation();
                    var screenshot = new CaptureResult { Image = current, Partial = false, Notice = "" };
                    current = null;
                    return screenshot;
                }
                current = await StableFrameAsync(null).ConfigureAwait(false);
                if (current == null) throw new InvalidOperationException(notice);
                frameCount = 1;
                rows = current.Height;
                Report(false, "Finding the top of the page.");
                long positioning = desktop.NowMs;
                bool positioned = false;
                for (int step = 0; step < 20 && desktop.NowMs - positioning < 5000; step++)
                {
                    if (!await ScrollAsync(1).ConfigureAwait(false)) break;
                    CaptureFrame next = await StableFrameAsync(current).ConfigureAwait(false);
                    if (next == null) break;
                    if (Same(current, next)) { next.Dispose(); positioned = true; break; }
                    current.Dispose();
                    current = next;
                }
                if (notice.Length == 0 && !positioned) notice = "The page top could not be reached within the positioning limit.";
                if (notice.Length == 0)
                {
                    stitcher = new CaptureStitcher(current);
                    CaptureFrame previous = current;
                    current = null;
                    while (await ReadyAsync().ConfigureAwait(false))
                    {
                        if (frameCount >= CaptureLimits.MaxFrames) { notice = "The capture reached the frame limit."; break; }
                        if (!await ScrollAsync(-1).ConfigureAwait(false)) break;
                        CaptureFrame next = await StableFrameAsync(previous).ConfigureAwait(false);
                        if (next == null) break;
                        try
                        {
                            StitchResult added = stitcher.Append(next);
                            if (added.Unchanged) { notice = "The page did not move. The captured portion may be incomplete."; break; }
                            if (!added.Matched) { notice = added.Notice; break; }
                            rows += added.AddedRows;
                            frameCount++;
                            previous = next;
                            next = null;
                        }
                        finally { if (next != null) next.Dispose(); }
                        Report(false, "Capturing the page.");
                    }
                }
                CheckCancellation();
                if (stitcher != null) current = stitcher.Build();
                CheckCancellation();
                CaptureFrame image = current;
                current = null;
                return new CaptureResult { Image = image, Partial = true, Notice = notice };
            }
            finally
            {
                if (current != null) current.Dispose();
                if (stitcher != null) stitcher.Dispose();
                this.request = null;
                Interlocked.Exchange(ref running, 0);
            }
        }

        void CheckCancellation()
        {
            cancel.ThrowIfCancellationRequested();
            if (desktop.EscapePressed || !desktop.TargetValid(request))
                throw new OperationCanceledException("The capture was cancelled because the target changed.", cancel);
        }

        async Task<bool> ReadyAsync()
        {
            long modifiersSince = -1;
            while (true)
            {
                CheckCancellation();
                if (Volatile.Read(ref stopped) != 0) { notice = "Capture stopped. The image contains the verified portion."; return false; }
                if (desktop.NowMs - started >= CaptureLimits.MaxDurationMilliseconds) { notice = "The capture reached the time limit."; return false; }
                bool modifiers = desktop.ModifiersPressed;
                bool outside = !desktop.PointerInside(request.Area);
                if (!modifiers && !outside) return true;
                if (modifiers)
                {
                    if (modifiersSince < 0) modifiersSince = desktop.NowMs;
                    if (desktop.NowMs - modifiersSince >= 5000) { notice = "Release Ctrl, Alt, Shift and Windows keys before capture."; return false; }
                }
                else modifiersSince = -1;
                Report(outside, modifiers ? "Release modifier keys to continue." : "Paused. Move the pointer inside the selected area.");
                CheckCancellation();
                if (Volatile.Read(ref stopped) != 0) continue;
                await desktop.WaitAsync(50, cancel).ConfigureAwait(false);
            }
        }

        async Task<bool> ScrollAsync(int direction)
        {
            while (await ReadyAsync().ConfigureAwait(false))
            {
                CheckCancellation();
                // Recheck immediately before input, even after ReadyAsync returned.
                if (desktop.ModifiersPressed || !desktop.PointerInside(request.Area)) continue;
                bool sent = desktop.Scroll(request, direction);
                CheckCancellation();
                if (sent) return true;
                if (desktop.ModifiersPressed || !desktop.PointerInside(request.Area)) continue;
                notice = "The browser could not be scrolled. The captured portion may be incomplete.";
                return false;
            }
            return false;
        }

        CaptureFrame ReadChecked()
        {
            CheckCancellation();
            CaptureFrame frame = desktop.ReadFrame(request.Area);
            if (frame == null || frame.Pixels == null || frame.Width != request.Area.Width || frame.Height != request.Area.Height)
            {
                if (frame != null) frame.Dispose();
                throw new InvalidOperationException("The selected screen area changed.");
            }
            return frame;
        }

        async Task<CaptureFrame> StableFrameAsync(CaptureFrame previous)
        {
            long began = desktop.NowMs;
            CaptureFrame pending = null;
            try
            {
                while (true)
                {
                    long waiting = desktop.NowMs;
                    if (!await ReadyAsync().ConfigureAwait(false)) return null;
                    began += desktop.NowMs - waiting;
                    if (desktop.NowMs - began >= 2000) { notice = "The page did not become stable. The captured portion may be incomplete."; return null; }
                    CaptureFrame next = ReadChecked();
                    bool stable = pending != null && Same(pending, next);
                    if (pending != null) pending.Dispose();
                    pending = next;
                    CheckCancellation();
                    // Allow delayed scrolling before interpreting two unchanged reads as no movement.
                    if (stable && (previous == null || !Same(previous, pending) || desktop.NowMs - began >= 1000))
                    {
                        CaptureFrame result = pending;
                        pending = null;
                        return result;
                    }
                    await desktop.WaitAsync(100, cancel).ConfigureAwait(false);
                }
            }
            finally { if (pending != null) pending.Dispose(); }
        }

        static bool Same(CaptureFrame first, CaptureFrame second)
        {
            if (first.Width != second.Width || first.Height != second.Height) return false;
            for (int i = 0; i < first.Pixels.Length; i++) if (first.Pixels[i] != second.Pixels[i]) return false;
            return true;
        }

        void Report(bool paused, string status)
        {
            Action<CaptureProgress> handler = Progress;
            if (handler != null) handler(new CaptureProgress { FrameCount = frameCount, Rows = rows, Paused = paused, Status = status });
        }
    }
}
