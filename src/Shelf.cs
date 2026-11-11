using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Capsule
{
    internal sealed class Shelf : IDisposable
    {
        internal const int MaxItems = 12;
        internal const long MaxPngBytes = 64L * 1024 * 1024;
        internal const string Full = "Shelf is full. Remove a file to add another.";
        internal const string PngFull = "This capture exceeds the remaining 64 MiB shelf limit for this session.";
        readonly List<string> items = new List<string>();
        readonly List<string> staged = new List<string>();
        readonly long maxPngBytes;
        string folder, failedStaging;
        long stagedBytes;
        bool disposed;
        internal event Action Changed;
        internal IList<string> Items { get { return items.AsReadOnly(); } }

        internal Shelf(long maxPngBytes = MaxPngBytes) { this.maxPngBytes = maxPngBytes; }

        internal int AddFiles(string[] files, out string status)
        {
            int added = 0;
            bool unavailable = false, full = false;
            if (!disposed && files != null)
            {
                var known = new HashSet<string>(items, StringComparer.OrdinalIgnoreCase);
                foreach (string path in files)
                {
                    string file = FullFilePath(path);
                    if (file == null) { unavailable = true; continue; }
                    if (known.Contains(file)) continue;
                    if (items.Count == MaxItems) { full = true; break; }
                    known.Add(file);
                    items.Add(file);
                    added++;
                }
            }
            status = full ? Full : unavailable ? "Some dropped files are unavailable." : added == 0 ? "These files are already on the shelf." : "";
            if (added != 0 && Changed != null) Changed();
            return added;
        }

        static string FullFilePath(string path)
        {
            if (String.IsNullOrEmpty(path) || path.Length < 3) return null;
            bool network = path.StartsWith(@"\\", StringComparison.Ordinal);
            bool drive = path[1] == ':' && (path[2] == '\\' || path[2] == '/');
            if (!network && !drive) return null;
            try
            {
                string full = Path.GetFullPath(path);
                return File.Exists(full) ? full : null;
            }
            catch (ArgumentException) { return null; }
            catch (NotSupportedException) { return null; }
            catch (PathTooLongException) { return null; }
            catch (System.Security.SecurityException) { return null; }
        }

        internal void Remove(string path)
        {
            if (items.Remove(path) && Changed != null) Changed();
        }

        internal string Keep(CaptureFrame frame)
        {
            if (disposed) return "The shelf is unavailable.";
            if (items.Count == MaxItems) return Full;
            if (staged.Count == MaxItems) return "This session reached the limit of 12 shelf captures.";
            if (stagedBytes == maxPngBytes) return PngFull;
            try
            {
                if (failedStaging != null)
                {
                    File.Delete(failedStaging);
                    failedStaging = null;
                }
                if (folder == null)
                {
                    string path = Path.Combine(Path.GetTempPath(), "Capsule-Shelf-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(path);
                    folder = path;
                }
                string png = Path.Combine(folder, "Capture-" + (staged.Count + 1).ToString("00", System.Globalization.CultureInfo.InvariantCulture) + ".png");
                long bytes = CapturePreviewWindow.ExportPng(frame, png, maxPngBytes - stagedBytes, false,
                    cleanupFailed: path => failedStaging = path);
                if (bytes == 0) return PngFull;
                stagedBytes += bytes;
                staged.Add(png);
                items.Add(png);
            }
            catch (Exception e)
            {
                if (!(e is IOException || e is UnauthorizedAccessException || e is ExternalException || e is System.Security.SecurityException)) throw;
                Log.Error("shelf export", e);
                return "Could not keep the capture. Check the Windows temporary folder.";
            }
            if (Changed != null) Changed();
            return "Kept on shelf.";
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            items.Clear();
            foreach (string path in staged) DeleteOwnedFile(path);
            if (failedStaging != null) DeleteOwnedFile(failedStaging);
            staged.Clear();
            if (folder == null) return;
            try { Directory.Delete(folder); }
            catch (IOException e) { Log.Error("shelf folder cleanup", e); }
            catch (UnauthorizedAccessException e) { Log.Error("shelf folder cleanup", e); }
            catch (System.Security.SecurityException e) { Log.Error("shelf folder cleanup", e); }
        }

        static void DeleteOwnedFile(string path)
        {
            try { File.Delete(path); }
            catch (IOException e) { Log.Error("shelf cleanup", e); }
            catch (UnauthorizedAccessException e) { Log.Error("shelf cleanup", e); }
            catch (System.Security.SecurityException e) { Log.Error("shelf cleanup", e); }
        }
    }
}
