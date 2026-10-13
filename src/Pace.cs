using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Capsule
{
    // One look at a limit: how full it was, when, and when it resets.
    public sealed class UsageSample
    {
        public string Provider = "", Window = "";
        public long AtMs, ResetMs;
        public double Used;
    }

    public enum ForecastKind { None, Full, AtReset, Reached }

    public sealed class Forecast
    {
        public ForecastKind Kind;
        public long FullAtMs;        // Full: when it reaches 100%
        public int AtResetPercent;   // AtReset: about how full at the reset
        public long ResetMs;
        public long LengthMs;
    }

    // The samples behind the pace forecast, and which alerts have been shown (spec: usage history §2). Saved as
    // usage-samples.json. Used on the UI thread only.
    public sealed class SampleStore
    {
        public const long KeepMs = 8L * 24 * 3600 * 1000;
        public List<UsageSample> Samples = new List<UsageSample>();
        public HashSet<string> Sent = new HashSet<string>();
        public Dictionary<string, int> FullStreak = new Dictionary<string, int>();   // window instance -> consecutive Full forecasts

        // Records a fresh reading's windows. Returns false (and records nothing) for a stale reading or numbers already
        // recorded.
        public bool Record(Reading r, long now)
        {
            if (!IsFresh(r, now)) return false;
            bool added = false;
            foreach (LimitWindow w in r.Windows)
            {
                if (w.ResetsAtMs <= 0) continue;
                if (Samples.Any(s => s.Provider == r.Provider && s.Window == w.Id && s.AtMs == r.DataAtMs)) continue;
                Samples.Add(new UsageSample { Provider = r.Provider, Window = w.Id, AtMs = r.DataAtMs, ResetMs = w.ResetsAtMs, Used = w.Used });
                added = true;
            }
            Prune(now);
            return added;
        }

        // Numbers worth forecasting from: a successful check, or a Codex log snapshot that isn't dimmed yet.
        public static bool IsFresh(Reading r, long now)
        {
            return r != null && r.Status == "ok" && r.DataAtMs > 0 && !r.LastCheckFailed && !r.IsDimmed(now);
        }

        public void Prune(long now)
        {
            Samples.RemoveAll(s => s.AtMs < now - KeepMs);
            Sent.RemoveWhere(k => { long reset; return long.TryParse(k.Split('|')[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out reset) && reset * 60000 < now - KeepMs; });
        }

        public List<UsageSample> For(string provider, string window, long resetMs)
        {
            long minute = resetMs / 60000;
            return Samples.Where(s => s.Provider == provider && s.Window == window && s.ResetMs / 60000 == minute).OrderBy(s => s.AtMs).ToList();
        }

        public Dictionary<string, object> ToJson()
        {
            var d = new Dictionary<string, object>();
            d["version"] = 1;
            d["samples"] = Samples.Select(s => (object)new object[] { s.Provider, s.Window, s.AtMs, s.Used, s.ResetMs }).ToList();
            d["sent"] = Sent.OrderBy(k => k, StringComparer.Ordinal).ToList();
            return d;
        }

        public static SampleStore Load(string path)
        {
            var s = new SampleStore();
            string text = Files.ReadText(path);
            if (text == null) return s;
            object o = Json.TryParse(text);
            if (Json.Num(Json.Get(o, "version")) != 1)
            {
                try { string bad = path + ".bad"; if (File.Exists(bad)) File.Delete(bad); File.Move(path, bad); } catch (Exception) { }
                Log.Info("Usage samples were damaged; starting again");
                return s;
            }
            foreach (object item in Json.Arr(Json.Get(o, "samples")) ?? new object[0])
            {
                object[] v = Json.Arr(item);
                if (v == null || v.Length != 5) continue;
                s.Samples.Add(new UsageSample { Provider = Json.Str(v[0]) ?? "", Window = Json.Str(v[1]) ?? "", AtMs = (long)(Json.Num(v[2]) ?? 0), Used = Json.Num(v[3]) ?? 0, ResetMs = (long)(Json.Num(v[4]) ?? 0) });
            }
            foreach (object k in Json.Arr(Json.Get(o, "sent")) ?? new object[0]) if (k is string) s.Sent.Add((string)k);
            return s;
        }

        public void Save(string path) { Files.WriteAtomic(path, Json.Write(ToJson())); }
    }

    public static class Pace
    {
        public const long Hour = 3600L * 1000, Day = 24 * Hour;

        // How long a limit window lasts: from Claude's ids, or Codex's labels. 0 when unknown.
        public static long LengthMs(string provider, LimitWindow w)
        {
            if (provider == "claude")
            {
                if (w.Id == "five_hour") return 5 * Hour;
                if (w.Id.StartsWith("seven_day", StringComparison.Ordinal)) return 7 * Day;
                return 0;
            }
            string label = (w.Label ?? "").ToLowerInvariant();
            if (label == "weekly limit") return 7 * Day;
            if (label == "monthly limit") return 30 * Day;
            int n;
            int dash = label.IndexOf('-');
            if (dash > 0 && int.TryParse(label.Substring(0, dash), NumberStyles.None, CultureInfo.InvariantCulture, out n))
            {
                if (label.Substring(dash).StartsWith("-hour", StringComparison.Ordinal)) return n * Hour;
                if (label.Substring(dash).StartsWith("-day", StringComparison.Ordinal)) return n * Day;
            }
            return 0;
        }

        // The forecast for one window from its samples (oldest first, this window instance only).
        public static Forecast For(List<UsageSample> samples, LimitWindow w, long lengthMs, long now)
        {
            var f = new Forecast { ResetMs = w.ResetsAtMs, LengthMs = lengthMs };
            if (w.Used >= 100) { f.Kind = ForecastKind.Reached; return f; }
            if (samples.Count < 2 || w.ResetsAtMs <= now) return f;
            bool longWindow = lengthMs > Day;
            long span = longWindow ? Day : Hour;
            long minSpan = longWindow ? 2 * Hour : 15 * 60 * 1000;
            double minRise = longWindow ? 1 : 2;
            UsageSample latest = samples[samples.Count - 1];
            long start = lengthMs > 0 ? w.ResetsAtMs - lengthMs : long.MinValue;
            UsageSample reference = samples.LastOrDefault(s => s.AtMs <= latest.AtMs - span && s.AtMs >= start) ?? samples.FirstOrDefault(s => s.AtMs >= start);
            if (reference == null || latest.AtMs - reference.AtMs < minSpan || latest.Used - reference.Used < minRise) return f;
            double rate = (latest.Used - reference.Used) / (latest.AtMs - reference.AtMs);   // points per ms
            double fullAt = latest.AtMs + (100 - latest.Used) / rate;
            if (fullAt < w.ResetsAtMs)
            {
                f.Kind = ForecastKind.Full;
                f.FullAtMs = (long)Math.Max(now, fullAt);
            }
            else
            {
                f.Kind = ForecastKind.AtReset;
                f.AtResetPercent = (int)Math.Round(Math.Min(100, latest.Used + rate * (w.ResetsAtMs - latest.AtMs)));
            }
            return f;
        }

        public static Forecast For(SampleStore store, string provider, LimitWindow w, long now)
        {
            long length = LengthMs(provider, w);
            return For(store.For(provider, w.Id, w.ResetsAtMs), w, length, now);
        }

        // A clock time, with the day for windows longer than a day: "4:10 PM", "Thu 2:00 PM".
        public static string When(long ms, bool withDay, CultureInfo culture)
        {
            DateTime t = Clock.FromMs(ms).ToLocalTime();
            string time = t.ToString(culture.DateTimeFormat.ShortTimePattern, culture);
            return withDay ? t.ToString("ddd", culture) + " " + time : time;
        }

        // The hover card's line: "At this pace: full at 4:10 PM", or null.
        public static string CardLine(Forecast f, CultureInfo culture)
        {
            bool day = f.LengthMs > Day;
            switch (f.Kind)
            {
                case ForecastKind.Full: return "At this pace: full " + (day ? "" : "at ") + When(f.FullAtMs, day, culture);
                case ForecastKind.AtReset: return "At this pace: about " + f.AtResetPercent + "% at reset";
                case ForecastKind.Reached: return "Limit reached · resets " + When(f.ResetMs, day, culture);
                default: return null;
            }
        }
    }

    // Which limit alerts to show (spec: usage history §2). Pure apart from the store's bookkeeping.
    public static class LimitAlerts
    {
        // The messages due for one fresh reading, marking them sent. Nothing when alerts are off or the reading is stale.
        public static List<string> Check(SampleStore store, Reading r, bool enabled, long now, CultureInfo culture)
        {
            var messages = new List<string>();
            if (!enabled || !SampleStore.IsFresh(r, now)) return messages;
            foreach (LimitWindow w in r.Windows)
            {
                if (w.ResetsAtMs <= 0) continue;
                string instance = r.Provider + "|" + w.Id + "|" + (w.ResetsAtMs / 60000).ToString(CultureInfo.InvariantCulture);
                long length = Pace.LengthMs(r.Provider, w);
                bool day = length > Pace.Day;
                string name = Owner(r.Provider) + " " + Name(r.Provider, w);
                if (w.Used >= 95 && store.Sent.Add(instance + "|95"))
                {
                    store.Sent.Add(instance + "|80");
                    messages.Add(name + " is at " + (int)Math.Floor(w.Used) + "%. It resets " + (day ? "" : "at ") + Pace.When(w.ResetsAtMs, day, culture) + ".");
                }
                else if (w.Used >= 80 && w.Used < 95 && store.Sent.Add(instance + "|80"))
                    messages.Add(name + " is at " + (int)Math.Floor(w.Used) + "%. It resets " + (day ? "" : "at ") + Pace.When(w.ResetsAtMs, day, culture) + ".");

                Forecast f = Pace.For(store, r.Provider, w, now);
                int streak;
                store.FullStreak.TryGetValue(instance, out streak);
                streak = f.Kind == ForecastKind.Full ? streak + 1 : 0;
                store.FullStreak[instance] = streak;
                if (streak >= 2 && w.Used >= 30 && w.Used < 95 && !store.Sent.Contains(instance + "|pace"))
                {
                    store.Sent.Add(instance + "|pace");
                    messages.Add("At this pace you'll hit " + name + " " + (day ? "on " : "at ") + Pace.When(f.FullAtMs, day, culture) + ", " + Gap(w.ResetsAtMs - f.FullAtMs) + " before it resets.");
                }
            }
            return messages;
        }

        static string Owner(string provider) { return provider == "claude" ? "Claude's" : "Codex's"; }

        // "5-hour limit", "weekly limit", "weekly Opus limit".
        public static string Name(string provider, LimitWindow w)
        {
            if (provider == "claude")
            {
                if (w.Id == "five_hour") return "5-hour limit";
                if (w.Id == "seven_day") return "weekly limit";
                if (w.Id.StartsWith("seven_day_", StringComparison.Ordinal))
                {
                    string model = w.Id.Substring("seven_day_".Length).Replace('_', ' ');
                    return "weekly " + CultureInfo.InvariantCulture.TextInfo.ToTitleCase(model) + " limit";
                }
                return "limit";
            }
            return (w.Label ?? "limit").ToLowerInvariant();
        }

        // "41 min", "3 hr", "a day", "2 days".
        public static string Gap(long ms)
        {
            long min = Math.Max(1, (long)Math.Round(ms / 60000.0));
            if (min < 60) return min + " min";
            long hr = (long)Math.Round(min / 60.0);
            if (hr < 24) return hr + " hr";
            long days = (long)Math.Round(hr / 24.0);
            return days == 1 ? "a day" : days + " days";
        }
    }
}
