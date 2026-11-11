using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Capsule
{
    internal sealed class ShelfView : Border
    {
        internal const string DropFailed = "The drop was canceled or rejected. Windows can block drops into apps running as administrator.";
        readonly StackPanel content = new StackPanel();
        Shelf shelf;
        string status = "";
        internal bool Dragging { get; private set; }
        internal event Action Resized;

        internal ShelfView()
        {
            Child = content;
            Visibility = Visibility.Collapsed;
        }

        internal void Bind(Shelf model)
        {
            shelf = model;
            shelf.Changed += Refresh;
            Visibility = Visibility.Visible;
            Refresh();
        }

        internal static bool HasFiles(IDataObject data)
        {
            try { return data.GetDataPresent(DataFormats.FileDrop, false); }
            catch (ExternalException) { return false; }
        }

        internal bool IsTarget(DependencyObject source)
        {
            return IsVisible && (source == this || (source is Visual && IsAncestorOf(source)));
        }

        internal bool Receive(IDataObject data)
        {
            if (shelf == null) return false;
            string[] files = null;
            try { files = data.GetData(DataFormats.FileDrop, false) as string[]; }
            catch (ExternalException) { }
            if (files == null || files.Length == 0)
            {
                SetStatus("Could not read the dropped files.");
                return false;
            }
            string result;
            int added = shelf.AddFiles(files, out result);
            SetStatus(result);
            return added != 0;
        }

        internal void Refresh()
        {
            if (shelf == null || Dragging) return;
            content.Children.Clear();
            var title = CardView.MakeText("Shelf", 12, Palette.Secondary, FontWeights.SemiBold);
            title.Margin = new Thickness(0, 0, 0, 6);
            content.Children.Add(title);
            if (shelf.Items.Count == 0) content.Children.Add(Label("Drop files here. The shelf clears when Capsule exits."));
            else
            {
                if (shelf.Items.Count > 1)
                    content.Children.Add(DragHandle("Drag all files", "", true, () => shelf.Items.ToArray()));
                foreach (string path in shelf.Items) content.Children.Add(Row(path));
            }
            if (status.Length != 0) content.Children.Add(Label(status));
            if (Resized != null) Resized();
        }

        static TextBlock Label(string text)
        {
            return CardView.Wrap(CardView.MakeText(text, 12, Palette.Secondary, FontWeights.Normal));
        }

        UIElement Row(string path)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
            string name = Path.GetFileName(path);
            var remove = CaptureControls.Button("Remove", delegate
            {
                status = "";
                shelf.Remove(path);
            });
            remove.Padding = new Thickness(6, 2, 6, 2);
            remove.Margin = new Thickness(4, 0, 0, 0);
            AutomationProperties.SetName(remove, "Remove " + name + " from shelf");
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            row.Children.Add(DragHandle(name, path, File.Exists(path), () => new[] { path }));
            return row;
        }

        UIElement DragHandle(string name, string path, bool available, Func<string[]> files)
        {
            string label = available ? name : name + " (unavailable)";
            var text = CardView.MakeText(label, 12.5, available ? Palette.Text : Palette.Secondary, FontWeights.Normal);
            text.VerticalAlignment = VerticalAlignment.Center;
            text.TextTrimming = TextTrimming.CharacterEllipsis;
            var handle = new Border
            {
                Background = NotchView.Brush(Palette.Track), CornerRadius = new CornerRadius(6), Child = text,
                Cursor = Cursors.Hand, Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(0, 0, 0, 2),
                ToolTip = path.Length == 0 ? name : path
            };
            AutomationProperties.SetName(handle, label);
            AutomationProperties.SetHelpText(handle, "Drag to another app to copy.");
            Point? start = null;
            handle.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                if (handle.CaptureMouse()) start = e.GetPosition(handle);
            };
            handle.MouseLeftButtonUp += delegate { handle.ReleaseMouseCapture(); };
            handle.LostMouseCapture += delegate { start = null; };
            handle.MouseMove += delegate(object sender, MouseEventArgs e)
            {
                if (!start.HasValue || e.LeftButton != MouseButtonState.Pressed || Dragging) return;
                Point point = e.GetPosition(handle);
                if (Math.Abs(point.X - start.Value.X) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(point.Y - start.Value.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                handle.ReleaseMouseCapture();
                DragFiles(handle, files());
                e.Handled = true;
            };
            return handle;
        }

        internal void DragFiles(DependencyObject source, string[] requested, Func<DependencyObject, object, DragDropEffects, DragDropEffects> drag = null)
        {
            string[] files = requested.Where(File.Exists).ToArray();
            if (files.Length == 0)
            {
                SetStatus("The requested files are no longer available.");
                return;
            }
            bool copied = false;
            Dragging = true;
            try
            {
                var data = new DataObject(DataFormats.FileDrop, files);
                copied = (drag ?? DragDrop.DoDragDrop)(source, data, DragDropEffects.Copy) == DragDropEffects.Copy;
            }
            catch (ExternalException e) { Log.Error("shelf drag", e); }
            finally { Dragging = false; }
            string missing = files.Length < requested.Length ? "Some files are no longer available. " : "";
            SetStatus(missing + (copied ? "" : DropFailed));
        }

        void SetStatus(string message)
        {
            status = message.Trim();
            Refresh();
        }
    }
}
