using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Capsule
{
    public static class CaptureSessionTests
    {
        public static void Run()
        {
            Screenshot();
            KnownOffsets();
            NoMovementAndFailures();
            Stability();
            Cancellation();
            PauseAndModifiers();
            Bounds();
            Geometry();
            BoundaryChanges();
        }

        static CaptureRequest Request(CaptureMode mode, int width = 3, int height = 120)
        {
            return new CaptureRequest { Mode = mode, Area = new RECT { Left = -200, Top = 20, Right = -200 + width, Bottom = 20 + height },
                Target = new IntPtr(42), TargetBounds = new RECT { Left = -300, Top = 0, Right = 100, Bottom = 1000 } };
        }

        static CaptureFrame Page(int width, int start, int height)
        {
            byte[] pixels = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    uint value = unchecked((uint)((start + y + 1) * 2654435761L) ^ (uint)((x + 1) * 2246822519L));
                    int p = (y * width + x) * 4;
                    pixels[p] = (byte)value;
                    pixels[p + 1] = (byte)(value >> 8);
                    pixels[p + 2] = (byte)(value >> 16);
                    pixels[p + 3] = (byte)(value >> 24);
                }
            return new CaptureFrame(width, height, pixels);
        }

        sealed class Desktop : ICaptureDesktop
        {
            public long Clock;
            public long NowMs { get { return Clock; } }
            public bool Valid = true, Escape, Inside = true, Modified, InputWorks = true;
            public bool EscapePressed { get { return Escape; } }
            public bool ModifiersPressed { get { return Modified; } }
            public int Reads, Up, Down, Offset, PreviousOffset, Bottom = 120, Step = 60;
            public long ScrollTime;
            public Action<Desktop> OnWait;
            public Action<Desktop> OnRead;
            public Action<Desktop> OnScroll;
            public Func<Desktop, bool> OnValid;
            public Func<Desktop, bool> OnPointer;
            public Func<Desktop, int> ReadOffset;
            public readonly List<CaptureFrame> Frames = new List<CaptureFrame>();
            public bool TargetValid(CaptureRequest request) { return OnValid == null ? Valid : OnValid(this); }
            public bool PointerInside(RECT area) { return OnPointer == null ? Inside : OnPointer(this); }
            public CaptureFrame ReadFrame(RECT area)
            {
                Reads++;
                CaptureFrame frame = Page(area.Width, ReadOffset == null ? Offset : ReadOffset(this), area.Height);
                Frames.Add(frame);
                if (OnRead != null) OnRead(this);
                return frame;
            }
            public bool Scroll(CaptureRequest request, int direction)
            {
                TestRunner.Check(Valid && !Escape && Inside && !Modified, "session checks target, pointer and modifiers before input");
                PreviousOffset = Offset;
                ScrollTime = Clock;
                if (direction > 0) { Up++; Offset = Math.Max(0, Offset - Step); }
                else { Down++; Offset = Math.Min(Bottom, Offset + Step); }
                if (OnScroll != null) OnScroll(this);
                return InputWorks;
            }
            public Task WaitAsync(int milliseconds, CancellationToken cancel)
            {
                cancel.ThrowIfCancellationRequested();
                Clock += milliseconds;
                if (OnWait != null) OnWait(this);
                return Task.FromResult(0);
            }
        }

        // Passes everything to the fake desktop, and reports the process id each target check is asked about.
        sealed class ProcessCheck : ICaptureDesktop
        {
            readonly Desktop inner;
            readonly Action<uint> seen;
            public ProcessCheck(Desktop inner, Action<uint> seen) { this.inner = inner; this.seen = seen; }
            public long NowMs { get { return inner.NowMs; } }
            public bool EscapePressed { get { return inner.EscapePressed; } }
            public bool ModifiersPressed { get { return inner.ModifiersPressed; } }
            public bool TargetValid(CaptureRequest request) { seen(request.Process); return inner.TargetValid(request); }
            public bool PointerInside(RECT area) { return inner.PointerInside(area); }
            public CaptureFrame ReadFrame(RECT area) { return inner.ReadFrame(area); }
            public bool Scroll(CaptureRequest request, int direction) { return inner.Scroll(request, direction); }
            public Task WaitAsync(int milliseconds, CancellationToken cancel) { return inner.WaitAsync(milliseconds, cancel); }
        }

        static CaptureResult Run(Desktop desktop, CaptureRequest request = null)
        {
            return new CaptureSession(desktop).RunAsync(request ?? Request(CaptureMode.Scrolling), CancellationToken.None).GetAwaiter().GetResult();
        }

        static void Released(Desktop desktop, CaptureFrame retained = null)
        {
            bool released = true;
            foreach (CaptureFrame frame in desktop.Frames)
                if (!Object.ReferenceEquals(frame, retained) && frame.Pixels != null) released = false;
            TestRunner.Check(released, "session releases every source except the caller's image");
        }

        static void KnownOffsets()
        {
            var desktop = new Desktop { Offset = 180 };
            CaptureResult result = Run(desktop);
            using (result.Image)
            using (CaptureFrame expected = Page(3, 0, 240))
            {
                TestRunner.Eq(240, result.Image.Height, "upward positioning starts output at the first verified row");
                bool same = result.Image.Pixels.Length == expected.Pixels.Length;
                for (int i = 0; same && i < expected.Pixels.Length; i++) same = expected.Pixels[i] == result.Image.Pixels[i];
                TestRunner.Check(same, "known scroll offsets preserve all bytes");
                TestRunner.Check(desktop.Up >= 3 && desktop.Down == 3, "session positions upward then scrolls downward");
                TestRunner.Check(result.Partial && !String.IsNullOrEmpty(result.Notice), "no movement cannot prove document completion");
                Released(desktop, result.Image);
            }
        }

        static void NoMovementAndFailures()
        {
            var stopped = new Desktop { Bottom = 0 };
            CaptureResult result = Run(stopped);
            TestRunner.Check(result.Partial && result.Image.Height == 120 && result.Notice.Contains("move"), "blocked scrolling keeps one partial frame");
            result.Image.Dispose();
            Released(stopped);
            var failed = new Desktop { InputWorks = false };
            result = Run(failed);
            TestRunner.Check(result.Partial && result.Notice.Contains("scroll"), "failed input returns a short partial notice");
            TestRunner.Eq(0, failed.Down, "failed upward input prevents downward input");
            result.Image.Dispose();
            Released(failed);
            var distant = new Desktop { Offset = 100000, Bottom = 100120 };
            result = Run(distant);
            TestRunner.Check(result.Partial && distant.Up <= CaptureSession.PositionSteps * CaptureSession.PositionNotches, "upward positioning is bounded");
            TestRunner.Check(distant.Down > 0 && result.Image.Height > 120 && result.Notice.Contains("top could not be reached"),
                "a page too far down to reach its top is still captured from where it is, and says so");
            using (CaptureFrame expected = Page(3, 100000 - distant.Up * distant.Step, result.Image.Height))
            {
                bool same = expected.Pixels.Length == result.Image.Pixels.Length;
                for (int i = 0; same && i < expected.Pixels.Length; i++) same = expected.Pixels[i] == result.Image.Pixels[i];
                TestRunner.Check(same, "the capture from below the top keeps every row in order");
            }
            result.Image.Dispose();
            Released(distant);
        }

        static void Stability()
        {
            var delayed = new Desktop();
            delayed.ReadOffset = d => d.Up + d.Down > 0 && d.Clock - d.ScrollTime < 700 ? d.PreviousOffset : d.Offset;
            CaptureResult result = Run(delayed);
            TestRunner.Eq(240, result.Image.Height, "delayed stable movement is not mistaken for blocked input");
            result.Image.Dispose();
            Released(delayed);

            var unstable = new Desktop();
            unstable.ReadOffset = d => d.Down > 0 ? 10000 + d.Reads * 100 : d.Offset;
            result = Run(unstable);
            TestRunner.Check(result.Partial && result.Image.Height == 120 && result.Notice.Contains("stable"), "unstable frames stop without an invented join");
            TestRunner.Check(unstable.Clock < 10000, "stable-frame wait is bounded");
            result.Image.Dispose();
            Released(unstable);

            var mismatch = new Desktop();
            mismatch.ReadOffset = d => d.Down > 0 ? 10000 : d.Offset;
            result = Run(mismatch);
            TestRunner.Check(result.Partial && result.Image.Height == 120 && result.Notice.Contains("overlap"), "unverified overlap keeps only accepted rows");
            result.Image.Dispose();
            Released(mismatch);
        }

        static void ExpectCancel(Desktop desktop, CancellationToken cancel, string name)
        {
            bool cancelled = false;
            try { CaptureResult result = new CaptureSession(desktop).RunAsync(Request(CaptureMode.Scrolling), cancel).GetAwaiter().GetResult(); if (result != null && result.Image != null) result.Image.Dispose(); }
            catch (OperationCanceledException) { cancelled = true; }
            TestRunner.Check(cancelled, name + " discards the capture");
            Released(desktop);
        }

        static void Cancellation()
        {
            var before = new Desktop();
            var token = new CancellationTokenSource();
            token.Cancel();
            ExpectCancel(before, token.Token, "pre-cancelled request");
            TestRunner.Eq(0, before.Reads + before.Up + before.Down, "pre-cancel sends no input or reads");
            token.Dispose();
            foreach (string reason in new[] { "Escape", "close", "move", "resize", "minimize", "foreground" })
            {
                var desktop = new Desktop();
                int readsAtCancel = -1, inputAtCancel = -1;
                desktop.OnWait = d =>
                {
                    if (readsAtCancel >= 0) return;
                    readsAtCancel = d.Reads;
                    inputAtCancel = d.Up + d.Down;
                    if (reason == "Escape") d.Escape = true;
                    else d.Valid = false;
                };
                ExpectCancel(desktop, CancellationToken.None, reason);
                TestRunner.Eq(readsAtCancel, desktop.Reads, reason + " permits no further reads");
                TestRunner.Eq(inputAtCancel, desktop.Up + desktop.Down, reason + " permits no further input");
            }
            var during = new Desktop();
            using (var source = new CancellationTokenSource())
            {
                during.OnRead = d => source.Cancel();
                ExpectCancel(during, source.Token, "cancellation during read");
                TestRunner.Eq(1, during.Reads, "in-flight read is the last read");
                TestRunner.Eq(0, during.Up + during.Down, "cancellation after read prevents input");
            }
        }

        static void PauseAndModifiers()
        {
            var desktop = new Desktop();
            var session = new CaptureSession(desktop);
            bool paused = false;
            session.Progress += p => { if (p.FrameCount >= 2) desktop.Inside = false; if (p.Paused) { paused = true; session.Stop(); } };
            CaptureResult result = session.RunAsync(Request(CaptureMode.Scrolling), CancellationToken.None).GetAwaiter().GetResult();
            TestRunner.Check(paused && result.Partial && result.Image.Height == 180, "Stop while paused preserves verified rows");
            TestRunner.Eq(1, desktop.Down, "pointer outside prevents the next scroll");
            result.Image.Dispose();
            Released(desktop);

            var cancelled = new Desktop();
            using (var source = new CancellationTokenSource())
            {
                var capture = new CaptureSession(cancelled);
                capture.Progress += p => { if (p.FrameCount >= 1) cancelled.Inside = false; if (p.Paused) source.Cancel(); };
                bool discarded = false;
                try { capture.RunAsync(Request(CaptureMode.Scrolling), source.Token).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { discarded = true; }
                TestRunner.Check(discarded && cancelled.Up + cancelled.Down == 0, "Cancel while paused discards without input");
                Released(cancelled);
            }
            var held = new Desktop { Modified = true };
            bool failed = false;
            try { result = Run(held); if (result != null) result.Image.Dispose(); }
            catch (InvalidOperationException) { failed = true; }
            TestRunner.Check(failed && held.Clock <= 5000 && held.Up + held.Down == 0, "held modifiers stop within five seconds without input");
            Released(held);
            var released = new Desktop { Modified = true };
            released.OnWait = d => { if (d.Clock >= 350) d.Modified = false; };
            result = Run(released);
            TestRunner.Check(released.Clock >= 350 && released.Down > 0, "capture proceeds after modifier release");
            result.Image.Dispose();
            Released(released);

            var later = new Desktop();
            var laterSession = new CaptureSession(later);
            laterSession.Progress += p => { if (p.FrameCount >= 2) later.Modified = true; };
            result = laterSession.RunAsync(Request(CaptureMode.Scrolling), CancellationToken.None).GetAwaiter().GetResult();
            TestRunner.Check(result.Partial && later.Down == 1, "newly held modifiers prevent the next scroll");
            result.Image.Dispose();
            Released(later);
        }

        static void Bounds()
        {
            var timed = new Desktop();
            timed.OnWait = d => d.Clock = 90000;
            bool noImage = false;
            try { CaptureResult early = Run(timed); if (early != null) early.Image.Dispose(); }
            catch (InvalidOperationException) { noImage = true; }
            TestRunner.Check(noImage && timed.Reads == 1 && timed.Up + timed.Down == 0, "90,000 ms deadline prevents further work before first stable frame");
            Released(timed);
            var paused = new Desktop();
            var session = new CaptureSession(paused);
            session.Progress += p => { if (p.FrameCount >= 1) paused.Inside = false; };
            paused.OnWait = d => { if (!d.Inside) d.Clock = 90000; };
            CaptureResult result = session.RunAsync(Request(CaptureMode.Scrolling), CancellationToken.None).GetAwaiter().GetResult();
            TestRunner.Check(result.Partial && result.Notice.Contains("time") && paused.Up + paused.Down == 0, "90,000 ms deadline also bounds pointer pause");
            result.Image.Dispose();
            Released(paused);

            foreach (int width in new[] { 1, 1100 })
            {
                var limit = new Desktop { Step = 900, Bottom = 1000000 };
                result = Run(limit, Request(CaptureMode.Scrolling, width, 1000));
                TestRunner.Check(result.Partial && result.Image.Height == (width == 1 ? 31600 : 28900), "output stops before next 900-row append crosses limit");
                TestRunner.Check(result.Notice.Contains(width == 1 ? "row limit" : "pixel limit"), "output explains the reached limit");
                result.Image.Dispose();
                Released(limit);
            }
            var frames = new Desktop { Step = 20, Bottom = 1000000 };
            result = Run(frames);
            TestRunner.Eq(2500, result.Image.Height, "120 accepted frames retain 120 + 119 * 20 rows");
            TestRunner.Check(result.Partial && result.Notice.Contains("frame limit"), "accepted frame count is bounded at 120");
            result.Image.Dispose();
            Released(frames);
        }

        static void Geometry()
        {
            foreach (double scale in new[] { 1.0, 1.25, 1.5, 2.0 })
            {
                var monitor = new MonitorInfo { Bounds = new RECT { Left = -2560, Top = -1440, Right = 0, Bottom = 0 }, Scale = scale };
                POINT point = CaptureDesktop.PhysicalPoint(monitor, 80, 40);
                TestRunner.Eq(-2560 + (int)(80 * scale), point.X, "negative monitor X adds scaled local coordinate");
                TestRunner.Eq(-1440 + (int)(40 * scale), point.Y, "negative monitor Y adds scaled local coordinate");
                RECT area = CaptureDesktop.PhysicalArea(monitor, 80, 40, 240, 120);
                TestRunner.Eq((int)(160 * scale), area.Width, "selection width uses physical edge difference");
                TestRunner.Eq((int)(80 * scale), area.Height, "selection height uses physical edge difference");
            }
            var fractional = new MonitorInfo { Bounds = new RECT { Left = -1920, Top = -100, Right = 0, Bottom = 980 }, Scale = 1.25 };
            RECT rounded = CaptureDesktop.PhysicalArea(fractional, 0.4, 0.4, 10, 10);
            TestRunner.Eq(-1919, rounded.Left, "fractional half pixels round away from the monitor origin");
            TestRunner.Eq(12, rounded.Width, "fractional edges preserve the selected pixel width");
            CaptureRequest request = Request(CaptureMode.Scrolling);
            TestRunner.Check(CaptureDesktop.WindowMatches(request, true, true, false, request.Target, request.TargetBounds, true), "valid unchanged foreground browser is eligible");
            TestRunner.Check(!CaptureDesktop.WindowMatches(request, false, true, false, request.Target, request.TargetBounds, true), "closed window invalidates target");
            TestRunner.Check(!CaptureDesktop.WindowMatches(request, true, false, false, request.Target, request.TargetBounds, true), "hidden window invalidates target");
            TestRunner.Check(!CaptureDesktop.WindowMatches(request, true, true, true, request.Target, request.TargetBounds, true), "minimized window invalidates target");
            TestRunner.Check(!CaptureDesktop.WindowMatches(request, true, true, false, IntPtr.Zero, request.TargetBounds, true), "foreground loss invalidates target");
            RECT changed = request.TargetBounds; changed.Left++;
            TestRunner.Check(!CaptureDesktop.WindowMatches(request, true, true, false, request.Target, changed, true), "window movement invalidates target");
            changed = request.TargetBounds; changed.Right++;
            TestRunner.Check(!CaptureDesktop.WindowMatches(request, true, true, false, request.Target, changed, true), "window resize invalidates target");
            TestRunner.Check(!CaptureDesktop.WindowMatches(request, true, true, false, request.Target, request.TargetBounds, false), "non-browser process is ineligible");
            var browser = new CaptureRequest { Process = 4321 };
            TestRunner.Check(CaptureDesktop.SameBrowser(browser, 4321), "the window still belongs to the browser found at the start");
            TestRunner.Check(!CaptureDesktop.SameBrowser(browser, 99) && !CaptureDesktop.SameBrowser(browser, 0), "another process, or none, is not the browser");
            TestRunner.Check(!CaptureDesktop.SameBrowser(new CaptureRequest(), 0), "a request without a browser process matches nothing");
            var dragged = new RECT { Left = 10, Top = 20, Right = 110, Bottom = 220 };
            TestRunner.Check(!CaptureDesktop.Contains(dragged, new POINT { X = 110, Y = 220 }) && !CaptureDesktop.Contains(dragged, new POINT { X = 110, Y = 100 }) &&
                !CaptureDesktop.Contains(dragged, new POINT { X = 60, Y = 220 }), "the pixel past the right or bottom edge, where a drag ends, is outside: it can be another window's");
            TestRunner.Check(CaptureDesktop.Contains(dragged, new POINT { X = 10, Y = 20 }) && CaptureDesktop.Contains(dragged, new POINT { X = 109, Y = 219 }) &&
                CaptureDesktop.Contains(dragged, new POINT { X = 60, Y = 100 }), "the first, last and middle captured pixels are inside");
            TestRunner.Check(!CaptureDesktop.Contains(dragged, new POINT { X = 9, Y = 100 }) && !CaptureDesktop.Contains(dragged, new POINT { X = 60, Y = 19 }), "a pixel before the left or top edge is outside");
            uint seen = 0;
            var tracked = new Desktop { Bottom = 0 };
            CaptureRequest withProcess = Request(CaptureMode.Scrolling);
            withProcess.Process = 4321;
            CaptureSession checker = new CaptureSession(new ProcessCheck(tracked, p => seen = p));
            using (checker.RunAsync(withProcess, System.Threading.CancellationToken.None).GetAwaiter().GetResult().Image) { }
            TestRunner.Eq(4321u, seen, "the session checks the target with the browser process it was given");
        }

        static void BoundaryChanges()
        {
            var changedAtInput = new Desktop { InputWorks = false };
            changedAtInput.OnScroll = d => d.Valid = false;
            ExpectCancel(changedAtInput, CancellationToken.None, "target invalidation during rejected input");

            var changedAtBuild = new Desktop();
            changedAtBuild.OnValid = d =>
            {
                if (d.Frames.Count == 0) return true;
                foreach (CaptureFrame frame in d.Frames) if (frame.Pixels != null) return true;
                return false;
            };
            ExpectCancel(changedAtBuild, CancellationToken.None, "target invalidation while output builds");

            var pointer = new Desktop { Offset = 180 };
            int pointerChecks = 0;
            pointer.OnPointer = d => { if (++pointerChecks == 4) d.Inside = false; return d.Inside; };
            pointer.OnWait = d => d.Inside = true;
            CaptureResult result = Run(pointer);
            using (CaptureFrame expected = Page(3, 0, 240))
            {
                bool same = result.Image.Pixels.Length == expected.Pixels.Length;
                for (int i = 0; same && i < expected.Pixels.Length; i++) same = result.Image.Pixels[i] == expected.Pixels[i];
                TestRunner.Check(same, "pointer departure immediately before input pauses and resumes positioning");
            }
            result.Image.Dispose();
            Released(pointer);

            var pausedRead = new Desktop();
            pausedRead.OnRead = d => { if (d.Reads == 1) d.Inside = false; };
            pausedRead.OnWait = d => { if (d.Clock >= 3100) d.Inside = true; };
            bool resumed = false;
            try
            {
                result = Run(pausedRead);
                resumed = result.Image.Height == 240;
                result.Image.Dispose();
            }
            catch (InvalidOperationException) { }
            TestRunner.Check(resumed, "pointer pause does not consume the stable-frame wait budget");
            Released(pausedRead);
        }

        static void Screenshot()
        {
            var desktop = new Desktop();
            CaptureResult result = new CaptureSession(desktop).RunAsync(Request(CaptureMode.Screenshot), CancellationToken.None).GetAwaiter().GetResult();
            TestRunner.Check(result != null && result.Image != null, "screenshot returns one image");
            if (result == null || result.Image == null) return;
            TestRunner.Eq(1, desktop.Reads, "screenshot reads exactly once");
            TestRunner.Eq(0, desktop.Up + desktop.Down, "screenshot sends no input");
            TestRunner.Check(!result.Partial, "screenshot is complete");
            TestRunner.Check(Object.ReferenceEquals(result.Image, desktop.Frames[0]), "screenshot transfers its frame to caller");
            result.Image.Dispose();
        }
    }
}
