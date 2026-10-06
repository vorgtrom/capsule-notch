using System;
using System.Collections.Generic;
using System.Linq;

namespace Capsule
{
    // A stand-in for www.googleapis.com/calendar/v3: answers by path (without the query), and keeps every request. The last
    // reply set for a path keeps answering once the earlier ones are used up.
    public sealed class FakeCalendarApi
    {
        public readonly List<CalendarRequest> Requests = new List<CalendarRequest>();
        readonly Dictionary<string, Queue<HttpResult>> replies = new Dictionary<string, Queue<HttpResult>>();
        public HttpResult Otherwise = new HttpResult { Status = 0, Error = "ConnectFailure" };

        public FakeCalendarApi Reply(string path, int status, string body)
        {
            lock (replies)
            {
                if (!replies.ContainsKey(path)) replies[path] = new Queue<HttpResult>();
                replies[path].Enqueue(new HttpResult { Status = status, Body = body ?? "" });
            }
            return this;
        }

        public HttpResult Answer(CalendarRequest request)
        {
            lock (Requests) Requests.Add(request);
            int q = request.Path.IndexOf('?');
            string path = q < 0 ? request.Path : request.Path.Substring(0, q);
            lock (replies)
            {
                Queue<HttpResult> queue;
                if (!replies.TryGetValue(path, out queue) || queue.Count == 0) return Otherwise;
                return queue.Count > 1 ? queue.Dequeue() : queue.Peek();
            }
        }

        public int Count(string pathStart)
        {
            lock (Requests) return Requests.Count(r => r.Path.StartsWith(pathStart, StringComparison.Ordinal));
        }

        public const string ListPath = "users/me/calendarList";
        public static string EventsPathOf(string calendarId) { return "calendars/" + Uri.EscapeDataString(calendarId) + "/events"; }
    }

    public static class GoogleCalendarTests
    {
        public const string Primary = "sample.user@example.com";
        public const string Family = "family0123456789@group.calendar.google.com";
        public const string Holidays = "en.usa#holiday@group.v.calendar.google.com";
        static readonly GoogleCalendar Mine = new GoogleCalendar { Id = Primary, Name = Primary, Color = "#9fe1e7", Selected = true, Primary = true };

        public static void Run()
        {
            ReadsTheCalendarList();
            ReadsEvents();
            AsksForTheDocumentedRequests();
            FailuresAreKept();
            NeverThrowsOnOddReplies();
            WritesAndLongerSpans();
        }

        // The month page (month spec §3): which calendars take new events, a longer span over pages, and adding one.
        static void WritesAndLongerSpans()
        {
            List<GoogleCalendar> list = GoogleCalendarClient.ParseCalendars(TestRunner.Fixture("google-calendar-list.json"));
            TestRunner.Check(list[0].CanWrite && list[1].CanWrite && !list[2].CanWrite && !list[3].CanWrite, "owner and writer calendars take new events; reader ones don't");
            var seen = new List<CalendarRequest>();
            var client = new GoogleCalendarClient("ya29.sample");
            client.Transport = r =>
            {
                seen.Add(r);
                if (r.Method == "POST") return new HttpResult { Status = 200, Body = "{\"id\": \"new1\", \"status\": \"confirmed\", \"summary\": \"Lunch with Sam\", \"start\": {\"dateTime\": \"2026-10-09T12:30:00-07:00\"}, \"end\": {\"dateTime\": \"2026-10-09T13:30:00-07:00\"}}" };
                bool second = r.Path.Contains("pageToken=next");
                return new HttpResult { Status = 200, Body = second ? "{\"items\": [{\"id\": \"late\", \"summary\": \"Late\", \"start\": {\"date\": \"2026-11-01\"}, \"end\": {\"date\": \"2026-11-02\"}}]}" : "{\"items\": [{\"id\": \"early\", \"summary\": \"Early\", \"start\": {\"date\": \"2026-10-01\"}, \"end\": {\"date\": \"2026-10-02\"}}], \"nextPageToken\": \"next\"}" };
            };
            List<CalendarEvent> month = client.MonthEvents(Mine, Utc(2026, 9, 27, 7, 0), Utc(2026, 11, 8, 8, 0));
            TestRunner.Eq("early,late", month == null ? null : string.Join(",", month.Select(e => e.Id)), "a longer span follows the next page");
            TestRunner.Check(seen[0].Path.Contains("&maxResults=250") && !seen[0].Path.Contains("pageToken") && seen[1].Path.EndsWith("&pageToken=next"), "250 a page, then the next page by its token");
            string body = CalendarMonth.EventBody("Lunch with Sam", new DateTime(2026, 10, 9), false, new TimeSpan(12, 30, 0), new TimeSpan(13, 30, 0), CalendarDayTests.Zone);
            CalendarEvent added = client.AddEvent(Mine, body);
            CalendarRequest post = seen.Last();
            TestRunner.Check(post.Method == "POST" && post.Path == "calendars/" + Uri.EscapeDataString(Primary) + "/events" && post.Body == body && post.Headers["Content-Type"].StartsWith("application/json"), "an event is POSTed to its calendar as JSON");
            TestRunner.Check(added != null && added.Id == "new1" && added.Title == "Lunch with Sam" && added.StartMs == Utc(2026, 10, 9, 19, 30) && added.CalendarId == Primary, "and comes back as Google made it");
            client.Transport = r => new HttpResult { Status = 403, Body = "{\"error\": {\"code\": 403, \"errors\": [{\"reason\": \"insufficientPermissions\"}]}}" };
            TestRunner.Check(client.AddEvent(Mine, body) == null && client.LastFailure.Status == 403, "a refused add is none, and the reply is kept");
        }

        static void ReadsTheCalendarList()
        {
            List<GoogleCalendar> list = GoogleCalendarClient.ParseCalendars(TestRunner.Fixture("google-calendar-list.json"));
            TestRunner.Eq(4, list.Count, "four calendars; an entry without an id isn't one");
            TestRunner.Check(list[0].Id == Primary && list[0].Primary && list[0].Selected && list[0].Color == "#9fe1e7", "the primary calendar, selected, with its colour");
            TestRunner.Check(list[1].Name == "Family" && list[1].Selected && !list[1].Primary, "a shared calendar by its name");
            TestRunner.Check(list[2].Id == Holidays && list[2].Name == "US holidays" && !list[2].Selected, "the user's own name for a calendar wins, and one not selected in Google says so");
            TestRunner.Eq(GoogleCalendarClient.DefaultColor, list[3].Color, "a colour that isn't #rrggbb falls back to Google blue");
        }

        static CalendarEvent Find(List<CalendarEvent> events, string id) { return events.FirstOrDefault(e => e.Id == id); }

        static long Utc(int year, int month, int day, int hour, int minute) { return Clock.ToMs(new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Utc)); }

        // Every kind of event the module has to tell apart, from one reply.
        static void ReadsEvents()
        {
            List<CalendarEvent> events = GoogleCalendarClient.ParseEvents(TestRunner.Fixture("google-events.json"), Mine);
            TestRunner.Eq("nightdeploy,offsite,standup,weekly_20261006T200000Z,london,designreview,oneonone,untitled,lunch,planning", string.Join(",", events.Select(e => e.Id)),
                "cancelled, declined, working-location and start-less events are left out; the rest keep Google's order");
            CalendarEvent standup = Find(events, "standup");
            TestRunner.Check(standup != null && !standup.AllDay && standup.Title == "Standup" && standup.CalendarId == Primary && standup.Color == "#9fe1e7", "a timed event, with its calendar and colour");
            TestRunner.Check(standup != null && standup.StartMs == Utc(2026, 10, 6, 16, 0) && standup.EndMs == Utc(2026, 10, 6, 16, 30), "its times in UTC");
            CalendarEvent offsite = Find(events, "offsite");
            TestRunner.Check(offsite != null && offsite.AllDay && offsite.StartDay == new DateTime(2026, 10, 6) && offsite.EndDay == new DateTime(2026, 10, 7), "an all-day event keeps its dates, the end exclusive");
            CalendarEvent weekly = Find(events, "weekly_20261006T200000Z");
            TestRunner.Check(weekly != null && weekly.Title == "Weekly review" && weekly.StartMs == Utc(2026, 10, 6, 20, 0), "a repeating event's instance is an event like any other");
            CalendarEvent london = Find(events, "london");
            TestRunner.Check(london != null && london.StartMs == Utc(2026, 10, 6, 22, 0) && london.EndMs == Utc(2026, 10, 6, 23, 0), "an event written in London's time");
            CalendarEvent design = Find(events, "designreview");
            TestRunner.Check(design != null && design.StartMs == Utc(2026, 10, 6, 23, 30) && design.EndMs == Utc(2026, 10, 7, 0, 15), "and one written in UTC");
            CalendarEvent night = Find(events, "nightdeploy");
            TestRunner.Check(night != null && night.StartMs == Utc(2026, 10, 6, 6, 0) && night.EndMs == Utc(2026, 10, 6, 8, 0), "an event across midnight");
            TestRunner.Eq(GoogleCalendarClient.NoTitle, Find(events, "untitled") != null ? Find(events, "untitled").Title : null, "an event without a title");
            TestRunner.Eq("Lunch", Find(events, "lunch") != null ? Find(events, "lunch").Title : null, "titles are trimmed");
            TestRunner.Check(Find(events, "oneonone") != null, "a tentative answer still counts");
        }

        static void AsksForTheDocumentedRequests()
        {
            var api = new FakeCalendarApi()
                .Reply(FakeCalendarApi.ListPath, 200, TestRunner.Fixture("google-calendar-list.json"))
                .Reply(FakeCalendarApi.EventsPathOf(Holidays), 200, TestRunner.Fixture("google-events.json"));
            var client = new GoogleCalendarClient("ya29.sample");
            client.Transport = api.Answer;
            TestRunner.Eq(4, client.Calendars().Count, "the list through the client");
            TestRunner.Eq("users/me/calendarList?maxResults=250", api.Requests[0].Path, "from users/me/calendarList");
            TestRunner.Eq("Bearer ya29.sample", api.Requests[0].Headers["Authorization"], "with the access token as a bearer token");
            var holidays = new GoogleCalendar { Id = Holidays, Color = "#16a765" };
            List<CalendarEvent> events = client.Events(holidays, Utc(2026, 10, 6, 7, 0), Utc(2026, 10, 8, 7, 0));
            TestRunner.Check(events != null && events.Count == 10 && events.All(e => e.CalendarId == Holidays && e.Color == "#16a765"), "events carry their calendar and its colour");
            TestRunner.Eq("calendars/en.usa%23holiday%40group.v.calendar.google.com/events?timeMin=2026-10-06T07%3A00%3A00Z&timeMax=2026-10-08T07%3A00%3A00Z&singleEvents=true&orderBy=startTime&maxResults=50",
                api.Requests[1].Path, "the calendar id and the times are escaped; repeating events expanded, in start order, at most 50");
        }

        static void FailuresAreKept()
        {
            var api = new FakeCalendarApi().Reply(FakeCalendarApi.ListPath, 401, "{\"error\": {\"code\": 401, \"message\": \"Invalid Credentials\"}}");
            var client = new GoogleCalendarClient("expired");
            client.Transport = api.Answer;
            TestRunner.Check(client.Calendars() == null && client.LastFailure != null && client.LastFailure.Status == 401, "a 401 is no list, and the reply is kept");
            TestRunner.Check(client.Events(Mine, 0, 1) == null && client.LastFailure.Status == 0, "no reply at all: status 0");
            TestRunner.Eq("Couldn't reach Google", GoogleCalendarClient.Problem(client.LastFailure), "said as the tile says it");
            TestRunner.Eq("no reply (ConnectFailure)", GoogleCalendarClient.Describe(client.LastFailure), "and logged as a status");
            TestRunner.Eq("Google is having trouble (HTTP 503)", GoogleCalendarClient.Problem(new HttpResult { Status = 503 }), "a server error");
            TestRunner.Eq("HTTP 429", GoogleCalendarClient.Describe(new HttpResult { Status = 429, Body = "{\"secret\": \"body\"}" }), "the log never gets a body");
        }

        static void NeverThrowsOnOddReplies()
        {
            var api = new FakeCalendarApi().Reply(FakeCalendarApi.ListPath, 200, "<html>captive portal</html>").Reply(FakeCalendarApi.EventsPathOf(Primary), 200, "{\"items\": [1, \"x\", {\"start\": {\"dateTime\": \"not a time\"}}, {\"start\": {\"date\": \"2026-13-45\"}}]}");
            var client = new GoogleCalendarClient("t");
            client.Transport = api.Answer;
            TestRunner.Check(client.Calendars() == null && client.LastFailure.Status == 200 && GoogleCalendarClient.Problem(client.LastFailure) == "Unexpected reply from Google", "a 200 that isn't JSON is an unexpected reply");
            List<CalendarEvent> events = client.Events(Mine, 0, 1);
            TestRunner.Check(events != null && events.Count == 0, "items that aren't events, or whose start can't be read, are skipped");
        }
    }
}
