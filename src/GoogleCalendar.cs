using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Capsule
{
    // One calendar in the user's list. Its name is shown on screen only: never logged or written.
    public sealed class GoogleCalendar
    {
        public string Id = "";
        public string Name = "";
        public string Color = GoogleCalendarClient.DefaultColor;   // "#rrggbb"
        public bool Selected;   // shown in Google Calendar: the default for whether Capsule shows it
        public bool Primary;    // the account's own calendar; its id is the account's email
        public bool CanWrite;   // the user may add events to it (owner or writer)
    }

    // One event, in memory only: its title is shown on screen, never logged or written.
    public sealed class CalendarEvent
    {
        public string Id = "";
        public string CalendarId = "";
        public string Title = "";
        public string Color = GoogleCalendarClient.DefaultColor;
        public bool AllDay;
        public long StartMs, EndMs;      // timed events: UTC milliseconds, whatever time zone they were written in
        public DateTime StartDay, EndDay; // all-day events: their dates, the end exclusive, as Google gives them
    }

    // One request to the Calendar API, as tests see it.
    public sealed class CalendarRequest
    {
        public string Method = "GET";
        public string Path = "";   // after the API's base (www.googleapis.com/calendar/v3/, tasks.googleapis.com/tasks/v1/)
        public string Body;        // JSON, or null
        public Dictionary<string, string> Headers = new Dictionary<string, string>();
    }

    // Google Calendar's API (calendar spec §3, month spec §3): the calendar list, each calendar's events for a span of time
    // with repeating events expanded, and adding an event. Never throws: a call that fails returns null and leaves the reply in
    // LastFailure. The access token only ever goes into the Authorization header; nothing here logs.
    public sealed class GoogleCalendarClient
    {
        public const string Base = "https://www.googleapis.com/calendar/v3/";
        public const string DefaultColor = "#4285F4";
        public const int TimeoutMs = 15000, MaxResults = 50, MonthResults = 250, MaxPages = 4;
        public const string NoTitle = "(No title)";
        static readonly Regex HexColor = new Regex("^#[0-9A-Fa-f]{6}$");

        readonly string accessToken;

        public Func<CalendarRequest, HttpResult> Transport;   // set by tests; null = www.googleapis.com
        public HttpResult LastFailure { get; private set; }

        public GoogleCalendarClient(string accessToken) { this.accessToken = accessToken; }

        // The calendars in the user's list, in Google's order.
        public List<GoogleCalendar> Calendars()
        {
            LastFailure = null;
            HttpResult r = Send("users/me/calendarList?maxResults=250");
            if (r.Status != 200) return Fail<List<GoogleCalendar>>(r);
            return ParseCalendars(r.Body) ?? Fail<List<GoogleCalendar>>(Unexpected());
        }

        // A calendar's events that end after fromMs and start before toMs, repeating ones as single instances, in start
        // order, without the cancelled ones, the ones the user declined, and working-location markers.
        public List<CalendarEvent> Events(GoogleCalendar calendar, long fromMs, long toMs)
        {
            LastFailure = null;
            HttpResult r = Send(EventsPath(calendar.Id, fromMs, toMs));
            if (r.Status != 200) return Fail<List<CalendarEvent>>(r);
            return ParseEvents(r.Body, calendar) ?? Fail<List<CalendarEvent>>(Unexpected());
        }

        // A longer span (the month page's six weeks): up to MonthResults events a page, following up to MaxPages pages.
        public List<CalendarEvent> MonthEvents(GoogleCalendar calendar, long fromMs, long toMs)
        {
            LastFailure = null;
            var all = new List<CalendarEvent>();
            string page = null;
            for (int i = 0; i < MaxPages; i++)
            {
                HttpResult r = Send(EventsPath(calendar.Id, fromMs, toMs, MonthResults, page));
                if (r.Status != 200) return Fail<List<CalendarEvent>>(r);
                List<CalendarEvent> some = ParseEvents(r.Body, calendar);
                if (some == null) return Fail<List<CalendarEvent>>(Unexpected());
                all.AddRange(some);
                page = Json.Str(Json.Get(Json.TryParse(r.Body), "nextPageToken"));
                if (string.IsNullOrEmpty(page)) break;
            }
            return all;
        }

        // Adds an event (its body from CalendarMonth.EventBody) to a calendar: the event as Google made it, or null.
        public CalendarEvent AddEvent(GoogleCalendar calendar, string body)
        {
            LastFailure = null;
            HttpResult r = Send("POST", "calendars/" + Uri.EscapeDataString(calendar.Id) + "/events", body);
            if (r.Status != 200) return Fail<CalendarEvent>(r);
            CalendarEvent e = ParseEvent(Json.TryParse(r.Body), calendar);
            return e ?? Fail<CalendarEvent>(Unexpected());
        }

        public static string EventsPath(string calendarId, long fromMs, long toMs)
        {
            return EventsPath(calendarId, fromMs, toMs, MaxResults, null);
        }

        public static string EventsPath(string calendarId, long fromMs, long toMs, int results, string page)
        {
            return "calendars/" + Uri.EscapeDataString(calendarId) + "/events?timeMin=" + Uri.EscapeDataString(Iso(fromMs))
                + "&timeMax=" + Uri.EscapeDataString(Iso(toMs)) + "&singleEvents=true&orderBy=startTime&maxResults=" + results
                + (page != null ? "&pageToken=" + Uri.EscapeDataString(page) : "");
        }

        static string Iso(long ms) { return Clock.FromMs(ms).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture); }

        T Fail<T>(HttpResult reply) where T : class
        {
            LastFailure = reply;
            return null;
        }

        // A 200 whose body isn't what Google documents.
        static HttpResult Unexpected() { return new HttpResult { Status = 200, Error = "unexpected reply" }; }

        HttpResult Send(string path) { return Send("GET", path, null); }

        HttpResult Send(string method, string path, string body)
        {
            var request = new CalendarRequest { Method = method, Path = path, Body = body };
            request.Headers["Authorization"] = "Bearer " + accessToken;
            if (body != null) request.Headers["Content-Type"] = "application/json; charset=utf-8";
            if (Transport != null) return Transport(request);
            return Http.Send(method, Base + path, request.Headers, body, TimeoutMs);
        }

        // A calendarList reply's calendars; null when it isn't one. Entries without an id are skipped. The name is the
        // user's own for it (summaryOverride) if they gave one.
        public static List<GoogleCalendar> ParseCalendars(string json)
        {
            object[] items = Json.Arr(Json.Get(Json.TryParse(json), "items"));
            if (items == null) return null;
            var list = new List<GoogleCalendar>();
            foreach (object item in items)
            {
                string id = Json.Str(Json.Get(item, "id"));
                if (string.IsNullOrEmpty(id)) continue;
                string name = Json.Str(Json.Get(item, "summaryOverride"));
                if (string.IsNullOrEmpty(name)) name = Json.Str(Json.Get(item, "summary"));
                list.Add(new GoogleCalendar
                {
                    Id = id,
                    Name = string.IsNullOrEmpty(name) ? id : name.Trim(),
                    Color = Color(Json.Str(Json.Get(item, "backgroundColor"))),
                    Selected = Json.Get(item, "selected") as bool? ?? false,
                    Primary = Json.Get(item, "primary") as bool? ?? false,
                    CanWrite = Json.Str(Json.Get(item, "accessRole")) == "owner" || Json.Str(Json.Get(item, "accessRole")) == "writer",
                });
            }
            return list;
        }

        public static string Color(string hex) { return hex != null && HexColor.IsMatch(hex) ? hex : DefaultColor; }

        // An events reply's events; null when it isn't one. An event without a readable start is skipped.
        public static List<CalendarEvent> ParseEvents(string json, GoogleCalendar calendar)
        {
            object[] items = Json.Arr(Json.Get(Json.TryParse(json), "items"));
            if (items == null) return null;
            var list = new List<CalendarEvent>();
            foreach (object item in items)
            {
                CalendarEvent e = ParseEvent(item, calendar);
                if (e != null) list.Add(e);
            }
            return list;
        }

        // One event resource; null when it isn't one to show: cancelled, declined, a working-location marker, or without a
        // readable start.
        public static CalendarEvent ParseEvent(object item, GoogleCalendar calendar)
        {
            if (Json.Obj(item) == null) return null;
            if (Json.Str(Json.Get(item, "status")) == "cancelled") return null;
            if (Json.Str(Json.Get(item, "eventType")) == "workingLocation") return null;
            if (Declined(item)) return null;
            var e = new CalendarEvent
            {
                Id = Json.Str(Json.Get(item, "id")) ?? "",
                CalendarId = calendar.Id,
                Color = calendar.Color,
            };
            string title = (Json.Str(Json.Get(item, "summary")) ?? "").Trim();
            e.Title = title != "" ? title : NoTitle;
            string startTime = Json.Str(Json.Get(item, "start", "dateTime"));
            string startDate = Json.Str(Json.Get(item, "start", "date"));
            if (startTime != null)
            {
                long start = Time(startTime);
                if (start == 0) return null;
                long end = Time(Json.Str(Json.Get(item, "end", "dateTime")));
                e.StartMs = start;
                e.EndMs = Math.Max(start, end);
            }
            else
            {
                DateTime day;
                if (!Date(startDate, out day)) return null;
                DateTime endDay;
                e.AllDay = true;
                e.StartDay = day;
                e.EndDay = Date(Json.Str(Json.Get(item, "end", "date")), out endDay) && endDay > day ? endDay : day.AddDays(1);
            }
            return e;
        }

        // The user is an attendee and said no.
        static bool Declined(object item)
        {
            foreach (object a in Json.Arr(Json.Get(item, "attendees")) ?? new object[0])
                if ((Json.Get(a, "self") as bool? ?? false) && Json.Str(Json.Get(a, "responseStatus")) == "declined") return true;
            return false;
        }

        // An RFC 3339 time ("2026-10-06T09:00:00-07:00", "…Z") as UTC milliseconds; 0 if it can't be read.
        static long Time(string text)
        {
            DateTimeOffset value;
            if (string.IsNullOrEmpty(text) || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out value)) return 0;
            return Clock.ToMs(value.UtcDateTime);
        }

        static bool Date(string text, out DateTime day)
        {
            day = DateTime.MinValue;
            return text != null && DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);
        }

        // What a failed call means, in a line for the tile or the settings.
        public static string Problem(HttpResult reply)
        {
            if (reply == null) return "";
            if (reply.Status == 0) return "Couldn't reach Google";
            if (reply.Status == 401) return "Google didn't accept the sign-in";
            if (reply.Status == 403) return "Google refused (HTTP 403). Is the Calendar API on for your client?";
            if (reply.Status == 429) return "Google asked Capsule to slow down";
            if (reply.Status >= 500) return "Google is having trouble (HTTP " + reply.Status + ")";
            if (reply.Status == 200) return "Unexpected reply from Google";
            return "Google returned HTTP " + reply.Status;
        }

        // A failure for the log: its status, never a body.
        public static string Describe(HttpResult reply)
        {
            if (reply == null) return "";
            return reply.Status == 0 ? "no reply (" + reply.Error + ")" : "HTTP " + reply.Status;
        }
    }
}
