using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Capsule
{
    public static class CalendarDayTests
    {
        // A fixed UTC-7 zone, like Los Angeles in October: the tests never depend on the PC's own zone.
        public static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("Capsule test UTC-7", TimeSpan.FromHours(-7), "Capsule test UTC-7", "Capsule test UTC-7");
        static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

        public static void Run()
        {
            NowAndNext();
            OverlapsStartFirstFirst();
            CellText();
            CellTimesFollowTheCulture();
            RingFillsAsTheEventRuns();
            KeptEventsDim();
            AllDayEventsSpanTheirDates();
            CardListsTheRestOfTodayAndTomorrow();
            TileHasSixRowsAtMost();
            TileHints();
            TheTileListsTasksNotDone();
            ReadsTodayAndTomorrow();
        }

        // Local time in the test zone, as UTC milliseconds.
        public static long At(int month, int day, int hour, int minute)
        {
            return Clock.ToMs(new DateTime(2026, month, day, hour, minute, 0, DateTimeKind.Utc).AddHours(7));
        }

        static void TheTileListsTasksNotDone()
        {
            CalendarSnapshot snap = Snapshot(new List<CalendarEvent>(), At(10, 6, 0, 0));
            snap.Tasks = new List<GoogleTask>
            {
                new GoogleTask { Id = "a", Title = "Send the invoice", Due = new DateTime(2026, 10, 6) },
                new GoogleTask { Id = "b", Title = "Water the plants", Due = new DateTime(2026, 10, 6), Done = true },
                new GoogleTask { Id = "c", Title = "Book flights", Due = new DateTime(2026, 10, 7) },
                new GoogleTask { Id = "d", Title = "Later", Due = new DateTime(2026, 10, 9) },
            };
            CalendarTile t = CalendarDay.Tile(snap, At(10, 6, 8, 0), Zone, En);
            TestRunner.Check(t.Today.Count == 1 && t.Today[0].Title == "Send the invoice" && t.Today[0].Time == CalendarDay.TaskTime && t.Today[0].Color == CalendarMonth.TaskDot
                && t.Tomorrow.Count == 1 && t.Tomorrow[0].Title == "Book flights" && t.Hint == "", "the tile lists today's and tomorrow's tasks not done yet");
            snap.Events = Fixture();
            t = CalendarDay.Tile(snap, At(10, 6, 22, 0), Zone, En);
            TestRunner.Check(t.Today.Count > 1 && t.Today.Last().Title == "Send the invoice", "after the day's events");
            CalendarCardModel card = CalendarDay.Card(snap, At(10, 6, 22, 0), Zone, En);
            TestRunner.Check(card.Sections[0].Rows.Last().Title == "Send the invoice" && card.Sections[0].Rows.Last().Time == CalendarDay.TaskTime
                && card.Sections[1].Rows.Last().Title == "Book flights" && !card.Sections.SelectMany(x => x.Rows).Any(r => r.Title == "Water the plants"),
                "the hover card lists them too, after each day's events");
        }

        public static List<CalendarEvent> Fixture()
        {
            var mine = new GoogleCalendar { Id = GoogleCalendarTests.Primary, Color = "#9fe1e7", CanWrite = true };
            return GoogleCalendarClient.ParseEvents(TestRunner.Fixture("google-events.json"), mine);
        }

        public static CalendarSnapshot Snapshot(List<CalendarEvent> events, long dataAt)
        {
            return new CalendarSnapshot { SignedIn = true, Loaded = true, Events = events, DataAtMs = dataAt };
        }

        static CalendarEvent Timed(string title, long start, long end) { return new CalendarEvent { Id = title, Title = title, StartMs = start, EndMs = end }; }

        static string IdOf(CalendarEvent e) { return e != null ? e.Id : null; }

        static void NowAndNext()
        {
            List<CalendarEvent> events = Fixture();
            TestRunner.Eq("nightdeploy", IdOf(CalendarDay.Current(events, At(10, 6, 0, 30))), "at 0:30 the night deploy, begun yesterday, is now");
            TestRunner.Eq("standup", IdOf(CalendarDay.Next(events, At(10, 6, 0, 30), Zone)), "and the standup is next");
            TestRunner.Eq(null, IdOf(CalendarDay.Current(events, At(10, 6, 8, 0))), "at 8:00 nothing is on");
            TestRunner.Eq("weekly_20261006T200000Z", IdOf(CalendarDay.Current(events, At(10, 6, 13, 0))), "an event is on from its first minute");
            TestRunner.Eq(null, IdOf(CalendarDay.Current(events, At(10, 6, 14, 0))), "and off at its end");
            TestRunner.Eq("london", IdOf(CalendarDay.Next(events, At(10, 6, 14, 0), Zone)), "next: the event written in London's time, at 15:00 here");
            TestRunner.Eq(null, IdOf(CalendarDay.Next(events, At(10, 6, 17, 30), Zone)), "after the last one nothing is next today, though tomorrow has events");
            TestRunner.Eq(null, IdOf(CalendarDay.Current(events.Where(e => e.AllDay).ToList(), At(10, 6, 12, 0))), "all-day events are never now");
        }

        static void OverlapsStartFirstFirst()
        {
            var events = new List<CalendarEvent> { Timed("later", At(10, 6, 10, 30), At(10, 6, 11, 30)), Timed("first", At(10, 6, 10, 0), At(10, 6, 11, 0)), Timed("after", At(10, 6, 11, 0), At(10, 6, 12, 0)) };
            TestRunner.Eq("first", IdOf(CalendarDay.Current(events, At(10, 6, 10, 45))), "of two overlapping events, the one that started first is now");
            TestRunner.Eq("after", IdOf(CalendarDay.Next(events, At(10, 6, 10, 45), Zone)), "and next is the next start after this moment");
            CalendarCardModel card = CalendarDay.Card(Snapshot(events, At(10, 6, 10, 45)), At(10, 6, 10, 45), Zone, En);
            TestRunner.Eq("first", string.Join(",", card.Sections[0].Rows.Where(r => r.Current).Select(r => r.Title)), "only that one is marked on the card");
        }

        static void CellText()
        {
            CalendarSnapshot s = Snapshot(Fixture(), At(10, 6, 0, 30));
            CellModel early = CalendarDay.Cell(s, At(10, 6, 8, 0), Zone, En);
            TestRunner.Check(early.Text == "9:00" && !early.ShowArc && early.Glyph == CalendarDay.Glyph && early.Provider == "calendar", "before the standup: its start, without AM, and the calendar glyph");
            CellModel during = CalendarDay.Cell(s, At(10, 6, 0, 30), Zone, En);
            TestRunner.Check(during.Text == "now" && during.ShowArc && during.ArcColor == Palette.Text, "during an event: now, with the ring in the text colour");
            TestRunner.Eq("—", CalendarDay.Cell(s, At(10, 6, 17, 30), Zone, En).Text, "nothing left today: a dash");
            TestRunner.Eq("—", CalendarDay.Cell(Snapshot(new List<CalendarEvent>(), 0), At(10, 6, 8, 0), Zone, En).Text, "and no events at all: the same");
            TestRunner.Check(CalendarDay.Glyph.Length == 1 && CalendarDay.Glyph[0] == (char)0xE787, "the glyph is the single character U+E787");
        }

        static CultureInfo WithShortTime(string pattern)
        {
            var c = (CultureInfo)CultureInfo.InvariantCulture.Clone();
            c.DateTimeFormat.ShortTimePattern = pattern;
            c.DateTimeFormat.AMDesignator = "AM";
            c.DateTimeFormat.PMDesignator = "PM";
            return c;
        }

        static void CellTimesFollowTheCulture()
        {
            long half2 = At(10, 6, 14, 30);
            TestRunner.Eq("2:30", CalendarDay.CellTime(half2, Zone, WithShortTime("h:mm tt")), "12-hour: 2:30, the PM dropped");
            TestRunner.Eq("14:30", CalendarDay.CellTime(half2, Zone, WithShortTime("HH:mm")), "24-hour: 14:30");
            TestRunner.Eq("2:30", CalendarDay.CellTime(half2, Zone, WithShortTime("tt h:mm")), "a designator in front is dropped too");
            TestRunner.Eq("2.30", CalendarDay.CellTime(half2, Zone, WithShortTime("h.mm t")), "and a one-letter one");
            TestRunner.Eq("HH:mm", CalendarDay.WithoutDesignator("HH:mm"), "a pattern without a designator is kept as it is");
            TestRunner.Eq("H 'at' mm", CalendarDay.WithoutDesignator("tt H 'at' mm"), "even with a t in it");
            TestRunner.Eq("9", CalendarDay.CellTime(At(10, 6, 9, 0), Zone, WithShortTime("h tt")), "a pattern left with one letter still formats");
            TestRunner.Eq("2:30", CalendarDay.CellTime(half2, Zone, En), "en-US");
            TestRunner.Eq("14:30", CalendarDay.CellTime(half2, Zone, CultureInfo.GetCultureInfo("en-GB")), "en-GB");
        }

        static void RingFillsAsTheEventRuns()
        {
            CalendarEvent night = Fixture().First(e => e.Id == "nightdeploy");
            TestRunner.Near(0.75, CalendarDay.Progress(night, At(10, 6, 0, 30)), "90 of 120 minutes: three quarters");
            TestRunner.Near(0, CalendarDay.Progress(night, At(10, 5, 22, 0)), "before it: nothing");
            TestRunner.Near(1, CalendarDay.Progress(night, At(10, 6, 2, 0)), "after it: full");
            TestRunner.Near(0, CalendarDay.Progress(Timed("instant", At(10, 6, 9, 0), At(10, 6, 9, 0)), At(10, 6, 9, 0)), "an event without length: nothing");
            CellModel c = CalendarDay.Cell(Snapshot(Fixture(), At(10, 6, 0, 30)), At(10, 6, 0, 30), Zone, En);
            TestRunner.Near(75, c.Used, "the cell's ring at 75%");
        }

        static void KeptEventsDim()
        {
            CalendarSnapshot s = Snapshot(Fixture(), At(10, 6, 8, 0));
            s.LastFailed = true;
            TestRunner.Check(!CalendarDay.Cell(s, At(10, 6, 8, 15), Zone, En).Dimmed, "kept events 15 minutes old aren't dimmed yet");
            TestRunner.Check(CalendarDay.Cell(s, At(10, 6, 8, 16), Zone, En).Dimmed, "older ones are");
            s.LastFailed = false;
            TestRunner.Check(!CalendarDay.Cell(s, At(10, 6, 9, 0), Zone, En).Dimmed, "while Google answers, nothing dims");
        }

        static void AllDayEventsSpanTheirDates()
        {
            var trip = new CalendarEvent { Id = "trip", Title = "Trip", AllDay = true, StartDay = new DateTime(2026, 10, 5), EndDay = new DateTime(2026, 10, 8) };
            var events = new List<CalendarEvent> { trip };
            TestRunner.Eq("Trip", string.Join(",", CalendarDay.Card(Snapshot(events, 1), At(10, 6, 12, 0), Zone, En).Sections[0].Rows.Select(r => r.Title)), "a three-day event is on today");
            TestRunner.Eq("Trip", string.Join(",", CalendarDay.Card(Snapshot(events, 1), At(10, 6, 12, 0), Zone, En).Sections[1].Rows.Select(r => r.Title)), "and tomorrow");
            TestRunner.Eq(0, CalendarDay.Card(Snapshot(events, 1), At(10, 7, 12, 0), Zone, En).Sections[1].Rows.Count, "but not the day it ends on (the end is exclusive)");
            TestRunner.Eq("All day", CalendarDay.Range(trip, Zone, En), "listed without times");
        }

        static string Titles(CalendarSection s) { return string.Join(",", s.Rows.Select(r => r.Title)); }

        static void CardListsTheRestOfTodayAndTomorrow()
        {
            CalendarCardModel night = CalendarDay.Card(Snapshot(Fixture(), At(10, 6, 0, 0)), At(10, 6, 0, 30), Zone, En);
            TestRunner.Eq(2, night.Sections.Count, "two days");
            TestRunner.Check(night.Sections[0].Heading == "Today" && night.Sections[1].Heading == "Tomorrow", "Today, then Tomorrow");
            TestRunner.Eq("Offsite,Night deploy,Standup,Weekly review,Call with London,Design review", Titles(night.Sections[0]), "all-day first, then the rest of today in order, the event begun yesterday included");
            CalendarRow deploy = night.Sections[0].Rows[1];
            TestRunner.Check(deploy.Current && deploy.Time == "11:00 PM – 1:00 AM" && deploy.Color == "#9fe1e7", "the current event is marked, with its times and colour");
            TestRunner.Check(night.Sections[0].Rows[0].Time == "All day" && !night.Sections[0].Rows[0].Current, "the all-day event without times");
            TestRunner.Eq("1:1,(No title),Lunch", Titles(night.Sections[1]), "tomorrow's first three");
            TestRunner.Eq("Updated 30 min ago", night.Footer, "how old the list is");
            CalendarCardModel late = CalendarDay.Card(Snapshot(Fixture(), At(10, 6, 17, 30)), At(10, 6, 17, 30), Zone, En);
            TestRunner.Eq("Offsite", Titles(late.Sections[0]), "in the evening only the all-day event is left");
            CalendarCardModel empty = CalendarDay.Card(Snapshot(new List<CalendarEvent>(), At(10, 6, 9, 0)), At(10, 6, 9, 0), Zone, En);
            TestRunner.Check(empty.Sections[0].Empty == "Nothing else today" && empty.Sections[1].Empty == "Nothing tomorrow", "an empty day says so");
            CalendarSnapshot away = Snapshot(Fixture(), At(10, 6, 8, 0));
            away.LastFailed = true;
            away.Problem = "Couldn't reach Google";
            TestRunner.Eq("Couldn't reach Google · Updated 20 min ago", CalendarDay.Card(away, At(10, 6, 8, 20), Zone, En).Footer, "what went wrong, and how old the list is");
        }

        static void TileHasSixRowsAtMost()
        {
            CalendarTile t = CalendarDay.Tile(Snapshot(Fixture(), At(10, 6, 0, 0)), At(10, 6, 8, 0), Zone, En);
            TestRunner.Eq("Offsite,Standup,Weekly review,Call with London,Design review", string.Join(",", t.Today.Select(r => r.Title)), "today's rows");
            TestRunner.Eq("1:1", string.Join(",", t.Tomorrow.Select(r => r.Title)), "then tomorrow's, six rows in all");
            TestRunner.Check(t.Today[1].Time == "9:00 AM – 9:30 AM" && t.Today[0].Time == "All day", "with their times, or All day");
            TestRunner.Check(t.Hint == "" && t.Note == "" && !t.Dimmed, "nothing to add");
            CalendarTile night = CalendarDay.Tile(Snapshot(Fixture(), At(10, 6, 0, 0)), At(10, 6, 0, 30), Zone, En);
            TestRunner.Check(night.Today.Count == 6 && night.Tomorrow.Count == 0 && night.Today[1].Current, "a full today leaves no room for tomorrow, and the current event is highlighted");
            CalendarTile evening = CalendarDay.Tile(Snapshot(Fixture(), At(10, 6, 0, 0)), At(10, 6, 17, 30), Zone, En);
            TestRunner.Check(evening.Today.Count == 1 && evening.Tomorrow.Count == 4, "in the evening, all of tomorrow");
        }

        static void TileHints()
        {
            CalendarTile signedOut = CalendarDay.Tile(new CalendarSnapshot(), At(10, 6, 8, 0), Zone, En);
            TestRunner.Check(signedOut.Hint == "Connect Google Calendar in ⚙" && signedOut.Today.Count == 0, "not connected: how to connect");
            TestRunner.Eq("Checking…", CalendarDay.Tile(new CalendarSnapshot { SignedIn = true }, At(10, 6, 8, 0), Zone, En).Hint, "signed in, before the first read");
            TestRunner.Eq("Nothing on today or tomorrow", CalendarDay.Tile(Snapshot(new List<CalendarEvent>(), 1), At(10, 6, 8, 0), Zone, En).Hint, "an empty calendar says so");
            CalendarSnapshot away = Snapshot(Fixture(), At(10, 6, 7, 0));
            away.LastFailed = true;
            away.Problem = "Couldn't reach Google";
            CalendarTile kept = CalendarDay.Tile(away, At(10, 6, 8, 0), Zone, En);
            TestRunner.Check(kept.Note == "Couldn't reach Google" && kept.Today.Count == 5 && kept.Dimmed, "Google unreachable: the last list, dimmed, with why");
        }

        static void ReadsTodayAndTomorrow()
        {
            TestRunner.Eq(At(10, 6, 0, 0), CalendarDay.WindowStartMs(At(10, 6, 15, 0), Zone), "from the start of today");
            TestRunner.Eq(At(10, 8, 0, 0), CalendarDay.WindowEndMs(At(10, 6, 15, 0), Zone), "to the end of tomorrow");
            TestRunner.Eq(At(10, 6, 0, 0), CalendarDay.WindowStartMs(At(10, 6, 23, 59), Zone), "local days, not UTC ones");
        }
    }
}
