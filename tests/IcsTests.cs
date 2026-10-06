using System;
using System.Collections.Generic;
using System.Linq;

namespace Capsule
{
    public static class IcsTests
    {
        public static void Run()
        {
            ParsesLinesAndValues();
            ReadsTimesAndDurations();
            TimeZonesFollowTheirRules();
            RulesPickTheirDays();
            ReadsTodayAndTomorrow();
            ExceptionsAndMovedOccurrences();
            CountsUntilsIntervalsAndPositions();
            TimeZoneChangesKeepTheLocalTime();
            OddFilesDontThrow();
            OnlyGoogleCalendarAddresses();
            BigRepliesAreCut();
        }

        static long Utc(int month, int day, int hour, int minute) { return Clock.ToMs(new DateTime(2026, month, day, hour, minute, 0, DateTimeKind.Utc)); }

        // The fixture's events in the local days [from, to) of the test zone (UTC-7).
        static IcsResult Days(int month, int day, int days)
        {
            long from = CalendarDayTests.At(month, day, 0, 0);
            return Ics.Read(TestRunner.Fixture("google-calendar.ics"), from, from + days * 24L * 3600 * 1000, CalendarDayTests.Zone);
        }

        static string Titles(IcsResult r) { return r == null ? null : string.Join("|", r.Events.Select(e => e.Title)); }

        static CalendarEvent Find(IcsResult r, string title) { return r == null ? null : r.Events.FirstOrDefault(e => e.Title == title); }

        static void ParsesLinesAndValues()
        {
            List<string> lines = IcsParser.Unfold("A:1\r\nB:long\r\n  value\r\n\tmore\nC:3");
            TestRunner.Eq("A:1|B:long valuemore|C:3", string.Join("|", lines), "folded lines are joined, the fold's space or tab dropped");
            IcsProperty p = IcsParser.Property("ATTENDEE;CN=\"Doe, Jane: CEO\";PARTSTAT=DECLINED:mailto:jane@example.com");
            TestRunner.Check(p != null && p.Name == "ATTENDEE" && p.Param("CN") == "Doe, Jane: CEO" && p.Param("partstat") == "DECLINED" && p.Value == "mailto:jane@example.com", "parameters, quoted ones with : and , inside, and the value after the first bare colon");
            TestRunner.Check(IcsParser.Property("no colon here") == null && IcsParser.Property(":no name") == null, "a line without a name or a colon is no property");
            TestRunner.Eq("Lunch, then a walk; outside \\ ok", IcsParser.Text("Lunch\\, then a walk\\; outside\\n\\\\ ok"), "text escapes; a line break becomes a space");
            IcsComponent cal = IcsParser.Parse(TestRunner.Fixture("google-calendar.ics"));
            TestRunner.Check(cal != null && cal.Children.Count(c => c.Name == "VEVENT") == 18 && cal.Children.Count(c => c.Name == "VTIMEZONE") == 2, "the fixture: 18 events and 2 time zones");
        }

        static void ReadsTimesAndDurations()
        {
            DateTime t;
            bool utc, date;
            TestRunner.Check(Ics.Moment("20261006T090000Z", out t, out utc, out date) && utc && !date && t == new DateTime(2026, 10, 6, 9, 0, 0), "a UTC time");
            TestRunner.Check(Ics.Moment("20261006T090000", out t, out utc, out date) && !utc && !date, "a local time");
            TestRunner.Check(Ics.Moment("20261006", out t, out utc, out date) && date && t == new DateTime(2026, 10, 6), "a date");
            TestRunner.Check(!Ics.Moment("2026-10-06", out t, out utc, out date) && !Ics.Moment("", out t, out utc, out date), "and nothing else");
            TimeSpan d;
            TestRunner.Check(Ics.Duration("PT1H30M", out d) && d == TimeSpan.FromMinutes(90), "PT1H30M");
            TestRunner.Check(Ics.Duration("P1DT2H", out d) && d == TimeSpan.FromHours(26) && Ics.Duration("P2W", out d) && d == TimeSpan.FromDays(14), "days, hours and weeks");
            TestRunner.Check(Ics.Duration("-PT15M", out d) && d == TimeSpan.FromMinutes(-15) && !Ics.Duration("1H", out d) && !Ics.Duration("PTH", out d), "a negative one, and nonsense refused");
        }

        static VTimeZone Zone(string id)
        {
            IcsComponent cal = IcsParser.Parse(TestRunner.Fixture("google-calendar.ics"));
            return VTimeZone.From(cal.Children.First(c => c.Name == "VTIMEZONE" && c.Value("TZID") == id));
        }

        static void TimeZonesFollowTheirRules()
        {
            VTimeZone la = Zone("America/Los_Angeles"), london = Zone("Europe/London");
            TestRunner.Eq(TimeSpan.FromHours(-7), la.OffsetAt(new DateTime(2026, 10, 6, 12, 0, 0)), "Los Angeles in October: UTC-7");
            TestRunner.Eq(TimeSpan.FromHours(-8), la.OffsetAt(new DateTime(2026, 11, 5, 9, 0, 0)), "after the first Sunday of November: UTC-8");
            TestRunner.Eq(TimeSpan.FromHours(-8), la.OffsetAt(new DateTime(2026, 3, 8, 1, 59, 0)), "just before 2:00 on the second Sunday of March: still -8");
            TestRunner.Eq(TimeSpan.FromHours(-7), la.OffsetAt(new DateTime(2026, 3, 8, 3, 0, 0)), "and after it -7");
            TestRunner.Eq(TimeSpan.FromHours(1), london.OffsetAt(new DateTime(2026, 10, 6, 23, 0, 0)), "London in early October: UTC+1");
            TestRunner.Eq(TimeSpan.Zero, london.OffsetAt(new DateTime(2026, 10, 26, 12, 0, 0)), "after the last Sunday of October: UTC");
            TestRunner.Eq(new DateTime(2026, 10, 6, 16, 0, 0, DateTimeKind.Utc), la.ToUtc(new DateTime(2026, 10, 6, 9, 0, 0)), "9:00 in Los Angeles is 16:00 UTC");
            TimeSpan off;
            TestRunner.Check(VTimeZone.Offset("+0530", out off) && off == new TimeSpan(5, 30, 0) && VTimeZone.Offset("-0700", out off) && off == TimeSpan.FromHours(-7) && !VTimeZone.Offset("0700", out off), "offsets with their sign");
        }

        static void RulesPickTheirDays()
        {
            IcsRule second = IcsRule.Parse("FREQ=YEARLY;BYMONTH=3;BYDAY=2SU");
            TestRunner.Eq("2026-03-08", string.Join(",", second.Days(new DateTime(1970, 3, 8), new DateTime(2026, 1, 1), new DateTime(2026, 12, 31)).Select(d => d.ToString("yyyy-MM-dd"))), "the second Sunday of March 2026");
            IcsRule last = IcsRule.Parse("FREQ=YEARLY;BYMONTH=10;BYDAY=-1SU");
            TestRunner.Eq("2026-10-25", string.Join(",", last.Days(new DateTime(1970, 10, 25), new DateTime(2026, 1, 1), new DateTime(2026, 12, 31)).Select(d => d.ToString("yyyy-MM-dd"))), "the last Sunday of October 2026");
            IcsRule monthEnd = IcsRule.Parse("FREQ=MONTHLY;BYMONTHDAY=-1");
            TestRunner.Eq("2028-01-31,2028-02-29,2028-03-31", string.Join(",", monthEnd.Days(new DateTime(2027, 12, 31), new DateTime(2028, 1, 1), new DateTime(2028, 3, 31)).Select(d => d.ToString("yyyy-MM-dd"))), "the last day of each month, leap February included");
            IcsRule fortnight = IcsRule.Parse("FREQ=WEEKLY;INTERVAL=2;BYDAY=TH");
            TestRunner.Check(fortnight.Matches(new DateTime(2026, 10, 8), new DateTime(2026, 1, 1)) && !fortnight.Matches(new DateTime(2026, 10, 1), new DateTime(2026, 1, 1)), "every other Thursday from 1 January 2026: 8 October, not 1 October");
            IcsRule weekdays = IcsRule.Parse("FREQ=WEEKLY;WKST=SU;BYDAY=MO,TU,WE,TH,FR");
            TestRunner.Check(weekdays.Matches(new DateTime(2026, 10, 9), new DateTime(2026, 1, 5)) && !weekdays.Matches(new DateTime(2026, 10, 10), new DateTime(2026, 1, 5)), "weekdays, not Saturday");
            TestRunner.Check(!weekdays.Matches(new DateTime(2026, 1, 2), new DateTime(2026, 1, 5)), "and nothing before the start");
            TestRunner.Check(IcsRule.Parse("FREQ=HOURLY;INTERVAL=4").Unsupported && IcsRule.Parse("FREQ=WEEKLY;BYHOUR=9").Unsupported && IcsRule.Parse("FREQ=YEARLY;BYDAY=20MO").Unsupported, "hourly rules, BYHOUR and the nth weekday of a year aren't followed");
            TestRunner.Check(!IcsRule.Parse("FREQ=MONTHLY;BYDAY=MO,TU,WE,TH,FR;BYSETPOS=-1").Unsupported, "BYSETPOS in a monthly rule is");
        }

        // The fixture's today (6 October, UTC-7) and tomorrow: every kind of event the feed has.
        static void ReadsTodayAndTomorrow()
        {
            IcsResult r = Days(10, 6, 2);
            TestRunner.Check(r != null && r.Name == "sample.user@example.com", "the calendar's name");
            TestRunner.Eq("Offsite|Sam's birthday|Night deploy|Every four hours|Standup|Monthly review|Call with London|Design review|Floating|Lunch, then a walk; outside|(No title)", Titles(r),
                "all-day first, then by start; cancelled, declined, ended and other days' events left out");
            CalendarEvent night = Find(r, "Night deploy");
            TestRunner.Check(night != null && night.StartMs == Utc(10, 6, 6, 0) && night.EndMs == Utc(10, 6, 8, 0) && night.CalendarId == Ics.CalendarId, "an event across midnight, in Los Angeles' time");
            CalendarEvent standup = Find(r, "Standup");
            TestRunner.Check(standup != null && standup.StartMs == Utc(10, 6, 16, 0) && standup.EndMs == Utc(10, 6, 16, 15), "today's standup from a weekday rule begun in January");
            CalendarEvent review = Find(r, "Monthly review");
            TestRunner.Check(review != null && review.StartMs == Utc(10, 6, 20, 0), "the first Tuesday of the month");
            CalendarEvent london = Find(r, "Call with London");
            TestRunner.Check(london != null && london.StartMs == Utc(10, 6, 22, 0) && london.EndMs == Utc(10, 6, 23, 0), "an event in London's time (BST)");
            CalendarEvent design = Find(r, "Design review");
            TestRunner.Check(design != null && design.StartMs == Utc(10, 6, 23, 30) && design.EndMs == Utc(10, 7, 0, 15), "an event in UTC");
            CalendarEvent floating = Find(r, "Floating");
            TestRunner.Check(floating != null && floating.StartMs == Utc(10, 7, 14, 0), "a time with no zone is the PC's");
            CalendarEvent lunch = Find(r, "Lunch, then a walk; outside");
            TestRunner.Check(lunch != null && lunch.EndMs - lunch.StartMs == 3600 * 1000L, "a folded, escaped title, and a DURATION instead of an end");
            CalendarEvent offsite = Find(r, "Offsite");
            TestRunner.Check(offsite != null && offsite.AllDay && offsite.StartDay == new DateTime(2026, 10, 6) && offsite.EndDay == new DateTime(2026, 10, 7), "an all-day event keeps its dates");
            CalendarEvent birthday = Find(r, "Sam's birthday");
            TestRunner.Check(birthday != null && birthday.AllDay && birthday.StartDay == new DateTime(2026, 10, 7), "a yearly all-day event begun in 1990");
            CalendarEvent hourly = Find(r, "Every four hours");
            TestRunner.Check(hourly != null && r.NotExpanded == 1 && r.Events.Count(e => e.Title == "Every four hours") == 1, "a rule Capsule can't follow: its first occurrence only, and counted");
            TestRunner.Check(Find(r, "Vendor call") == null && Find(r, "Old sync") == null, "an event the owner declined, and a cancelled one, are left out");
        }

        static void ExceptionsAndMovedOccurrences()
        {
            IcsResult wednesday = Days(10, 7, 1);
            TestRunner.Check(Find(wednesday, "Standup") == null, "an EXDATE takes that day's standup out");
            IcsResult thursday = Days(10, 8, 1);
            CalendarEvent moved = Find(thursday, "Standup (moved)");
            TestRunner.Check(moved != null && moved.StartMs == Utc(10, 8, 17, 0) && Find(thursday, "Standup") == null, "an occurrence moved to 10:00 shows there, and not at 9:00");
            TestRunner.Check(Find(thursday, "Fortnightly 1:1") != null, "every other Thursday");
            TestRunner.Check(Find(Days(10, 9, 1), "Standup") != null, "the next day's standup is back as usual");
        }

        static void CountsUntilsIntervalsAndPositions()
        {
            TestRunner.Eq(3, Days(10, 3, 3).Events.Count(e => e.Title == "Three mornings"), "COUNT=3: three mornings");
            TestRunner.Check(Find(Days(10, 6, 1), "Three mornings") == null, "and none after");
            TestRunner.Check(Find(Days(9, 29, 1), "Old Tuesday class") != null && Find(Days(10, 6, 1), "Old Tuesday class") == null, "UNTIL: the last Tuesday of September, then no more");
            TestRunner.Check(Find(Days(10, 30, 1), "Month-end report") != null && Find(Days(10, 29, 1), "Month-end report") == null, "BYSETPOS=-1: the month's last weekday, Friday 30 October");
            TestRunner.Check(Find(Days(10, 1, 1), "Fortnightly 1:1") == null, "not on the Thursdays between");
        }

        // A repeating event keeps its local time across a change of clocks: 9:00 in Los Angeles is 17:00 UTC in November.
        static void TimeZoneChangesKeepTheLocalTime()
        {
            IcsResult november = Days(11, 5, 1);
            CalendarEvent standup = Find(november, "Standup");
            TestRunner.Check(standup != null && standup.StartMs == Utc(11, 5, 17, 0), "9:00 PST is 17:00 UTC");
            CalendarEvent fortnight = Find(november, "Fortnightly 1:1");
            TestRunner.Check(fortnight != null && fortnight.StartMs == Utc(11, 5, 19, 0), "11:00 PST is 19:00 UTC");
        }

        static void OddFilesDontThrow()
        {
            TestRunner.Check(Ics.Read("<html>Sign in</html>", 0, 1, CalendarDayTests.Zone) == null && Ics.Read("", 0, 1, CalendarDayTests.Zone) == null, "a page that isn't a calendar is none");
            IcsResult odd = Ics.Read("BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nSUMMARY:No start\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nDTSTART:garbage\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nDTSTART;TZID=Nowhere/Unknown:20261006T090000\r\nDTEND;TZID=Nowhere/Unknown:20261006T100000\r\nSUMMARY:Unknown zone\r\nRRULE:FREQ=DAILY;INTERVAL=0\r\nEND:VEVENT\r\nEND:VCALENDAR", CalendarDayTests.At(10, 6, 0, 0), CalendarDayTests.At(10, 7, 0, 0), CalendarDayTests.Zone);
            TestRunner.Check(odd != null && odd.Events.Count == 1 && odd.Events[0].StartMs == Utc(10, 6, 16, 0), "events without a readable start are skipped; a zone the file doesn't define is the PC's");
        }

        static void OnlyGoogleCalendarAddresses()
        {
            const string Secret = "https://calendar.google.com/calendar/ical/sample.user%40example.com/private-0123456789abcdef0123456789abcdef/basic.ics";
            TestRunner.Eq(Secret, IcsLink.Normalize("  " + Secret + "  "), "a secret iCal address, trimmed");
            TestRunner.Eq(Secret, IcsLink.Normalize("webcal://calendar.google.com/calendar/ical/sample.user%40example.com/private-0123456789abcdef0123456789abcdef/basic.ics"), "webcal:// is https://");
            TestRunner.Eq(null, IcsLink.Normalize("http://calendar.google.com/calendar/ical/x/basic.ics"), "not over plain http");
            TestRunner.Eq(null, IcsLink.Normalize("https://calendar.google.com.example.com/calendar/ical/x/basic.ics"), "not on a look-alike host");
            TestRunner.Eq(null, IcsLink.Normalize("https://calendar.google.com@example.com/calendar/ical/x/basic.ics"), "nor one hidden behind an @");
            TestRunner.Eq(null, IcsLink.Normalize("https://calendar.google.com/calendar/embed?src=x"), "not the embed page");
            TestRunner.Eq(null, IcsLink.Normalize("https://calendar.google.com:8443/calendar/ical/x/basic.ics"), "not another port");
            TestRunner.Check(IcsLink.Normalize(null) == null && IcsLink.Normalize("  ") == null, "nothing is no address");
        }

        // A calendar feed is read with a larger limit than other replies, and a reply over it says so.
        static void BigRepliesAreCut()
        {
            using (var fake = new FakeHttp().Reply("/big.ics", 200, new string('x', 5000)))
            {
                HttpResult small = Http.Get(fake.Url("/big.ics"), new Dictionary<string, string>(), 5000, 1000);
                TestRunner.Check(small.Status == 200 && small.Body.Length == 1000 && small.Truncated, "over the limit: cut there, and Truncated");
                HttpResult whole = Http.Get(fake.Url("/big.ics"), new Dictionary<string, string>(), 5000, 5000);
                TestRunner.Check(whole.Body.Length == 5000 && !whole.Truncated, "exactly at the limit: whole");
            }
        }
    }
}
