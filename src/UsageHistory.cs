using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Capsule
{
    // How far a log has been read, and (for Codex) what it said last. Keyed by a hash of the log's path.
    public sealed class FileMark
    {
        public long Offset, Length, MtimeMs;
        public CodexFileState Codex = new CodexFileState();

        public FileMark Clone() { return new FileMark { Offset = Offset, Length = Length, MtimeMs = MtimeMs, Codex = Codex.Clone() }; }
    }

    // A Claude reply already counted: where it went and the numbers it was counted at.
    public sealed class SeenReply
    {
        public string Day = "", Model = "";
        public long Tokens, CacheRead, AtMs;
    }

    // Day totals per model, plus what the scanner needs to carry on next time (spec: usage history §1). Never changed
    // once published: a scan works on a copy.
    public sealed class UsageStore
    {
        public const int Version = 1;
        public bool BackfillDone;
        public long LastScanStartMs;
        public SortedDictionary<string, Dictionary<string, long[]>> Days = new SortedDictionary<string, Dictionary<string, long[]>>(StringComparer.Ordinal);
        public Dictionary<string, FileMark> Files = new Dictionary<string, FileMark>();
        public Dictionary<string, SeenReply> Seen = new Dictionary<string, SeenReply>();

        // Bytes read and bytes known, over every log: for "Reading your logs… 40%".
        public double Progress
        {
            get
            {
                if (BackfillDone) return 1;
                long read = 0, known = 0;
                foreach (FileMark m in Files.Values) { read += Math.Min(m.Offset, m.Length); known += m.Length; }
                return known == 0 ? 0 : (double)read / known;
            }
        }

        public void Add(string day, string model, long tokens, long cacheRead)
        {
            Dictionary<string, long[]> models;
            if (!Days.TryGetValue(day, out models)) Days[day] = models = new Dictionary<string, long[]>();
            long[] t;
            if (!models.TryGetValue(model, out t)) models[model] = t = new long[2];
            t[0] += tokens;
            t[1] += cacheRead;
        }

        // Tokens and cache reads for one day and model; zeros when there are none.
        public long[] Get(string day, string model)
        {
            Dictionary<string, long[]> models;
            long[] t;
            return Days.TryGetValue(day, out models) && models.TryGetValue(model, out t) ? t : new long[2];
        }

        public UsageStore Clone()
        {
            var s = new UsageStore { BackfillDone = BackfillDone, LastScanStartMs = LastScanStartMs };
            foreach (var d in Days) s.Days[d.Key] = d.Value.ToDictionary(p => p.Key, p => (long[])p.Value.Clone());
            foreach (var f in Files) s.Files[f.Key] = f.Value.Clone();
            foreach (var r in Seen) s.Seen[r.Key] = new SeenReply { Day = r.Value.Day, Model = r.Value.Model, Tokens = r.Value.Tokens, CacheRead = r.Value.CacheRead, AtMs = r.Value.AtMs };
            return s;
        }

        public Dictionary<string, object> ToJson()
        {
            var d = new Dictionary<string, object>();
            d["version"] = Version;
            d["backfill_done"] = BackfillDone;
            d["last_scan_start"] = LastScanStartMs;
            var days = new Dictionary<string, object>();
            foreach (var day in Days) days[day.Key] = day.Value.ToDictionary(p => p.Key, p => (object)p.Value);
            d["days"] = days;
            var files = new Dictionary<string, object>();
            foreach (var f in Files)
            {
                var m = new Dictionary<string, object>();
                m["offset"] = f.Value.Offset;
                m["length"] = f.Value.Length;
                m["mtime"] = f.Value.MtimeMs;
                if (f.Value.Codex.Model != "") m["model"] = f.Value.Codex.Model;
                if (f.Value.Codex.Totals != null) m["totals"] = f.Value.Codex.Totals;
                files[f.Key] = m;
            }
            d["files"] = files;
            var seen = new Dictionary<string, object>();
            foreach (var r in Seen) seen[r.Key] = new object[] { r.Value.Day, r.Value.Model, r.Value.Tokens, r.Value.CacheRead, r.Value.AtMs };
            d["seen"] = seen;
            return d;
        }

        // Null when the text isn't a store this version can read.
        public static UsageStore FromJson(string text)
        {
            object o = Json.TryParse(text);
            if (Json.Obj(o) == null || Json.Num(Json.Get(o, "version")) != Version) return null;
            try
            {
                var s = new UsageStore();
                s.BackfillDone = Json.Get(o, "backfill_done") as bool? ?? false;
                s.LastScanStartMs = L(Json.Get(o, "last_scan_start"));
                foreach (var day in Json.Obj(Json.Get(o, "days")) ?? new Dictionary<string, object>())
                    foreach (var model in Json.Obj(day.Value) ?? new Dictionary<string, object>())
                    {
                        object[] t = Json.Arr(model.Value);
                        if (t != null && t.Length == 2) s.Add(day.Key, model.Key, L(t[0]), L(t[1]));
                    }
                foreach (var f in Json.Obj(Json.Get(o, "files")) ?? new Dictionary<string, object>())
                {
                    var m = new FileMark { Offset = L(Json.Get(f.Value, "offset")), Length = L(Json.Get(f.Value, "length")), MtimeMs = L(Json.Get(f.Value, "mtime")) };
                    m.Codex.Model = Json.Str(Json.Get(f.Value, "model")) ?? "";
                    object[] totals = Json.Arr(Json.Get(f.Value, "totals"));
                    if (totals != null && totals.Length == 3) m.Codex.Totals = totals.Select(L).ToArray();
                    s.Files[f.Key] = m;
                }
                foreach (var r in Json.Obj(Json.Get(o, "seen")) ?? new Dictionary<string, object>())
                {
                    object[] v = Json.Arr(r.Value);
                    if (v != null && v.Length == 5) s.Seen[r.Key] = new SeenReply { Day = Json.Str(v[0]) ?? "", Model = Json.Str(v[1]) ?? "", Tokens = L(v[2]), CacheRead = L(v[3]), AtMs = L(v[4]) };
                }
                return s;
            }
            catch (Exception) { return null; }
        }

        static long L(object v) { double? n = Json.Num(v); return n.HasValue ? (long)n.Value : 0; }

        // The store saved at path; a fresh one when there is none. A file that can't be read as a store is renamed
        // .bad (replacing an older one) so the history starts over instead of failing every time.
        public static UsageStore Load(string path)
        {
            string text = Capsule.Files.ReadText(path);
            if (text == null) return new UsageStore();
            UsageStore s = FromJson(text);
            if (s != null) return s;
            try
            {
                string bad = path + ".bad";
                if (File.Exists(bad)) File.Delete(bad);
                File.Move(path, bad);
            }
            catch (Exception) { }
            Log.Info("Usage history was damaged; counting again");
            return new UsageStore();
        }
    }

    // A log to read: its path and whose it is.
    public sealed class LogFile
    {
        public string Path;
        public bool Codex;
    }

    // Reads what is new in each log into a store (spec: usage history §1).
    public static class UsageScanner
    {
        public const long BudgetBytes = 20L * 1024 * 1024;
        public const long ResumeGraceMs = 48L * 3600 * 1000;   // after the backfill, older replies are copies from a resume
        public const long SeenKeepMs = 3L * 24 * 3600 * 1000;
        const int Block = 1024 * 1024;

        // Every Claude Code transcript and Codex rollout on this PC. Never throws.
        public static List<LogFile> Discover(string claudeDir, string codexHome)
        {
            var list = new List<LogFile>();
            string projects = claudeDir == null ? null : System.IO.Path.Combine(claudeDir, "projects");
            if (projects != null && Directory.Exists(projects))
            {
                try { foreach (string f in Directory.EnumerateFiles(projects, "*.jsonl", SearchOption.AllDirectories)) list.Add(new LogFile { Path = f }); }
                catch (Exception) { }
            }
            foreach (string day in CodexLogs.AllDayDirs(codexHome))
                foreach (string f in CodexLogs.Rollouts(day)) list.Add(new LogFile { Path = f, Codex = true });
            return list;
        }

        public static string Hash(string path)
        {
            using (var sha = SHA1.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(path.ToLowerInvariant()))).Replace("-", "").ToLowerInvariant();
        }

        public static string Day(long atMs)
        {
            return Clock.FromMs(atMs).ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        // Reads up to `budget` new bytes across the logs into the store. Returns true when anything changed.
        public static bool Scan(UsageStore store, List<LogFile> logs, long now, long budget)
        {
            bool changed = false;
            long previousStart = store.LastScanStartMs;
            long ignoreBefore = store.BackfillDone && previousStart > 0 ? previousStart - ResumeGraceMs : long.MinValue;
            var present = new HashSet<string>();
            bool budgetHit = false;
            foreach (LogFile log in logs)
            {
                string key = Hash(log.Path);
                present.Add(key);
                FileInfo info;
                long length, mtime;
                try
                {
                    info = new FileInfo(log.Path);
                    length = info.Length;
                    mtime = Clock.ToMs(info.LastWriteTimeUtc);
                }
                catch (Exception) { continue; }
                FileMark mark;
                if (!store.Files.TryGetValue(key, out mark)) { store.Files[key] = mark = new FileMark(); changed = true; }
                if (length < mark.Offset) { mark.Offset = 0; mark.Codex = new CodexFileState(); }
                if (mark.Length != length || mark.MtimeMs != mtime) { mark.Length = length; mark.MtimeMs = mtime; changed = true; }
                if (mark.Offset >= length) continue;
                if (budget <= 0) { budgetHit = true; continue; }
                List<string> lines;
                long consumed = ReadLines(log.Path, mark.Offset, budget, out lines);
                if (consumed < 0) continue;   // couldn't be read now: try again next time
                budget -= consumed;
                if (consumed > 0) { mark.Offset += consumed; changed = true; }
                if (budget <= 0 && mark.Offset < length) budgetHit = true;   // stopped for the budget, not at the end
                foreach (string line in lines)
                {
                    UsageEntry e = log.Codex ? UsageLogs.ParseCodex(line, mark.Codex) : UsageLogs.ParseClaude(line);
                    if (e == null || e.AtMs < ignoreBefore) continue;
                    Count(store, e);
                }
            }
            foreach (string gone in store.Files.Keys.Where(k => !present.Contains(k)).ToList()) { store.Files.Remove(gone); changed = true; }
            if (!budgetHit && !store.BackfillDone) { store.BackfillDone = true; changed = true; }
            // Kept whole through the backfill, so a resumed session's copies read in a later piece still match.
            if (store.BackfillDone)
                foreach (string old in store.Seen.Where(p => p.Value.AtMs < now - SeenKeepMs).Select(p => p.Key).ToList()) store.Seen.Remove(old);
            store.LastScanStartMs = now;
            return changed;
        }

        // A repeated Claude reply tops up to its largest numbers; everything else is added once.
        static void Count(UsageStore store, UsageEntry e)
        {
            if (e.Key == null) { store.Add(Day(e.AtMs), e.Model, e.Tokens, e.CacheRead); return; }
            SeenReply seen;
            if (store.Seen.TryGetValue(e.Key, out seen))
            {
                long dt = Math.Max(0, e.Tokens - seen.Tokens), dc = Math.Max(0, e.CacheRead - seen.CacheRead);
                if (dt == 0 && dc == 0) return;
                store.Add(seen.Day, seen.Model, dt, dc);
                seen.Tokens += dt;
                seen.CacheRead += dc;
                return;
            }
            string day = Day(e.AtMs);
            store.Add(day, e.Model, e.Tokens, e.CacheRead);
            store.Seen[e.Key] = new SeenReply { Day = day, Model = e.Model, Tokens = e.Tokens, CacheRead = e.CacheRead, AtMs = e.AtMs };
        }

        // The complete lines from `offset` on, reading about `budget` bytes (more if a line is longer). Returns the bytes
        // consumed (up to the last complete line), or -1 when the file can't be read now.
        public static long ReadLines(string path, long offset, long budget, out List<string> lines)
        {
            lines = new List<string>();
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    stream.Seek(offset, SeekOrigin.Begin);
                    var data = new MemoryStream();
                    var buffer = new byte[Block];
                    int n;
                    long lastNewline = -1;
                    while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        long before = data.Length;
                        data.Write(buffer, 0, n);
                        for (int i = n - 1; i >= 0; i--) if (buffer[i] == (byte)'\n') { lastNewline = before + i; break; }
                        if (data.Length >= budget && lastNewline >= 0) break;
                    }
                    if (lastNewline < 0) return 0;
                    long consumed = lastNewline + 1;
                    string text = Files.Utf8.GetString(data.GetBuffer(), 0, (int)consumed);
                    foreach (string line in text.Split('\n'))
                    {
                        string l = line.TrimEnd('\r');
                        if (l.Length > 0) lines.Add(l);
                    }
                    return consumed;
                }
            }
            catch (Exception) { return -1; }
        }
    }

    // The usage history module: scans the logs every 5 minutes on a background thread, saves the store, and tells the
    // UI when the totals changed.
    public sealed class UsageHistory : IModule
    {
        public const long IntervalMs = 5 * 60 * 1000;

        readonly string storePath;
        readonly Func<List<LogFile>> discover;
        readonly TaskScheduler ui;
        readonly object gate = new object();
        UsageStore store;
        long due;
        bool busy;

        public event Action Changed;   // raised on the UI thread after a scan that changed something

        public UsageHistory(string storePath, Func<List<LogFile>> discover)
        {
            this.storePath = storePath;
            this.discover = discover;
            store = UsageStore.Load(storePath);
            ui = TaskScheduler.FromCurrentSynchronizationContext();
        }

        // The latest totals. Never changed after it is handed out.
        public UsageStore Snapshot { get { lock (gate) return store; } }

        public void Tick(long now) { if (!busy && now >= due) Scan(); }
        public void Refresh() { due = 0; if (!busy) Scan(); }
        public void NetworkBack() { }
        public void Resumed() { due = 0; }

        void Scan()
        {
            busy = true;
            due = long.MaxValue;
            UsageStore work = Snapshot.Clone();
            Task.Run(() =>
            {
                bool changed = UsageScanner.Scan(work, discover(), Clock.NowMs(), UsageScanner.BudgetBytes);
                if (changed) Files.WriteAtomic(storePath, Json.Write(work.ToJson()));
                return changed;
            }).ContinueWith(t =>
            {
                busy = false;
                if (t.IsFaulted) Log.Error("Usage history scan", t.Exception.GetBaseException());
                else lock (gate) store = work;
                // Still reading the backfill: carry on right away rather than in 5 minutes.
                if (due != 0) due = Clock.NowMs() + (!t.IsFaulted && !work.BackfillDone ? 0 : IntervalMs);
                if (!t.IsFaulted && t.Result && Changed != null) Changed();
            }, ui);
        }
    }
}
