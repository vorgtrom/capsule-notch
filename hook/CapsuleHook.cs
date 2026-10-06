using System;
using System.IO;
using System.Text;

namespace Capsule
{
    // capsule-hook.exe. Claude Code starts it for each registered hook event with the event JSON on stdin.
    // It updates that session's status file and exits 0. For a PermissionRequest it then asks Capsule, which may answer
    // from the notch (act-from-notch spec §4), and prints Capsule's decision; otherwise, and whenever Capsule passes, isn't
    // running or anything fails, it prints nothing. It never fails visibly.
    public static class CapsuleHook
    {
        const int MaxChars = 8 * 1024 * 1024;

        public static int Main(string[] args)
        {
            try
            {
                string input = ReadStdin();
                // A `claude -p` that Capsule itself started to renew the sign-in is not a real session.
                if (Environment.GetEnvironmentVariable("CAPSULE_SKIP_HOOK") == "1") return 0;
                bool probe = args.Length > 0 && args[0] == "--probe";
                if (input == null) return 0;
                long now = Clock.NowMs();
                HookEvents.Handle(input, probe, now);
                // A hook connected for probing only watches.
                if (!probe) Print(PromptClient.Handle(input, now, PromptPipe.Name, PromptClient.ConnectMs, PromptClient.ReplyMs));
            }
            catch (Exception) { }
            return 0;
        }

        // Claude Code reads the decision as UTF-8. The console's own encoding is the ANSI code page, which would mangle an
        // answer that isn't ASCII, so the bytes are written as they are.
        static void Print(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            byte[] bytes = new UTF8Encoding(false).GetBytes(text);
            using (Stream stdout = Console.OpenStandardOutput())
            {
                stdout.Write(bytes, 0, bytes.Length);
                stdout.Flush();
            }
        }

        // Reads stdin to the end so Claude Code never sees a broken pipe. Null when it is over the size cap.
        static string ReadStdin()
        {
            using (var reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false)))
            {
                var sb = new StringBuilder();
                var buffer = new char[65536];
                bool tooBig = false;
                int n;
                while ((n = reader.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (tooBig) continue;
                    sb.Append(buffer, 0, n);
                    if (sb.Length > MaxChars) { tooBig = true; sb.Clear(); }
                }
                return tooBig ? null : sb.ToString();
            }
        }
    }
}
