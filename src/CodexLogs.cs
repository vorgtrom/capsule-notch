using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Capsule
{
    // Where Codex keeps its session logs, listed the safe way for CodexUsage and CodexActivity: %CODEX_HOME%\sessions\
    // YYYY\MM\DD\rollout-*.jsonl. Nothing here throws: a folder that can't be listed now is looked at again next time.
    public static class CodexLogs
    {
        public const string RolloutPattern = "rollout-*.jsonl";

        // The sessions folder, or null when the home is unset or isn't a usable path (a quote or a control character in
        // CODEX_HOME makes Path.Combine throw).
        public static string SessionsDir(string home)
        {
            if (string.IsNullOrEmpty(home)) return null;
            try { return Path.Combine(home, "sessions"); }
            catch (Exception) { return null; }
        }

        // The day folder Codex names for a local date, or null as for SessionsDir.
        public static string DayDir(string home, DateTime day)
        {
            string sessions = SessionsDir(home);
            if (sessions == null) return null;
            try
            {
                return Path.Combine(sessions, day.ToString("yyyy", CultureInfo.InvariantCulture), day.ToString("MM", CultureInfo.InvariantCulture), day.ToString("dd", CultureInfo.InvariantCulture));
            }
            catch (Exception) { return null; }
        }

        // Every YYYY\MM\DD folder under sessions, in no particular order.
        public static List<string> AllDayDirs(string home)
        {
            var days = new List<string>();
            string sessions = SessionsDir(home);
            if (sessions == null) return days;
            foreach (string year in Dirs(sessions))
                foreach (string month in Dirs(year))
                    foreach (string day in Dirs(month))
                        days.Add(day);
            return days;
        }

        // The YYYY\MM\DD folders under sessions whose date is `oldest` or later, in no particular order. Years and months
        // that end before `oldest` are not listed into, so the cost doesn't grow with the history. A folder not named like
        // a date (four digits, two, two) is passed over.
        public static List<string> DayDirsSince(string home, DateTime oldest)
        {
            var days = new List<string>();
            string sessions = SessionsDir(home);
            if (sessions == null) return days;
            oldest = oldest.Date;
            foreach (string year in Dirs(sessions))
            {
                int y = Number(year, 4);
                if (y < oldest.Year) continue;
                foreach (string month in Dirs(year))
                {
                    int m = Number(month, 2);
                    if (m < 1 || m > 12 || y == oldest.Year && m < oldest.Month) continue;
                    foreach (string day in Dirs(month))
                    {
                        int d = Number(day, 2);
                        if (d < 1 || d > DateTime.DaysInMonth(y, m)) continue;
                        if (new DateTime(y, m, d) >= oldest) days.Add(day);
                    }
                }
            }
            return days;
        }

        // The number a folder's name spells with exactly `digits` digits, or -1.
        static int Number(string dir, int digits)
        {
            string name = Path.GetFileName(dir);
            int n;
            if (name == null || name.Length != digits) return -1;
            foreach (char c in name) if (c < '0' || c > '9') return -1;
            return int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out n) ? n : -1;
        }

        // The subfolders of a folder; none when it is missing or can't be listed.
        public static string[] Dirs(string dir)
        {
            try { return Directory.GetDirectories(dir); }
            catch (Exception) { return new string[0]; }
        }

        // The rollout files in a folder; none when it is missing or can't be listed. The listing's own metadata isn't to be
        // trusted for a file Codex still has open: ask the file (new FileInfo) for its length and write time.
        public static string[] Rollouts(string dir)
        {
            if (dir == null) return new string[0];
            try { return Directory.GetFiles(dir, RolloutPattern); }
            catch (Exception) { return new string[0]; }
        }
    }
}
