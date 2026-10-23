using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Capsule
{
    public sealed class CaptureCoordinator : IDisposable
    {
        readonly Action<bool> visibility;
        readonly Func<uint, uint, bool> register;
        readonly Action releaseShortcut;
        readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        readonly GlobalHotkey shortcut;
        CancellationTokenSource active;
        CaptureChooser chooser;
        CaptureProgressWindow progress;
        CapturePreviewWindow preview;
        string registeredText;
        bool disposed, suppressed;
        internal Func<CancellationToken, Task<CaptureRequest>> SelectRequest;
        internal Func<CaptureRequest, CancellationToken, Task<CaptureResult>> Capture;
        internal Func<CaptureResult, CapturePreviewWindow> CreatePreview;
        internal Action<Window> ShowWindow;
        internal Action<string> ShowError;
        internal Action WaitForComposition = delegate
        {
            if (DwmFlush() != 0) throw new InvalidOperationException("The desktop composition did not finish.");
        };
        public bool Busy { get; private set; }
        internal bool ShortcutRegistered { get; private set; }

        [DllImport("dwmapi.dll")] static extern int DwmFlush();

        public CaptureCoordinator(Action<bool> captureVisibilityChanged) : this(captureVisibilityChanged, null, null) { }

        internal CaptureCoordinator(Action<bool> visibility, Func<uint, uint, bool> register, Action release)
        {
            if (visibility == null) throw new ArgumentNullException("visibility");
            this.visibility = visibility;
            if (register == null)
            {
                shortcut = new GlobalHotkey();
                shortcut.Pressed += Open;
                this.register = shortcut.Register;
                releaseShortcut = shortcut.Dispose;
            }
            else { this.register = register; releaseShortcut = release; }
            SelectRequest = SelectAsync;
            Capture = CaptureAsync;
            CreatePreview = r => new CapturePreviewWindow(r);
            ShowWindow = w => w.Show();
            ShowError = text => MessageBox.Show(text, "Capture", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        public bool RegisterShortcut(string text)
        {
            uint modifiers, key;
            if (disposed || !HotkeyText.TryParse(text, out modifiers, out key)) return false;
            if (register(modifiers, key)) { ShortcutRegistered = true; registeredText = HotkeyText.Format(modifiers, key); return true; }
            ShortcutRegistered = HotkeyText.TryParse(registeredText, out modifiers, out key) && register(modifiers, key);
            return false;
        }

        public async void Open()
        {
            if (disposed || Busy) return;
            Busy = true;
            var source = new CancellationTokenSource();
            active = source;
            CaptureResult result = null;
            try
            {
                suppressed = true;
                visibility(true);
                CaptureRequest request = await SelectRequest(source.Token);
                source.Token.ThrowIfCancellationRequested();
                result = await Capture(request, source.Token);
                source.Token.ThrowIfCancellationRequested();
                RestoreVisibility();
                preview = CreatePreview(result);
                var closed = new TaskCompletionSource<bool>();
                preview.Closed += delegate { closed.TrySetResult(true); };
                preview.RetakeRequested += delegate { dispatcher.BeginInvoke(new Action(Open)); };
                ShowWindow(preview);
                await closed.Task;
            }
            catch (OperationCanceledException) { }
            catch (Exception)
            {
                // Never expose page content, native details, or private paths in an error.
                RestoreVisibility();
                if (!disposed) ShowError("Capture could not finish. Try another area or use Screenshot.");
            }
            finally
            {
                if (preview != null) { preview.Close(); preview = null; }
                if (progress != null) { progress.Close(); progress = null; }
                if (chooser != null) { chooser.Close(); chooser = null; }
                if (result != null && result.Image != null) result.Image.Dispose();
                RestoreVisibility();
                if (active == source) { active = null; Busy = false; }
                source.Dispose();
            }
        }

        async Task<CaptureRequest> SelectAsync(CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();
            var choice = new TaskCompletionSource<CaptureMode>();
            chooser = new CaptureChooser();
            chooser.Chosen += mode => choice.TrySetResult(mode);
            chooser.Cancelled += delegate { choice.TrySetCanceled(); };
            using (cancel.Register(delegate { dispatcher.BeginInvoke(new Action(delegate { if (chooser != null) chooser.Close(); })); }))
            {
                ShowWindow(chooser);
                CaptureMode mode = await choice.Task;
                chooser = null;
                cancel.ThrowIfCancellationRequested();
                return await CaptureSelection.Open(mode, cancel);
            }
        }

        async Task<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken cancel)
        {
            var desktop = new CaptureDesktop();
            var session = new CaptureSession(desktop);
            if (request.Mode == CaptureMode.Scrolling)
            {
                progress = new CaptureProgressWindow();
                foreach (MonitorInfo monitor in Native.Monitors())
                    if (request.Area.Left >= monitor.Bounds.Left && request.Area.Top >= monitor.Bounds.Top &&
                        request.Area.Right <= monitor.Bounds.Right && request.Area.Bottom <= monitor.Bounds.Bottom)
                    { progress.Place(request.Area, monitor); break; }
                progress.StopRequested += session.Stop;
                progress.CancelRequested += delegate { if (active != null) active.Cancel(); };
                session.Progress += progress.Update;
                ShowWindow(progress);
            }
            CaptureProgressWindow display = progress;
            EventHandler closed = delegate { if (active != null) active.Cancel(); };
            if (display != null) display.Closed += closed;
            ConfigureReads(desktop, display, cancel, () => desktop.TargetValid(request) && !desktop.EscapePressed);
            try { return await Task.Run(() => session.RunAsync(request, cancel)); }
            finally
            {
                if (display != null)
                {
                    display.Closed -= closed;
                    session.Progress -= display.Update;
                    if (progress == display) progress = null;
                    display.Close();
                }
            }
        }

        internal void ConfigureReads(CaptureDesktop desktop, CaptureProgressWindow display, CancellationToken cancel, Func<bool> valid)
        {
            bool closed = false, ready = false;
            if (display != null) display.Closed += delegate { closed = true; };
            desktop.BeforeRead = delegate
            {
                ready = false;
                if (disposed) throw new OperationCanceledException();
                cancel.ThrowIfCancellationRequested();
                if (display != null && display.OverlapsSelection)
                {
                    dispatcher.Invoke(new Action(delegate { if (!closed) display.Hide(); }));
                    WaitForComposition();
                }
                cancel.ThrowIfCancellationRequested();
                if (disposed || closed || !valid()) throw new OperationCanceledException();
                ready = true;
            };
            desktop.AfterRead = delegate
            {
                if (display == null || !display.OverlapsSelection) return;
                dispatcher.Invoke(new Action(delegate
                {
                    if (ready && !disposed && !closed && !cancel.IsCancellationRequested) ShowWindow(display);
                }));
            };
        }

        void RestoreVisibility()
        {
            if (!suppressed) return;
            suppressed = false;
            visibility(false);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (active != null) active.Cancel();
            if (chooser != null) chooser.Close();
            if (progress != null) { progress.Close(); progress = null; }
            if (preview != null) preview.Close();
            RestoreVisibility();
            if (releaseShortcut != null) releaseShortcut();
            Busy = false;
        }
    }
}
