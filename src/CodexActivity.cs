using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Capsule
{
    // Whether Codex is working (polish spec §3): a turn has started in one of its session logs and not ended. Codex writes
    // each session to %CODEX_HOME%\sessions\YYYY\MM\DD\rollout-*.jsonl, a JSON object a line; a turn's start and end are
    // lines whose payload.type is task_started (or turn_started), task_complete (or turn_complete) or turn_aborted.
    // These files hold your prompts and Codex's replies: only those five names are looked for, and nothing else from them
    // is kept, shown or logged. Not thread-safe: one Check at a time.
    public sealed class CodexActivity
    {
        public const long RecentMs = 60 * 60 * 1000;   // files written in the last hour are looked at
        public const long SilentMs = 30 * 60 * 1000;   // a turn whose log has been silent this long has stopped (Codex crashed)
        public const int TailBytes = 64 * 1024;        // how much of a changed file is read, from its end
        public const long ScanEveryMs = 15 * 1000;     // how often every day folder is searched, not just today's and yesterday's
        public const int ScanDays = 90;                // how far back that search goes, by the folder's date, so its cost stops growing

        static readonly string[] Starts = { "task_started", "turn_started" };
        static readonly string[] Ends = { "task_complete", "turn_complete", "turn_aborted" };

        // What the last check found in one file.
        sealed class Seen
        {
            public long Length;
            public DateTime WrittenUtc;
            public bool Started;   // its latest turn event is a start
        }

        readonly string home;
        readonly Dictionary<string, Seen> files = new Dictionary<string, Seen>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // what scans found, until silent
        long nextScanMs;   // 0: the first call scans

        public CodexActivity(string codexHome) { home = codexHome; }

        // True while a Codex turn runs: some log written in the last 30 minutes has a start as its latest turn event.
        // Where it looks: today's and yesterday's day folders (local dates, as Codex names them) on every call, so a turn
        // that crosses midnight counts; plus the files a full scan of every day folder found. The scan is the way to see a
        // session Codex keeps appending to in the folder of the day it began (left open for days, or an old thread
        // resumed): it runs on the first call and then when ScanEveryMs have passed by the `now` it is given, and what it
        // finds is remembered, and stat'd on every call with the rest, until it has been silent past SilentMs. The scan
        // goes back ScanDays days only (a turn left open for longer than that isn't seen). Reads only
        // files whose length or write time changed since the last call, and only their last 64 KB. Never throws: a file
        // that can't be read now keeps what was known of it (nothing, if it was never read).
        public bool Check(long now)
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in Logs(now)) paths.Add(path);
            if (now >= nextScanMs || now < nextScanMs - ScanEveryMs)   // due; or the clock went back
            {
                foreach (string path in Scan(now)) candidates.Add(path);
                nextScanMs = now + ScanEveryMs;
            }
            paths.UnionWith(candidates);
            var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool working = false;
            foreach (string path in paths)
            {
                long length;
                DateTime written;
                if (!Stat(path, out length, out written))   // gone meanwhile
                {
                    candidates.Remove(path);
                    continue;
                }
                long age = now - Clock.ToMs(written);
                if (age > RecentMs)
                {
                    candidates.Remove(path);
                    continue;
                }
                if (age > SilentMs) candidates.Remove(path);   // can't be working now; a later write brings it back via a scan
                listed.Add(path);
                Seen seen;
                files.TryGetValue(path, out seen);
                if (seen == null || seen.Length != length || seen.WrittenUtc != written)
                {
                    string tail = Files.ReadTail(path, TailBytes);
                    if (tail != null)
                    {
                        if (seen == null) seen = new Seen();
                        string latest = LatestTurnEvent(tail);
                        if (latest != null) seen.Started = Starts.Contains(latest);   // none in the tail: what was known stands
                        seen.Length = length;
                        seen.WrittenUtc = written;
                        files[path] = seen;
                    }
                }
                if (seen != null && seen.Started && age <= SilentMs) working = true;
            }
            foreach (string gone in files.Keys.Where(p => !listed.Contains(p)).ToList()) files.Remove(gone);
            return working;
        }

        // The rollout files in today's and yesterday's day folders.
        List<string> Logs(long now)
        {
            var paths = new List<string>();
            DateTime today = Clock.FromMs(now).ToLocalTime().Date;
            foreach (DateTime day in new[] { today, today.AddDays(-1) })
                paths.AddRange(CodexLogs.Rollouts(CodexLogs.DayDir(home, day)));
            return paths;
        }

        // The rollout files in the day folders of the last ScanDays days that were written to in the last SilentMs (a file silent longer can't be
        // working, and a later write shows in the next scan). Each is asked for its own write time: the folder listing
        // lags behind a file Codex has held open for days.
        List<string> Scan(long now)
        {
            var found = new List<string>();
            DateTime oldest = Clock.FromMs(now).ToLocalTime().Date.AddDays(-ScanDays);
            foreach (string day in CodexLogs.DayDirsSince(home, oldest))
                foreach (string path in CodexLogs.Rollouts(day))
                {
                    long length;
                    DateTime written;
                    if (Stat(path, out length, out written) && now - Clock.ToMs(written) <= SilentMs) found.Add(path);
                }
            return found;
        }

        // The file's length and last write time, asked of the file itself: a folder listing can lag behind a file that
        // Codex still has open.
        static bool Stat(string path, out long length, out DateTime written)
        {
            length = 0;
            written = DateTime.MinValue;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return false;
                length = info.Length;
                written = info.LastWriteTimeUtc;
                return true;
            }
            catch (Exception) { return false; }
        }

        // The latest turn event in a piece of a log, or null when it has none.
        public static string LatestTurnEvent(string text)
        {
            if (text == null) return null;
            string[] lines = text.Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string turnEvent = TurnEvent(lines[i]);
                if (turnEvent != null) return turnEvent;
            }
            return null;
        }

        // The turn event one line records, or null. A line is parsed only if it mentions one of the five names, and counts
        // only if its payload.type is one: a prompt, a reply or a tool's output that merely contains the words never does.
        public static string TurnEvent(string line)
        {
            if (string.IsNullOrEmpty(line)) return null;
            if (!Starts.Concat(Ends).Any(name => line.IndexOf(name, StringComparison.Ordinal) >= 0)) return null;
            string type = Json.Str(Json.Get(Json.TryParse(line), "payload", "type"));
            return type != null && (Starts.Contains(type) || Ends.Contains(type)) ? type : null;
        }
    }
}
