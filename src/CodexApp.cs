using System;

namespace Capsule
{
    public static class CodexApp
    {
        public static bool IsInFront() { return IsAppPath(ClaudeApp.ForegroundExe()); }

        public static bool IsAppPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string[] parts = path.Split('\\', '/');
            int n = parts.Length;
            if (n < 4 || !parts[n - 2].Equals("app", StringComparison.OrdinalIgnoreCase)
                || !parts[n - 4].Equals("WindowsApps", StringComparison.OrdinalIgnoreCase)) return false;
            string exe = parts[n - 1], package = parts[n - 3];
            return (exe.Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase) || exe.Equals("Codex.exe", StringComparison.OrdinalIgnoreCase))
                && (package.StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) || package.StartsWith("OpenAI.ChatGPT_", StringComparison.OrdinalIgnoreCase));
        }
    }
}
