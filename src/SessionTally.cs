using System;
using System.Collections.Generic;
using System.IO;

namespace Capsule
{
    // A session's model and token use so far, for its row: "Opus 4.8 · 1.2M tokens".
    public sealed class SessionUse
    {
        public string Model = "";   // the model of its latest reply
        public long Tokens;         // input, output and cache writes, as the Usage page counts them: no cache reads

        public string Text
        {
            get
            {
                if (Model == "" && Tokens == 0) return "";
                string tokens = Format.Tokens(Tokens) + " tokens";
                return Model == "" ? tokens : UsageModel.ModelName(Model) + " · " + tokens;
            }
        }
    }

    // Each Claude Code session's use, read from its own transcript (~/.claude/projects/<project>/<session id>.jsonl).
    // Only new lines are read, and only the model and token counts are kept. A session is read again when it has had an
    // event since, at most every EveryMs while it works, and at once when it stops working. Update runs on one worker
    // thread at a time; For may be called from any thread.
    public sealed class SessionTally
    {
        public const long EveryMs = 10 * 1000;
        const long BudgetBytes = 8L * 1024 * 1024;   // per read: a long session's first read takes a few turns of this

        sealed class Entry
        {
            public string Path;      // null until found
            public long Offset, ReadAtMs, SeenAt = -1;
            public readonly HashSet<string> Keys = new HashSet<string>();   // a reply is written as several lines with the same usage
            public readonly SessionUse Use = new SessionUse();
        }

        readonly string projectsDir;
        readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>();
        readonly object gate = new object();

        public SessionTally(string claudeDir) { projectsDir = Path.Combine(claudeDir, "projects"); }

        public SessionUse For(string sessionId)
        {
            lock (gate)
            {
                Entry e;
                if (sessionId == null || !entries.TryGetValue(sessionId, out e)) return null;
                return new SessionUse { Model = e.Use.Model, Tokens = e.Use.Tokens };
            }
        }

        // Reads what is due. True when a row would change. Never throws.
        public bool Update(IList<SessionStatus> sessions, long now)
        {
            bool changed = false;
            var live = new HashSet<string>();
            foreach (SessionStatus s in sessions)
            {
                if (!SafeId(s.SessionId)) continue;
                live.Add(s.SessionId);
                Entry e;
                lock (gate)
                {
                    if (!entries.TryGetValue(s.SessionId, out e)) entries[s.SessionId] = e = new Entry();
                }
                bool due = e.SeenAt != s.At && (now - e.ReadAtMs >= EveryMs || s.State != States.Working);
                if (!due) continue;
                e.SeenAt = s.At;
                e.ReadAtMs = now;
                try { changed |= Read(s.SessionId, e); }
                catch (Exception) { }   // read again after the next event
            }
            lock (gate)
            {
                var gone = new List<string>();
                foreach (string id in entries.Keys) if (!live.Contains(id)) gone.Add(id);
                foreach (string id in gone) entries.Remove(id);
            }
            return changed;
        }

        bool Read(string id, Entry e)
        {
            if (e.Path == null) e.Path = Find(id);
            if (e.Path == null) return false;
            long length = new FileInfo(e.Path).Length;
            if (length < e.Offset)   // rewritten: count again from the start
            {
                lock (gate)
                {
                    e.Offset = 0;
                    e.Keys.Clear();
                    e.Use.Tokens = 0;
                    e.Use.Model = "";
                }
            }
            bool changed = false;
            while (e.Offset < length)
            {
                List<string> lines;
                long consumed = UsageScanner.ReadLines(e.Path, e.Offset, BudgetBytes, out lines);
                if (consumed <= 0) break;   // unreadable now, or no complete line yet
                lock (gate)
                {
                    e.Offset += consumed;
                    foreach (string line in lines)
                    {
                        UsageEntry u = UsageLogs.ParseClaude(line);
                        if (u == null || !e.Keys.Add(u.Key)) continue;
                        e.Use.Tokens += u.Tokens;
                        e.Use.Model = u.Model;
                        changed = true;
                    }
                }
            }
            return changed;
        }

        string Find(string id)
        {
            if (!Directory.Exists(projectsDir)) return null;
            foreach (string dir in Directory.EnumerateDirectories(projectsDir))
            {
                string path = Path.Combine(dir, id + ".jsonl");
                if (File.Exists(path)) return path;
            }
            return null;
        }

        // A session id names a file: letters, digits and dashes only, so it can't point anywhere else.
        internal static bool SafeId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 100) return false;
            foreach (char c in id) if (!(char.IsLetterOrDigit(c) && c < 128) && c != '-') return false;
            return true;
        }
    }
}
