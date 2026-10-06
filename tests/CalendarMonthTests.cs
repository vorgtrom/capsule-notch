using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Capsule
{
    public static class CalendarMonthTests
    {
        static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");
        static readonly CultureInfo Gb = CultureInfo.GetCultureInfo("en-GB");

        public static void Run()
        {
            SixWeeksFromTheFirstDayOfTheWeek();
            DotsMarkBusyDays();
            TheSelectedDayListsEventsThenTasks();
            TimesAreReadAsTyped();
            TheEventFormIsChecked();
            BodiesAreWhatGoogleExpects();
            RowsSayWhatCanChange();
            AnEditIsShownAsTyped();
        }

        static void AnEditIsShownAsTyped()
        {
            CalendarEvent timed = CalendarMonth.EditedEvent(" Lunch\r\nwith Sam ", new DateTime(2026, 10, 9), false, new TimeSpan(12, 30, 0), new TimeSpan(13, 30, 0), CalendarDayTests.Zone);
            TestRunner.Check(timed.Title == "Lunch with Sam" && !timed.AllDay && timed.StartMs == CalendarDayTests.At(10, 9, 12, 30) && timed.EndMs == CalendarDayTests.At(10, 9, 13, 30), "an edited event's title and times, in the PC's zone");
            CalendarEvent allDay = CalendarMonth.EditedEvent("", new DateTime(2026, 10, 9), true, TimeSpan.Zero, TimeSpan.Zero, CalendarDayTests.Zone);
            TestRunner.Check(allDay.AllDay && allDay.StartDay == new DateTime(2026, 10, 9) && allDay.EndDay == new DateTime(2026, 10, 10) && allDay.Title == GoogleCalendarClient.NoTitle, "an all-day one's day; no title shows as Google shows it");
        }

        static void RowsSayWhatCanChange()
        {
            MonthModel m = October(En, new DateTime(2026, 10, 6));
            CalendarRow offsite = m.Events[0], night = m.Events[1], standup = m.Events[2], weekly = m.Events[3];
            TestRunner.Check(offsite.CanEdit && offsite.CanDelete && offsite.AllDay && offsite.Id == "offsite" && offsite.CalendarId == GoogleCalendarTests.Primary, "the user's own all-day event: edit and delete, with its ids");
            TestRunner.Check(weekly.CanEdit && weekly.CanDelete && !weekly.AllDay && weekly.StartText == "1:00 PM" && weekly.EndText == "2:00 PM", "a timed one on the day, with its times for the form");
            TestRunner.Check(!night.CanEdit && night.CanDelete, "one over two days: delete only, as the form keeps to a day");
            TestRunner.Check(!standup.CanEdit && !standup.CanDelete, "one someone else organised: neither");
            string timed = CalendarMonth.EventBody("Offsite", new DateTime(2026, 10, 9), false, TimeSpan.FromHours(9), TimeSpan.FromHours(10), CalendarDayTests.Zone, true);
            string allDay = CalendarMonth.EventBody("Offsite", new DateTime(2026, 10, 9), true, TimeSpan.Zero, TimeSpan.Zero, CalendarDayTests.Zone, true);
            TestRunner.Check(timed.Contains("\"date\": null") && !timed.Contains("\"dateTime\": null") && allDay.Contains("\"dateTime\": null") && !allDay.Contains("\"date\": null"),
                "an edit's body clears the kind of time the event no longer has");
        }

        static List<GoogleTask> Tasks()
        {
            return new List<GoogleTask>
            {
                new GoogleTask { Id = "t1", ListId = "l", Title = "Send the invoice", Due = new DateTime(2026, 10, 6) },
                new GoogleTask { Id = "t2", ListId = "l", Title = "Water the plants", Due = new DateTime(2026, 10, 6), Done = true },
                new GoogleTask { Id = "t3", ListId = "l", Title = "Book flights", Due = new DateTime(2026, 10, 20) },
            };
        }

        static MonthModel October(CultureInfo culture, DateTime selected)
        {
            return CalendarMonth.Build(2026, 10, selected, CalendarDayTests.Fixture(), Tasks(), CalendarDayTests.At(10, 6, 8, 0), CalendarDayTests.Zone, culture);
        }

        static void SixWeeksFromTheFirstDayOfTheWeek()
        {
            MonthModel sunday = October(En, new DateTime(2026, 10, 6));
            TestRunner.Check(sunday.Days.Count == 42 && sunday.Days[0].Date == new DateTime(2026, 9, 27) && sunday.Days[41].Date == new DateTime(2026, 11, 7), "en-US: six weeks from Sunday 27 September");
            TestRunner.Eq("Sun,Mon,Tue,Wed,Thu,Fri,Sat", string.Join(",", sunday.Weekdays), "headed Sunday first");
            TestRunner.Eq("October 2026", sunday.Title, "the month's name");
            MonthModel monday = October(Gb, new DateTime(2026, 10, 6));
            TestRunner.Check(monday.Days[0].Date == new DateTime(2026, 9, 28) && monday.Weekdays[0] == "Mon", "en-GB: from Monday 28 September");
            TestRunner.Check(!sunday.Days[0].InMonth && sunday.Days[4].InMonth && sunday.Days[4].Date.Day == 1 && !sunday.Days[41].InMonth, "days of the months around it are marked");
            MonthDay today = sunday.Days.First(d => d.Today);
            TestRunner.Check(today.Date == new DateTime(2026, 10, 6) && today.Selected && sunday.Days.Count(d => d.Today) == 1, "today, selected");
            TestRunner.Eq(new DateTime(2026, 11, 1), CalendarMonth.FirstCell(2026, 11, En), "a month starting on a Sunday starts the grid itself");
            TestRunner.Eq(CalendarDayTests.At(9, 27, 0, 0), CalendarMonth.SpanStartMs(2026, 10, En, CalendarDayTests.Zone), "the page reads from the grid's first local midnight");
            TestRunner.Eq(CalendarDayTests.At(11, 8, 0, 0), CalendarMonth.SpanEndMs(2026, 10, En, CalendarDayTests.Zone), "to the one after its last day");
        }

        static void DotsMarkBusyDays()
        {
            MonthModel m = October(En, new DateTime(2026, 10, 6));
            MonthDay sixth = m.Days.First(d => d.Date == new DateTime(2026, 10, 6));
            TestRunner.Check(sixth.Dots.Count == 3 && sixth.More, "a busy day: three dots, and more");
            TestRunner.Check(sixth.Dots[0] == "#9fe1e7", "in its calendar's colour, all-day events first");
            MonthDay fifth = m.Days.First(d => d.Date == new DateTime(2026, 10, 5));
            TestRunner.Check(fifth.Dots.Count == 1 && !fifth.More, "the night deploy marks the day it starts on");
            MonthDay twentieth = m.Days.First(d => d.Date == new DateTime(2026, 10, 20));
            TestRunner.Check(twentieth.Dots.Count == 1 && twentieth.Dots[0] == CalendarMonth.TaskDot, "a task's day gets a grey dot");
            var trip = new CalendarEvent { Title = "Trip", AllDay = true, StartDay = new DateTime(2026, 10, 12), EndDay = new DateTime(2026, 10, 15), Color = "#f83a22" };
            TestRunner.Eq("2026-10-12,2026-10-13,2026-10-14", string.Join(",", CalendarMonth.DaysOf(trip, CalendarDayTests.Zone).Select(d => d.ToString("yyyy-MM-dd"))), "an all-day event marks each of its days, not the one it ends before");
        }

        static void TheSelectedDayListsEventsThenTasks()
        {
            MonthModel m = October(En, new DateTime(2026, 10, 6));
            TestRunner.Eq("Tue 6 Oct", m.SelectedTitle, "the selected day's name");
            TestRunner.Eq("Offsite,Night deploy,Standup,Weekly review,Call with London,Design review", string.Join(",", m.Events.Select(r => r.Title)), "all-day first, then the day's timed events, the one begun the night before too");
            TestRunner.Check(m.Events[0].Time == "All day" && m.Events[2].Time == "9:00 AM – 9:30 AM", "with times as on the tile");
            TestRunner.Eq("Send the invoice,Water the plants", string.Join(",", m.Tasks.Select(t => t.Title)), "then the day's tasks, not done first");
            TestRunner.Check(m.Tasks[1].Done && m.Tasks[0].Id == "t1" && m.Tasks[0].ListId == "l", "with their state and ids");
            MonthModel quiet = October(En, new DateTime(2026, 10, 25));
            TestRunner.Check(quiet.Events.Count == 0 && quiet.Tasks.Count == 0 && quiet.Empty == "Nothing on this day", "a free day says so");
        }

        static void TimesAreReadAsTyped()
        {
            TimeSpan t;
            TestRunner.Check(CalendarMonth.ParseTime("2:30 PM", En, out t) && t == new TimeSpan(14, 30, 0), "the culture's own: 2:30 PM");
            TestRunner.Check(CalendarMonth.ParseTime("14:30", Gb, out t) && t == new TimeSpan(14, 30, 0), "14:30");
            TestRunner.Check(CalendarMonth.ParseTime("2:30p", En, out t) && t == new TimeSpan(14, 30, 0) && CalendarMonth.ParseTime("2pm", En, out t) && t == TimeSpan.FromHours(14), "2:30p and 2pm");
            TestRunner.Check(CalendarMonth.ParseTime("12am", En, out t) && t == TimeSpan.Zero && CalendarMonth.ParseTime("12 PM", En, out t) && t == TimeSpan.FromHours(12), "midnight and noon");
            TestRunner.Check(CalendarMonth.ParseTime("14", En, out t) && t == TimeSpan.FromHours(14) && CalendarMonth.ParseTime("9.15", Gb, out t) && t == new TimeSpan(9, 15, 0), "a bare hour, and a dot");
            TestRunner.Check(!CalendarMonth.ParseTime("25", En, out t) && !CalendarMonth.ParseTime("13pm", En, out t) && !CalendarMonth.ParseTime("9:75", En, out t) && !CalendarMonth.ParseTime("lunch", En, out t) && !CalendarMonth.ParseTime("", En, out t), "nonsense refused");
            TestRunner.Eq("2:30 PM", CalendarMonth.TimeText(new TimeSpan(14, 30, 0), En), "shown in the culture's short time");
            TestRunner.Eq(TimeSpan.FromHours(9), CalendarMonth.DefaultStart(new DateTime(2026, 10, 9), new DateTime(2026, 10, 6, 15, 20, 0)), "another day starts at 9:00");
            TestRunner.Eq(TimeSpan.FromHours(16), CalendarMonth.DefaultStart(new DateTime(2026, 10, 6), new DateTime(2026, 10, 6, 15, 20, 0)), "today, at the next whole hour");
            TestRunner.Check(CalendarMonth.DefaultEnd(TimeSpan.FromHours(16)) == TimeSpan.FromHours(17) && CalendarMonth.DefaultEnd(TimeSpan.FromHours(23)) == new TimeSpan(23, 59, 0), "and ends an hour later, within the day");
        }

        static void TheEventFormIsChecked()
        {
            TimeSpan s, e;
            TestRunner.Eq(CalendarMonth.TitleNeeded, CalendarMonth.CheckEvent("  ", true, "", "", En, out s, out e), "a title is needed");
            TestRunner.Eq(null, CalendarMonth.CheckEvent("Offsite", true, "garbage", "", En, out s, out e), "an all-day event needs no times");
            TestRunner.Eq("Start: type a time like 2:30 PM", CalendarMonth.CheckEvent("Lunch", false, "noonish", "1pm", En, out s, out e), "a start that isn't a time says how to write one");
            TestRunner.Eq("End: type a time like 14:30", CalendarMonth.CheckEvent("Lunch", false, "12:00", "x", Gb, out s, out e), "in the culture's format");
            TestRunner.Eq(CalendarMonth.EndBeforeStart, CalendarMonth.CheckEvent("Lunch", false, "1pm", "12pm", En, out s, out e), "an end before the start is refused");
            TestRunner.Check(CalendarMonth.CheckEvent("Lunch", false, "12pm", "1:15pm", En, out s, out e) == null && s == TimeSpan.FromHours(12) && e == new TimeSpan(13, 15, 0), "a good one gives its times");
            TestRunner.Eq("one line", CalendarMonth.CleanTitle(" one\r\nline ").Replace("  ", " "), "a title is one line");
        }

        static void BodiesAreWhatGoogleExpects()
        {
            object timed = Json.TryParse(CalendarMonth.EventBody("Lunch with Sam", new DateTime(2026, 10, 9), false, new TimeSpan(12, 30, 0), new TimeSpan(13, 30, 0), CalendarDayTests.Zone));
            TestRunner.Check(Json.Str(Json.Get(timed, "summary")) == "Lunch with Sam" && Json.Str(Json.Get(timed, "start", "dateTime")) == "2026-10-09T12:30:00-07:00" && Json.Str(Json.Get(timed, "end", "dateTime")) == "2026-10-09T13:30:00-07:00", "a timed event: its times with this PC's offset");
            TestRunner.Check(Json.Get(timed, "start", "timeZone") == null && Json.Get(timed, "start", "date") == null, "and no zone name, no date");
            object allDay = Json.TryParse(CalendarMonth.EventBody("Offsite", new DateTime(2026, 10, 31), true, TimeSpan.Zero, TimeSpan.Zero, CalendarDayTests.Zone));
            TestRunner.Check(Json.Str(Json.Get(allDay, "start", "date")) == "2026-10-31" && Json.Str(Json.Get(allDay, "end", "date")) == "2026-11-01" && Json.Get(allDay, "start", "dateTime") == null, "an all-day event: its date, and the next as the end");
            TimeZoneInfo plus = TimeZoneInfo.CreateCustomTimeZone("Capsule test UTC+5:30", new TimeSpan(5, 30, 0), "x", "x");
            TestRunner.Eq("2026-10-09T09:00:00+05:30", CalendarMonth.Rfc3339(new DateTime(2026, 10, 9, 9, 0, 0), plus), "a positive offset with minutes");
        }
    }
}
