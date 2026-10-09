using System.Collections.Generic;
using System.Linq;

namespace Capsule
{
    // One limit window, e.g. Claude's current 5-hour session.
    public sealed class LimitWindow
    {
        public string Id = "";
        public string Label = "";
        public double Used;          // percent used, 0-100
        public long ResetsAtMs;      // 0 when unknown
        public string Note = "";     // e.g. "reset since last use"

        public LimitWindow Clone() { return (LimitWindow)MemberwiseClone(); }
    }

    // What the notch knows about one provider. Never changed after it is published: pollers clone, then replace.
    public sealed class Reading
    {
        public string Provider = "";      // "claude" or "codex"
        public string Status = "none";    // "ok", "signin", "error", or "none" (nothing known; for Codex: absent)
        public List<LimitWindow> Windows = new List<LimitWindow>();
        public string HeadlineId = "";    // the window the ring shows
        public string Plan = "";
        public long DataAtMs;             // when these numbers were true
        public bool FromLogs;             // Codex numbers read from its session logs
        public bool LastCheckFailed;
        public string Note = "";          // what went wrong, for the card's footer

        public LimitWindow Headline { get { return Windows.FirstOrDefault(w => w.Id == HeadlineId); } }
        public LimitWindow DisplayWindow { get { return Windows.OrderByDescending(w => w.Used).ThenBy(w => w.Id == HeadlineId ? 0 : 1).FirstOrDefault(); } }

        // A failed check or a log snapshot over 10 minutes old is visibly stale.
        public bool IsDimmed(long now)
        {
            return (LastCheckFailed || FromLogs) && DataAtMs > 0 && now - DataAtMs > 10 * 60 * 1000;
        }

        public Reading Clone()
        {
            var r = (Reading)MemberwiseClone();
            r.Windows = Windows.Select(w => w.Clone()).ToList();
            return r;
        }

        public Dictionary<string, object> ToJson()
        {
            var d = new Dictionary<string, object>();
            d["provider"] = Provider;
            d["status"] = Status;
            d["headline"] = HeadlineId;
            d["plan"] = Plan;
            d["data_at"] = DataAtMs;
            d["from_logs"] = FromLogs;
            d["last_check_failed"] = LastCheckFailed;
            d["note"] = Note;
            var windows = new List<object>();
            foreach (LimitWindow w in Windows)
            {
                var wd = new Dictionary<string, object>();
                wd["id"] = w.Id;
                wd["label"] = w.Label;
                wd["used"] = w.Used;
                wd["resets_at"] = w.ResetsAtMs;
                wd["note"] = w.Note;
                windows.Add(wd);
            }
            d["windows"] = windows;
            return d;
        }

        public static Reading FromJson(object value)
        {
            var o = Json.Obj(value);
            if (o == null) return null;
            var r = new Reading();
            r.Provider = Json.Str(Json.Get(o, "provider")) ?? "";
            r.Status = Json.Str(Json.Get(o, "status")) ?? "none";
            r.HeadlineId = Json.Str(Json.Get(o, "headline")) ?? "";
            r.Plan = Json.Str(Json.Get(o, "plan")) ?? "";
            r.DataAtMs = (long)(Json.Num(Json.Get(o, "data_at")) ?? 0);
            r.FromLogs = Json.Get(o, "from_logs") as bool? ?? false;
            r.LastCheckFailed = Json.Get(o, "last_check_failed") as bool? ?? false;
            r.Note = Json.Str(Json.Get(o, "note")) ?? "";
            foreach (object item in Json.Arr(Json.Get(o, "windows")) ?? new object[0])
            {
                var w = new LimitWindow();
                w.Id = Json.Str(Json.Get(item, "id")) ?? "";
                w.Label = Json.Str(Json.Get(item, "label")) ?? "";
                w.Used = Json.Num(Json.Get(item, "used")) ?? 0;
                w.ResetsAtMs = (long)(Json.Num(Json.Get(item, "resets_at")) ?? 0);
                w.Note = Json.Str(Json.Get(item, "note")) ?? "";
                r.Windows.Add(w);
            }
            return r;
        }
    }

    // The last readings on disk, so the notch shows numbers the moment it starts.
    public static class ReadingsCache
    {
        public static Dictionary<string, Reading> Load(string path)
        {
            var result = new Dictionary<string, Reading>();
            foreach (object item in Json.Arr(Json.TryParse(Files.ReadText(path))) ?? new object[0])
            {
                Reading r = Reading.FromJson(item);
                if (r != null && r.Provider != "") result[r.Provider] = r;
            }
            return result;
        }

        public static void Save(string path, IEnumerable<Reading> readings)
        {
            var list = new List<object>();
            foreach (Reading r in readings) list.Add(r.ToJson());
            Files.WriteAtomic(path, Json.Write(list));
        }
    }
}
