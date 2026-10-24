using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;

namespace Capsule
{
    public sealed class CapturePreviewWindow : Window
    {
        CaptureFrame original, cropped;
        Func<CaptureFrame, bool> copy, save;
        readonly Image image = new Image { Stretch = Stretch.Fill };
        readonly Canvas canvas = new Canvas { Background = Brushes.Transparent };
        readonly ScrollViewer scroll = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        readonly Rectangle selection = new Rectangle { Stroke = Brushes.DodgerBlue, StrokeThickness = 1, Fill = new SolidColorBrush(Color.FromArgb(30, 40, 120, 255)), IsHitTestVisible = false };
        readonly TextBlock status = CaptureControls.Text("");
        readonly TextBlock notice;
        readonly List<Button> actions = new List<Button>();
        bool fit = true, cropMode, dragging;
        Point start, pan;
        double horizontal, vertical;
        RECT cropArea;
        public event Action RetakeRequested;
        public Button CopyButton { get; private set; }
        public Button SaveButton { get; private set; }
        public Button RetakeButton { get; private set; }
        public IEnumerable<Button> ActionButtons { get { return actions; } }
        public CaptureFrame CurrentImage { get { return cropped ?? original; } }
        public double Zoom { get; private set; }
        public string NoticeText { get { return notice.Text; } }
        public string StatusText { get { return status.Text; } }
        public bool IsReleased { get; private set; }

        // Borrows result.Image. The caller releases it after Closed. Only the crop belongs to this window.
        public CapturePreviewWindow(CaptureResult result, Func<CaptureFrame, bool> copy = null, Func<CaptureFrame, bool> save = null)
        {
            if (result == null || result.Image == null || result.Image.Pixels == null) throw new ArgumentException("A capture image is required.", "result");
            original = result.Image;
            this.copy = copy ?? CopyDefault;
            this.save = save ?? SaveDefault;
            CaptureControls.Window(this, "Capture preview", 960, 720);
            MinWidth = 640;
            MinHeight = 400;
            var root = new DockPanel { Margin = new Thickness(12), LastChildFill = true };
            var top = new StackPanel();
            var buttons = new WrapPanel();
            AddButton(buttons, "_Fit", delegate { fit = true; Fit(scroll.ViewportWidth, scroll.ViewportHeight); });
            AddButton(buttons, "_100%", delegate { SetZoom(1); });
            AddButton(buttons, "Zoom _in", delegate { SetZoom(Zoom * 1.25); });
            AddButton(buttons, "Zoom _out", delegate { SetZoom(Zoom / 1.25); });
            AddButton(buttons, "_Crop", ToggleCrop);
            AddButton(buttons, "_Reset crop", ResetCrop);
            CopyButton = AddButton(buttons, "Cop_y", delegate { Write(true); });
            SaveButton = AddButton(buttons, "_Save PNG", delegate { Write(false); });
            RetakeButton = AddButton(buttons, "_Retake", Retake);
            AddButton(buttons, "C_lose", Close);
            top.Children.Add(buttons);
            notice = CaptureControls.Text((result.Partial ? "Partial capture. " : "") + (result.Notice ?? ""));
            notice.Foreground = Theme.Current.Brush(result.Partial ? Theme.Current.Amber : Theme.Current.Secondary);
            top.Children.Add(notice);
            top.Children.Add(status);
            DockPanel.SetDock(top, Dock.Top);
            root.Children.Add(top);
            canvas.Children.Add(image);
            canvas.Children.Add(selection);
            scroll.Content = canvas;
            root.Children.Add(scroll);
            Content = root;
            Zoom = 1;
            RefreshImage();
            scroll.SizeChanged += delegate { ClearPendingCrop(); if (fit) Fit(scroll.ViewportWidth, scroll.ViewportHeight); };
            scroll.ScrollChanged += delegate(object sender, ScrollChangedEventArgs e)
            {
                if (e.ViewportWidthChange == 0 && e.ViewportHeightChange == 0) return;
                ClearPendingCrop();
                if (fit) Fit(scroll.ViewportWidth, scroll.ViewportHeight);
            };
            scroll.PreviewMouseWheel += delegate(object sender, MouseWheelEventArgs e)
            {
                if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
                SetZoom(Zoom * (e.Delta > 0 ? 1.25 : 0.8));
                e.Handled = true;
            };
            canvas.MouseLeftButtonDown += BeginDrag;
            canvas.MouseMove += MoveDrag;
            canvas.MouseLeftButtonUp += EndDrag;
            DpiChanged += delegate
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (IsReleased) return;
                    ClearPendingCrop();
                    if (fit) Fit(scroll.ViewportWidth, scroll.ViewportHeight);
                    else ResizeImage();
                }));
            };
            PreviewKeyDown += delegate(object sender, KeyEventArgs e) { if (HandleKey(e.Key)) e.Handled = true; };
            Closed += delegate { Release(); };
        }

        Button AddButton(Panel panel, string text, Action action)
        {
            Button button = CaptureControls.Button(text, action);
            actions.Add(button);
            panel.Children.Add(button);
            return button;
        }

        double DeviceScale
        {
            get
            {
                return VisualTreeHelper.GetDpi(this).DpiScaleX;
            }
        }
        internal double ViewScale { get { return Zoom / DeviceScale; } }

        public void Fit(double width, double height)
        {
            if (IsReleased || width <= 0 || height <= 0 || Double.IsInfinity(width) || Double.IsInfinity(height)) return;
            fit = true;
            Zoom = Math.Min(width / CurrentImage.Width, height / CurrentImage.Height) * DeviceScale;
            ResizeImage();
        }

        public void SetZoom(double value)
        {
            if (IsReleased || Double.IsNaN(value) || Double.IsInfinity(value)) return;
            fit = false;
            Zoom = Math.Max(0.01, Math.Min(8, value));
            ResizeImage();
        }

        void ResizeImage()
        {
            if (IsReleased) return;
            ClearPendingCrop();
            canvas.Width = image.Width = CurrentImage.Width * ViewScale;
            canvas.Height = image.Height = CurrentImage.Height * ViewScale;
        }

        void ClearPendingCrop()
        {
            dragging = false;
            canvas.ReleaseMouseCapture();
            cropArea = new RECT();
            selection.Visibility = Visibility.Collapsed;
        }

        void RefreshImage()
        {
            image.Source = Bitmap(CurrentImage);
            ResizeImage();
            if (fit) Fit(scroll.ViewportWidth, scroll.ViewportHeight);
        }

        internal static BitmapSource Bitmap(CaptureFrame frame)
        {
            var bitmap = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null, frame.Pixels, checked(frame.Width * 4));
            bitmap.Freeze();
            return bitmap;
        }

        public static RECT PixelArea(Point first, Point last, double scale, int width, int height)
        {
            if (Double.IsNaN(scale) || Double.IsInfinity(scale) || scale <= 0) throw new ArgumentOutOfRangeException("scale");
            if (Double.IsNaN(first.X) || Double.IsNaN(first.Y) || Double.IsNaN(last.X) || Double.IsNaN(last.Y) ||
                Double.IsInfinity(first.X) || Double.IsInfinity(first.Y) || Double.IsInfinity(last.X) || Double.IsInfinity(last.Y))
                throw new ArgumentException("Crop coordinates must be finite.");
            return new RECT { Left = (int)Math.Max(0, Math.Min(width, Math.Round(Math.Min(first.X, last.X) / scale, MidpointRounding.AwayFromZero))),
                Top = (int)Math.Max(0, Math.Min(height, Math.Round(Math.Min(first.Y, last.Y) / scale, MidpointRounding.AwayFromZero))),
                Right = (int)Math.Max(0, Math.Min(width, Math.Round(Math.Max(first.X, last.X) / scale, MidpointRounding.AwayFromZero))),
                Bottom = (int)Math.Max(0, Math.Min(height, Math.Round(Math.Max(first.Y, last.Y) / scale, MidpointRounding.AwayFromZero))) };
        }

        public static CaptureFrame CropPixels(CaptureFrame source, RECT pixels)
        {
            if (source == null || source.Pixels == null) throw new ArgumentException("The source image is unavailable.", "source");
            int left = Math.Max(0, pixels.Left), top = Math.Max(0, pixels.Top);
            int right = Math.Min(source.Width, pixels.Right), bottom = Math.Min(source.Height, pixels.Bottom);
            if (right <= left || bottom <= top) throw new ArgumentException("Select a nonempty crop inside the image.", "pixels");
            int width = right - left, height = bottom - top;
            byte[] data = new byte[checked(width * height * 4)];
            for (int y = 0; y < height; y++) Buffer.BlockCopy(source.Pixels, ((y + top) * source.Width + left) * 4, data, y * width * 4, width * 4);
            return new CaptureFrame(width, height, data);
        }

        public void ApplyCrop(RECT pixels)
        {
            if (IsReleased) return;
            CaptureFrame next = CropPixels(CurrentImage, pixels);
            if (cropped != null) cropped.Dispose();
            cropped = next;
            cropMode = false;
            status.Text = "Crop applied. Reset crop restores the original image.";
            RefreshImage();
        }

        public void ResetCrop()
        {
            if (IsReleased) return;
            if (cropped != null) cropped.Dispose();
            cropped = null;
            cropMode = false;
            status.Text = "Original image restored.";
            RefreshImage();
        }

        void ToggleCrop()
        {
            cropMode = !cropMode;
            cropArea = new RECT();
            selection.Visibility = Visibility.Collapsed;
            status.Text = cropMode ? "Drag a crop. Press Enter to apply. Escape cancels the crop." : "Drag the image to pan.";
        }

        void BeginDrag(object sender, MouseButtonEventArgs e)
        {
            if (IsReleased) return;
            dragging = true;
            start = e.GetPosition(canvas);
            pan = e.GetPosition(scroll);
            horizontal = scroll.HorizontalOffset;
            vertical = scroll.VerticalOffset;
            canvas.CaptureMouse();
            e.Handled = true;
        }

        void MoveDrag(object sender, MouseEventArgs e)
        {
            if (!dragging) return;
            if (cropMode)
            {
                cropArea = PixelArea(start, e.GetPosition(canvas), ViewScale, CurrentImage.Width, CurrentImage.Height);
                Canvas.SetLeft(selection, cropArea.Left * ViewScale);
                Canvas.SetTop(selection, cropArea.Top * ViewScale);
                selection.Width = cropArea.Width * ViewScale;
                selection.Height = cropArea.Height * ViewScale;
                selection.Visibility = Visibility.Visible;
            }
            else
            {
                Point at = e.GetPosition(scroll);
                scroll.ScrollToHorizontalOffset(horizontal + pan.X - at.X);
                scroll.ScrollToVerticalOffset(vertical + pan.Y - at.Y);
            }
        }

        void EndDrag(object sender, MouseButtonEventArgs e)
        {
            if (!dragging) return;
            MoveDrag(sender, e);
            dragging = false;
            canvas.ReleaseMouseCapture();
            if (cropMode && (cropArea.Width <= 0 || cropArea.Height <= 0)) status.Text = "Select a nonempty crop inside the image.";
            e.Handled = true;
        }

        internal bool HandleKey(Key key)
        {
            if (key == Key.Escape)
            {
                if (cropMode) { cropMode = false; selection.Visibility = Visibility.Collapsed; status.Text = "Crop canceled."; }
                else Close();
            }
            else if (key == Key.Enter && cropMode && cropArea.Width > 0 && cropArea.Height > 0) ApplyCrop(cropArea);
            else if (key == Key.Add || key == Key.OemPlus) SetZoom(Zoom * 1.25);
            else if (key == Key.Subtract || key == Key.OemMinus) SetZoom(Zoom / 1.25);
            else return false;
            return true;
        }

        void Write(bool toClipboard)
        {
            if (IsReleased) return;
            try
            {
                bool written = (toClipboard ? copy : save)(CurrentImage);
                status.Text = written ? (toClipboard ? "Copied." : "Saved PNG.") : (toClipboard ? "Image was not copied." : "Image was not saved.");
            }
            catch (ExternalException) { status.Text = "The image could not be copied. Try again when the clipboard is available."; }
            catch (IOException) { status.Text = "The image could not be saved. Choose another location."; }
            catch (UnauthorizedAccessException) { status.Text = "The image could not be saved. Choose a writable location."; }
        }

        static bool CopyDefault(CaptureFrame frame)
        {
            Clipboard.SetImage(Bitmap(frame));
            return true;
        }

        bool SaveDefault(CaptureFrame frame)
        {
            var dialog = new SaveFileDialog { Title = "Save capture", Filter = "PNG image (*.png)|*.png", DefaultExt = ".png", AddExtension = true,
                OverwritePrompt = true, FileName = "Capture.png" };
            if (dialog.ShowDialog(this) != true) return false;
            return SavePng(frame, dialog.FileName);
        }

        internal static bool SavePng(CaptureFrame frame, string path, Func<string, Stream> open = null)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(Bitmap(frame)));
            string staging = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)), ".capsule-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var data = new MemoryStream())
                {
                    encoder.Save(data);
                    using (var file = open == null ? new FileStream(staging, FileMode.CreateNew, FileAccess.Write) : open(staging)) data.WriteTo(file);
                }
                // Commit only after the PNG and staging stream close successfully.
                if (File.Exists(path)) File.Replace(staging, path, null);
                else File.Move(staging, path);
            }
            finally { if (File.Exists(staging)) File.Delete(staging); }
            return true;
        }

        void Retake()
        {
            if (IsReleased) return;
            Close();
            if (RetakeRequested != null) RetakeRequested();
        }

        void Release()
        {
            if (IsReleased) return;
            IsReleased = true;
            canvas.ReleaseMouseCapture();
            image.Source = null;
            if (cropped != null) cropped.Dispose();
            cropped = null;
            original = null;
            copy = null;
            save = null;
        }
    }
}
