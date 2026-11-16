using System;
using System.Collections.Generic;
using System.IO;

namespace Capsule
{
    internal struct UsageParts
    {
        internal long Input, Write5m, Write1h, CacheRead, Output;

        internal UsageParts Minus(UsageParts before)
        {
            return new UsageParts { Input = Input - before.Input, Write5m = Write5m - before.Write5m,
                Write1h = Write1h - before.Write1h, CacheRead = CacheRead - before.CacheRead, Output = Output - before.Output };
        }

        internal UsageParts Merged(UsageParts next, long knownWrite5m)
        {
            long write1 = Math.Max(Write1h, next.Write1h);
            // An earlier unsplit cache count can be reclassified once the log supplies the durations.
            long writes = Math.Max(Write5m + Write1h, next.Write5m + next.Write1h);
            return new UsageParts { Input = Math.Max(Input, next.Input), Write5m = Math.Max(knownWrite5m, writes - write1),
                Write1h = write1, CacheRead = Math.Max(CacheRead, next.CacheRead), Output = Math.Max(Output, next.Output) };
        }

        internal bool Any { get { return Input != 0 || Write5m != 0 || Write1h != 0 || CacheRead != 0 || Output != 0; } }
    }

    public sealed class UsageDetailRow
    {
        public string Day = "", Model = "", ProjectKey = "", ProjectName = "";
        public long Input, Write5m, Write1h, CacheRead, Output;
        public long Tokens { get { return Input + Write5m + Write1h + Output; } }
    }

    internal sealed class SeenUsageDetail
    {
        internal string Day, Model, ProjectKey, ProjectName;
        internal long AtMs, KnownWrite5m;
        internal UsageParts Parts;
        internal SeenUsageDetail Clone() { return (SeenUsageDetail)MemberwiseClone(); }
    }

    // Kept separately from the legacy history so its day totals and file format stay intact.
    public sealed class UsageDetailStore : UsageScanState
    {
        public readonly List<UsageDetailRow> Rows = new List<UsageDetailRow>();
        readonly Dictionary<Tuple<string, string, string>, UsageDetailRow> byKey =
            new Dictionary<Tuple<string, string, string>, UsageDetailRow>();
        internal readonly Dictionary<string, SeenUsageDetail> Seen = new Dictionary<string, SeenUsageDetail>();

        internal void Add(string day, string model, string key, string name, UsageParts delta)
        {
            if (!delta.Any) return;
            var id = Tuple.Create(day, model, key);
            UsageDetailRow row;
            if (!byKey.TryGetValue(id, out row))
            {
                row = new UsageDetailRow { Day = day, Model = model, ProjectKey = key, ProjectName = name };
                byKey[id] = row;
                Rows.Add(row);
            }
            row.Input += delta.Input;
            row.Write5m += delta.Write5m;
            row.Write1h += delta.Write1h;
            row.CacheRead += delta.CacheRead;
            row.Output += delta.Output;
        }

        internal UsageDetailStore Clone()
        {
            var copy = new UsageDetailStore();
            CopyIndexTo(copy);
            foreach (UsageDetailRow row in Rows)
                copy.Add(row.Day, row.Model, row.ProjectKey, row.ProjectName,
                    new UsageParts { Input = row.Input, Write5m = row.Write5m, Write1h = row.Write1h, CacheRead = row.CacheRead, Output = row.Output });
            foreach (var seen in Seen) copy.Seen[seen.Key] = seen.Value.Clone();
            return copy;
        }

        internal Dictionary<string, object> ToJson()
        {
            var rows = new List<object>();
            foreach (UsageDetailRow r in Rows)
                rows.Add(new object[] { r.Day, r.Model, r.ProjectKey, r.ProjectName, r.Input, r.Write5m, r.Write1h, r.CacheRead, r.Output });
            var seen = new Dictionary<string, object>();
            foreach (var pair in Seen)
            {
                SeenUsageDetail s = pair.Value;
                seen[pair.Key] = new object[] { s.Day, s.Model, s.ProjectKey, s.ProjectName,
                    s.Parts.Input, s.Parts.Write5m, s.Parts.Write1h, s.Parts.CacheRead, s.Parts.Output, s.AtMs, s.KnownWrite5m };
            }
            return new Dictionary<string, object> { { "version", 1 }, { "backfill_done", BackfillDone },
                { "last_scan_start", LastScanStartMs }, { "rows", rows }, { "files", FileIndex(true) }, { "seen", seen } };
        }

        internal static UsageDetailStore FromJson(string text)
        {
            object root = Json.TryParse(text);
            if (Json.Num(Json.Get(root, "version")) != 1) return null;
            var store = new UsageDetailStore();
            store.ReadIndex(root, true);
            foreach (object item in Json.Arr(Json.Get(root, "rows")) ?? new object[0])
            {
                object[] row = Json.Arr(item);
                if (row != null && row.Length == 9)
                    store.Add(S(row[0]), S(row[1]), S(row[2]), S(row[3]), Parts(row));
            }
            foreach (var pair in Json.Obj(Json.Get(root, "seen")) ?? new Dictionary<string, object>())
            {
                object[] s = Json.Arr(pair.Value);
                if (s != null && s.Length == 11)
                    store.Seen[pair.Key] = new SeenUsageDetail { Day = S(s[0]), Model = S(s[1]), ProjectKey = S(s[2]),
                        ProjectName = S(s[3]), Parts = Parts(s), AtMs = L(s[9]), KnownWrite5m = L(s[10]) };
            }
            return store;
        }

        static string S(object value) { return Json.Str(value) ?? ""; }

        static UsageParts Parts(object[] row)
        {
            return new UsageParts { Input = L(row[4]), Write5m = L(row[5]), Write1h = L(row[6]), CacheRead = L(row[7]), Output = L(row[8]) };
        }

        internal static UsageDetailStore Load(string path)
        {
            string text = Capsule.Files.ReadText(path);
            if (text == null) return new UsageDetailStore();
            UsageDetailStore store = FromJson(text);
            if (store != null) return store;
            try
            {
                string bad = path + ".bad";
                if (File.Exists(bad)) File.Delete(bad);
                File.Move(path, bad);
            }
            catch (Exception) { }
            Log.Info("Usage detail was damaged; counting again");
            return new UsageDetailStore();
        }
    }
}
