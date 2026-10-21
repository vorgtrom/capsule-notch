using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Capsule
{
    // One day of the heatmap.
    public sealed class HeatCell
    {
        public DateTime Day;
        public long Tokens;
        public int Level;          // 0 none, 1 to 4 by how busy the day was against the others shown
        public bool Today, Future;
        public string Tip = "";    // "Tue, Oct 14 · 1.2M tokens" and its biggest models
    }

    // A small figure under the heatmap: "12 days" over "Longest streak".
    public sealed class HeatStat
    {
        public string Value = "", Label = "";
    }

    // The Usage page's heatmap, like Claude Code's /stats: Weeks weeks of days, a column a week, shaded by tokens (no
    // cache reads), and the figures under it. Streaks count every day on record; the rest counts the weeks shown.
    // Built from a store snapshot; no WPF.
    public sealed class HeatModel
    {
        public const int Weeks = 26;
        public List<List<HeatCell>> Columns = new List<List<HeatCell>>();   // a week each, oldest first; 7 days from the week's first day
        public List<KeyValuePair<int, string>> Months = new List<KeyValuePair<int, string>>();   // the column where a month starts, and its name
        public List<string> DayNames = new List<string>();   // the rows' names, from the week's first day
        public List<HeatStat> Stats = new List<HeatStat>();

        public static HeatModel Build(UsageStore store, DateTime from, DateTime to, DateTime today, CultureInfo culture)
        {
            var m = new HeatModel();
            today = today.Date;
            var days = new List<HeatCell>();
            var perModel = new Dictionary<string, long>();
            for (DateTime d = from; d <= to; d = d.AddDays(1))
            {
                var c = new HeatCell { Day = d, Today = d == today, Future = d > today };
                var models = new List<KeyValuePair<string, long>>();
                foreach (var p in DayModels(store, d))
                {
                    c.Tokens += p.Value[0];
                    if (p.Value[0] > 0) models.Add(new KeyValuePair<string, long>(p.Key, p.Value[0]));
                    long t;
                    perModel.TryGetValue(p.Key, out t);
                    perModel[p.Key] = t + p.Value[0];
                }
                c.Tip = d.ToString("ddd, MMM d", culture) + " · " + (c.Tokens > 0 ? Format.Tokens(c.Tokens) + " tokens" : "no activity");
                foreach (var p in models.OrderByDescending(p => p.Value).Take(3)) c.Tip += "\n" + UsageModel.ModelName(p.Key) + " · " + Format.Tokens(p.Value);
                days.Add(c);
            }
            // Four shades, split at the quartiles of the busy days shown, as GitHub's and Claude Code's do.
            List<long> busy = days.Where(c => c.Tokens > 0).Select(c => c.Tokens).OrderBy(v => v).ToList();
            Func<double, long> q = f => busy.Count == 0 ? 0 : busy[Math.Min(busy.Count - 1, (int)Math.Floor(f * busy.Count))];
            long q1 = q(0.25), q2 = q(0.5), q3 = q(0.75);
            foreach (HeatCell c in days)
                c.Level = c.Tokens <= 0 ? 0 : c.Tokens <= q1 ? 1 : c.Tokens <= q2 ? 2 : c.Tokens <= q3 ? 3 : 4;
            for (int i = 0; i < days.Count; i += 7) m.Columns.Add(days.Skip(i).Take(7).ToList());
            int lastMonth = -1;
            for (int col = 0; col < m.Columns.Count; col++)
            {
                // A month is named over the first column that holds its 1st, or over the first column at all.
                HeatCell first = m.Columns[col].FirstOrDefault(c => c.Day.Day == 1) ?? (col == 0 ? m.Columns[col][0] : null);
                if (first == null || first.Day.Month == lastMonth) continue;
                lastMonth = first.Day.Month;
                m.Months.Add(new KeyValuePair<int, string>(col, first.Day.ToString("MMM", culture)));
            }
            if (m.Months.Count > 1 && m.Months[1].Key - m.Months[0].Key < 3) m.Months.RemoveAt(0);   // too close to fit both names
            for (int i = 0; i < 7; i++) m.DayNames.Add(culture.DateTimeFormat.GetAbbreviatedDayName((DayOfWeek)(((int)from.DayOfWeek + i) % 7)));

            // The figures.
            List<HeatCell> counted = days.Where(c => !c.Future).ToList();
            int active = counted.Count(c => c.Tokens > 0);
            m.Stats.Add(new HeatStat { Value = active > 0 ? Format.Tokens(days.Sum(c => c.Tokens) / active) : "–", Label = "Per active day" });   // the total is the page's big number
            m.Stats.Add(new HeatStat { Value = active + " of " + counted.Count, Label = "Active days" });
            int current, longest;
            Streaks(store, today, out current, out longest);
            m.Stats.Add(new HeatStat { Value = Days(current), Label = "Current streak" });
            m.Stats.Add(new HeatStat { Value = Days(longest), Label = "Longest streak" });
            HeatCell top = counted.OrderByDescending(c => c.Tokens).FirstOrDefault();
            m.Stats.Add(new HeatStat { Value = top != null && top.Tokens > 0 ? top.Day.ToString("MMM d", culture) : "–", Label = "Most active day" });
            string favourite = perModel.Where(p => p.Value > 0).OrderByDescending(p => p.Value).Select(p => p.Key).FirstOrDefault();
            m.Stats.Add(new HeatStat { Value = favourite != null ? UsageModel.ModelName(favourite) : "–", Label = "Favourite model" });
            return m;
        }

        static string Days(int n) { return n == 1 ? "1 day" : n + " days"; }

        // Runs of days in a row with any tokens, over everything on record. The current one ends today, or yesterday when
        // today has none yet.
        public static void Streaks(UsageStore store, DateTime today, out int current, out int longest)
        {
            var active = new HashSet<DateTime>();
            foreach (var day in store.Days)
            {
                DateTime d;
                if (day.Value.Values.Any(v => v[0] > 0) && DateTime.TryParseExact(day.Key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) active.Add(d.Date);
            }
            longest = 0;
            foreach (DateTime d in active)
            {
                if (active.Contains(d.AddDays(-1))) continue;   // not the start of a run
                int n = 1;
                while (active.Contains(d.AddDays(n))) n++;
                longest = Math.Max(longest, n);
            }
            DateTime end = active.Contains(today) ? today : today.AddDays(-1);
            current = 0;
            while (active.Contains(end.AddDays(-current))) current++;
        }

        static IEnumerable<KeyValuePair<string, long[]>> DayModels(UsageStore store, DateTime d)
        {
            Dictionary<string, long[]> models;
            return store.Days.TryGetValue(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), out models) ? models : Enumerable.Empty<KeyValuePair<string, long[]>>();
        }
    }
}
