using System;
using System.Globalization;
using System.IO;

namespace Capsule
{
    // Where things live. Tests redirect the data folder with the CAPSULE_DATA environment variable.
    public static class Paths
    {
        // C:\Users\<you>\.capsule, not AppData: Windows redirects AppData writes made inside an app package (Claude
        // Code in the Claude desktop app, and so its hooks) into a private per-package copy Capsule can't see.
        // The user folder itself isn't redirected.
        public static string DataDir
        {
            get
            {
                string over = Environment.GetEnvironmentVariable("CAPSULE_DATA");
                string dir = string.IsNullOrEmpty(over) ? Path.Combine(Home, ".capsule") : over;
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static string SessionsDir
        {
            get
            {
                string dir = Path.Combine(DataDir, "sessions");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static string ConfigFile { get { return Path.Combine(DataDir, "config.json"); } }
        public static string ReadingsFile { get { return Path.Combine(DataDir, "readings.json"); } }
        public static string LogFile { get { return Path.Combine(DataDir, "log.txt"); } }
        public static string NotionSecretFile { get { return Path.Combine(DataDir, "notion-secret.bin"); } }
        public static string GoogleTokenFile { get { return Path.Combine(DataDir, "google-token.bin"); } }
        public static string GoogleClientSecretFile { get { return Path.Combine(DataDir, "google-client-secret.bin"); } }
        public static string GoogleLinkFile { get { return Path.Combine(DataDir, "google-calendar-link.bin"); } }
        public static string IdeasQueueFile { get { return Path.Combine(DataDir, "ideas-queue.json"); } }
        public static string IdeasDiscardedFile { get { return Path.Combine(DataDir, "ideas-discarded.json"); } }
        public static string ProbeLogFile { get { return Path.Combine(DataDir, "hook-probe.log"); } }

        public static string Home { get { return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); } }
        public static string ClaudeDir { get { return Path.Combine(Home, ".claude"); } }
        public static string ClaudeCredentials { get { return Path.Combine(ClaudeDir, ".credentials.json"); } }
        public static string ClaudeSettings { get { return Path.Combine(ClaudeDir, "settings.json"); } }

        public static string CodexHome
        {
            get
            {
                string home = Environment.GetEnvironmentVariable("CODEX_HOME");
                return string.IsNullOrEmpty(home) ? Path.Combine(Home, ".codex") : home;
            }
        }

        public static string ExeDir { get { return AppDomain.CurrentDomain.BaseDirectory; } }
    }

    // Milliseconds since 1970-01-01 UTC: the unit every timestamp in Capsule uses.
    public static class Clock
    {
        static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static long NowMs() { return ToMs(DateTime.UtcNow); }
        public static long ToMs(DateTime utc) { return (long)(utc.ToUniversalTime() - Epoch).TotalMilliseconds; }
        public static DateTime FromMs(long ms) { return Epoch.AddMilliseconds(ms); }

        // An ISO 8601 time such as 2026-09-29T15:00:00.000000+00:00, or 0 if it cannot be read.
        public static long ParseIsoMs(string text)
        {
            DateTimeOffset value;
            if (string.IsNullOrEmpty(text)) return 0;
            if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out value)) return 0;
            return ToMs(value.UtcDateTime);
        }
    }
}
