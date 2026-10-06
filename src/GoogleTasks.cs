using System;
using System.Collections.Generic;
using System.Globalization;

namespace Capsule
{
    // One of the user's Google Tasks lists ("My Tasks"). Its title is shown on screen only.
    public sealed class TaskList
    {
        public string Id = "";
        public string Title = "";
    }

    // One task with a due date, in memory only: its title is shown on screen, never logged or written.
    public sealed class GoogleTask
    {
        public string Id = "";
        public string ListId = "";
        public string Title = "";
        public DateTime Due;   // the date it is due; Google Tasks keeps no time
        public bool Done;
    }

    // Google Tasks' API (month spec §3): the lists, the tasks due in a span of days, adding one, ticking one done or
    // undone, renaming one, and deleting one. Never throws: a call that fails returns null (or false) and leaves the reply
    // in LastFailure. The access token only ever goes into the Authorization header; nothing here logs.
    public sealed class GoogleTasksClient
    {
        public const string Base = "https://tasks.googleapis.com/tasks/v1/";
        public const int TimeoutMs = 15000, MaxPages = 4;

        readonly string accessToken;

        public Func<CalendarRequest, HttpResult> Transport;   // set by tests; null = tasks.googleapis.com
        public HttpResult LastFailure { get; private set; }

        public GoogleTasksClient(string accessToken) { this.accessToken = accessToken; }

        public List<TaskList> Lists()
        {
            LastFailure = null;
            HttpResult r = Send("GET", "users/@me/lists?maxResults=100", null);
            if (r.Status != 200) return Fail<List<TaskList>>(r);
            return ParseLists(r.Body) ?? Fail<List<TaskList>>(Unexpected());
        }

        // A list's tasks due from 'first' through 'last' (dates), done ones too, following up to MaxPages pages.
        public List<GoogleTask> Tasks(TaskList list, DateTime first, DateTime last)
        {
            LastFailure = null;
            var all = new List<GoogleTask>();
            string page = null;
            for (int i = 0; i < MaxPages; i++)
            {
                HttpResult r = Send("GET", TasksPath(list.Id, first, last, page), null);
                if (r.Status != 200) return Fail<List<GoogleTask>>(r);
                List<GoogleTask> some = ParseTasks(r.Body, list.Id);
                if (some == null) return Fail<List<GoogleTask>>(Unexpected());
                all.AddRange(some);
                page = Json.Str(Json.Get(Json.TryParse(r.Body), "nextPageToken"));
                if (string.IsNullOrEmpty(page)) break;
            }
            return all;
        }

        public static string TasksPath(string listId, DateTime first, DateTime last, string page)
        {
            return "lists/" + Uri.EscapeDataString(listId) + "/tasks?dueMin=" + Uri.EscapeDataString(DueText(first))
                + "&dueMax=" + Uri.EscapeDataString(DueText(last.AddDays(1))) + "&showCompleted=true&showHidden=true&maxResults=100"
                + (page != null ? "&pageToken=" + Uri.EscapeDataString(page) : "");
        }

        // A new task due on a day. Null when it couldn't be added.
        public GoogleTask Add(string listId, string title, DateTime due)
        {
            LastFailure = null;
            HttpResult r = Send("POST", "lists/" + Uri.EscapeDataString(listId) + "/tasks", AddBody(title, due));
            if (r.Status != 200) return Fail<GoogleTask>(r);
            GoogleTask t = Task(Json.TryParse(r.Body), listId);
            return t ?? Fail<GoogleTask>(Unexpected());
        }

        public static string AddBody(string title, DateTime due)
        {
            return Json.Write(new Dictionary<string, object> { { "title", title }, { "due", DueText(due) } });
        }

        // Ticks a task done, or back to not done. False when Google didn't take it.
        public bool SetDone(GoogleTask task, bool done)
        {
            LastFailure = null;
            HttpResult r = Send("PATCH", TaskPath(task), DoneBody(done));
            if (r.Status == 200) return true;
            LastFailure = r;
            return false;
        }

        // Gives a task a new title. False when Google didn't take it.
        public bool Rename(GoogleTask task, string title)
        {
            LastFailure = null;
            HttpResult r = Send("PATCH", TaskPath(task), Json.Write(new Dictionary<string, object> { { "title", title } }));
            if (r.Status == 200) return true;
            LastFailure = r;
            return false;
        }

        // Deletes a task. One already gone counts as deleted.
        public bool Delete(GoogleTask task)
        {
            LastFailure = null;
            HttpResult r = Send("DELETE", TaskPath(task), null);
            if (r.Status == 200 || r.Status == 204 || r.Status == 404) return true;
            LastFailure = r;
            return false;
        }

        static string TaskPath(GoogleTask task) { return "lists/" + Uri.EscapeDataString(task.ListId) + "/tasks/" + Uri.EscapeDataString(task.Id); }

        // Undone, the completion time goes too, or Google keeps the task completed.
        public static string DoneBody(bool done)
        {
            var body = new Dictionary<string, object> { { "status", done ? "completed" : "needsAction" } };
            if (!done) body["completed"] = null;
            return Json.Write(body);
        }

        // Google's 403 when the Cloud project the client belongs to hasn't turned the Tasks API on: a new sign-in won't fix
        // it, turning the API on will. The reply is only looked at, never logged.
        public static bool ApiOff(HttpResult reply)
        {
            return reply != null && reply.Status == 403 && reply.Body != null
                && (reply.Body.Contains("accessNotConfigured") || reply.Body.Contains("SERVICE_DISABLED"));
        }

        // Google Tasks stores a due date as that date's midnight in UTC.
        public static string DueText(DateTime day)
        {
            return day.Date.ToString("yyyy-MM-dd'T'00:00:00.000'Z'", CultureInfo.InvariantCulture);
        }

        T Fail<T>(HttpResult reply) where T : class
        {
            LastFailure = reply;
            return null;
        }

        static HttpResult Unexpected() { return new HttpResult { Status = 200, Error = "unexpected reply" }; }

        HttpResult Send(string method, string path, string body)
        {
            var request = new CalendarRequest { Method = method, Path = path, Body = body };
            request.Headers["Authorization"] = "Bearer " + accessToken;
            if (body != null) request.Headers["Content-Type"] = "application/json; charset=utf-8";
            if (Transport != null) return Transport(request);
            return Http.Send(method, Base + path, request.Headers, body, TimeoutMs);
        }

        public static List<TaskList> ParseLists(string json)
        {
            object[] items = Json.Arr(Json.Get(Json.TryParse(json), "items"));
            if (items == null) return null;
            var lists = new List<TaskList>();
            foreach (object item in items)
            {
                string id = Json.Str(Json.Get(item, "id"));
                if (string.IsNullOrEmpty(id)) continue;
                string title = (Json.Str(Json.Get(item, "title")) ?? "").Trim();
                lists.Add(new TaskList { Id = id, Title = title != "" ? title : "Tasks" });
            }
            return lists;
        }

        // A tasks reply's tasks that have a due date and aren't deleted; null when it isn't a reply.
        public static List<GoogleTask> ParseTasks(string json, string listId)
        {
            object root = Json.TryParse(json);
            if (Json.Obj(root) == null) return null;
            var tasks = new List<GoogleTask>();
            foreach (object item in Json.Arr(Json.Get(root, "items")) ?? new object[0])
            {
                GoogleTask t = Task(item, listId);
                if (t != null && !(Json.Get(item, "deleted") as bool? ?? false)) tasks.Add(t);
            }
            return tasks;
        }

        static GoogleTask Task(object item, string listId)
        {
            string id = Json.Str(Json.Get(item, "id"));
            DateTimeOffset due;
            string dueText = Json.Str(Json.Get(item, "due"));
            if (string.IsNullOrEmpty(id) || dueText == null || !DateTimeOffset.TryParse(dueText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out due)) return null;
            string title = (Json.Str(Json.Get(item, "title")) ?? "").Trim();
            return new GoogleTask
            {
                Id = id,
                ListId = listId,
                Title = title != "" ? title : GoogleCalendarClient.NoTitle,
                Due = due.UtcDateTime.Date,   // the date Google stored, whatever the PC's zone
                Done = Json.Str(Json.Get(item, "status")) == "completed",
            };
        }
    }
}
