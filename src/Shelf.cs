using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace Capsule
{
    internal sealed class Shelf : IDisposable
    {
        internal const int MaxItems = 12;
        internal const long MaxPngBytes = 64L * 1024 * 1024;
        internal const string Full = "Shelf is full. Remove a file to add another.";
        internal const string PngFull = "This capture exceeds the remaining 64 MiB shelf limit for this session.";
        const string FolderPrefix = "Capsule-Shelf-";
        static readonly Regex StagedFolderName = new Regex(@"\A" + FolderPrefix + @"[0-9a-fA-F]{32}\z");
        static readonly Regex StagedFileName = new Regex(@"\ACapture-[0-9]{2}\.png\z");
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
            bool folders = false, unavailable = false, full = false;
            if (!disposed && files != null)
            {
                var known = new HashSet<string>(items, StringComparer.OrdinalIgnoreCase);
                foreach (string path in files)
                {
                    string file = FullPath(path);
                    if (!File.Exists(file))
                    {
                        if (Directory.Exists(file)) folders = true;
                        else unavailable = true;
                        continue;
                    }
                    if (known.Contains(file)) continue;
                    if (items.Count == MaxItems) { full = true; continue; }
                    known.Add(file);
                    items.Add(file);
                    added++;
                }
            }
            var messages = new List<string>();
            if (folders) messages.Add("Folders can't go on the shelf.");
            if (unavailable) messages.Add("Some dropped files are unavailable.");
            if (full) messages.Add(Full);
            if (messages.Count == 0 && added == 0) messages.Add("These files are already on the shelf.");
            status = string.Join(" ", messages);
            if (added != 0 && Changed != null) Changed();
            return added;
        }

        static string FullPath(string path)
        {
            if (String.IsNullOrEmpty(path) || path.Length < 3) return null;
            bool network = path.StartsWith(@"\\", StringComparison.Ordinal);
            bool drive = path[1] == ':' && (path[2] == '\\' || path[2] == '/');
            if (!network && !drive) return null;
            try { return Path.GetFullPath(path); }
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
                    string path = Path.Combine(Path.GetTempPath(), FolderPrefix + Guid.NewGuid().ToString("N"));
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

        internal static void SweepTempFolders(string tempRoot = null, DateTime? nowUtc = null)
        {
            try
            {
                var root = new DirectoryInfo(tempRoot ?? Path.GetTempPath());
                DateTime cutoff = (nowUtc ?? DateTime.UtcNow).AddHours(-24);
                WithPlainDirectoryPath(root, delegate
                {
                    foreach (string path in Directory.EnumerateDirectories(root.FullName, FolderPrefix + "*"))
                    {
                        if (!StagedFolderName.IsMatch(Path.GetFileName(path))) continue;
                        try { SweepFolder(path, cutoff); }
                        catch (Exception e) { Log.Error("shelf temp cleanup", e); }
                    }
                });
            }
            catch (Exception e) { Log.Error("shelf temp cleanup", e); }
        }

        static void SweepFolder(string folder, DateTime cutoff)
        {
            using (var handle = OpenSweepDirectory(folder))
            {
                if (handle.IsInvalid || !PlainDirectory(folder)) return;
                DateTime lastWrite = Directory.GetLastWriteTimeUtc(folder);
                if (lastWrite >= cutoff) return;

                string[] entries = Directory.GetFileSystemEntries(folder);
                foreach (string path in entries)
                    if (!StagedFile(path)) return;

                if (Directory.GetLastWriteTimeUtc(folder) != lastWrite) return;
                foreach (string path in entries)
                {
                    if (!StagedFile(path)) return;
                    File.Delete(path);
                }
            }
            // The parent remains pinned. The nonrecursive delete cannot descend into a replacement.
            if (PlainDirectory(folder)) Directory.Delete(folder);
        }

        static bool StagedFile(string path)
        {
            return StagedFileName.IsMatch(Path.GetFileName(path)) &&
                (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;
        }

        // Open each component itself and deny write and delete sharing so it cannot redirect child operations.
        static void WithPlainDirectoryPath(DirectoryInfo folder, Action action)
        {
            DirectoryInfo parent = folder.Parent;
            Action visit = delegate
            {
                string path = parent == null ? folder.FullName : folder.FullName.TrimEnd(Path.DirectorySeparatorChar);
                using (var handle = OpenSweepDirectory(path))
                    if (!handle.IsInvalid && PlainDirectory(path)) action();
            };
            if (parent == null) visit();
            else WithPlainDirectoryPath(parent, visit);
        }

        static bool PlainDirectory(string path)
        {
            return (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == FileAttributes.Directory;
        }

        static SafeFileHandle OpenSweepDirectory(string path)
        {
            const uint GenericRead = 0x80000000, BackupSemantics = 0x02000000, OpenReparsePoint = 0x00200000;
            return CreateFile(path, GenericRead, FileShare.Read, IntPtr.Zero, FileMode.Open,
                BackupSemantics | OpenReparsePoint, IntPtr.Zero);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, IntPtr security,
            FileMode mode, uint flags, IntPtr template);

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
