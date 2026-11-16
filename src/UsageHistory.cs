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
        public bool Initial;   // there before the first count finished, and not yet read to its end: its old replies still count
        public CodexFileState Codex = new CodexFileState();

        public FileMark Clone() { return new FileMark { Offset = Offset, Length = Length, MtimeMs = MtimeMs, Initial = Initial, Codex = Codex.Clone() }; }
    }

    // A Claude reply already counted: where it went and the numbers it was counted at.
    public sealed class SeenReply
    {
        public string Day = "", Model = "";
        public long Tokens, CacheRead, AtMs;
    }

    // Both stores keep independent scan cursors so upgrading can backfill details without recounting history.
    public abstract class UsageScanState
    {
        public bool BackfillDone;
        public long LastScanStartMs;
        public Dictionary<string, FileMark> Files = new Dictionary<string, FileMark>();

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

        internal void CopyIndexTo(UsageScanState target)
        {
            target.BackfillDone = BackfillDone;
            target.LastScanStartMs = LastScanStartMs;
            foreach (var f in Files) target.Files[f.Key] = f.Value.Clone();
        }

        internal object FileIndex(bool detail)
        {
            var files = new Dictionary<string, object>();
            foreach (var f in Files)
            {
                FileMark mark = f.Value;
                var m = new Dictionary<string, object>();
                m["offset"] = mark.Offset;
                m["length"] = mark.Length;
                m["mtime"] = mark.MtimeMs;
                if (mark.Initial) m["initial"] = true;
                if (mark.Codex.Lineage != "") m["lineage"] = mark.Codex.Lineage;
                if (mark.Codex.Model != "") m["model"] = mark.Codex.Model;
                if (mark.Codex.Totals != null) m["totals"] = mark.Codex.Totals;
                if (detail && mark.Codex.ProjectMetaSeen) m["project_meta_seen"] = true;
                if (detail && mark.Codex.ProjectKey != "")
                {
                    m["project_key"] = mark.Codex.ProjectKey;
                    m["project_name"] = mark.Codex.ProjectName;
                }
                files[f.Key] = m;
            }
            return files;
        }

        internal void ReadIndex(object root, bool detail)
        {
            BackfillDone = Json.Get(root, "backfill_done") as bool? ?? false;
            LastScanStartMs = L(Json.Get(root, "last_scan_start"));
            foreach (var f in Json.Obj(Json.Get(root, "files")) ?? new Dictionary<string, object>())
            {
                var m = new FileMark { Offset = L(Json.Get(f.Value, "offset")), Length = L(Json.Get(f.Value, "length")),
                    MtimeMs = L(Json.Get(f.Value, "mtime")), Initial = Json.Get(f.Value, "initial") as bool? ?? false };
                m.Codex.Model = Json.Str(Json.Get(f.Value, "model")) ?? "";
                m.Codex.Lineage = Json.Str(Json.Get(f.Value, "lineage")) ?? "";
                object[] totals = Json.Arr(Json.Get(f.Value, "totals"));
                if (totals != null && totals.Length == 3) m.Codex.Totals = totals.Select(L).ToArray();
                if (detail)
                {
                    m.Codex.ProjectMetaSeen = Json.Get(f.Value, "project_meta_seen") as bool? ?? false;
                    m.Codex.ProjectKey = Json.Str(Json.Get(f.Value, "project_key")) ?? "";
                    m.Codex.ProjectName = Json.Str(Json.Get(f.Value, "project_name")) ?? "";
                }
                Files[f.Key] = m;
            }
        }

        internal static long L(object v) { double? n = Json.Num(v); return n.HasValue ? (long)n.Value : 0; }
    }

    // Day totals per model. Once published, a scan works on a copy.
    public sealed class UsageStore : UsageScanState
    {
        public const int Version = 1;
        public SortedDictionary<string, Dictionary<string, long[]>> Days = new SortedDictionary<string, Dictionary<string, long[]>>(StringComparer.Ordinal);
        public Dictionary<string, SeenReply> Seen = new Dictionary<string, SeenReply>();

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
            var s = new UsageStore();
            CopyIndexTo(s);
            foreach (var d in Days) s.Days[d.Key] = d.Value.ToDictionary(p => p.Key, p => (long[])p.Value.Clone());
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
            d["files"] = FileIndex(false);
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
                s.ReadIndex(o, false);
                foreach (var day in Json.Obj(Json.Get(o, "days")) ?? new Dictionary<string, object>())
                    foreach (var model in Json.Obj(day.Value) ?? new Dictionary<string, object>())
                    {
                        object[] t = Json.Arr(model.Value);
                        if (t != null && t.Length == 2) s.Add(day.Key, model.Key, L(t[0]), L(t[1]));
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
        public const long CodexSeenKeepMs = 35L * 24 * 3600 * 1000;   // a chat can be forked weeks later
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
            // Archived chats: Codex moves their logs here.
            string archived = string.IsNullOrEmpty(codexHome) ? null : SafeCombine(codexHome, "archived_sessions");
            if (archived != null && Directory.Exists(archived))
            {
                try { foreach (string f in Directory.EnumerateFiles(archived, CodexLogs.RolloutPattern, SearchOption.AllDirectories)) list.Add(new LogFile { Path = f, Codex = true }); }
                catch (Exception) { }
            }
            return list;
        }

        static string SafeCombine(string a, string b)
        {
            try { return System.IO.Path.Combine(a, b); }
            catch (Exception) { return null; }
        }

        // A Codex log is known by its file name, which Codex keeps when it archives the chat; a Claude transcript by its
        // path.
        public static string Key(LogFile log)
        {
            return log.Codex ? Hash("codex:" + System.IO.Path.GetFileName(log.Path)) : Hash(log.Path);
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

        public static bool Scan(UsageStore store, List<LogFile> logs, long now, long budget)
        {
            bool detailsChanged;
            return Scan(store, null, logs, now, budget, out detailsChanged);
        }

        // Read from the earlier cursor once. Only deliver each line to the stores that still need it.
        internal static bool Scan(UsageStore store, UsageDetailStore detail, List<LogFile> logs, long now, long budget, out bool detailsChanged)
        {
            bool changed = false, totalsBudgetHit = false, detailBudgetHit = false;
            detailsChanged = false;
            long totalsCutoff = Cutoff(store), detailCutoff = detail == null ? 0 : Cutoff(detail);
            var present = new HashSet<string>();
            foreach (LogFile log in logs)
            {
                string key = Key(log);
                if (!present.Add(key)) continue;   // a Codex chat can appear twice during archival
                FileMark totalsMark = Mark(store, log, key, ref changed);
                FileMark detailMark = detail == null ? null : Mark(detail, log, key, ref detailsChanged);
                long length, mtime;
                try
                {
                    var info = new FileInfo(log.Path);
                    length = info.Length;
                    mtime = Clock.ToMs(info.LastWriteTimeUtc);
                }
                catch (Exception) { continue; }
                UpdateMark(totalsMark, length, mtime, ref changed);
                if (detailMark != null) UpdateMark(detailMark, length, mtime, ref detailsChanged);
                long offset = Math.Min(totalsMark.Offset, detailMark == null ? long.MaxValue : detailMark.Offset);
                if (offset >= length) continue;
                if (budget <= 0)
                {
                    totalsBudgetHit |= totalsMark.Offset < length;
                    detailBudgetHit |= detailMark != null && detailMark.Offset < length;
                    continue;
                }

                List<LogLine> lines;
                long consumed = ReadBatch(log.Path, offset, budget, out lines);
                if (consumed < 0) continue;
                budget -= consumed;
                long totalsFrom = totalsMark.Offset, detailFrom = detailMark == null ? long.MaxValue : detailMark.Offset;
                bool shareCodex = log.Codex && detailMark != null && totalsFrom == detailFrom &&
                    SameCodexPosition(totalsMark.Codex, detailMark.Codex);
                foreach (LogLine line in lines)
                {
                    bool forTotals = line.Offset >= totalsFrom, forDetail = line.Offset >= detailFrom;
                    UsageEntry detailed = !forDetail ? null : log.Codex
                        ? UsageLogs.ParseCodex(line.Text, detailMark.Codex, true) : UsageLogs.ParseClaude(line.Text, true);
                    if (forDetail && detailed != null && (detailMark.Initial || detailed.AtMs >= detailCutoff))
                        CountDetail(detail, detailed);
                    if (!forTotals) continue;
                    UsageEntry entry = shareCodex || (forDetail && !log.Codex) ? detailed : log.Codex
                        ? UsageLogs.ParseCodex(line.Text, totalsMark.Codex, false) : UsageLogs.ParseClaude(line.Text, false);
                    if (entry != null && (totalsMark.Initial || entry.AtMs >= totalsCutoff)) Count(store, entry);
                }
                if (shareCodex) totalsMark.Codex = detailMark.Codex.Clone();
                Advance(totalsMark, offset + consumed, length, budget, ref changed, ref totalsBudgetHit);
                if (detailMark != null) Advance(detailMark, offset + consumed, length, budget, ref detailsChanged, ref detailBudgetHit);
            }
            FinishScan(store, present, now, totalsBudgetHit, store.Seen, s => s.AtMs, ref changed);
            if (detail != null)
                FinishScan(detail, present, now, detailBudgetHit, detail.Seen, s => s.AtMs, ref detailsChanged);
            return changed;
        }

        static long Cutoff(UsageScanState state)
        {
            return state.BackfillDone && state.LastScanStartMs > 0 ? state.LastScanStartMs - ResumeGraceMs : long.MinValue;
        }

        static FileMark Mark(UsageScanState state, LogFile log, string key, ref bool changed)
        {
            FileMark mark;
            if (!state.Files.TryGetValue(key, out mark))
            {
                // Older history knew a Codex log by its path, before archival kept the same identity.
                string legacy = log.Codex ? Hash(log.Path) : "";
                if (log.Codex && state.Files.TryGetValue(legacy, out mark)) state.Files.Remove(legacy);
                else mark = new FileMark { Initial = !state.BackfillDone };
                state.Files[key] = mark;
                changed = true;
            }
            mark.Codex.Fallback = key;
            return mark;
        }

        static void UpdateMark(FileMark mark, long length, long mtime, ref bool changed)
        {
            if (length < mark.Offset)
            {
                string fallback = mark.Codex.Fallback;
                mark.Offset = 0;
                mark.Codex = new CodexFileState { Fallback = fallback };
                changed = true;
            }
            if (mark.Length != length || mark.MtimeMs != mtime)
            {
                mark.Length = length;
                mark.MtimeMs = mtime;
                changed = true;
            }
        }

        static bool SameCodexPosition(CodexFileState a, CodexFileState b)
        {
            return a.Model == b.Model && a.Lineage == b.Lineage &&
                (a.Totals == null ? b.Totals == null : b.Totals != null && a.Totals.SequenceEqual(b.Totals));
        }

        static void Advance(FileMark mark, long end, long length, long budget, ref bool changed, ref bool budgetHit)
        {
            if (end > mark.Offset)
            {
                mark.Offset = end;
                changed = true;
            }
            if (budget <= 0 && mark.Offset < length) budgetHit = true;
            if (mark.Initial && mark.Offset >= length)
            {
                mark.Initial = false;
                changed = true;
            }
        }

        static void FinishScan<T>(UsageScanState state, HashSet<string> present, long now, bool budgetHit,
            Dictionary<string, T> seen, Func<T, long> at, ref bool changed)
        {
            foreach (string gone in state.Files.Keys.Where(k => !present.Contains(k)).ToList())
            {
                state.Files.Remove(gone);
                changed = true;
            }
            // Unreadable files keep their Initial flag so they can finish their backfill later.
            if (!budgetHit && !state.BackfillDone)
            {
                state.BackfillDone = true;
                changed = true;
            }
            if (state.BackfillDone)
                foreach (string old in seen.Where(p => at(p.Value) < now - (IsCodexKey(p.Key) ? CodexSeenKeepMs : SeenKeepMs)).Select(p => p.Key).ToList())
                    seen.Remove(old);
            state.LastScanStartMs = now;
        }

        static bool IsCodexKey(string key) { return key.StartsWith(UsageLogs.CodexKeyPrefix, StringComparison.Ordinal); }

        static void CountDetail(UsageDetailStore store, UsageEntry entry)
        {
            SeenUsageDetail seen;
            if (entry.Key != null && store.Seen.TryGetValue(entry.Key, out seen))
            {
                if (IsCodexKey(entry.Key)) return;
                if (entry.SplitCacheWrites) seen.KnownWrite5m = Math.Max(seen.KnownWrite5m, entry.Parts.Write5m);
                UsageParts next = seen.Parts.Merged(entry.Parts, seen.KnownWrite5m);
                store.Add(seen.Day, seen.Model, seen.ProjectKey, seen.ProjectName, next.Minus(seen.Parts));
                seen.Parts = next;
                return;
            }
            string day = Day(entry.AtMs);
            store.Add(day, entry.Model, entry.ProjectKey, entry.ProjectName, entry.Parts);
            if (entry.Key != null)
                store.Seen[entry.Key] = new SeenUsageDetail { Day = day, Model = entry.Model, ProjectKey = entry.ProjectKey,
                    ProjectName = entry.ProjectName, AtMs = entry.AtMs, KnownWrite5m = entry.SplitCacheWrites ? entry.Parts.Write5m : 0, Parts = entry.Parts };
        }

        // A repeated Claude reply tops up to its largest numbers; a Codex turn already counted (a fork's copy) adds nothing;
        // everything else is added once.
        static void Count(UsageStore store, UsageEntry e)
        {
            if (e.Key == null) { store.Add(Day(e.AtMs), e.Model, e.Tokens, e.CacheRead); return; }
            SeenReply seen;
            if (store.Seen.TryGetValue(e.Key, out seen) && e.Key.StartsWith(UsageLogs.CodexKeyPrefix, StringComparison.Ordinal)) return;
            if (seen != null)
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
            List<LogLine> batch;
            long consumed = ReadBatch(path, offset, budget, out batch);
            lines = batch.Select(line => line.Text).ToList();
            return consumed;
        }

        struct LogLine
        {
            internal long Offset;
            internal string Text;
        }

        static long ReadBatch(string path, long offset, long budget, out List<LogLine> lines)
        {
            lines = new List<LogLine>();
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
                    byte[] bytes = data.GetBuffer();
                    int start = 0;
                    for (int i = 0; i < consumed; i++)
                    {
                        if (bytes[i] != (byte)'\n') continue;
                        int end = i;
                        while (end > start && bytes[end - 1] == (byte)'\r') end--;
                        if (end > start)
                            lines.Add(new LogLine { Offset = offset + start, Text = Files.Utf8.GetString(bytes, start, end - start) });
                        start = i + 1;
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

        readonly string storePath, detailPath;
        readonly Func<List<LogFile>> discover;
        readonly TaskScheduler ui;
        readonly object gate = new object();
        UsageStore store;
        UsageDetailStore detail;
        long due;
        bool busy;

        public event Action Changed;   // raised on the UI thread after a scan that changed something

        public UsageHistory(string storePath, string detailPath, Func<List<LogFile>> discover)
        {
            this.storePath = storePath;
            this.detailPath = detailPath;
            this.discover = discover;
            store = UsageStore.Load(storePath);
            detail = UsageDetailStore.Load(detailPath);
            ui = TaskScheduler.FromCurrentSynchronizationContext();
        }

        // The latest totals. Never changed after it is handed out.
        public UsageStore Snapshot { get { lock (gate) return store; } }
        public UsageDetailStore DetailSnapshot { get { lock (gate) return detail; } }

        public void Tick(long now) { if (!busy && now >= due) Scan(); }
        public void Refresh() { due = 0; if (!busy) Scan(); }
        public void NetworkBack() { }
        public void Resumed() { due = 0; }

        void Scan()
        {
            busy = true;
            due = long.MaxValue;
            UsageStore work = null;
            UsageDetailStore detailWork = null;
            Task.Run(() =>
            {
                work = Snapshot.Clone();
                detailWork = DetailSnapshot.Clone();
                bool detailsChanged;
                bool changed = UsageScanner.Scan(work, detailWork, discover(), Clock.NowMs(), UsageScanner.BudgetBytes, out detailsChanged);
                if (changed) Files.WriteAtomic(storePath, Json.Write(work.ToJson()));
                if (detailsChanged) Files.WriteAtomic(detailPath, Json.Write(detailWork.ToJson()));
                return changed || detailsChanged;
            }).ContinueWith(t =>
            {
                busy = false;
                if (t.IsFaulted) Log.Error("Usage history scan", t.Exception.GetBaseException());
                else lock (gate) { store = work; detail = detailWork; }
                // Still reading either backfill: carry on right away rather than in 5 minutes.
                if (due != 0) due = Clock.NowMs() + (!t.IsFaulted && (!work.BackfillDone || !detailWork.BackfillDone) ? 0 : IntervalMs);
                if (!t.IsFaulted && t.Result && Changed != null) Changed();
            }, ui);
        }
    }
}
