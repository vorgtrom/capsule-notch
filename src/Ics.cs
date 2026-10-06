using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Capsule
{
    // One property line of an iCalendar file: NAME;PARAM=value:VALUE.
    public sealed class IcsProperty
    {
        public string Name = "";
        public Dictionary<string, string> Params = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string Value = "";

        public string Param(string name)
        {
            string v;
            return Params.TryGetValue(name, out v) ? v : null;
        }
    }

    // A BEGIN:…/END:… block: VCALENDAR, VEVENT, VTIMEZONE, STANDARD, DAYLIGHT, VALARM…
    public sealed class IcsComponent
    {
        public string Name = "";
        public List<IcsProperty> Properties = new List<IcsProperty>();
        public List<IcsComponent> Children = new List<IcsComponent>();

        public IcsProperty First(string name) { return Properties.FirstOrDefault(p => p.Name == name); }
        public string Value(string name)
        {
            IcsProperty p = First(name);
            return p != null ? p.Value : null;
        }
        public IEnumerable<IcsProperty> All(string name) { return Properties.Where(p => p.Name == name); }
    }

    // iCalendar (RFC 5545) text into components: lines unfolded, properties split into name, parameters and value.
    // Never throws: text that isn't a calendar gives null.
    public static class IcsParser
    {
        public static IcsComponent Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            try
            {
                var stack = new Stack<IcsComponent>();
                IcsComponent root = null;
                foreach (string line in Unfold(text))
                {
                    IcsProperty p = Property(line);
                    if (p == null) continue;
                    if (p.Name == "BEGIN")
                    {
                        var c = new IcsComponent { Name = p.Value.Trim().ToUpperInvariant() };
                        if (stack.Count > 0) stack.Peek().Children.Add(c);
                        else if (root == null) root = c;
                        else continue;   // a second calendar in one file: only the first is read
                        stack.Push(c);
                    }
                    else if (p.Name == "END")
                    {
                        if (stack.Count > 0) stack.Pop();
                    }
                    else if (stack.Count > 0) stack.Peek().Properties.Add(p);
                }
                return root != null && root.Name == "VCALENDAR" ? root : null;
            }
            catch (Exception) { return null; }
        }

        // Physical lines into logical ones: a line that starts with a space or a tab continues the one before.
        public static List<string> Unfold(string text)
        {
            var lines = new List<string>();
            var current = new StringBuilder();
            bool any = false;
            foreach (string raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                if (raw.Length > 0 && (raw[0] == ' ' || raw[0] == '\t'))
                {
                    current.Append(raw, 1, raw.Length - 1);
                    continue;
                }
                if (any) lines.Add(current.ToString());
                current.Clear();
                current.Append(raw);
                any = true;
            }
            if (any) lines.Add(current.ToString());
            return lines;
        }

        // NAME;P1=a;P2="b:c":VALUE. Null for a line that has no name or no colon.
        public static IcsProperty Property(string line)
        {
            int i = 0;
            while (i < line.Length && line[i] != ';' && line[i] != ':') i++;
            if (i == 0 || i >= line.Length) return null;
            var p = new IcsProperty { Name = line.Substring(0, i).Trim().ToUpperInvariant() };
            while (i < line.Length && line[i] == ';')
            {
                int nameStart = ++i;
                while (i < line.Length && line[i] != '=' && line[i] != ':' && line[i] != ';') i++;
                string name = line.Substring(nameStart, i - nameStart).Trim();
                var value = new StringBuilder();
                if (i < line.Length && line[i] == '=')
                {
                    i++;
                    bool quoted = false;
                    while (i < line.Length && (quoted || (line[i] != ';' && line[i] != ':')))
                    {
                        if (line[i] == '"') quoted = !quoted;
                        else value.Append(line[i]);
                        i++;
                    }
                }
                if (name != "") p.Params[name] = value.ToString();
            }
            if (i >= line.Length || line[i] != ':') return null;
            p.Value = line.Substring(i + 1);
            return p;
        }

        // TEXT values: \n becomes a space here (titles are one line), \, \; and \\ their characters.
        public static string Text(string value)
        {
            if (value == null) return null;
            var sb = new StringBuilder();
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '\\' && i + 1 < value.Length)
                {
                    char next = value[++i];
                    sb.Append(next == 'n' || next == 'N' ? ' ' : next);
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }

    // Local wall-clock time to UTC, for one time zone.
    public interface IIcsZone
    {
        DateTime ToUtc(DateTime local);   // local: Kind Unspecified; returns Kind Utc
    }

    public sealed class UtcZone : IIcsZone
    {
        public static readonly UtcZone Instance = new UtcZone();
        public DateTime ToUtc(DateTime local) { return DateTime.SpecifyKind(local, DateTimeKind.Utc); }
    }

    // A Windows time zone (the PC's own, or one the file names by its Windows id).
    public sealed class SystemZone : IIcsZone
    {
        readonly TimeZoneInfo zone;
        public SystemZone(TimeZoneInfo zone) { this.zone = zone; }

        public DateTime ToUtc(DateTime local)
        {
            DateTime t = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(t)) t = t.AddHours(1);   // skipped by the clocks going forward: the moment after
            return TimeZoneInfo.ConvertTimeToUtc(t, zone);
        }
    }

    // A VTIMEZONE: its STANDARD and DAYLIGHT observances, each starting at a local time and, usually, every year by a
    // rule ("the second Sunday of March"). Google's feeds name zones by IANA ids, which .NET Framework can't look up, but
    // they carry these definitions.
    public sealed class VTimeZone : IIcsZone
    {
        sealed class Observance
        {
            public DateTime Start;        // local, in the offset before it (TZOFFSETFROM)
            public TimeSpan From, To;
            public IcsRule Rule;          // null: only Start (and RDates)
            public List<DateTime> Dates = new List<DateTime>();
        }

        readonly List<Observance> observances = new List<Observance>();
        readonly Dictionary<int, List<KeyValuePair<DateTime, TimeSpan>>> years = new Dictionary<int, List<KeyValuePair<DateTime, TimeSpan>>>();   // by year: the onsets from the year before to the year after, in order, with the offset each brings

        public static VTimeZone From(IcsComponent component)
        {
            var z = new VTimeZone();
            foreach (IcsComponent c in component.Children)
            {
                if (c.Name != "STANDARD" && c.Name != "DAYLIGHT") continue;
                DateTime start;
                TimeSpan from, to;
                if (!Ics.LocalTime(c.Value("DTSTART"), out start) || !Offset(c.Value("TZOFFSETFROM"), out from) || !Offset(c.Value("TZOFFSETTO"), out to)) continue;
                var o = new Observance { Start = start, From = from, To = to };
                string rule = c.Value("RRULE");
                if (rule != null) o.Rule = IcsRule.Parse(rule);
                foreach (IcsProperty r in c.All("RDATE"))
                    foreach (string v in r.Value.Split(','))
                    {
                        DateTime d;
                        if (Ics.LocalTime(v, out d)) o.Dates.Add(d);
                    }
                z.observances.Add(o);
            }
            return z.observances.Count > 0 ? z : null;
        }

        // "+0100", "-0700", "+053000".
        public static bool Offset(string text, out TimeSpan offset)
        {
            offset = TimeSpan.Zero;
            if (text == null) return false;
            string t = text.Trim();
            if (t.Length < 5 || (t[0] != '+' && t[0] != '-')) return false;
            int h, m, s = 0;
            if (!int.TryParse(t.Substring(1, 2), NumberStyles.None, CultureInfo.InvariantCulture, out h)) return false;
            if (!int.TryParse(t.Substring(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out m)) return false;
            if (t.Length >= 7 && !int.TryParse(t.Substring(5, 2), NumberStyles.None, CultureInfo.InvariantCulture, out s)) return false;
            offset = new TimeSpan(h, m, s);
            if (t[0] == '-') offset = offset.Negate();
            return true;
        }

        // The offset in force at a local time: the one the latest onset at or before it brought. Before every onset, the
        // offset before the first observance.
        public TimeSpan OffsetAt(DateTime local)
        {
            List<KeyValuePair<DateTime, TimeSpan>> onsets;
            if (!years.TryGetValue(local.Year, out onsets))
            {
                onsets = new List<KeyValuePair<DateTime, TimeSpan>>();
                foreach (Observance o in observances)
                    foreach (DateTime onset in Onsets(o, local.Year)) onsets.Add(new KeyValuePair<DateTime, TimeSpan>(onset, o.To));
                onsets = onsets.OrderBy(x => x.Key).ToList();
                years[local.Year] = onsets;
            }
            for (int i = onsets.Count - 1; i >= 0; i--)
                if (onsets[i].Key <= local) return onsets[i].Value;
            DateTime earliest = DateTime.MaxValue;
            TimeSpan before = TimeSpan.Zero;
            foreach (Observance o in observances)
                if (o.Start < earliest)
                {
                    earliest = o.Start;
                    before = o.From;
                }
            return before;
        }

        // The observance's onsets from the year before the given one to the year after (and its first, if earlier).
        static IEnumerable<DateTime> Onsets(Observance o, int year)
        {
            if (o.Start.Year <= year + 1) yield return o.Start;
            foreach (DateTime d in o.Dates) yield return d;
            if (o.Rule == null || o.Rule.Unsupported || o.Start.Year > year + 1) yield break;
            DateTime from = o.Rule.Count >= 0 ? o.Start.Date : new DateTime(Math.Max(1, year - 1), 1, 1);   // a COUNT is counted from the first
            DateTime to = new DateTime(year + 1, 12, 31);
            int n = 0;
            foreach (DateTime day in o.Rule.Days(o.Start.Date, from, to))
            {
                DateTime onset = day + o.Start.TimeOfDay;
                if (!o.Rule.Allows(onset, onset - o.From)) yield break;   // UNTIL is in UTC: the local time less the offset before
                if (o.Rule.Count >= 0 && ++n > o.Rule.Count) yield break;
                yield return onset;
            }
        }

        public DateTime ToUtc(DateTime local)
        {
            return DateTime.SpecifyKind(local - OffsetAt(local), DateTimeKind.Utc);
        }
    }

    // A weekday in a BYDAY list, with its place in the month or year: 2TU (second Tuesday), -1FR (last Friday), MO (every
    // Monday, Ordinal 0).
    public struct IcsWeekday
    {
        public int Ordinal;
        public DayOfWeek Day;
    }

    // An RRULE, as far as Google Calendar writes them: DAILY, WEEKLY, MONTHLY and YEARLY, with INTERVAL, COUNT, UNTIL,
    // BYDAY (with places), BYMONTHDAY, BYMONTH, BYSETPOS (monthly) and WKST. Anything else (hourly rules, BYWEEKNO,
    // BYYEARDAY, BYHOUR…) marks it Unsupported, and the event then shows only its first occurrence.
    public sealed class IcsRule
    {
        public string Freq = "";
        public int Interval = 1;
        public int Count = -1;                 // -1: no COUNT
        public bool HasUntil;
        public DateTime Until;                 // local or UTC, as UntilUtc says
        public bool UntilUtc, UntilIsDate;
        public List<int> ByMonth = new List<int>();
        public List<int> ByMonthDay = new List<int>();
        public List<IcsWeekday> ByDay = new List<IcsWeekday>();
        public List<int> BySetPos = new List<int>();
        public DayOfWeek WeekStart = DayOfWeek.Monday;
        public bool Unsupported;

        static readonly string[] DayNames = { "SU", "MO", "TU", "WE", "TH", "FR", "SA" };

        public static IcsRule Parse(string value)
        {
            var r = new IcsRule();
            foreach (string part in (value ?? "").Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                string name = part.Substring(0, eq).Trim().ToUpperInvariant(), v = part.Substring(eq + 1).Trim().ToUpperInvariant();
                switch (name)
                {
                    case "FREQ": r.Freq = v; break;
                    case "INTERVAL": r.Interval = Math.Max(1, Int(v, 1)); break;
                    case "COUNT": r.Count = Math.Max(0, Int(v, 0)); break;
                    case "UNTIL":
                        DateTime until;
                        bool utc, date;
                        if (Ics.Moment(v, out until, out utc, out date))
                        {
                            r.HasUntil = true;
                            r.Until = until;
                            r.UntilUtc = utc;
                            r.UntilIsDate = date;
                        }
                        break;
                    case "BYMONTH": r.ByMonth = Ints(v); break;
                    case "BYMONTHDAY": r.ByMonthDay = Ints(v); break;
                    case "BYSETPOS": r.BySetPos = Ints(v); break;
                    case "BYDAY":
                        foreach (string d in v.Split(','))
                        {
                            string day = d.Trim();
                            if (day.Length < 2) { r.Unsupported = true; continue; }
                            int at = Array.IndexOf(DayNames, day.Substring(day.Length - 2));
                            if (at < 0) { r.Unsupported = true; continue; }
                            int ordinal = day.Length > 2 ? Int(day.Substring(0, day.Length - 2), int.MinValue) : 0;
                            if (ordinal == int.MinValue) { r.Unsupported = true; continue; }
                            r.ByDay.Add(new IcsWeekday { Ordinal = ordinal, Day = (DayOfWeek)at });
                        }
                        break;
                    case "WKST":
                        int w = Array.IndexOf(DayNames, v);
                        if (w >= 0) r.WeekStart = (DayOfWeek)w;
                        break;
                    default: r.Unsupported = true; break;   // BYHOUR, BYWEEKNO, BYYEARDAY, RSCALE…
                }
            }
            if (r.Freq != "DAILY" && r.Freq != "WEEKLY" && r.Freq != "MONTHLY" && r.Freq != "YEARLY") r.Unsupported = true;
            if (r.BySetPos.Count > 0 && r.Freq != "MONTHLY") r.Unsupported = true;
            if (r.Freq == "YEARLY" && r.ByMonth.Count == 0 && r.ByDay.Any(d => d.Ordinal != 0)) r.Unsupported = true;   // "the 20th Monday of the year"
            return r;
        }

        static int Int(string s, int fallback)
        {
            int n;
            return int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out n) ? n : fallback;
        }

        static List<int> Ints(string v)
        {
            var list = new List<int>();
            foreach (string s in v.Split(','))
            {
                int n = Int(s.Trim(), 0);
                if (n != 0) list.Add(n);
            }
            return list;
        }

        // True while an occurrence starting at this local time (whose UTC time is also given) is within UNTIL.
        public bool Allows(DateTime local, DateTime utc)
        {
            if (!HasUntil) return true;
            if (UntilIsDate) return local.Date <= Until.Date;
            return UntilUtc ? utc <= Until : local <= Until;
        }

        // The days from 'from' through 'to' (both dates) on which the rule, begun on 'start', has an occurrence, in order.
        // COUNT and UNTIL are the caller's: they depend on times. Days before 'from' aren't looked at, so a caller that
        // counts occurrences passes from = start.
        public IEnumerable<DateTime> Days(DateTime start, DateTime from, DateTime to)
        {
            if (Unsupported) yield break;
            for (DateTime d = from.Date < start.Date ? start.Date : from.Date; d <= to.Date; d = d.AddDays(1))
                if (Matches(d, start.Date)) yield return d;
        }

        public bool Matches(DateTime d, DateTime start)
        {
            if (d < start) return false;
            if (ByMonth.Count > 0 && !ByMonth.Contains(d.Month)) return false;
            switch (Freq)
            {
                case "DAILY":
                    if ((d - start).Days % Interval != 0) return false;
                    if (ByMonthDay.Count > 0 && !ByMonthDay.Any(md => IsMonthDay(d, md))) return false;
                    return ByDay.Count == 0 || ByDay.Any(w => w.Day == d.DayOfWeek);
                case "WEEKLY":
                    if (((WeekOf(d) - WeekOf(start)).Days / 7) % Interval != 0) return false;
                    if (ByDay.Count == 0) return d.DayOfWeek == start.DayOfWeek;
                    return ByDay.Any(w => w.Day == d.DayOfWeek);
                case "MONTHLY":
                    if (((d.Year - start.Year) * 12 + d.Month - start.Month) % Interval != 0) return false;
                    if (BySetPos.Count == 0) return InMonth(d, start);
                    var candidates = new List<DateTime>();
                    for (DateTime c = new DateTime(d.Year, d.Month, 1); c.Month == d.Month; c = c.AddDays(1))
                        if (InMonth(c, start)) candidates.Add(c);
                    foreach (int pos in BySetPos)
                    {
                        int i = pos > 0 ? pos - 1 : candidates.Count + pos;
                        if (i >= 0 && i < candidates.Count && candidates[i] == d) return true;
                    }
                    return false;
                case "YEARLY":
                    if ((d.Year - start.Year) % Interval != 0) return false;
                    if (ByMonth.Count == 0 && d.Month != start.Month) return false;
                    return InMonth(d, start);
            }
            return false;
        }

        // Within a month the rule picks: BYMONTHDAY and BYDAY together (both must hold), either alone, or the start's day.
        bool InMonth(DateTime d, DateTime start)
        {
            if (ByMonthDay.Count > 0 && !ByMonthDay.Any(md => IsMonthDay(d, md))) return false;
            if (ByDay.Count > 0) return ByDay.Any(w => IsWeekday(d, w));
            if (ByMonthDay.Count > 0) return true;
            return d.Day == start.Day;
        }

        static bool IsMonthDay(DateTime d, int md)
        {
            int days = DateTime.DaysInMonth(d.Year, d.Month);
            return md > 0 ? d.Day == md : d.Day == days + md + 1;
        }

        static bool IsWeekday(DateTime d, IcsWeekday w)
        {
            if (d.DayOfWeek != w.Day) return false;
            if (w.Ordinal == 0) return true;
            if (w.Ordinal > 0) return (d.Day - 1) / 7 + 1 == w.Ordinal;
            return (DateTime.DaysInMonth(d.Year, d.Month) - d.Day) / 7 + 1 == -w.Ordinal;
        }

        DateTime WeekOf(DateTime d)
        {
            int back = ((int)d.DayOfWeek - (int)WeekStart + 7) % 7;
            return d.AddDays(-back);
        }
    }

    // What a calendar feed gave for a span of time.
    public sealed class IcsResult
    {
        public List<CalendarEvent> Events = new List<CalendarEvent>();
        public string Name = "";        // X-WR-CALNAME: shown on screen only
        public int NotExpanded;         // repeating events whose rule Capsule can't follow: only their first occurrence shows
    }

    // A Google Calendar iCal feed (its secret address) into the events of a span of time, repeating events expanded,
    // without cancelled events and ones the calendar's owner declined. Pure; never throws (null for text that isn't a
    // calendar).
    public static class Ics
    {
        public const string CalendarId = "ical";
        const int MaxYears = 150;   // a rule begun longer ago than this isn't followed (a birthday from 1950 still is)

        public static IcsResult Read(string text, long fromMs, long toMs, TimeZoneInfo pcZone)
        {
            IcsComponent cal = IcsParser.Parse(text);
            if (cal == null) return null;
            try { return Expand(cal, fromMs, toMs, pcZone); }
            catch (Exception) { return null; }
        }

        static IcsResult Expand(IcsComponent cal, long fromMs, long toMs, TimeZoneInfo pcZone)
        {
            var result = new IcsResult { Name = IcsParser.Text(cal.Value("X-WR-CALNAME")) ?? "" };
            string owner = result.Name.Contains("@") ? result.Name.Trim().ToLowerInvariant() : null;
            var zones = new Dictionary<string, IIcsZone>(StringComparer.OrdinalIgnoreCase);
            foreach (IcsComponent c in cal.Children.Where(c => c.Name == "VTIMEZONE"))
            {
                string id = c.Value("TZID");
                VTimeZone z = VTimeZone.From(c);
                if (!string.IsNullOrEmpty(id) && z != null) zones[id.Trim()] = z;
            }
            IIcsZone floating = new SystemZone(pcZone);   // times with no zone are the PC's (RFC 5545 "floating")
            List<IcsComponent> events = cal.Children.Where(c => c.Name == "VEVENT").ToList();

            // Occurrences moved or cancelled one at a time, by UID: the original start they replace (UTC ms for timed events,
            // the day for all-day ones).
            var replaced = new Dictionary<string, HashSet<long>>();
            var replacedDays = new Dictionary<string, HashSet<DateTime>>();
            foreach (IcsComponent e in events)
            {
                IcsProperty rid = e.First("RECURRENCE-ID");
                string uid = e.Value("UID") ?? "";
                if (rid == null) continue;
                DateTime day;
                bool u, d;
                if (IsDate(rid) && Moment(rid.Value, out day, out u, out d))
                {
                    HashSet<DateTime> days;
                    if (!replacedDays.TryGetValue(uid, out days)) replacedDays[uid] = days = new HashSet<DateTime>();
                    days.Add(day.Date);
                    continue;
                }
                long at;
                if (!Instant(rid, zones, floating, out at)) continue;
                HashSet<long> set;
                if (!replaced.TryGetValue(uid, out set)) replaced[uid] = set = new HashSet<long>();
                set.Add(at);
            }

            foreach (IcsComponent e in events)
            {
                if ((e.Value("STATUS") ?? "").Trim().ToUpperInvariant() == "CANCELLED") continue;
                if (owner != null && Declined(e, owner)) continue;
                IcsProperty start = e.First("DTSTART");
                if (start == null) continue;
                string title = (IcsParser.Text(e.Value("SUMMARY")) ?? "").Trim();
                string uid = e.Value("UID") ?? "";
                HashSet<long> skip;
                replaced.TryGetValue(uid, out skip);
                HashSet<DateTime> skipDays;
                replacedDays.TryGetValue(uid, out skipDays);
                string rrule = e.Value("RRULE");
                bool isOverride = e.First("RECURRENCE-ID") != null;
                IcsRule rule = rrule != null && !isOverride ? IcsRule.Parse(rrule) : null;
                if (rule != null && rule.Unsupported)
                {
                    result.NotExpanded++;
                    rule = null;
                }
                if (IsDate(start)) AllDay(e, start, rule, title, uid, skipDays, fromMs, toMs, pcZone, result);
                else Timed(e, start, rule, title, uid, skip, isOverride, zones, floating, fromMs, toMs, result);
            }
            result.Events = result.Events.OrderBy(x => x.AllDay ? 0 : 1).ThenBy(x => x.StartMs).ThenBy(x => x.StartDay).ToList();
            return result;
        }

        static bool IsDate(IcsProperty p)
        {
            return string.Equals(p.Param("VALUE"), "DATE", StringComparison.OrdinalIgnoreCase) || p.Value.Trim().Length == 8;
        }

        // The owner is an attendee and answered no.
        static bool Declined(IcsComponent e, string owner)
        {
            foreach (IcsProperty a in e.All("ATTENDEE"))
            {
                string who = a.Value.Trim();
                if (who.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) who = who.Substring(7);
                if (who.Trim().ToLowerInvariant() == owner && string.Equals(a.Param("PARTSTAT"), "DECLINED", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        static void Timed(IcsComponent e, IcsProperty start, IcsRule rule, string title, string uid, HashSet<long> skip, bool isOverride,
            Dictionary<string, IIcsZone> zones, IIcsZone floating, long fromMs, long toMs, IcsResult result)
        {
            DateTime local;
            bool utc, date;
            if (!Moment(start.Value, out local, out utc, out date)) return;
            IIcsZone zone = ZoneOf(start, utc, zones, floating);
            long length = Length(e, local, zone, zones, floating);
            var exdates = new HashSet<long>();
            foreach (IcsProperty ex in e.All("EXDATE"))
                foreach (string v in ex.Value.Split(','))
                {
                    DateTime x;
                    bool xu, xd;
                    if (!Moment(v.Trim(), out x, out xu, out xd)) continue;
                    exdates.Add(Clock.ToMs(ZoneOf(ex, xu, zones, floating).ToUtc(xd ? x.Date + local.TimeOfDay : x)));
                }
            if (rule == null)
            {
                long s = Clock.ToMs(zone.ToUtc(local));
                if (s < toMs && s + length > fromMs) result.Events.Add(Timed(uid, title, s, s + length));
                return;
            }
            DateTime last = Clock.FromMs(toMs).AddDays(2);   // in any zone, the last local day the span can reach
            DateTime first = Clock.FromMs(fromMs - length).AddDays(-2);
            if (last.Year - local.Year > MaxYears) return;
            int n = 0;
            foreach (DateTime day in rule.Days(local, local, last))
            {
                DateTime occurrence = day + local.TimeOfDay;
                DateTime occurrenceUtc = zone.ToUtc(occurrence);
                if (!rule.Allows(occurrence, occurrenceUtc)) break;
                if (rule.Count >= 0 && ++n > rule.Count) break;
                if (day < first.Date) continue;
                long s = Clock.ToMs(occurrenceUtc);
                if (exdates.Contains(s) || (skip != null && skip.Contains(s))) continue;
                if (s < toMs && s + length > fromMs) result.Events.Add(Timed(uid, title, s, s + length));
            }
        }

        static CalendarEvent Timed(string uid, string title, long start, long end)
        {
            return new CalendarEvent { Id = uid, CalendarId = CalendarId, Title = title != "" ? title : GoogleCalendarClient.NoTitle, StartMs = start, EndMs = end };
        }

        static void AllDay(IcsComponent e, IcsProperty start, IcsRule rule, string title, string uid, HashSet<DateTime> skipDays, long fromMs, long toMs, TimeZoneInfo pcZone, IcsResult result)
        {
            DateTime day, end;
            bool utc, date;
            if (!Moment(start.Value, out day, out utc, out date)) return;
            day = day.Date;
            int span = 1;
            IcsProperty dtend = e.First("DTEND");
            if (dtend != null && Moment(dtend.Value, out end, out utc, out date) && end.Date > day) span = (end.Date - day).Days;
            else
            {
                TimeSpan d;
                if (Duration(e.Value("DURATION"), out d) && d.TotalDays >= 1) span = (int)d.TotalDays;
            }
            var exdays = new HashSet<DateTime>();
            foreach (IcsProperty ex in e.All("EXDATE"))
                foreach (string v in ex.Value.Split(','))
                {
                    DateTime x;
                    bool xu, xd;
                    if (Moment(v.Trim(), out x, out xu, out xd)) exdays.Add(x.Date);
                }
            // The span's local days: from the day it starts on to the day its last moment is on (toMs is exclusive).
            DateTime firstDay = CalendarDay.LocalDay(fromMs, pcZone), lastDay = CalendarDay.LocalDay(toMs - 1, pcZone);
            if (rule == null)
            {
                if (day <= lastDay && day.AddDays(span) > firstDay) result.Events.Add(AllDayEvent(uid, title, day, span));
                return;
            }
            if (lastDay.Year - day.Year > MaxYears) return;
            int n = 0;
            foreach (DateTime d in rule.Days(day, day, lastDay))
            {
                if (!rule.Allows(d, DateTime.SpecifyKind(d, DateTimeKind.Utc))) break;
                if (rule.Count >= 0 && ++n > rule.Count) break;
                if (d.AddDays(span) <= firstDay || exdays.Contains(d) || (skipDays != null && skipDays.Contains(d))) continue;
                result.Events.Add(AllDayEvent(uid, title, d, span));
            }
        }

        static CalendarEvent AllDayEvent(string uid, string title, DateTime day, int span)
        {
            return new CalendarEvent { Id = uid, CalendarId = CalendarId, Title = title != "" ? title : GoogleCalendarClient.NoTitle, AllDay = true, StartDay = day, EndDay = day.AddDays(span) };
        }

        // How long a timed event lasts: DTEND (in its own zone, or the start's) less DTSTART, or DURATION; neither: no length.
        static long Length(IcsComponent e, DateTime start, IIcsZone startZone, Dictionary<string, IIcsZone> zones, IIcsZone floating)
        {
            IcsProperty end = e.First("DTEND");
            DateTime local;
            bool utc, date;
            if (end != null && Moment(end.Value, out local, out utc, out date))
            {
                IIcsZone endZone = utc || end.Param("TZID") != null ? ZoneOf(end, utc, zones, floating) : startZone;
                return Math.Max(0, Clock.ToMs(endZone.ToUtc(local)) - Clock.ToMs(startZone.ToUtc(start)));
            }
            TimeSpan d;
            return Duration(e.Value("DURATION"), out d) ? (long)Math.Max(0, d.TotalMilliseconds) : 0;
        }

        // UTC for a time ending in Z; the file's VTIMEZONE for a TZID it defines, or a Windows zone by that id; otherwise
        // (no zone, or one Capsule can't find) the PC's own.
        static IIcsZone ZoneOf(IcsProperty p, bool utc, Dictionary<string, IIcsZone> zones, IIcsZone floating)
        {
            if (utc) return UtcZone.Instance;
            string id = p.Param("TZID");
            if (string.IsNullOrEmpty(id)) return floating;
            IIcsZone z;
            if (zones.TryGetValue(id.Trim(), out z)) return z;
            try { return new SystemZone(TimeZoneInfo.FindSystemTimeZoneById(id.Trim())); }
            catch (Exception) { return floating; }
        }

        // A RECURRENCE-ID as the UTC ms of the occurrence it replaces.
        static bool Instant(IcsProperty p, Dictionary<string, IIcsZone> zones, IIcsZone floating, out long ms)
        {
            ms = 0;
            DateTime local;
            bool utc, date;
            if (!Moment(p.Value, out local, out utc, out date)) return false;
            ms = Clock.ToMs(ZoneOf(p, utc, zones, floating).ToUtc(local));
            return true;
        }

        // "20261006T090000Z", "20261006T090000" or "20261006". utc: it ended in Z; date: it had no time.
        public static bool Moment(string text, out DateTime value, out bool utc, out bool date)
        {
            value = DateTime.MinValue;
            utc = false;
            date = false;
            if (string.IsNullOrEmpty(text)) return false;
            string t = text.Trim();
            if (t.EndsWith("Z", StringComparison.OrdinalIgnoreCase))
            {
                utc = true;
                t = t.Substring(0, t.Length - 1);
            }
            if (t.Length == 8)
            {
                date = true;
                return DateTime.TryParseExact(t, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
            }
            return DateTime.TryParseExact(t, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
        }

        // A local time without a zone (VTIMEZONE's DTSTART).
        public static bool LocalTime(string text, out DateTime value)
        {
            bool utc, date;
            return Moment(text, out value, out utc, out date);
        }

        // "PT1H30M", "P1D", "P2W", "-PT15M".
        public static bool Duration(string text, out TimeSpan value)
        {
            value = TimeSpan.Zero;
            if (string.IsNullOrEmpty(text)) return false;
            string t = text.Trim().ToUpperInvariant();
            bool negative = t.StartsWith("-");
            if (t.StartsWith("+") || negative) t = t.Substring(1);
            if (!t.StartsWith("P")) return false;
            bool time = false;
            int number = -1;
            for (int i = 1; i < t.Length; i++)
            {
                char c = t[i];
                if (c == 'T') { time = true; continue; }
                if (char.IsDigit(c))
                {
                    number = (number < 0 ? 0 : number) * 10 + (c - '0');
                    continue;
                }
                if (number < 0) return false;
                if (c == 'W') value += TimeSpan.FromDays(7 * number);
                else if (c == 'D') value += TimeSpan.FromDays(number);
                else if (c == 'H' && time) value += TimeSpan.FromHours(number);
                else if (c == 'M' && time) value += TimeSpan.FromMinutes(number);
                else if (c == 'S' && time) value += TimeSpan.FromSeconds(number);
                else return false;
                number = -1;
            }
            if (negative) value = value.Negate();
            return true;
        }
    }

    // The address Capsule fetches a calendar from: a Google calendar's iCal address (its secret one, usually). Only https on
    // calendar.google.com under /calendar/ical/, ending in .ics: Capsule connects nowhere else for it. The address is a
    // secret (anyone with it can read the calendar): it is kept encrypted, and never logged or shown.
    public static class IcsLink
    {
        public const string Host = "calendar.google.com";

        // The address as Capsule keeps and fetches it, or null when it isn't one. webcal:// is taken as https://.
        public static string Normalize(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string t = text.Trim();
            if (t.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)) t = "https://" + t.Substring(9);
            Uri u;
            if (!Uri.TryCreate(t, UriKind.Absolute, out u)) return null;
            if (u.Scheme != Uri.UriSchemeHttps || u.Host.ToLowerInvariant() != Host || !u.IsDefaultPort || u.UserInfo != "") return null;
            string path = u.AbsolutePath;
            if (!path.StartsWith("/calendar/ical/", StringComparison.Ordinal) || !path.EndsWith(".ics", StringComparison.OrdinalIgnoreCase)) return null;
            return u.GetLeftPart(UriPartial.Path);
        }
    }
}
