using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Capsule
{
    public static class CaptureIntegrationTests
    {
        public static void Run()
        {
            Lifecycle();
            Disposal();
            RetakeAndLateResult();
            Shortcuts();
            SelectionCancellation();
            ReadHooks();
            ProgressExclusion();
            ControllerAndCopy();
            PanelEntry();
        }
        static CaptureRequest Request() { return new CaptureRequest { Mode = CaptureMode.Screenshot, Area = new RECT { Right = 2, Bottom = 2 } }; }
        static CaptureResult Result() { return new CaptureResult { Image = new CaptureFrame(2, 2, new byte[16]), Notice = "" }; }
        static CaptureCoordinator Coordinator(List<bool> visibility)
        {
            var c = new CaptureCoordinator(visibility.Add, delegate { return true; }, delegate { });
            c.SelectRequest = delegate { return Task.FromResult(Request()); };
            c.Capture = delegate { return Task.FromResult(Result()); };
            c.CreatePreview = r => new CapturePreviewWindow(r, delegate { return true; }, delegate { return true; });
            c.ShowWindow = delegate { };
            c.ShowError = delegate(string error) { TestRunner.Check(false, "unexpected capture error: " + error); };
            return c;
        }
        static void Lifecycle()
        {
            var visibility = new List<bool>();
            var c = Coordinator(visibility);
            var selection = new TaskCompletionSource<CaptureRequest>();
            int selections = 0, runs = 0;
            CaptureResult result = Result();
            CapturePreviewWindow preview = null;
            c.SelectRequest = delegate { selections++; return selection.Task; };
            c.Capture = delegate { runs++; return Task.FromResult(result); };
            c.ShowWindow = w => preview = (CapturePreviewWindow)w;
            c.Open(); c.Open();
            TestRunner.Check(c.Busy && selections == 1, "repeated Open while selecting starts one workflow");
            TestRunner.Check(visibility.SequenceEqual(new[] { true }), "selection suppresses Capsule once");
            selection.SetResult(Request()); Pump();
            TestRunner.Check(runs == 1 && c.Busy && preview != null, "one capture owns its preview while Busy");
            TestRunner.Check(visibility.SequenceEqual(new[] { true, false }), "Capsule is restored before preview");
            TestRunner.Check(result.Image.Pixels != null, "preview borrows coordinator image");
            c.Open();
            TestRunner.Check(selections == 1 && runs == 1, "repeated Open while preview is open preserves its one image");
            if (preview != null) preview.Close(); Pump();
            TestRunner.Check(!c.Busy && result.Image.Pixels == null, "preview close releases original image and Busy");
            c.Dispose();
        }
        static void Disposal()
        {
            var visibility = new List<bool>();
            var c = Coordinator(visibility);
            var pending = new TaskCompletionSource<CaptureRequest>();
            bool cancelled = false;
            c.SelectRequest = delegate(CancellationToken token) { token.Register(delegate { cancelled = true; pending.TrySetCanceled(); }); return pending.Task; };
            c.Open(); c.Dispose(); Pump(); c.Open();
            TestRunner.Check(cancelled && !c.Busy, "dispose cancels owned selection and prevents another Open");
            TestRunner.Check(visibility.SequenceEqual(new[] { true, false }), "dispose restores suppressed Capsule exactly once");
            visibility.Clear(); c = Coordinator(visibility);
            var running = new TaskCompletionSource<CaptureResult>();
            c.Capture = delegate(CaptureRequest request, CancellationToken token) { token.Register(delegate { running.TrySetCanceled(); }); return running.Task; };
            c.Open(); TestRunner.Check(c.Busy, "capture is Busy until completion"); c.Dispose(); Pump();
            TestRunner.Check(running.Task.IsCanceled && !c.Busy, "dispose cancels owned running capture");
            TestRunner.Check(visibility.SequenceEqual(new[] { true, false }), "capture disposal restores visibility");
            var failure = Coordinator(new List<bool>());
            CaptureResult abandoned = Result();
            failure.Capture = delegate { return Task.FromResult(abandoned); };
            failure.CreatePreview = delegate { throw new InvalidOperationException("injected"); };
            string error = null; failure.ShowError = text => error = text;
            failure.Open(); Pump();
            TestRunner.Check(abandoned.Image.Pixels == null && !failure.Busy && error != null, "preview creation failure releases its original image");
            failure.Dispose();
        }
        static void RetakeAndLateResult()
        {
            var visibility = new List<bool>();
            var c = Coordinator(visibility);
            var results = new List<CaptureResult>();
            var previews = new List<CapturePreviewWindow>();
            c.Capture = delegate { var result = Result(); results.Add(result); return Task.FromResult(result); };
            c.ShowWindow = window => previews.Add((CapturePreviewWindow)window);
            c.Open();
            if (previews.Count > 0) previews[0].RetakeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Wait(() => previews.Count == 2);
            TestRunner.Check(previews.Count == 2 && results[0].Image.Pixels == null && results[1].Image.Pixels != null, "Retake releases prior original before owning a fresh capture");
            TestRunner.Check(visibility.SequenceEqual(new[] { true, false, true, false }), "Retake has its own hide and restore pair");
            c.Dispose(); Pump();
            TestRunner.Check(results.Count == 2 && results[1].Image.Pixels == null && !c.Busy, "dispose during preview releases its retained original");
            visibility.Clear(); c = Coordinator(visibility);
            var late = new TaskCompletionSource<CaptureResult>();
            int shown = 0;
            c.Capture = delegate { return late.Task; };
            c.ShowWindow = delegate { shown++; };
            c.Open(); c.Dispose();
            CaptureResult abandoned = Result(); late.SetResult(abandoned); Pump();
            TestRunner.Check(shown == 0 && abandoned.Image.Pixels == null && !c.Busy, "late capture result after disposal is discarded without a preview");
            TestRunner.Check(visibility.SequenceEqual(new[] { true, false }), "late result cannot restore visibility twice");
        }
        static void Shortcuts()
        {
            var attempted = new List<uint>();
            int releases = 0;
            var c = new CaptureCoordinator(delegate { }, delegate(uint mods, uint key) { attempted.Add(key); return key != 0x4E && key != 0x58; }, delegate { releases++; });
            TestRunner.Check(c.RegisterShortcut("Ctrl+Alt+S"), "capture registers its configured shortcut");
            TestRunner.Check(!c.RegisterShortcut("Ctrl+Alt+N"), "capture refuses occupied Ideas shortcut");
            TestRunner.Check(attempted.SequenceEqual(new uint[] { 0x53, 0x4E, 0x53 }), "conflict restores the prior capture shortcut");
            TestRunner.Check(!c.RegisterShortcut("Ctrl+Alt+X"), "external conflict is refused");
            TestRunner.Check(c.RegisterShortcut("Ctrl+Shift+S"), "shortcut registration recovers after conflict");
            int count = attempted.Count;
            TestRunner.Check(!c.RegisterShortcut("Shift+K") && attempted.Count == count, "invalid shortcut does not release existing registration");
            c.Dispose(); c.Dispose();
            TestRunner.Check(releases == 1 && !c.RegisterShortcut("Ctrl+Alt+S"), "dispose releases owned shortcut once");
        }
        static void SelectionCancellation()
        {
            var monitor = new MonitorInfo { Bounds = new RECT { Left = -400, Top = 0, Right = 400, Bottom = 600 }, Scale = 1.25 };
            var unrelated = new Window();
            bool unrelatedClosed = false; unrelated.Closed += delegate { unrelatedClosed = true; };
            using (var token = new CancellationTokenSource())
            {
                var selection = new CaptureSelection(CaptureMode.Screenshot, new[] { monitor }, token.Token);
                token.Cancel(); Pump();
                TestRunner.Check(selection.Completion.IsCanceled && selection.OverlaysClosed, "owned selection token closes its overlays and cancels completion");
                TestRunner.Check(!unrelatedClosed, "selection cancellation leaves unrelated windows open");
            }
            using (var token = new CancellationTokenSource())
            {
                var selection = new CaptureSelection(CaptureMode.Screenshot, new[] { monitor }, token.Token);
                selection.Complete(new RECT { Right = 2, Bottom = 2 });
                token.Cancel(); Pump();
                TestRunner.Check(selection.Completion.IsCanceled, "cancellation during composition wait cannot return a screenshot request");
            }
            using (var token = new CancellationTokenSource())
            {
                var selection = new CaptureSelection(CaptureMode.Screenshot, new[] { monitor }, token.Token);
                Task.Run(() => token.Cancel()).Wait();
                Wait(() => selection.Completion.IsCompleted);
                TestRunner.Check(selection.Completion.IsCanceled && selection.OverlaysClosed && !unrelatedClosed, "worker cancellation marshals cleanup of owned overlays only");
            }
            var complete = new CaptureSelection(CaptureMode.Screenshot, new[] { monitor });
            complete.Complete(new RECT { Right = 2, Bottom = 2 });
            Wait(() => complete.Completion.IsCompleted);
            TestRunner.Check(complete.Completion.Status == TaskStatus.RanToCompletion, "closing owned selection overlays does not cancel a chosen area");
            unrelated.Close();
        }
        static void ReadHooks()
        {
            var desktop = new CaptureDesktop();
            var order = new List<string>();
            var token = new CancellationTokenSource();
            desktop.BeforeRead = delegate { order.Add("hide"); order.Add("compose"); token.Token.ThrowIfCancellationRequested(); order.Add("guard"); };
            desktop.AfterRead = delegate { order.Add("restore"); };
            using (var frame = desktop.ReadFrame(Request().Area, delegate { order.Add("read"); return Result().Image; })) { }
            TestRunner.Check(order.SequenceEqual(new[] { "hide", "compose", "guard", "read", "restore" }), "native adapter brackets its one screen reader after composition and guard");
            order.Clear();
            desktop.BeforeRead = delegate { order.Add("hide"); token.Cancel(); token.Token.ThrowIfCancellationRequested(); };
            bool cancelled = false;
            try { desktop.ReadFrame(Request().Area, delegate { order.Add("read"); return Result().Image; }); }
            catch (OperationCanceledException) { cancelled = true; }
            TestRunner.Check(cancelled && order.SequenceEqual(new[] { "hide", "restore" }), "cancellation after hiding prevents native read and restores through finally");
            order.Clear(); desktop.BeforeRead = delegate { order.Add("hide"); };
            try { desktop.ReadFrame(Request().Area, delegate { throw new InvalidOperationException(); }); } catch (InvalidOperationException) { }
            TestRunner.Check(order.SequenceEqual(new[] { "hide", "restore" }), "failed native read restores through finally");
            token.Dispose();
        }
        static void ProgressExclusion()
        {
            MethodInfo configure = typeof(CaptureCoordinator).GetMethod("ConfigureReads", BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo wait = typeof(CaptureCoordinator).GetField("WaitForComposition", BindingFlags.Instance | BindingFlags.NonPublic);
            TestRunner.Check(configure != null && wait != null, "coordinator exposes its real per-frame composition boundary for inert checks");
            if (configure == null || wait == null) return;
            var c = Coordinator(new List<bool>());
            var desktop = new CaptureDesktop();
            var display = new CaptureProgressWindow();
            var monitor = new MonitorInfo { Bounds = new RECT { Right = 100, Bottom = 100 }, Work = new RECT { Right = 100, Bottom = 100 }, Scale = 1 };
            display.Place(monitor.Bounds, monitor);
            TestRunner.Check(display.OverlapsSelection, "full-monitor area requires temporary progress hiding");
            var order = new List<string>();
            c.ShowWindow = delegate { order.Add("restore"); };
            using (var token = new CancellationTokenSource())
            {
                wait.SetValue(c, new Action(delegate { TestRunner.Check(!display.IsVisible, "progress is hidden during composition wait"); order.Add("compose"); }));
                Func<bool> valid = delegate { order.Add("guard"); return true; };
                configure.Invoke(c, new object[] { desktop, display, token.Token, valid });
                using (var result = desktop.ReadFrame(monitor.Bounds, delegate { order.Add("read"); return Result().Image; })) { }
                TestRunner.Check(order.SequenceEqual(new[] { "compose", "guard", "read", "restore" }), "real coordinator hook waits, guards, reads, then restores progress");
                order.Clear();
                wait.SetValue(c, new Action(delegate { order.Add("compose"); token.Cancel(); }));
                bool cancelled = false;
                try { desktop.ReadFrame(monitor.Bounds, delegate { order.Add("read"); return Result().Image; }); }
                catch (OperationCanceledException) { cancelled = true; }
                TestRunner.Check(cancelled && order.SequenceEqual(new[] { "compose" }), "cancellation during composition prevents read and progress resurrection");
            }
            order.Clear();
            using (var token = new CancellationTokenSource())
            {
                bool valid = true;
                wait.SetValue(c, new Action(delegate { order.Add("compose"); valid = false; }));
                configure.Invoke(c, new object[] { desktop, display, token.Token, new Func<bool>(() => valid) });
                bool cancelled = false;
                try { desktop.ReadFrame(monitor.Bounds, delegate { order.Add("read"); return Result().Image; }); }
                catch (OperationCanceledException) { cancelled = true; }
                TestRunner.Check(cancelled && order.SequenceEqual(new[] { "compose" }), "target invalidation during composition prevents native read");
                wait.SetValue(c, new Action(delegate { }));
                configure.Invoke(c, new object[] { desktop, display, token.Token, new Func<bool>(() => true) });
                order.Clear();
                using (var result = desktop.ReadFrame(monitor.Bounds, delegate { display.Close(); return Result().Image; })) { }
                TestRunner.Check(order.Count == 0, "finally cannot show a closed progress window");
            }
            var outside = new CaptureProgressWindow();
            outside.Place(new RECT { Right = 40, Bottom = 30 }, new MonitorInfo { Bounds = new RECT { Right = 1000, Bottom = 1000 }, Work = new RECT { Right = 1000, Bottom = 1000 }, Scale = 1 });
            order.Clear();
            wait.SetValue(c, new Action(delegate { order.Add("compose"); }));
            configure.Invoke(c, new object[] { desktop, outside, CancellationToken.None, new Func<bool>(() => true) });
            using (var result = desktop.ReadFrame(Request().Area, delegate { order.Add("read"); return Result().Image; })) { }
            TestRunner.Check(!outside.OverlapsSelection && order.SequenceEqual(new[] { "read" }), "outside progress needs no composition wait or repeated show");
            outside.Close();
            c.Dispose();
        }
        static void Set(Controller controller, string name, object value)
        { typeof(Controller).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(controller, value); }
        static void ControllerAndCopy()
        {
            MethodInfo change = typeof(Controller).GetMethod("CaptureVisibilityChanged", BindingFlags.Instance | BindingFlags.NonPublic);
            MethodInfo read = typeof(Controller).GetMethod("ReadClipboard", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(Func<long, ClipItem>) }, null);
            PropertyInfo visible = typeof(Controller).GetProperty("CapsuleVisible", BindingFlags.Instance | BindingFlags.NonPublic);
            TestRunner.Check(change != null && read != null && visible != null, "Controller provides capture visibility and watcher boundary");
            if (change == null || read == null || visible == null) return;
            var controller = (Controller)FormatterServices.GetUninitializedObject(typeof(Controller));
            var config = new Config { ShowNotch = false, ClipboardHistory = false };
            var history = new ClipboardHistory();
            var first = new PromptBroker(() => (bool)typeof(Controller).GetField("captureHidden", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(controller));
            var second = new PromptBroker(() => (bool)typeof(Controller).GetField("captureHidden", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(controller));
            var replies = new List<PromptReply>();
            first.Arrive(new PromptRequest { ToolName = "Bash", At = Clock.NowMs() }, replies.Add, Clock.NowMs());
            second.Arrive(new PromptRequest { ToolName = "Bash", At = Clock.NowMs() }, replies.Add, Clock.NowMs());
            Set(controller, "config", config); Set(controller, "clips", history);
            Set(controller, "prompts", first); Set(controller, "codexPrompts", second);
            var panel = new PanelWindow(); var notch = new NotchWindow(); var card = new HoverCard();
            Set(controller, "panel", panel); Set(controller, "notch", notch); Set(controller, "card", card);
            Set(controller, "openCard", new DispatcherTimer());
            change.Invoke(controller, new object[] { true });
            TestRunner.Check(first.Count == 0 && second.Count == 0 && replies.Count == 2 && replies.All(r => r.Kind == "pass"), "capture releases both providers to host prompts without approval");
            first.Arrive(new PromptRequest { ToolName = "Bash", At = Clock.NowMs() }, replies.Add, Clock.NowMs());
            TestRunner.Check(first.Count == 0 && replies.Count == 3 && replies[2].Kind == "pass", "new approval during capture immediately falls back to its host");
            TestRunner.Check(!(bool)visible.GetValue(controller, null), "capture suppresses Capsule visibility");
            config.ShowNotch = true; Set(controller, "fullscreen", true);
            change.Invoke(controller, new object[] { false });
            TestRunner.Check(!(bool)visible.GetValue(controller, null), "restore respects current fullscreen state");
            config.ShowNotch = false; Set(controller, "fullscreen", false);
            TestRunner.Check(!(bool)visible.GetValue(controller, null), "restore respects current disabled config");
            config.ShowNotch = true;
            TestRunner.Check((bool)visible.GetValue(controller, null), "visibility uses current config rather than a saved visibility snapshot");
            config.ShowNotch = false;
            using (CaptureFrame image = Result().Image)
            {
                var result = new CaptureResult { Image = image, Notice = "" };
                BitmapSource clipboard = null;
                int copies = 0, reads = 0;
                var preview = new CapturePreviewWindow(result, delegate(CaptureFrame f) { copies++; clipboard = CapturePreviewWindow.Bitmap(f); return true; }, delegate { return false; });
                preview.CopyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Func<long, ClipItem> watcher = delegate(long now) { reads++; return ClipboardHistory.FromImage(clipboard, now); };
                read.Invoke(controller, new object[] { watcher });
                TestRunner.Check(copies == 1 && reads == 0 && history.Items.Count == 0 && !config.ClipboardHistory, "explicit Copy with history off writes only clipboard and keeps history off");
                config.ClipboardHistory = true;
                read.Invoke(controller, new object[] { watcher }); read.Invoke(controller, new object[] { watcher });
                TestRunner.Check(history.Items.Count == 1 && reads == 2 && copies == 1, "watcher alone admits at most one item for one explicit copy");
                preview.Close();
            }
            using (var large = new CaptureFrame(4001, 4000, new byte[4001 * 4000 * 4]))
            {
                int copies = 0;
                var preview = new CapturePreviewWindow(new CaptureResult { Image = large, Notice = "" }, delegate { copies++; return true; }, delegate { return false; });
                preview.CopyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                read.Invoke(controller, new object[] { new Func<long, ClipItem>(now => ClipboardHistory.FromImage(CapturePreviewWindow.Bitmap(large), now)) });
                TestRunner.Check(copies == 1 && history.Items.Count == 1, "oversized preview remains copyable without history retention");
                preview.Close();
            }
        }
        static void PanelEntry()
        {
            var view = new PanelView();
            view.Measure(new Size(1200, 2000)); view.Arrange(new Rect(view.DesiredSize)); view.UpdateLayout();
            EventInfo entry = typeof(PanelView).GetEvent("CaptureClicked");
            EventInfo shortcut = typeof(PanelView).GetEvent("CaptureShortcutChosen");
            MethodInfo status = typeof(PanelView).GetMethod("ShowCaptureShortcutResult");
            TestRunner.Check(entry != null && shortcut != null && status != null, "panel exposes capture entry and shortcut result");
            if (entry == null || shortcut == null || status == null) return;
            bool clicked = false; entry.AddEventHandler(view, new Action(delegate { clicked = true; }));
            Border button = All(view).OfType<Border>().FirstOrDefault(x => System.Windows.Automation.AutomationProperties.GetName(x) == "Capture");
            TestRunner.Check(button != null, "panel header has an accessible Capture button");
            if (button != null) button.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            TestRunner.Check(clicked, "panel Capture click reaches coordinator entry event");
            view.ShowSettings(false, "", HotkeyText.Default);
            status.Invoke(view, new object[] { false, "Ctrl+Alt+S", "Shortcut unavailable" });
            view.Measure(new Size(1200, 2000)); view.Arrange(new Rect(view.DesiredSize)); view.UpdateLayout();
            TestRunner.Check(All(view).OfType<TextBlock>().Any(t => t.Text == "Ctrl+Alt+S") && All(view).OfType<TextBlock>().Any(t => t.Text == "Shortcut unavailable"), "capture settings show configured shortcut and registration result");
        }
        static IEnumerable<DependencyObject> All(DependencyObject root)
        {
            yield return root;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                foreach (DependencyObject child in All(VisualTreeHelper.GetChild(root, i))) yield return child;
        }
        static void Wait(Func<bool> done)
        {
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (!done() && DateTime.UtcNow < deadline) { Pump(); Thread.Sleep(5); }
        }
        static void Pump()
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(delegate { frame.Continue = false; }));
            Dispatcher.PushFrame(frame);
        }
    }
}
