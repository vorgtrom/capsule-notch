using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Capsule
{
    // The Claude desktop app's windows (act-from-notch spec §3, §4). The window in front is the app's when its process
    // runs the app's own executable: Claude.exe in the app folder of the Microsoft Store package
    // (…\WindowsApps\Claude_<version>_<arch>__<publisher>\app\Claude.exe) or of the per-user install
    // (…\AnthropicClaude\app-<version>\claude.exe). Claude Code's own claude.exe (…\claude-code\<version>\…, a console
    // program with no window) and any other claude.exe don't count.
    public static class ClaudeApp
    {
        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);

        public static bool IsAppPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string[] parts = path.Split('\\', '/');
            int n = parts.Length;
            if (n < 4 || !Same(parts[n - 1], "claude.exe")) return false;
            string folder = parts[n - 2], parent = parts[n - 3], above = parts[n - 4];
            if (Same(folder, "app") && parent.StartsWith("Claude_", StringComparison.OrdinalIgnoreCase) && Same(above, "WindowsApps")) return true;
            return folder.StartsWith("app-", StringComparison.OrdinalIgnoreCase) && Same(parent, "AnthropicClaude");
        }

        static bool Same(string a, string b) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }

        // Whether the window in front is the Claude app's. False when that can't be told.
        public static bool IsInFront() { return IsAppPath(ForegroundExe()); }

        // The full path of the executable whose window is in front, or null.
        public static string ForegroundExe()
        {
            try
            {
                IntPtr window = Native.Foreground();
                if (window == IntPtr.Zero) return null;
                uint pid;
                GetWindowThreadProcessId(window, out pid);
                if (pid == 0) return null;
                IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (process == IntPtr.Zero) return null;
                try
                {
                    var name = new StringBuilder(1024);
                    int size = name.Capacity;
                    return QueryFullProcessImageName(process, 0, name, ref size) ? name.ToString(0, size) : null;
                }
                finally { CloseHandle(process); }
            }
            catch (Exception) { return null; }
        }
    }
}
