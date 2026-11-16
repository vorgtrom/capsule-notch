using System;
using System.IO;
using System.Diagnostics;
using System.Text;

namespace Capsule
{
    // Append-only log in the data folder, renamed to log.old.txt at 1 MB.
    // Callers never pass tokens, prompts or response bodies, and an exception is named by its type, never by its message: a
    // message can carry a typed answer, a tool's input, a file's path or part of a response.
    public static class Log
    {
        const long MaxBytes = 1024 * 1024;
        static readonly object Gate = new object();

        public static void Info(string message) { Write("INFO ", message); }

        public static void Error(string message, Exception e)
        {
            try
            {
                if (e != null)
                {
                    var details = new StringBuilder(" (").Append(e.GetType().Name).Append(')');
                    if (e.InnerException != null) details.Append(" inner=").Append(e.InnerException.GetType().Name);
                    var trace = new StackTrace(e, false);
                    for (int i = 0; i < Math.Min(8, trace.FrameCount); i++)
                    {
                        var method = trace.GetFrame(i).GetMethod();
                        if (method == null || method.DeclaringType == null) continue;
                        details.Append(i == 0 ? " at " : " < ").Append(method.DeclaringType.Name).Append('.').Append(method.Name);
                    }
                    message += details.ToString();
                }
            }
            catch (Exception) { }   // formatting must never break the app
            Write("ERROR", message);
        }

        static void Write(string level, string message)
        {
            try
            {
                lock (Gate)
                {
                    string path = Paths.LogFile;
                    var info = new FileInfo(path);
                    if (info.Exists && info.Length > MaxBytes)
                    {
                        string old = Path.Combine(Paths.DataDir, "log.old.txt");
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(path, old);
                    }
                    File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + level + " " + message + Environment.NewLine, Files.Utf8);
                }
            }
            catch (Exception) { }   // logging must never break the app
        }
    }
}
