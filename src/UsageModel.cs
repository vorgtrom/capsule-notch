using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Capsule
{
    public enum UsagePeriod { Week, Month, Year, Heat }   // Heat: the heatmap's HeatModel.Weeks weeks, the page's view

    public sealed class UsageSegment
    {
        public string Model = "";   // the legend name
        public string Color = "";
        public long Tokens;
    }

    public sealed class UsageBar
    {
        public string Label = "";
        public string Tip = "";        // "Thu, Oct 9 · 410K" and a line per model
        public bool IsToday, Future;
        public long Total;
        public List<UsageSegment> Segments = new List<UsageSegment>();   // bottom to top
    }

    public sealed class LegendRow
    {
        public string Name = "", Color = "";
        public long Tokens;
    }

    public sealed class PaceLine
    {
        public string Text = "";
        public bool Warn;   // amber: full before the reset
    }

    // What the Usage page draws for one period (spec: usage history §3). Built from a store snapshot; no WPF.
    public sealed class UsageModel
    {
        public const int LegendMax = 6;
        public const string OtherName = "Other";

        public UsagePeriod Period;
        public string Title = "";
        public bool CanNext;
        public long Total;
        public List<UsageBar> Bars = new List<UsageBar>();
        public List<LegendRow> Legend = new List<LegendRow>();
        public List<string> Details = new List<string>();
        public List<PaceLine> Pace = new List<PaceLine>();
        public string Progress = "";   // "Reading your logs… 40%" during the first backfill
        public bool Empty;              // no activity counted at all
        public HeatModel Heat;          // the heatmap, for UsagePeriod.Heat; null for the bar periods

        // offset: 0 for the current period, -1 for the one before, and so on.
        public static UsageModel Build(UsageStore store, UsagePeriod period, int offset, DateTime today, CultureInfo culture, bool dark)
        {
            var m = new UsageModel { Period = period, CanNext = offset < 0 };
            today = today.Date;
            DateTime from, to;   // inclusive days
            Range(period, offset, today, culture, out from, out to);
            m.Title = PeriodTitle(period, from, to, culture);
            m.Empty = store.Days.Count == 0;
            if (!store.BackfillDone) m.Progress = "Reading your logs… " + (int)Math.Floor(store.Progress * 100) + "%";

            // Per-model totals over the period decide the legend and its colours.
            var perModel = new Dictionary<string, long>();
            var perTool = new Dictionary<string, long[]>();
            long busiest = 0;
            DateTime busiestDay = from;
            for (DateTime d = from; d <= to; d = d.AddDays(1))
            {
                long dayTotal = 0;
                foreach (var p in DayModels(store, d))
                {
                    long t;
                    perModel.TryGetValue(p.Key, out t);
                    perModel[p.Key] = t + p.Value[0];
                    string tool = IsClaude(p.Key) ? "Claude" : "Codex";
                    long[] tt;
                    if (!perTool.TryGetValue(tool, out tt)) perTool[tool] = tt = new long[2];
                    tt[0] += p.Value[0];
                    tt[1] += p.Value[1];
                    dayTotal += p.Value[0];
                }
                if (dayTotal > busiest) { busiest = dayTotal; busiestDay = d; }
            }
            m.Total = perModel.Values.Sum();
            List<string> ordered = perModel.Where(p => p.Value > 0).OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key).ToList();
            List<string> shown = ordered.Take(ordered.Count > LegendMax ? LegendMax - 1 : LegendMax).ToList();
            Dictionary<string, string> colors = Colors(shown, dark);
            foreach (string model in shown) m.Legend.Add(new LegendRow { Name = ModelName(model), Color = colors[model], Tokens = perModel[model] });
            if (ordered.Count > shown.Count)
                m.Legend.Add(new LegendRow { Name = OtherName, Color = Palette.Secondary, Tokens = ordered.Skip(shown.Count).Sum(k => perModel[k]) });

            // Bars: a day each for a week or a month, a month each for a year. The heatmap has its own cells instead.
            if (period == UsagePeriod.Heat) m.Heat = HeatModel.Build(store, from, to, today, culture);
            else if (period == UsagePeriod.Year)
                for (int i = 0; i < 12; i++)
                {
                    DateTime first = new DateTime(from.Year, i + 1, 1);
                    DateTime last = first.AddMonths(1).AddDays(-1);
                    UsageBar bar = MakeBar(store, first, last, shown, colors, culture);
                    bar.Label = first.ToString("MMM", culture);
                    bar.Future = first > today;
                    bar.IsToday = first.Year == today.Year && first.Month == today.Month;
                    bar.Tip = first.ToString("MMMM yyyy", culture) + " · " + Format.Tokens(bar.Total) + bar.Tip;
                    m.Bars.Add(bar);
                }
            else
                for (DateTime d = from; d <= to; d = d.AddDays(1))
                {
                    UsageBar bar = MakeBar(store, d, d, shown, colors, culture);
                    bar.Label = period == UsagePeriod.Week ? d.ToString("ddd", culture) : (d.Day % 7 == 1 ? d.Day.ToString(culture) : "");
                    bar.Future = d > today;
                    bar.IsToday = d == today;
                    bar.Tip = d.ToString("ddd, MMM d", culture) + " · " + Format.Tokens(bar.Total) + bar.Tip;
                    m.Bars.Add(bar);
                }

            foreach (string tool in new[] { "Claude", "Codex" })
            {
                long[] t;
                if (perTool.TryGetValue(tool, out t) && t[0] + t[1] > 0)
                    m.Details.Add(tool + " " + Format.Tokens(t[0]) + " · " + Format.Tokens(t[1]) + (tool == "Claude" ? " cache reads" : " cached"));
            }
            if (busiest > 0) m.Details.Add("Busiest day: " + busiestDay.ToString("ddd, MMM d", culture) + " · " + Format.Tokens(busiest));
            DateTime lastCounted = to < today ? to : today;
            int elapsed = Math.Max(1, (int)(lastCounted - from).TotalDays + 1);
            if (m.Total > 0) m.Details.Add("Daily average: " + Format.Tokens(m.Total / elapsed));
            if (store.Days.Count > 0)
            {
                DateTime since;
                if (DateTime.TryParseExact(store.Days.Keys.First(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out since))
                    m.Details.Add("Counted from Claude Code and Codex logs on this PC since " + since.ToString("MMM d", culture) + (since.Year != today.Year ? since.ToString(", yyyy", culture) : "") + ".");
            }
            return m;
        }

        static UsageBar MakeBar(UsageStore store, DateTime first, DateTime last, List<string> shown, Dictionary<string, string> colors, CultureInfo culture)
        {
            var sums = new Dictionary<string, long>();
            for (DateTime d = first; d <= last; d = d.AddDays(1))
                foreach (var p in DayModels(store, d))
                {
                    string key = shown.Contains(p.Key) ? p.Key : OtherName;
                    long t;
                    sums.TryGetValue(key, out t);
                    sums[key] = t + p.Value[0];
                }
            var bar = new UsageBar();
            foreach (string model in shown.Concat(new[] { OtherName }))
            {
                long t;
                if (!sums.TryGetValue(model, out t) || t <= 0) continue;
                string name = model == OtherName ? OtherName : ModelName(model);
                bar.Segments.Add(new UsageSegment { Model = name, Color = model == OtherName ? Palette.Secondary : colors[model], Tokens = t });
                bar.Total += t;
                bar.Tip += "\n" + name + " · " + Format.Tokens(t);
            }
            return bar;
        }

        static IEnumerable<KeyValuePair<string, long[]>> DayModels(UsageStore store, DateTime d)
        {
            Dictionary<string, long[]> models;
            return store.Days.TryGetValue(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), out models) ? models : Enumerable.Empty<KeyValuePair<string, long[]>>();
        }

        public static void Range(UsagePeriod period, int offset, DateTime today, CultureInfo culture, out DateTime from, out DateTime to)
        {
            switch (period)
            {
                case UsagePeriod.Week:
                    int back = ((int)today.DayOfWeek - (int)culture.DateTimeFormat.FirstDayOfWeek + 7) % 7;
                    from = today.AddDays(-back + 7 * offset);
                    to = from.AddDays(6);
                    break;
                case UsagePeriod.Heat:
                    // Whole weeks, the last one this week; each step back is the weeks before.
                    int lead = ((int)today.DayOfWeek - (int)culture.DateTimeFormat.FirstDayOfWeek + 7) % 7;
                    to = today.AddDays(6 - lead + 7 * HeatModel.Weeks * offset);
                    from = to.AddDays(-7 * HeatModel.Weeks + 1);
                    break;
                case UsagePeriod.Month:
                    from = new DateTime(today.Year, today.Month, 1).AddMonths(offset);
                    to = from.AddMonths(1).AddDays(-1);
                    break;
                default:
                    from = new DateTime(today.Year + offset, 1, 1);
                    to = new DateTime(today.Year + offset, 12, 31);
                    break;
            }
        }

        static string PeriodTitle(UsagePeriod period, DateTime from, DateTime to, CultureInfo culture)
        {
            if (period == UsagePeriod.Year) return from.Year.ToString(culture);
            if (period == UsagePeriod.Heat) return from.ToString("MMM d", culture) + (from.Year != to.Year ? from.ToString(", yyyy", culture) : "") + " – " + to.ToString("MMM d, yyyy", culture);
            if (period == UsagePeriod.Month) return from.ToString("MMMM yyyy", culture);
            return from.Month == to.Month
                ? from.ToString("MMM d", culture) + " – " + to.Day.ToString(culture)
                : from.ToString("MMM d", culture) + " – " + to.ToString("MMM d", culture);
        }

        public static bool IsClaude(string model) { return model.StartsWith("claude", StringComparison.OrdinalIgnoreCase); }

        // "claude-opus-4-1-20250805" -> "Opus 4.1", "claude-3-5-sonnet-20241022" -> "Sonnet 3.5", "gpt-5-codex" -> "GPT-5 Codex".
        public static string ModelName(string model)
        {
            if (string.IsNullOrEmpty(model)) return "Unknown";
            string m = model.ToLowerInvariant();
            if (m == "codex") return "Codex";
            string[] parts = m.Split('-');
            if (parts[0] == "claude" && parts.Length > 1)
            {
                var nums = new List<string>();
                string family = null;
                for (int i = 1; i < parts.Length; i++)
                {
                    string p = parts[i];
                    if (p.Length == 8 && p.All(char.IsDigit)) continue;   // a date
                    if (p.Length > 0 && p.All(char.IsDigit)) nums.Add(p);
                    else if (family == null && p.Length > 0) family = p;
                }
                if (family == null) return model;
                string name = char.ToUpperInvariant(family[0]) + family.Substring(1);
                return nums.Count == 0 ? name : name + " " + string.Join(".", nums);
            }
            if (parts[0] == "gpt" && parts.Length > 1)
            {
                string name = "GPT-" + parts[1];
                for (int i = 2; i < parts.Length; i++)
                    if (parts[i].Length > 0) name += " " + char.ToUpperInvariant(parts[i][0]) + parts[i].Substring(1);
                return name;
            }
            return model;
        }

        static readonly string[] Fallback = { "#FF375F", "#FFD60A", "#AC8E68", "#64D2FF" };

        static string Family(string model)
        {
            string m = model.ToLowerInvariant();
            if (m.Contains("opus")) return "opus";
            if (m.Contains("sonnet")) return "sonnet";
            if (m.Contains("haiku")) return "haiku";
            if (m.Contains("codex")) return "codex";
            if (m.StartsWith("gpt", StringComparison.Ordinal)) return "gpt";
            return null;
        }

        static string Base(string family)
        {
            switch (family)
            {
                case "opus": return "#0A84FF";
                case "sonnet": return "#BF5AF2";
                case "haiku": return "#40C8E0";
                case "codex": return "#FF9F0A";
                case "gpt": return "#30D158";
                default: return null;
            }
        }

        // A colour per model: by family, newest version in the base colour, older ones 18% lighter (dark theme) or darker
        // (light theme) per step; unknown families from a fixed list in order of appearance.
        public static Dictionary<string, string> Colors(List<string> models, bool dark)
        {
            var colors = new Dictionary<string, string>();
            int next = 0;
            foreach (var group in models.GroupBy(Family))
            {
                if (group.Key == null)
                {
                    foreach (string model in group) colors[model] = Fallback[next++ % Fallback.Length];
                    continue;
                }
                int step = 0;
                foreach (string model in group.OrderByDescending(VersionOf).ThenBy(x => x, StringComparer.Ordinal))
                    colors[model] = Shade(Base(group.Key), step++ * 0.18, dark);
            }
            return colors;
        }

        static double VersionOf(string model)
        {
            string name = ModelName(model);
            int space = name.LastIndexOf(' ');
            double v;
            string tail = space >= 0 ? name.Substring(space + 1) : name;
            if (tail.StartsWith("GPT-", StringComparison.Ordinal)) tail = tail.Substring(4);
            return double.TryParse(tail, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        // Mixes a #RRGGBB colour toward white (dark theme) or black (light theme).
        public static string Shade(string hex, double amount, bool dark)
        {
            if (amount <= 0) return hex;
            amount = Math.Min(0.8, amount);
            int r = Convert.ToInt32(hex.Substring(1, 2), 16), g = Convert.ToInt32(hex.Substring(3, 2), 16), b = Convert.ToInt32(hex.Substring(5, 2), 16);
            Func<int, int> mix = c => (int)Math.Round(dark ? c + (255 - c) * amount : c * (1 - amount));
            return "#" + mix(r).ToString("X2") + mix(g).ToString("X2") + mix(b).ToString("X2");
        }
    }
}
