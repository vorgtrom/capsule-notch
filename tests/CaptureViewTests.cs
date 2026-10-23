using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Capsule
{
    public static class CaptureViewTests
    {
        public static void Run()
        {
            Chooser();
            Selection();
            Crop();
            PreviewActions();
            InitialFit();
            DpiTransition();
            Progress();
            ThemeRendering();
        }

        static CaptureFrame Image()
        {
            byte[] pixels = new byte[80 * 100 * 4];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = (byte)(i * 17 + i / 320);
            return new CaptureFrame(80, 100, pixels);
        }

        static void Chooser()
        {
            foreach (Key key in new[] { Key.S, Key.W, Key.Escape })
            {
                var chooser = new CaptureChooser();
                int chosen = 0, cancelled = 0;
                CaptureMode mode = CaptureMode.Screenshot;
                chooser.Chosen += delegate(CaptureMode value) { chosen++; mode = value; };
                chooser.Cancelled += delegate { cancelled++; };
                TestRunner.Check(chooser.HandleKey(key), "chooser handles mode/cancel key");
                TestRunner.Eq(key == Key.Escape ? 0 : 1, chosen, "chooser chooses once");
                TestRunner.Eq(key == Key.Escape ? 1 : 0, cancelled, "chooser cancellation");
                if (key == Key.W) TestRunner.Eq(CaptureMode.Scrolling, mode, "W chooses scrolling");
                chooser.Close();
            }
        }

        static void Selection()
        {
            foreach (double scale in new[] { 1.0, 1.25, 1.5, 2.0 })
            {
                var monitor = new MonitorInfo { Scale = scale, Bounds = new RECT { Left = -1000, Top = -200, Right = 0, Bottom = 600 } };
                RECT area;
                TestRunner.Check(CaptureSelection.TryArea(monitor, new Point(29 / scale, 49 / scale), new Point(10 / scale, 20 / scale), out area), "reverse drag accepted");
                TestRunner.Eq(-990, area.Left, "negative physical origin preserved");
                TestRunner.Eq(19, area.Width, "reverse drag width");
                TestRunner.Check(!CaptureSelection.TryArea(monitor, new Point(5, 5), new Point(5, 5), out area), "zero selection refused");
                TestRunner.Check(!CaptureSelection.TryArea(monitor, new Point(5, 5), new Point(1001 / scale, 5), out area), "cross monitor selection refused");
                var selection = new CaptureSelection(CaptureMode.Screenshot, new[] { monitor, monitor });
                TestRunner.Check(selection.HandleKey(Key.Escape), "Escape cancels selection");
                TestRunner.Check(selection.Completion.IsCanceled && selection.OverlaysClosed, "Escape closes all overlays before cancellation");
                selection = new CaptureSelection(CaptureMode.Screenshot, new[] { monitor });
                selection.Complete(new RECT { Left = -990, Top = -180, Right = -970, Bottom = -150 });
                Pump(selection.Completion);
                TestRunner.Check(selection.OverlaysClosed, "selection closes all overlays before completion");
                TestRunner.Eq(20, selection.Completion.Result.Area.Width, "selection returns exact area");
            }
        }

        static void Pump(Task task)
        {
            var frame = new DispatcherFrame();
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            task.ContinueWith(delegate { dispatcher.BeginInvoke(new Action(delegate { frame.Continue = false; })); });
            if (!task.IsCompleted) Dispatcher.PushFrame(frame);
            task.GetAwaiter().GetResult();
        }

        static void Crop()
        {
            using (CaptureFrame source = Image())
            {
                foreach (double scale in new[] { 1.0, 1.25, 1.5, 2.0 })
                {
                    RECT pixels = CapturePreviewWindow.PixelArea(new Point(10 * scale, 20 * scale), new Point(30 * scale, 50 * scale), scale, source.Width, source.Height);
                    using (CaptureFrame crop = CapturePreviewWindow.CropPixels(source, pixels))
                    {
                        TestRunner.Eq(20, crop.Width, "crop width");
                        TestRunner.Eq(30, crop.Height, "crop height");
                        bool equal = true;
                        for (int y = 0; y < 30; y++) for (int x = 0; x < 80; x++)
                            if (crop.Pixels[y * 80 + x] != source.Pixels[((y + 20) * 80 + 10) * 4 + x]) equal = false;
                        TestRunner.Check(equal, "crop exact BGRA equality at scale " + scale);
                    }
                }
                using (CaptureFrame clipped = CapturePreviewWindow.CropPixels(source, new RECT { Left = -10, Top = -5, Right = 10, Bottom = 15 }))
                    TestRunner.Eq(10 * 15 * 4, clipped.Pixels.Length, "crop clips to bounds");
                bool refused = false;
                try { CapturePreviewWindow.CropPixels(source, new RECT()); } catch (ArgumentException) { refused = true; }
                TestRunner.Check(refused, "empty crop refused");
            }
        }

        static void PreviewActions()
        {
            using (CaptureFrame source = Image())
            {
                int copied = 0, saved = 0, retaken = 0;
                CaptureFrame written = null;
                var preview = new CapturePreviewWindow(new CaptureResult { Image = source, Partial = true, Notice = "Stopped at a repeated section." },
                    delegate(CaptureFrame frame) { copied++; written = frame; return true; },
                    delegate(CaptureFrame frame) { saved++; written = frame; return false; });
                TestRunner.Eq(0, copied + saved, "preview never writes on open");
                TestRunner.Check(preview.NoticeText.Contains("Partial") && preview.NoticeText.Contains("repeated"), "partial notice visible");
                preview.Fit(400, 300);
                TestRunner.Near(3, preview.Zoom, "Fit uses viewport dimensions");
                preview.SetZoom(1);
                TestRunner.Near(1, preview.Zoom, "100 percent uses one DIP per source pixel");
                preview.ApplyCrop(new RECT { Left = 10, Top = 20, Right = 30, Bottom = 50 });
                CaptureFrame crop = preview.CurrentImage;
                preview.CopyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                TestRunner.Check(copied == 1 && Object.ReferenceEquals(crop, written), "explicit copy receives crop");
                preview.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                TestRunner.Check(saved == 1 && preview.StatusText.Contains("not saved"), "canceled save keeps preview");
                preview.ResetCrop();
                TestRunner.Check(crop.Pixels == null && Object.ReferenceEquals(source, preview.CurrentImage), "reset releases crop and restores source");
                foreach (Button button in preview.ActionButtons)
                    TestRunner.Check(button.Focusable && button.IsTabStop && AutomationProperties.GetName(button).Length > 0, "preview action is keyboard and screen-reader accessible");
                preview.RetakeRequested += delegate { retaken++; TestRunner.Check(preview.IsReleased, "retake closes preview before event"); };
                preview.RetakeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                TestRunner.Check(retaken == 1 && source.Pixels != null && preview.IsReleased, "retake preserves caller image ownership");
                preview.Close();
                TestRunner.Eq(1, retaken, "close cannot repeat retake");
                foreach (bool clipboard in new[] { true, false })
                {
                    var failed = new CapturePreviewWindow(new CaptureResult { Image = source },
                        delegate { throw new ExternalException("locked"); }, delegate { throw new IOException("unwritable"); });
                    (clipboard ? failed.CopyButton : failed.SaveButton).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    TestRunner.Check(failed.StatusText.Contains("could not"), "write failure remains in preview");
                    failed.HandleKey(Key.Escape);
                    TestRunner.Check(failed.IsReleased && source.Pixels != null, "Escape safely releases view only");
                }
                var closed = new CapturePreviewWindow(new CaptureResult { Image = source }, delegate { copied++; return true; }, delegate { saved++; return true; });
                closed.ApplyCrop(new RECT { Left = 10, Top = 20, Right = 30, Bottom = 50 });
                CaptureFrame releasedCrop = closed.CurrentImage;
                var scroll = (ScrollViewer)((DockPanel)closed.Content).Children[1];
                var display = (System.Windows.Controls.Image)((Canvas)scroll.Content).Children[0];
                int writes = copied + saved;
                closed.Close();
                closed.CopyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                closed.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                TestRunner.Check(closed.IsReleased && closed.CurrentImage == null && display.Source == null, "close releases image and display references");
                TestRunner.Check(releasedCrop.Pixels == null && source.Pixels != null, "close disposes crop and preserves caller source");
                TestRunner.Eq(writes, copied + saved, "closed preview cannot export");
                closed.Close();
            }
        }

        static void ThemeRendering()
        {
            foreach (bool light in new[] { false, true })
            {
                Theme.Use(Theme.For(light, false));
                var button = CaptureControls.Button("Copy", delegate { });
                var host = new Grid { Width = 120, Height = 48, Background = Theme.Current.Brush(Theme.Current.Surface) };
                host.Children.Add(button);
                host.Measure(new Size(120, 48));
                host.Arrange(new Rect(0, 0, 120, 48));
                host.UpdateLayout();
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(120, 48, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(host);
                byte[] sample = new byte[4];
                bitmap.CopyPixels(new Int32Rect(14, 24, 1, 1), sample, 4, 0);
                TestRunner.Check(light ? sample[0] > 150 : sample[0] < 100, "actual button surface follows " + (light ? "light" : "dark") + " theme");
                TestRunner.Check(button.Template.FindName("CaptureButtonSurface", button) != null, "button uses its themed surface rather than system chrome");
                bool focusCue = false;
                foreach (Trigger trigger in button.Template.Triggers)
                    if (trigger.Property == UIElement.IsKeyboardFocusedProperty && Object.Equals(trigger.Value, true))
                        foreach (Setter setter in trigger.Setters)
                            if (setter.TargetName == "CaptureButtonSurface" && setter.Property == Border.BorderBrushProperty) focusCue = true;
                TestRunner.Check(focusCue, "button template has a visible keyboard focus border");
            }
            Theme.Use(Theme.For(false, false));
        }
        static void InitialFit()
        {
            using (CaptureFrame source = Image())
            {
                var preview = new CapturePreviewWindow(new CaptureResult { Image = source }, delegate { return false; }, delegate { return false; });
                var content = (DockPanel)preview.Content;
                var scroll = (ScrollViewer)content.Children[1];
                preview.Content = null;
                var host = new Grid { Width = 920, Height = 650, Background = preview.Background };
                host.Children.Add(content);
                host.Measure(new Size(920, 650));
                host.Arrange(new Rect(0, 0, 920, 650));
                host.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(new Action(delegate { }), DispatcherPriority.ContextIdle);
                host.UpdateLayout();
                TestRunner.Near(Math.Min(scroll.ViewportWidth / source.Width, scroll.ViewportHeight / source.Height), preview.Zoom, "initial preview fits the laid-out viewport");
                preview.SetZoom(1);
                host.Height = 600;
                host.Measure(new Size(920, 600));
                host.Arrange(new Rect(0, 0, 920, 600));
                host.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(new Action(delegate { }), DispatcherPriority.ContextIdle);
                TestRunner.Near(1, preview.Zoom, "resizing preserves explicit 100 percent zoom");
                preview.Close();
            }
        }
        static void DpiTransition()
        {
            using (CaptureFrame source = Image())
            {
                var preview = new CapturePreviewWindow(new CaptureResult { Image = source }, delegate { return false; }, delegate { return false; });
                var content = (DockPanel)preview.Content;
                var scroll = (ScrollViewer)content.Children[1];
                var canvas = (Canvas)scroll.Content;
                var image = (System.Windows.Controls.Image)canvas.Children[0];
                var selection = (System.Windows.Shapes.Rectangle)canvas.Children[1];
                preview.Content = null;
                var host = new Grid { Width = 920, Height = 650 };
                host.Children.Add(content);
                host.Measure(new Size(920, 650));
                host.Arrange(new Rect(0, 0, 920, 650));
                host.UpdateLayout();
                VisualTreeHelper.SetRootDpi(preview, new DpiScale(1, 1));
                Dispatcher.CurrentDispatcher.Invoke(new Action(delegate { }), DispatcherPriority.ContextIdle);
                int transitions = 0;
                preview.DpiChanged += delegate { transitions++; };
                preview.SetZoom(1);
                foreach (double scale in new[] { 2.0, 1.25, 1.5, 1.0 })
                {
                    double previousWidth = image.Width;
                    VisualTreeHelper.SetRootDpi(preview, new DpiScale(scale, scale));
                    TestRunner.Near(previousWidth, image.Width, "DPI refresh waits for deferred window layout");
                    Dispatcher.CurrentDispatcher.Invoke(new Action(delegate { }), DispatcherPriority.ContextIdle);
                    TestRunner.Near(scale, VisualTreeHelper.GetDpi(preview).DpiScaleX, "effective preview DPI changes");
                    TestRunner.Near(1, preview.Zoom, "DPI transition retains physical 100 percent zoom");
                    TestRunner.Near(source.Width / scale, image.Width, "image width follows effective DPI at fixed zoom");
                    TestRunner.Near(source.Height / scale, image.Height, "image height follows effective DPI at fixed zoom");
                    TestRunner.Near(source.Width / scale, canvas.Width, "canvas width follows effective DPI at fixed zoom");
                    TestRunner.Near(source.Height / scale, canvas.Height, "canvas height follows effective DPI at fixed zoom");
                    RECT pixels = CapturePreviewWindow.PixelArea(new Point(10 / scale, 20 / scale), new Point(30 / scale, 50 / scale), preview.ViewScale, source.Width, source.Height);
                    TestRunner.Check(pixels.Left == 10 && pixels.Top == 20 && pixels.Right == 30 && pixels.Bottom == 50, "displayed crop maps to exact pixels after DPI transition");
                }
                TestRunner.Eq(4, transitions, "effective DPI changes raise production window events");
                preview.Fit(scroll.ViewportWidth, scroll.ViewportHeight);
                double fitZoom = preview.Zoom;
                VisualTreeHelper.SetRootDpi(preview, new DpiScale(2, 2));
                Dispatcher.CurrentDispatcher.Invoke(new Action(delegate { }), DispatcherPriority.ContextIdle);
                TestRunner.Near(fitZoom * 2, preview.Zoom, "DPI transition refits in Fit mode");
                TestRunner.Near(Math.Min(scroll.ViewportWidth / source.Width, scroll.ViewportHeight / source.Height), image.Width / source.Width, "Fit display fills viewport after DPI transition");
                preview.SetZoom(1);
                foreach (Button button in preview.ActionButtons)
                    if (AutomationProperties.GetName(button) == "Crop") button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                typeof(CapturePreviewWindow).GetField("cropArea", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    .SetValue(preview, new RECT { Left = 10, Top = 20, Right = 30, Bottom = 50 });
                selection.Visibility = Visibility.Visible;
                VisualTreeHelper.SetRootDpi(preview, new DpiScale(1, 1));
                Dispatcher.CurrentDispatcher.Invoke(new Action(delegate { }), DispatcherPriority.ContextIdle);
                TestRunner.Eq(Visibility.Collapsed, selection.Visibility, "DPI transition clears displayed pending crop");
                preview.HandleKey(Key.Enter);
                TestRunner.Check(Object.ReferenceEquals(source, preview.CurrentImage), "DPI transition clears pending pixel crop before Enter");
                VisualTreeHelper.SetRootDpi(preview, new DpiScale(2, 2));
                preview.Close();
                Dispatcher.CurrentDispatcher.Invoke(new Action(delegate { }), DispatcherPriority.ContextIdle);
                TestRunner.Check(preview.IsReleased && source.Pixels != null, "queued DPI refresh after close preserves caller ownership");
            }
        }
        static void Progress()
        {
            var progress = new CaptureProgressWindow();
            int stops = 0, cancels = 0;
            progress.StopRequested += delegate { stops++; };
            progress.CancelRequested += delegate { cancels++; };
            TestRunner.Check(!progress.ShowActivated, "progress does not activate");
            bool handled = false;
            TestRunner.Eq(new IntPtr(3), CaptureProgressWindow.NoActivate(IntPtr.Zero, 0x0021, IntPtr.Zero, IntPtr.Zero, ref handled), "mouse activation hook refuses activation");
            TestRunner.Check(handled, "mouse activation message handled");
            progress.StopButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            progress.CancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            TestRunner.Check(stops == 1 && cancels == 1, "progress controls raise distinct actions");
            var monitor = new MonitorInfo { Bounds = new RECT { Right = 1000, Bottom = 800 }, Work = new RECT { Right = 1000, Bottom = 800 }, Scale = 1.25 };
            progress.Place(new RECT { Left = 100, Top = 100, Right = 700, Bottom = 500 }, monitor);
            TestRunner.Check(!progress.OverlapsSelection, "progress placed outside capture");
            progress.Place(monitor.Bounds, monitor);
            TestRunner.Check(progress.OverlapsSelection, "full screen progress overlap exposed");
            progress.Update(new CaptureProgress { FrameCount = 4, Rows = 500, Paused = true, Status = "Paused." });
            TestRunner.Check(progress.StatusText.Contains("Paused") && progress.StatusText.Contains("500"), "progress displays pause and row count");
            progress.Close();
        }
    }
}
