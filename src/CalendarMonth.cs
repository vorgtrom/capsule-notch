using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Capsule
{
    // One day of the month page's grid.
    public sealed class MonthDay
    {
        public DateTime Date;
        public bool InMonth;      // false: a day of the month before or after, dimmed
        public bool Today, Selected;
        public List<string> Dots = new List<string>();   // up to three colours, one per event or task
        public bool More;         // there are more than three
    }

    // A task on the selected day, with its check box.
    public sealed class MonthTask
    {
        public string Id = "";
        public string ListId = "";
        public string Title = "";
        public bool Done;
    }

    // Everything the month page shows (month spec §2), as plain text. Built here; drawn by MonthView.
    public sealed class MonthModel
    {
        public int Year, Month;
        public string Title = "";                         // "October 2026"
        public List<string> Weekdays = new List<string>(); // seven, from the culture's first day of the week
        public List<MonthDay> Days = new List<MonthDay>(); // 42: six weeks
        public DateTime Selected;
        public string SelectedTitle = "";                 // "Tue 6 Oct"
        public List<CalendarRow> Events = new List<CalendarRow>();
        public List<MonthTask> Tasks = new List<MonthTask>();
        public string Empty = "";                         // said when the day has nothing
        public bool CanAdd;                               // signed in with the permissions to add events and tasks
        public bool ShowTasks;                            // signed in with Tasks: the day lists tasks and offers Add task
        public string Note = "";                          // what went wrong, or that a sign-in is needed to add
        public bool Loading;                              // the month hasn't been read yet
        public List<CalendarChoice> Calendars = new List<CalendarChoice>();   // the ones events can be added to, primary first
        public List<TaskList> Lists = new List<TaskList>();
        public bool AllDayDefault;                        // the event form starts all-day for a day other than today
        public string StartText = "", EndText = "";       // and with these times
    }

    // The month page (month spec §2): the six weeks of a month, a selected day's events and tasks, and the add forms'
    // times, checks and request bodies. Pure: every function takes the culture, the zone and the moment.
    public static class CalendarMonth
    {
        public const int Weeks = 6, MaxDots = 3, MaxTitle = 1000;
        public const string TaskDot = "#8E8E93";
        public const string TitleNeeded = "Give it a title";
        public const string EndBeforeStart = "The end has to be after the start";

        // The grid's first day: the culture's first day of the week on or before the 1st.
        public static DateTime FirstCell(int year, int month, CultureInfo culture)
        {
            var first = new DateTime(year, month, 1);
            int back = ((int)first.DayOfWeek - (int)culture.DateTimeFormat.FirstDayOfWeek + 7) % 7;
            return first.AddDays(-back);
        }

        // The span the page reads, local midnight to local midnight: its six weeks.
        public static long SpanStartMs(int year, int month, CultureInfo culture, TimeZoneInfo zone) { return CalendarDay.DayStartMs(FirstCell(year, month, culture), zone); }
        public static long SpanEndMs(int year, int month, CultureInfo culture, TimeZoneInfo zone) { return CalendarDay.DayStartMs(FirstCell(year, month, culture).AddDays(7 * Weeks), zone); }

        // The local days an event is on: an all-day one's dates; a timed one's days from its start to its last moment.
        public static IEnumerable<DateTime> DaysOf(CalendarEvent e, TimeZoneInfo zone)
        {
            DateTime first = e.AllDay ? e.StartDay.Date : CalendarDay.LocalDay(e.StartMs, zone);
            DateTime last = e.AllDay ? e.EndDay.Date.AddDays(-1) : CalendarDay.LocalDay(Math.Max(e.StartMs, e.EndMs - 1), zone);
            for (DateTime d = first; d <= last && (d - first).TotalDays < 400; d = d.AddDays(1)) yield return d;
        }

        public static MonthModel Build(int year, int month, DateTime selected, IList<CalendarEvent> events, IList<GoogleTask> tasks, long now, TimeZoneInfo zone, CultureInfo culture)
        {
            var m = new MonthModel { Year = year, Month = month, Selected = selected.Date };
            DateTimeFormatInfo f = culture.DateTimeFormat;
            m.Title = new DateTime(year, month, 1).ToString(f.YearMonthPattern, culture);
            for (int i = 0; i < 7; i++) m.Weekdays.Add(f.AbbreviatedDayNames[((int)f.FirstDayOfWeek + i) % 7]);
            DateTime today = CalendarDay.LocalDay(now, zone);
            DateTime start = FirstCell(year, month, culture);
            var byDay = new Dictionary<DateTime, List<string>>();
            foreach (CalendarEvent e in events.OrderBy(x => x.AllDay ? 0 : 1).ThenBy(x => x.StartMs))
                foreach (DateTime d in DaysOf(e, zone)) Add(byDay, d, e.Color);
            foreach (GoogleTask t in tasks) Add(byDay, t.Due.Date, TaskDot);
            for (int i = 0; i < 7 * Weeks; i++)
            {
                DateTime d = start.AddDays(i);
                var day = new MonthDay { Date = d, InMonth = d.Month == month, Today = d == today, Selected = d == m.Selected };
                List<string> dots;
                if (byDay.TryGetValue(d, out dots))
                {
                    day.Dots = dots.Take(MaxDots).ToList();
                    day.More = dots.Count > MaxDots;
                }
                m.Days.Add(day);
            }
            m.SelectedTitle = m.Selected.ToString("ddd d MMM", culture);
            m.AllDayDefault = m.Selected != today;
            TimeSpan from = DefaultStart(m.Selected, CalendarDay.Local(now, zone));
            m.StartText = TimeText(from, culture);
            m.EndText = TimeText(DefaultEnd(from), culture);
            DayRows(m, events, tasks, now, zone, culture);
            m.Empty = "Nothing on this day";
            return m;
        }

        static void Add(Dictionary<DateTime, List<string>> byDay, DateTime d, string color)
        {
            List<string> list;
            if (!byDay.TryGetValue(d, out list)) byDay[d] = list = new List<string>();
            list.Add(color);
        }

        // The selected day: all-day events, then timed ones on it (the one happening now marked), then its tasks, not done first.
        static void DayRows(MonthModel m, IList<CalendarEvent> events, IList<GoogleTask> tasks, long now, TimeZoneInfo zone, CultureInfo culture)
        {
            DateTime day = m.Selected;
            long dayStart = CalendarDay.DayStartMs(day, zone), dayEnd = CalendarDay.DayStartMs(day.AddDays(1), zone);
            CalendarEvent current = CalendarDay.Current(events, now);
            foreach (CalendarEvent e in events.Where(x => x.AllDay && x.StartDay <= day && day < x.EndDay).OrderBy(x => x.Title, StringComparer.CurrentCulture))
            {
                // The form edits one day: an event over several can only be deleted from here.
                bool oneDay = e.EndDay == e.StartDay.AddDays(1);
                m.Events.Add(new CalendarRow { Time = "All day", Title = e.Title, Color = e.Color, Id = e.Id, CalendarId = e.CalendarId, AllDay = true,
                    CanEdit = e.Editable && oneDay, CanDelete = e.Editable, StartText = m.StartText, EndText = m.EndText });
            }
            foreach (CalendarEvent e in events.Where(x => !x.AllDay && x.StartMs < dayEnd && x.EndMs > dayStart || (!x.AllDay && x.StartMs == x.EndMs && x.StartMs >= dayStart && x.StartMs < dayEnd)).OrderBy(x => x.StartMs))
            {
                DateTime from = CalendarDay.Local(e.StartMs, zone), to = CalendarDay.Local(e.EndMs, zone);
                bool oneDay = from.Date == day && to.Date == day;   // within the selected day, as the form's times are
                m.Events.Add(new CalendarRow { Time = CalendarDay.Range(e, zone, culture), Title = e.Title, Color = e.Color, Current = e == current,
                    Id = e.Id, CalendarId = e.CalendarId, CanEdit = e.Editable && oneDay && e.EndMs > e.StartMs, CanDelete = e.Editable,
                    StartText = TimeText(from.TimeOfDay, culture), EndText = TimeText(to.TimeOfDay, culture) });
            }
            foreach (GoogleTask t in tasks.Where(x => x.Due.Date == day).OrderBy(x => x.Done))
                m.Tasks.Add(new MonthTask { Id = t.Id, ListId = t.ListId, Title = t.Title, Done = t.Done });
        }

        // ---- The add forms ----

        // A time as typed: the culture's short time ("2:30 PM", "14:30"), or short forms: "2:30p", "2pm", "14", "9.15".
        public static bool ParseTime(string text, CultureInfo culture, out TimeSpan time)
        {
            time = TimeSpan.Zero;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string t = text.Trim();
            DateTime parsed;
            if (DateTime.TryParseExact(t, culture.DateTimeFormat.ShortTimePattern, culture, DateTimeStyles.AllowWhiteSpaces, out parsed)
                || DateTime.TryParseExact(t, culture.DateTimeFormat.LongTimePattern, culture, DateTimeStyles.AllowWhiteSpaces, out parsed))
            {
                time = parsed.TimeOfDay;
                return true;
            }
            string am = (culture.DateTimeFormat.AMDesignator ?? "").ToLowerInvariant(), pm = (culture.DateTimeFormat.PMDesignator ?? "").ToLowerInvariant();
            string lower = t.ToLowerInvariant();
            int half = 0;   // 1: AM, 2: PM
            foreach (string mark in new[] { pm, "pm", "p.m.", "p" })
                if (mark != "" && lower.EndsWith(mark)) { half = 2; lower = lower.Substring(0, lower.Length - mark.Length); break; }
            if (half == 0)
                foreach (string mark in new[] { am, "am", "a.m.", "a" })
                    if (mark != "" && lower.EndsWith(mark)) { half = 1; lower = lower.Substring(0, lower.Length - mark.Length); break; }
            Match m = Regex.Match(lower.Trim(), @"^(\d{1,2})(?:[:.](\d{2}))?$");
            if (!m.Success) return false;
            int h = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            int min = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
            if (min > 59) return false;
            if (half != 0)
            {
                if (h < 1 || h > 12) return false;
                if (half == 1 && h == 12) h = 0;
                if (half == 2 && h != 12) h += 12;
            }
            if (h > 23) return false;
            time = new TimeSpan(h, min, 0);
            return true;
        }

        // A time as the form shows it: the culture's short time.
        public static string TimeText(TimeSpan t, CultureInfo culture)
        {
            return DateTime.Today.Add(t).ToString(culture.DateTimeFormat.ShortTimePattern.Length == 1 ? "%" + culture.DateTimeFormat.ShortTimePattern : culture.DateTimeFormat.ShortTimePattern, culture);
        }

        // The form's starting times: today the next whole hour, another day 9:00; and an hour later (at most 23:59).
        public static TimeSpan DefaultStart(DateTime day, DateTime nowLocal)
        {
            if (day.Date != nowLocal.Date) return TimeSpan.FromHours(9);
            int next = Math.Min(nowLocal.Hour + 1, 23);
            return TimeSpan.FromHours(next);
        }

        public static TimeSpan DefaultEnd(TimeSpan start)
        {
            TimeSpan end = start + TimeSpan.FromHours(1);
            return end >= TimeSpan.FromHours(24) ? new TimeSpan(23, 59, 0) : end;
        }

        // A title as it is sent: one line, trimmed, at most MaxTitle characters.
        public static string CleanTitle(string title)
        {
            string t = (title ?? "").Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (t.Length > MaxTitle) t = t.Substring(0, char.IsHighSurrogate(t[MaxTitle - 1]) ? MaxTitle - 1 : MaxTitle).TrimEnd();
            return t;
        }

        // What's wrong with the event form, in a line for it, or null when it can be added.
        public static string CheckEvent(string title, bool allDay, string startText, string endText, CultureInfo culture, out TimeSpan start, out TimeSpan end)
        {
            start = TimeSpan.Zero;
            end = TimeSpan.Zero;
            if (CleanTitle(title) == "") return TitleNeeded;
            if (allDay) return null;
            string example = TimeText(new TimeSpan(14, 30, 0), culture);
            if (!ParseTime(startText, culture, out start)) return "Start: type a time like " + example;
            if (!ParseTime(endText, culture, out end)) return "End: type a time like " + example;
            if (end <= start) return EndBeforeStart;
            return null;
        }

        // The JSON for a new event on a day: all-day (its date, and the next as the exclusive end), or from start to end in
        // this PC's zone, written with its UTC offset (Windows' zone names aren't the IANA ones Google uses).
        public static string EventBody(string title, DateTime day, bool allDay, TimeSpan start, TimeSpan end, TimeZoneInfo zone)
        {
            return EventBody(title, day, allDay, start, end, zone, false);
        }

        // replacing: an edit's body, which clears the kind of time the event no longer has (an all-day event's date once
        // it has times, and the other way round), since Google keeps a field a change leaves out.
        public static string EventBody(string title, DateTime day, bool allDay, TimeSpan start, TimeSpan end, TimeZoneInfo zone, bool replacing)
        {
            var body = new Dictionary<string, object> { { "summary", CleanTitle(title) } };
            if (allDay)
            {
                body["start"] = Time("date", day.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "dateTime", replacing);
                body["end"] = Time("date", day.Date.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "dateTime", replacing);
            }
            else
            {
                body["start"] = Time("dateTime", Rfc3339(day.Date + start, zone), "date", replacing);
                body["end"] = Time("dateTime", Rfc3339(day.Date + end, zone), "date", replacing);
            }
            return Json.Write(body);
        }

        static Dictionary<string, object> Time(string field, string value, string other, bool replacing)
        {
            var time = new Dictionary<string, object> { { field, value } };
            if (replacing) time[other] = null;
            return time;
        }

        // "2026-10-09T14:30:00-07:00": a local time with its offset in the zone (a time the clocks skip moves past the gap).
        public static string Rfc3339(DateTime local, TimeZoneInfo zone)
        {
            DateTime t = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(t)) t = t.AddHours(1);
            TimeSpan offset = zone.GetUtcOffset(t);
            string sign = offset < TimeSpan.Zero ? "-" : "+";
            TimeSpan abs = offset.Duration();
            return t.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + sign + abs.Hours.ToString("00", CultureInfo.InvariantCulture) + ":" + abs.Minutes.ToString("00", CultureInfo.InvariantCulture);
        }
    }
}
