using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Capsule
{
    // The Claude Code sessions capsule-hook.exe reports, read back from their status files.
    public static class Sessions
    {
        public const long StaleMs = 30 * 60 * 1000;               // working or waiting this long with no event: crashed, closed or left
        public const long DeleteAfterMs = 24L * 60 * 60 * 1000;   // status files this old are deleted
        public const long RecentMs = 10 * 60 * 1000;              // done/idle sessions stay on the card this long
        public const int MaxRows = 5;

        public static List<SessionStatus> Load(string dir, long now)
        {
            var list = new List<SessionStatus>();
            string[] files;
            try { files = Directory.GetFiles(dir, "*.json"); }
            catch (Exception) { return list; }
            foreach (string file in files)
            {
                SessionStatus s = SessionStatus.Read(file);
                if (s == null)
                {
                    DeleteIfOld(file, now);   // unreadable: may be mid-swap now, but never valid a day later
                    continue;
                }
                if (now - s.At > DeleteAfterMs)
                {
                    try { File.Delete(file); }
                    catch (Exception) { }
                    continue;
                }
                if (s.Since <= 0) s.Since = s.At;   // an older or damaged file without a start time
                if ((s.State == States.Working || s.State == States.Waiting) && now - s.At > StaleMs)
                {
                    s.State = States.Idle;
                    s.Since = s.At;
                }
                list.Add(s);
            }
            // Temp files left by interrupted writes: the hook's own, and File.Replace's "~RF" ones.
            string[] leftovers;
            try { leftovers = Directory.GetFiles(dir, "*.tmp"); }
            catch (Exception) { leftovers = new string[0]; }
            foreach (string file in leftovers) DeleteIfOld(file, now);
            return list;
        }

        // Deletes a leftover file once its last write is more than a day old. A file that is gone
        // (mid-swap) is left alone: its time would read as the year 1601 and look ancient.
        static void DeleteIfOld(string file, long now)
        {
            try
            {
                var info = new FileInfo(file);
                if (info.Exists && now - Clock.ToMs(info.LastWriteTimeUtc) > DeleteAfterMs) info.Delete();
            }
            catch (Exception) { }
        }

        // Waiting if any session waits on you, otherwise working if any works, otherwise null.
        public static string Aggregate(IEnumerable<SessionStatus> list)
        {
            bool working = false;
            foreach (SessionStatus s in list)
            {
                if (s.State == States.Waiting) return States.Waiting;
                if (s.State == States.Working) working = true;
            }
            return working ? States.Working : null;
        }

        // Rows for the card: waiting first, then working, then recently done or idle; newest first within each.
        public static List<SessionStatus> ForCard(IEnumerable<SessionStatus> list, long now, out int more)
        {
            List<SessionStatus> picked = list
                .Where(s => s.State == States.Waiting || s.State == States.Working || now - s.At <= RecentMs)
                .OrderBy(s => Rank(s.State))
                .ThenByDescending(s => s.At)
                .ToList();
            more = Math.Max(0, picked.Count - MaxRows);
            return picked.Take(MaxRows).ToList();
        }

        public static string RowText(SessionStatus s, long now)
        {
            if (s.State == States.Working) return "working · " + Format.Elapsed(now - s.Since);
            if (s.State == States.Waiting) return "waiting · " + (s.Reason != "" ? s.Reason : "needs you");
            if (s.State == States.Done) return "done · " + Format.Ago(s.At, now);
            return "idle";
        }

        static int Rank(string state)
        {
            if (state == States.Waiting) return 0;
            if (state == States.Working) return 1;
            return 2;
        }
    }
}
