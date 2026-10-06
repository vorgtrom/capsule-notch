using System;
using System.IO;
using System.Text;
using System.Threading;

namespace Capsule
{
    // Small file helpers that never throw at the caller.
    public static class Files
    {
        public static readonly Encoding Utf8 = new UTF8Encoding(false);

        // The whole file as text, or null when it is missing or cannot be read right now.
        public static string ReadText(string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Utf8, true))
                    return reader.ReadToEnd();
            }
            catch (Exception) { return null; }
        }

        // Writes UTF-8 (no BOM) to a temporary file, then swaps it in, so readers never see half a file.
        // Retries briefly if another process has the file open. Returns false if it gave up.
        public static bool WriteAtomic(string path, string text)
        {
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    File.WriteAllText(temp, text, Utf8);
                    if (File.Exists(path)) File.Replace(temp, path, null);
                    else File.Move(temp, path);
                    return true;
                }
                catch (IOException) { Thread.Sleep(20); }
                catch (UnauthorizedAccessException) { Thread.Sleep(20); }
            }
            try { File.Delete(temp); } catch (Exception) { }
            return false;
        }

        // The last maxBytes of a file as text, starting at the beginning of a line. Null if unreadable.
        public static string ReadTail(string path, int maxBytes)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    long start = Math.Max(0, stream.Length - maxBytes);
                    long from = Math.Max(0, start - 1);   // one byte early, to see whether start is a line start
                    stream.Seek(from, SeekOrigin.Begin);
                    var buffer = new byte[stream.Length - from];
                    int read = 0, n;
                    while (read < buffer.Length && (n = stream.Read(buffer, read, buffer.Length - read)) > 0) read += n;
                    string text = Utf8.GetString(buffer, 0, read);
                    if (start == 0) return text;
                    int newline = text.IndexOf('\n');
                    return newline < 0 ? "" : text.Substring(newline + 1);
                }
            }
            catch (Exception) { return null; }
        }
    }
}
