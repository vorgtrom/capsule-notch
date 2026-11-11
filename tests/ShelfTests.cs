using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace Capsule
{
    public static class ShelfTests
    {
        public static void Run()
        {
            References();
            CroppedCaptureAndBudget();
            CleanupWithLockedFile();
            DragGap();
        }

        static void References()
        {
            string dir = TestRunner.NewTempDir();
            string[] paths = Enumerable.Range(0, 13).Select(i => Path.Combine(dir, "file-" + i + ".txt")).ToArray();
            foreach (string path in paths) File.WriteAllText(path, path);
            using (var shelf = new Shelf())
            {
                var tile = new ShelfView();
                tile.Bind(shelf);
                var drop = new DataObject(DataFormats.FileDrop, new[] { paths[0], paths[0], paths[1] });
                TestRunner.Check(ShelfView.HasFiles(drop) && tile.Receive(drop), "Shelf receives a Windows file drop");
                TestRunner.Eq(2, shelf.Items.Count, "duplicate paths make one shelf row");
                string status;
                string alias = Path.Combine(dir, ".", "FILE-0.TXT");
                TestRunner.Eq(0, shelf.AddFiles(new[] { alias }, out status), "aliases and Windows casing do not duplicate a shelf row");
                TestRunner.Check(status.Contains("already"), "Shelf explains an all-duplicate drop");
                TestRunner.Eq(0, shelf.AddFiles(new[] { dir, Path.Combine(dir, "absent.txt"), "relative.txt" }, out status), "Shelf accepts existing absolute file paths only");
                TestRunner.Check(status.Contains("unavailable"), "Shelf explains unavailable paths in an inbound drop");
                TestRunner.Eq(10, shelf.AddFiles(paths, out status), "a partial drop fills the remaining slots");
                TestRunner.Eq(Shelf.MaxItems, shelf.Items.Count, "Shelf holds at most 12 rows");
                TestRunner.Eq(Shelf.Full, status, "Shelf explains a partial drop at the limit");
                shelf.Remove(paths[0]);
                TestRunner.Eq(1, shelf.AddFiles(new[] { paths[12] }, out status), "removing a reference frees a slot");
                TestRunner.Eq(paths[0], File.ReadAllText(paths[0]), "Shelf removal leaves the original file intact");
                File.Delete(paths[1]);
                tile.Refresh();
                var rows = ((StackPanel)tile.Child).Children.OfType<DockPanel>();
                var label = (TextBlock)((Border)rows.First().Children[1]).Child;
                TestRunner.Check(label.Text.Contains("unavailable"), "Shelf shows a missing reference on its row");
                tile.DragFiles(tile, shelf.Items.ToArray(), delegate(DependencyObject source, object data, DragDropEffects effects)
                {
                    TestRunner.Check(tile.Dragging && source == tile, "outbound drag keeps the Shelf in its dragging state");
                    TestRunner.Eq(DragDropEffects.Copy, effects, "Shelf offers only the copy effect");
                    var fileDrop = (string[])((IDataObject)data).GetData(DataFormats.FileDrop, false);
                    TestRunner.Check(fileDrop.SequenceEqual(shelf.Items.Where(File.Exists)), "Drag all offers only available file paths");
                    return DragDropEffects.None;
                });
                string failure = ((StackPanel)tile.Child).Children.OfType<TextBlock>().Last().Text;
                TestRunner.Check(failure.Contains("no longer available") && failure.Contains("canceled or rejected") && failure.Contains("administrator"),
                    "failed outbound drops explain missing files and the possible Windows restriction");
                TestRunner.Check(!tile.Dragging, "Shelf clears the dragging state after a canceled or rejected drop");
                tile.DragFiles(tile, new[] { paths[2] }, delegate(DependencyObject source, object data, DragDropEffects effects)
                {
                    var fileDrop = (string[])((IDataObject)data).GetData(DataFormats.FileDrop, false);
                    TestRunner.Check(fileDrop.SequenceEqual(new[] { paths[2] }), "dragging one row offers only that file");
                    return DragDropEffects.Copy;
                });
            }
            foreach (string path in paths.Where(p => p != paths[1]))
            {
                TestRunner.Eq(path, File.ReadAllText(path), "Shelf exit leaves the original file intact");
                File.Delete(path);
            }
            Directory.Delete(dir);
        }

        static void CroppedCaptureAndBudget()
        {
            string dir = TestRunner.NewTempDir();
            string sample = Path.Combine(dir, "sample.png");
            using (var source = new CaptureFrame(12, 10, new byte[12 * 10 * 4]))
            {
                var area = new RECT { Left = 2, Top = 3, Right = 10, Bottom = 8 };
                long bytes;
                using (CaptureFrame crop = CapturePreviewWindow.CropPixels(source, area))
                    bytes = CapturePreviewWindow.ExportPng(crop, sample, Int64.MaxValue, false);
                TestRunner.Eq(new FileInfo(sample).Length, bytes, "shared PNG export counts written bytes");
                TestRunner.Eq(64L * 1024 * 1024, Shelf.MaxPngBytes, "Shelf's production PNG limit is 64 MiB");
                string staged, stagedFolder;
                using (var shelf = new Shelf(bytes * 2 - 1))
                {
                    var preview = new CapturePreviewWindow(new CaptureResult { Image = source }, keep: shelf.Keep);
                    preview.ApplyCrop(area);
                    preview.KeepButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    TestRunner.Eq("Kept on shelf.", preview.StatusText, "Keep on shelf reports success in capture preview");
                    TestRunner.Eq(1, shelf.Items.Count, "Keep on shelf adds a file to the current session");
                    staged = shelf.Items[0];
                    stagedFolder = Path.GetDirectoryName(staged);
                    TestRunner.Eq(bytes, new FileInfo(staged).Length, "Shelf saves the same PNG as Save PNG");
                    using (var stream = File.OpenRead(staged))
                    {
                        BitmapFrame frame = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                        TestRunner.Eq(8, frame.PixelWidth, "Shelf PNG uses crop width");
                        TestRunner.Eq(5, frame.PixelHeight, "Shelf PNG uses crop height");
                    }
                    shelf.Remove(staged);
                    TestRunner.Eq(0, shelf.Items.Count, "removing a capture clears its visible row");
                    TestRunner.Check(File.Exists(staged), "removed capture remains readable after its row disappears");
                    preview.KeepButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    TestRunner.Eq(Shelf.PngFull, preview.StatusText, "removed capture still reserves its PNG bytes");
                    TestRunner.Eq(1, Directory.GetFiles(stagedFolder).Length, "over-budget export leaves no new file");
                    preview.Close();
                    TestRunner.Check(File.Exists(staged), "closing preview leaves a staged capture available");
                    preview.KeepButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    TestRunner.Eq(0, shelf.Items.Count, "closed preview cannot add to the shelf");
                }
                TestRunner.Check(!File.Exists(staged) && !Directory.Exists(stagedFolder), "Shelf exit deletes a removed capture and its empty folder");
                TestRunner.Check(File.Exists(sample), "Shelf exit leaves a regular PNG intact");
                using (var shelf = new Shelf(bytes))
                using (CaptureFrame crop = CapturePreviewWindow.CropPixels(source, area))
                    TestRunner.Eq("Kept on shelf.", shelf.Keep(crop), "Shelf accepts an export at the exact byte limit");
                using (var shelf = new Shelf())
                {
                    for (int i = 0; i < Shelf.MaxItems; i++)
                    {
                        TestRunner.Eq("Kept on shelf.", shelf.Keep(source), "Shelf can stage 12 session captures");
                        shelf.Remove(shelf.Items[0]);
                    }
                    TestRunner.Check(shelf.Keep(source).Contains("12"), "removing capture rows keeps the staged slot reservation");
                }
            }
            File.Delete(sample);
            Directory.Delete(dir);
        }

        static void CleanupWithLockedFile()
        {
            string locked, other, folder, unrelated;
            var shelf = new Shelf();
            using (var capture = new CaptureFrame(4, 4, new byte[4 * 4 * 4]))
            {
                shelf.Keep(capture);
                shelf.Keep(capture);
            }
            locked = shelf.Items[0];
            other = shelf.Items[1];
            folder = Path.GetDirectoryName(locked);
            unrelated = Path.Combine(folder, "unrelated.txt");
            File.WriteAllText(unrelated, "keep");
            using (File.Open(locked, FileMode.Open, FileAccess.Read, FileShare.None)) shelf.Dispose();
            TestRunner.Check(File.Exists(locked), "Shelf exit can leave a PNG locked by another app");
            TestRunner.Check(!File.Exists(other), "failed cleanup of one PNG does not prevent cleanup of other PNGs");
            TestRunner.Eq("keep", File.ReadAllText(unrelated), "Shelf exit deletes only its own files");
            TestRunner.Eq(0, shelf.Items.Count, "Shelf clears even after a cleanup error");
            shelf.Dispose();
            File.Delete(locked);
            File.Delete(unrelated);
            Directory.Delete(folder);
        }

        static void DragGap()
        {
            var left = new RECT { Left = -56, Right = 0, Top = 100, Bottom = 400 };
            var right = new RECT { Left = 8, Right = 368, Top = 150, Bottom = 450 };
            var cursor = new POINT { X = 4, Y = 220 };
            TestRunner.Check(PanelWindow.InFileDragGap(cursor, left, right, true), "incoming drag can cross the gap from a left capsule");
            TestRunner.Check(PanelWindow.InFileDragGap(cursor, right, left, false), "incoming drag can cross the gap from a right capsule");
            cursor.Y = 130;
            TestRunner.Check(!PanelWindow.InFileDragGap(cursor, left, right, true), "leaving the shared vertical span leaves the drag gap");
            cursor.Y = 220;
            cursor.X = -10;
            TestRunner.Check(!PanelWindow.InFileDragGap(cursor, left, right, true), "the drag gap excludes the capsule body");
        }
    }
}
