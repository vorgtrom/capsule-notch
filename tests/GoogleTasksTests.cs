using System;
using System.Collections.Generic;
using System.Linq;

namespace Capsule
{
    public static class GoogleTasksTests
    {
        public const string MyTasks = "MDk4NTk5NzQ3ODM1";

        public static void Run()
        {
            ReadsListsAndTasks();
            AsksForTheDocumentedRequests();
            AddsAndTicksTasks();
            FailuresAreKept();
            RenamesAndDeletes();
            TellsAnApiTurnedOffFromARefusal();
        }

        public const string ApiOffReply = "{\"error\": {\"code\": 403, \"errors\": [{\"reason\": \"accessNotConfigured\"}], \"status\": \"PERMISSION_DENIED\", \"details\": [{\"reason\": \"SERVICE_DISABLED\"}]}}";

        static void TellsAnApiTurnedOffFromARefusal()
        {
            TestRunner.Check(GoogleTasksClient.ApiOff(new HttpResult { Status = 403, Body = ApiOffReply }), "a 403 saying the Tasks API is off in the project is told apart");
            TestRunner.Check(!GoogleTasksClient.ApiOff(new HttpResult { Status = 403, Body = "{\"error\": {\"code\": 403, \"errors\": [{\"reason\": \"insufficientPermissions\"}]}}" })
                && !GoogleTasksClient.ApiOff(new HttpResult { Status = 404, Body = "accessNotConfigured" }) && !GoogleTasksClient.ApiOff(null), "from one about the sign-in's permissions, or anything else");
        }

        static void RenamesAndDeletes()
        {
            var seen = new List<CalendarRequest>();
            GoogleTasksClient c = Client(seen);
            var task = new GoogleTask { Id = "bmV3", ListId = MyTasks };
            object body = null;
            TestRunner.Check(c.Rename(task, "Call the dentist at 9") && seen.Last().Method == "PATCH" && seen.Last().Path == "lists/" + MyTasks + "/tasks/bmV3"
                && Json.Str(Json.Get(body = Json.TryParse(seen.Last().Body), "title")) == "Call the dentist at 9" && Json.Get(body, "status") == null, "renaming PATCHes the title only");
            TestRunner.Check(c.Delete(task) && seen.Last().Method == "DELETE" && seen.Last().Path == "lists/" + MyTasks + "/tasks/bmV3" && seen.Last().Body == null, "deleting DELETEs it");
            c.Transport = r => new HttpResult { Status = 404 };
            TestRunner.Check(c.Delete(task), "one already gone counts as deleted");
            c.Transport = r => new HttpResult { Status = 403 };
            TestRunner.Check(!c.Rename(task, "x") && !c.Delete(task) && c.LastFailure.Status == 403, "a refused rename or delete is false");
        }

        // A stand-in for tasks.googleapis.com: the lists, and a list's tasks over two pages.
        public static HttpResult Answer(CalendarRequest r, List<CalendarRequest> seen)
        {
            lock (seen) seen.Add(r);
            if (r.Method == "GET" && r.Path.StartsWith("users/@me/lists", StringComparison.Ordinal)) return new HttpResult { Status = 200, Body = TestRunner.Fixture("google-tasklists.json") };
            if (r.Method == "GET" && r.Path.Contains("/tasks?")) return new HttpResult { Status = 200, Body = TestRunner.Fixture(r.Path.Contains("pageToken=page-2") ? "google-tasks-page2.json" : "google-tasks.json") };
            if (r.Method == "POST") return new HttpResult { Status = 200, Body = "{\"id\": \"bmV3\", \"title\": \"Call the dentist\", \"status\": \"needsAction\", \"due\": \"2026-10-09T00:00:00.000Z\"}" };
            if (r.Method == "PATCH") return new HttpResult { Status = 200, Body = "{}" };
            if (r.Method == "DELETE") return new HttpResult { Status = 204 };
            return new HttpResult { Status = 404 };
        }

        static GoogleTasksClient Client(List<CalendarRequest> seen)
        {
            var c = new GoogleTasksClient("ya29.sample");
            c.Transport = r => Answer(r, seen);
            return c;
        }

        static void ReadsListsAndTasks()
        {
            List<TaskList> lists = GoogleTasksClient.ParseLists(TestRunner.Fixture("google-tasklists.json"));
            TestRunner.Check(lists.Count == 2 && lists[0].Id == MyTasks && lists[0].Title == "My Tasks" && lists[1].Title == "Chores", "two lists, by their titles; an entry without an id isn't one");
            List<GoogleTask> tasks = GoogleTasksClient.ParseTasks(TestRunner.Fixture("google-tasks.json"), MyTasks);
            TestRunner.Eq("Send the invoice,Water the plants,(No title)", string.Join(",", tasks.Select(t => t.Title)), "tasks with a due date; not one without, nor a deleted one");
            TestRunner.Check(tasks[0].Due == new DateTime(2026, 10, 6) && !tasks[0].Done && tasks[0].ListId == MyTasks, "due on its date, not done, in its list");
            TestRunner.Check(tasks[1].Done && tasks[1].Due == new DateTime(2026, 10, 7), "a completed (hidden) task is read, done");
            TestRunner.Check(GoogleTasksClient.ParseTasks("{\"kind\": \"tasks#tasks\"}", MyTasks).Count == 0 && GoogleTasksClient.ParseTasks("<html>", MyTasks) == null, "an empty list has no items; a page isn't a reply");
        }

        static void AsksForTheDocumentedRequests()
        {
            var seen = new List<CalendarRequest>();
            GoogleTasksClient c = Client(seen);
            TestRunner.Eq(2, c.Lists().Count, "the lists through the client");
            TestRunner.Check(seen[0].Path == "users/@me/lists?maxResults=100" && seen[0].Headers["Authorization"] == "Bearer ya29.sample", "from users/@me/lists, with the access token");
            List<GoogleTask> tasks = c.Tasks(new TaskList { Id = MyTasks }, new DateTime(2026, 9, 28), new DateTime(2026, 11, 8));
            TestRunner.Check(tasks != null && tasks.Count == 4 && tasks.Last().Title == "Book flights", "a second page is followed");
            TestRunner.Eq("lists/" + MyTasks + "/tasks?dueMin=2026-09-28T00%3A00%3A00.000Z&dueMax=2026-11-09T00%3A00%3A00.000Z&showCompleted=true&showHidden=true&maxResults=100", seen[1].Path,
                "due from the first day to the day after the last, done and hidden ones too");
            TestRunner.Check(seen[2].Path.EndsWith("&pageToken=page-2"), "the next page by its token");
        }

        static void AddsAndTicksTasks()
        {
            var seen = new List<CalendarRequest>();
            GoogleTasksClient c = Client(seen);
            GoogleTask added = c.Add(MyTasks, "Call the dentist", new DateTime(2026, 10, 9));
            TestRunner.Check(added != null && added.Id == "bmV3" && added.Due == new DateTime(2026, 10, 9) && added.ListId == MyTasks, "a task is added, and comes back as Google has it");
            CalendarRequest post = seen.Last();
            object body = Json.TryParse(post.Body);
            TestRunner.Check(post.Method == "POST" && post.Path == "lists/" + MyTasks + "/tasks" && Json.Str(Json.Get(body, "title")) == "Call the dentist" && Json.Str(Json.Get(body, "due")) == "2026-10-09T00:00:00.000Z",
                "POSTed to its list with its title, due at the date's midnight UTC");
            TestRunner.Check(post.Headers["Content-Type"].StartsWith("application/json"), "as JSON");
            TestRunner.Check(c.SetDone(added, true) && seen.Last().Method == "PATCH" && seen.Last().Path == "lists/" + MyTasks + "/tasks/bmV3" && Json.Str(Json.Get(Json.TryParse(seen.Last().Body), "status")) == "completed", "ticking it done PATCHes its status");
            TestRunner.Check(c.SetDone(added, false) && Json.Str(Json.Get(Json.TryParse(seen.Last().Body), "status")) == "needsAction" && seen.Last().Body.Contains("\"completed\": null"), "and undoing it clears the completion too");
        }

        static void FailuresAreKept()
        {
            var c = new GoogleTasksClient("t");
            c.Transport = r => new HttpResult { Status = 403, Body = "{\"error\": {\"code\": 403, \"status\": \"PERMISSION_DENIED\"}}" };
            TestRunner.Check(c.Lists() == null && c.LastFailure.Status == 403, "a refused call is no list, and the reply is kept");
            TestRunner.Check(c.Add(MyTasks, "x", DateTime.Today) == null && !c.SetDone(new GoogleTask { Id = "a", ListId = "b" }, true) && c.LastFailure.Status == 403, "nor a task, nor a tick");
        }
    }
}
