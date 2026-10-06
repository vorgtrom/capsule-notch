using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Capsule
{
    // What the calendar module knows at one moment, for its faces. Events live in memory only.
    public sealed class CalendarSnapshot
    {
        public bool SignedIn;
        public bool Loaded;                 // a pass has filled the events since signing in (or since Capsule started)
        public List<CalendarEvent> Events = new List<CalendarEvent>();
        public List<GoogleTask> Tasks = new List<GoogleTask>();   // signed in: the tasks due today and tomorrow
        public long DataAtMs;               // when the events were read
        public bool LastFailed;             // the last pass didn't get them; they are the last ones read
        public string Problem = "";         // what went wrong, for the tile and the card
    }

    // One line of the hover card or the tile.
    public sealed class CalendarRow
    {
        public string Time = "";        // "9:00 AM – 9:30 AM", or "All day"
        public string Title = "";
        public string Color = GoogleCalendarClient.DefaultColor;
        public bool Current;            // the event happening now: highlighted
        // The month page only: the event's ids, whether it may be edited (the user's own, on one day) or deleted (the
        // user's own), and its times for the edit form.
        public string Id = "", CalendarId = "";
        public bool CanEdit, CanDelete, AllDay;
        public string StartText = "", EndText = "";
    }

    // A day on the hover card: its heading, its all-day events, then its timed ones.
    public sealed class CalendarSection
    {
        public string Heading = "";
        public List<CalendarRow> Rows = new List<CalendarRow>();
        public string Empty = "";       // said instead of rows when there are none
    }

    // Everything the calendar's hover card shows, as plain text. Built here; drawn by CardView.
    public sealed class CalendarCardModel
    {
        public List<CalendarSection> Sections = new List<CalendarSection>();
        public string Footer = "";
    }

    // What the panel's Calendar tile shows (calendar spec §2).
    public sealed class CalendarTile
    {
        public List<CalendarRow> Today = new List<CalendarRow>();
        public List<CalendarRow> Tomorrow = new List<CalendarRow>();
        public string Hint = "";    // instead of rows: not signed in, checking, or nothing coming up
        public string Note = "";    // what went wrong ("Couldn't reach Google"); the rows are then the last ones read
        public bool Dimmed;
        public bool CanOpenMonth;   // connected: the tile offers the month page
    }

    // The calendar's "now" and "next", and what its cell, card and tile show (calendar spec §2). Pure: every function takes
    // the moment, the time zone and the culture, so tests don't depend on the PC's. Only timed events count for now and
    // next; all-day events are listed under their day.
    public static class CalendarDay
    {
        public const string Glyph = "\uE787";   // U+E787 Calendar in the Segoe icon fonts, kept as an escape like the panel's icons
        public const string Provider = "calendar";
        public const string Nothing = "—";
        public const long StaleMs = 15 * 60 * 1000;   // kept events dim once they are this old and Google can't be reached
        public const int TileRows = 6, TomorrowOnCard = 3;
        public const string TaskTime = "Task";
        public const string SignInHint = "Connect Google Calendar in ⚙";

        // The event happening now: started, not yet ended. When several are, the one that started first.
        public static CalendarEvent Current(IEnumerable<CalendarEvent> events, long now)
        {
            CalendarEvent found = null;
            foreach (CalendarEvent e in events)
                if (!e.AllDay && e.StartMs <= now && now < e.EndMs && (found == null || e.StartMs < found.StartMs)) found = e;
            return found;
        }

        // The next event to start after now, today.
        public static CalendarEvent Next(IEnumerable<CalendarEvent> events, long now, TimeZoneInfo zone)
        {
            DateTime today = LocalDay(now, zone);
            CalendarEvent found = null;
            foreach (CalendarEvent e in events)
                if (!e.AllDay && e.StartMs > now && LocalDay(e.StartMs, zone) == today && (found == null || e.StartMs < found.StartMs)) found = e;
            return found;
        }

        // How far an event has got: 0 at its start, 1 at its end.
        public static double Progress(CalendarEvent e, long now)
        {
            if (e == null || e.EndMs <= e.StartMs) return 0;
            return Math.Max(0, Math.Min(1, (double)(now - e.StartMs) / (e.EndMs - e.StartMs)));
        }

        // Kept events dim once Google couldn't be reached and they are over 15 minutes old.
        public static bool IsDimmed(CalendarSnapshot s, long now)
        {
            return s.LastFailed && s.DataAtMs > 0 && now - s.DataAtMs > StaleMs;
        }

        // The cell: "now" with the ring filling as the event runs, the next start today, or a dash.
        public static CellModel Cell(CalendarSnapshot s, long now, TimeZoneInfo zone, CultureInfo culture)
        {
            var c = new CellModel { Provider = Provider, Glyph = Glyph, ArcColor = Palette.Text, Text = Nothing };
            CalendarEvent current = Current(s.Events, now);
            if (current != null)
            {
                c.Text = "now";
                c.ShowArc = true;
                c.Used = Progress(current, now) * 100;
            }
            else
            {
                CalendarEvent next = Next(s.Events, now, zone);
                if (next != null) c.Text = CellTime(next.StartMs, zone, culture);
            }
            c.Dimmed = IsDimmed(s, now);
            return c;
        }

        // A start time for the cell: the culture's short time without its AM/PM designator, so it fits ("2:30", "14:30").
        public static string CellTime(long ms, TimeZoneInfo zone, CultureInfo culture)
        {
            return Local(ms, zone).ToString(Single(WithoutDesignator(culture.DateTimeFormat.ShortTimePattern)), culture);
        }

        // A format pattern with its AM/PM designator ("t", "tt") left out, wherever it is, and the spaces round it tidied.
        // Quoted text and escaped characters are kept as they are.
        public static string WithoutDesignator(string pattern)
        {
            var sb = new StringBuilder();
            char quote = '\0';
            for (int i = 0; i < pattern.Length; i++)
            {
                char c = pattern[i];
                if (quote != '\0')
                {
                    sb.Append(c);
                    if (c == quote) quote = '\0';
                    continue;
                }
                if (c == '\\' && i + 1 < pattern.Length)
                {
                    sb.Append(c).Append(pattern[++i]);
                    continue;
                }
                if (c == '\'' || c == '"') quote = c;
                if (c != 't') sb.Append(c);
            }
            string tidy = sb.ToString().Trim();
            while (tidy.Contains("  ")) tidy = tidy.Replace("  ", " ");
            return tidy == "" ? "H:mm" : tidy;
        }

        // A one-letter pattern would be read as a standard format ("t", "d"): % makes it a custom one.
        static string Single(string pattern) { return pattern.Length == 1 ? "%" + pattern : pattern; }

        // "9:00 AM – 9:30 AM" in the culture's short time, for the card and the tile.
        public static string Range(CalendarEvent e, TimeZoneInfo zone, CultureInfo culture)
        {
            if (e.AllDay) return "All day";
            string pattern = Single(culture.DateTimeFormat.ShortTimePattern);
            return Local(e.StartMs, zone).ToString(pattern, culture) + " – " + Local(e.EndMs, zone).ToString(pattern, culture);
        }

        // The hover card: the rest of today (all-day events first, the current event marked), then tomorrow's all-day
        // events and its first three timed ones.
        public static CalendarCardModel Card(CalendarSnapshot s, long now, TimeZoneInfo zone, CultureInfo culture)
        {
            var m = new CalendarCardModel();
            var today = new CalendarSection { Heading = "Today", Empty = s.Loaded ? "Nothing else today" : "Checking…" };
            DateTime day = LocalDay(now, zone);
            today.Rows = RestOfToday(s.Events, now, zone, culture);
            today.Rows.AddRange(TaskRows(s.Tasks, day));   // and the day's tasks not done, after its events, as on the tile
            var tomorrow = new CalendarSection { Heading = "Tomorrow", Empty = s.Loaded ? "Nothing tomorrow" : "" };
            tomorrow.Rows = TomorrowRows(s.Events, now, zone, culture, TomorrowOnCard);
            tomorrow.Rows.AddRange(TaskRows(s.Tasks, day.AddDays(1)).Take(TomorrowOnCard));
            m.Sections.Add(today);
            m.Sections.Add(tomorrow);
            var parts = new List<string>();
            if (s.Problem != "") parts.Add(s.Problem);
            if (s.DataAtMs > 0) parts.Add("Updated " + Format.Ago(s.DataAtMs, now));
            m.Footer = string.Join(" · ", parts);
            return m;
        }

        // The panel's tile: today's rows as on the card, then tomorrow's, 6 in all.
        public static CalendarTile Tile(CalendarSnapshot s, long now, TimeZoneInfo zone, CultureInfo culture)
        {
            var t = new CalendarTile();
            if (!s.SignedIn)
            {
                t.Hint = SignInHint;
                return t;
            }
            t.CanOpenMonth = true;
            DateTime today = LocalDay(now, zone);
            t.Today = RestOfToday(s.Events, now, zone, culture);
            t.Today.AddRange(TaskRows(s.Tasks, today));
            if (t.Today.Count > TileRows) t.Today.RemoveRange(TileRows, t.Today.Count - TileRows);
            t.Tomorrow = TomorrowRows(s.Events, now, zone, culture, TileRows);
            t.Tomorrow.AddRange(TaskRows(s.Tasks, today.AddDays(1)));
            int room = TileRows - t.Today.Count;
            if (t.Tomorrow.Count > room) t.Tomorrow.RemoveRange(room, t.Tomorrow.Count - room);
            t.Note = s.Problem;
            t.Dimmed = IsDimmed(s, now);
            if (t.Today.Count + t.Tomorrow.Count == 0) t.Hint = s.Loaded ? "Nothing on today or tomorrow" : s.Problem != "" ? "" : "Checking…";
            return t;
        }

        // A day's tasks not done yet, after its events: a grey dot, and "Task" where the time goes.
        static IEnumerable<CalendarRow> TaskRows(IList<GoogleTask> tasks, DateTime day)
        {
            return (tasks ?? new List<GoogleTask>()).Where(x => x.Due.Date == day && !x.Done).Select(x => new CalendarRow { Time = TaskTime, Title = x.Title, Color = CalendarMonth.TaskDot });
        }

        static List<CalendarRow> RestOfToday(IList<CalendarEvent> events, long now, TimeZoneInfo zone, CultureInfo culture)
        {
            DateTime today = LocalDay(now, zone);
            long tomorrowStart = DayStartMs(today.AddDays(1), zone);
            CalendarEvent current = Current(events, now);
            var rows = new List<CalendarRow>();
            foreach (CalendarEvent e in events.Where(x => x.AllDay && OnDay(x, today)).OrderBy(x => x.Title, StringComparer.CurrentCulture))
                rows.Add(Row(e, false, zone, culture));
            foreach (CalendarEvent e in events.Where(x => !x.AllDay && x.EndMs > now && x.StartMs < tomorrowStart).OrderBy(x => x.StartMs))
                rows.Add(Row(e, e == current, zone, culture));
            return rows;
        }

        static List<CalendarRow> TomorrowRows(IList<CalendarEvent> events, long now, TimeZoneInfo zone, CultureInfo culture, int timedMax)
        {
            DateTime tomorrow = LocalDay(now, zone).AddDays(1);
            long start = DayStartMs(tomorrow, zone), end = DayStartMs(tomorrow.AddDays(1), zone);
            var rows = new List<CalendarRow>();
            foreach (CalendarEvent e in events.Where(x => x.AllDay && OnDay(x, tomorrow)).OrderBy(x => x.Title, StringComparer.CurrentCulture))
                rows.Add(Row(e, false, zone, culture));
            foreach (CalendarEvent e in events.Where(x => !x.AllDay && x.StartMs >= start && x.StartMs < end).OrderBy(x => x.StartMs).Take(Math.Max(0, timedMax)))
                rows.Add(Row(e, false, zone, culture));
            return rows;
        }

        static CalendarRow Row(CalendarEvent e, bool current, TimeZoneInfo zone, CultureInfo culture)
        {
            return new CalendarRow { Time = Range(e, zone, culture), Title = e.Title, Color = e.Color, Current = current };
        }

        static bool OnDay(CalendarEvent e, DateTime day) { return e.StartDay <= day && day < e.EndDay; }

        // The span to read: from the start of today to the end of tomorrow, local time.
        public static long WindowStartMs(long now, TimeZoneInfo zone) { return DayStartMs(LocalDay(now, zone), zone); }
        public static long WindowEndMs(long now, TimeZoneInfo zone) { return DayStartMs(LocalDay(now, zone).AddDays(2), zone); }

        public static DateTime Local(long ms, TimeZoneInfo zone) { return TimeZoneInfo.ConvertTimeFromUtc(Clock.FromMs(ms), zone); }
        public static DateTime LocalDay(long ms, TimeZoneInfo zone) { return Local(ms, zone).Date; }

        // Local midnight of a day, in UTC milliseconds. Where clocks skip midnight itself, the first moment that exists.
        public static long DayStartMs(DateTime day, TimeZoneInfo zone)
        {
            DateTime midnight = DateTime.SpecifyKind(day.Date, DateTimeKind.Unspecified);
            for (int i = 0; i < 4 && zone.IsInvalidTime(midnight); i++) midnight = midnight.AddMinutes(30);
            return Clock.ToMs(TimeZoneInfo.ConvertTimeToUtc(midnight, zone));
        }
    }
}
