using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;

namespace Capsule
{
    // The pipe between capsule-hook.exe and Capsule (act-from-notch spec §4): its name and its messages. A message is a
    // 4-byte little-endian length, then that many bytes of UTF-8 JSON, at most MaxBytes. One request and one reply per
    // connection. Every read and write has a time limit; a message that is too big, cut short, not UTF-8 or late reads
    // as nothing, and the caller then closes the pipe.
    public static class PromptPipe
    {
        public const int MaxBytes = 1024 * 1024;
        static readonly UTF8Encoding Strict = new UTF8Encoding(false, true);

        // capsule-<the current user's SID>: one pipe per Windows user. CAPSULE_PIPE replaces it, for the tests and the live
        // checks, so they never reach the Capsule that is running.
        public static string Name
        {
            get
            {
                string over = Environment.GetEnvironmentVariable("CAPSULE_PIPE");
                if (!string.IsNullOrEmpty(over)) return over;
                using (WindowsIdentity me = WindowsIdentity.GetCurrent()) return NameFor(me.User);
            }
        }

        // A user without a SID (an anonymous or odd token) gets a name from the account name instead of an exception.
        internal static string NameFor(SecurityIdentifier user)
        {
            if (user != null) return "capsule-" + user.Value;
            var safe = new StringBuilder();
            foreach (char c in Environment.UserName ?? "") safe.Append(char.IsLetterOrDigit(c) ? c : '_');
            return "capsule-" + safe;
        }

        // Writes one message. False when it is empty or too big, the pipe broke, or it didn't go within timeoutMs (a
        // negative limit is no time at all, never "wait forever").
        public static bool Write(Stream stream, string json, int timeoutMs)
        {
            try
            {
                if (timeoutMs < 0) return false;
                byte[] body = Strict.GetBytes(json ?? "");
                if (body.Length == 0 || body.Length > MaxBytes) return false;
                var frame = new byte[4 + body.Length];
                frame[0] = (byte)body.Length;
                frame[1] = (byte)(body.Length >> 8);
                frame[2] = (byte)(body.Length >> 16);
                frame[3] = (byte)(body.Length >> 24);
                Buffer.BlockCopy(body, 0, frame, 4, body.Length);
                Task write = stream.WriteAsync(frame, 0, frame.Length);
                if (!write.Wait(timeoutMs))
                {
                    Observe(write);
                    return false;   // the caller closes the pipe, which ends the write
                }
                stream.Flush();
                return true;
            }
            catch (Exception) { return false; }
        }

        // Reads one message: its text, or null when the pipe closed or broke, the length is 0 or over MaxBytes, the bytes
        // aren't UTF-8, or the whole message didn't arrive within timeoutMs.
        public static string Read(Stream stream, int timeoutMs)
        {
            try
            {
                var clock = Stopwatch.StartNew();
                byte[] head = ReadExactly(stream, 4, timeoutMs, clock);
                if (head == null) return null;
                int length = head[0] | (head[1] << 8) | (head[2] << 16) | (head[3] << 24);
                if (length <= 0 || length > MaxBytes) return null;
                byte[] body = ReadExactly(stream, length, timeoutMs, clock);
                if (body == null) return null;
                string text = Strict.GetString(body);
                return text.Length > 0 && text[0] == '\uFEFF' ? text.Substring(1) : text;   // a peer's BOM isn't part of the message
            }
            catch (Exception) { return null; }
        }

        // A read or write given up on ends in an error once the caller closes the pipe: look at it, so it is never left
        // unobserved.
        static void Observe(Task abandoned)
        {
            abandoned.ContinueWith(delegate(Task t) { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }

        static byte[] ReadExactly(Stream stream, int count, int timeoutMs, Stopwatch clock)
        {
            var buffer = new byte[count];
            int got = 0;
            while (got < count)
            {
                long left = timeoutMs - clock.ElapsedMilliseconds;
                if (left <= 0) return null;
                Task<int> read = stream.ReadAsync(buffer, got, count - got);
                if (!read.Wait((int)left))
                {
                    Observe(read);
                    return null;   // the caller closes the pipe, which ends the read
                }
                if (read.Result <= 0) return null;        // closed before the message was whole
                got += read.Result;
            }
            return buffer;
        }
    }
}
