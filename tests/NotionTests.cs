using System.Collections.Generic;

namespace Capsule
{
    // A stand-in for api.notion.com: answers by method and path, and keeps every request. The last reply set for a
    // path keeps answering once the earlier ones are used up.
    public sealed class FakeNotion
    {
        public readonly List<NotionRequest> Requests = new List<NotionRequest>();
        readonly Dictionary<string, Queue<HttpResult>> replies = new Dictionary<string, Queue<HttpResult>>();
        public HttpResult Otherwise = new HttpResult { Status = 0, Error = "ConnectFailure" };

        public FakeNotion Reply(string method, string path, int status, string body)
        {
            string key = method + " " + path;
            if (!replies.ContainsKey(key)) replies[key] = new Queue<HttpResult>();
            replies[key].Enqueue(new HttpResult { Status = status, Body = body ?? "" });
            return this;
        }

        public HttpResult Answer(NotionRequest request)
        {
            lock (Requests) Requests.Add(request);
            Queue<HttpResult> queue;
            if (!replies.TryGetValue(request.Method + " " + request.Path, out queue) || queue.Count == 0) return Otherwise;
            return queue.Count > 1 ? queue.Dequeue() : queue.Peek();
        }

        // A shared database with one data source, where creating pages and querying work.
        public static FakeNotion Working()
        {
            return new FakeNotion()
                .Reply("GET", "databases/" + NotionTests.Db, 200, TestRunner.Fixture("notion-database.json"))
                .Reply("GET", "data_sources/" + NotionTests.Source, 200, TestRunner.Fixture("notion-data-source.json"))
                .Reply("POST", "pages", 200, TestRunner.Fixture("notion-page.json"))
                .Reply("POST", "data_sources/" + NotionTests.Source + "/query", 200, TestRunner.Fixture("notion-query.json"));
        }
    }

    public static class NotionTests
    {
        public const string Db = "0123456789abcdef0123456789abcdef";
        public const string Source = "bc1211ca-e3f1-4939-ae34-5260b16f627c";

        public static void Run()
        {
            ReadsDatabaseLinks();
            DescribesADatabase();
            NamesTheDatabaseByItsOwnTitle();
            SendsTheDocumentedRequests();
            ReadsRecentIdeas();
            MapsErrors();
            OnlyRealErrorCodesAreShown();
            NeverThrowsOnOddReplies();
        }

        static void ReadsDatabaseLinks()
        {
            TestRunner.Eq(Db, NotionLink.DatabaseId("https://www.notion.so/myteam/Ideas-" + Db + "?v=fedcba9876543210fedcba9876543210"), "workspace, title and view");
            TestRunner.Eq(Db, NotionLink.DatabaseId("https://www.notion.so/" + Db + "?v=fedcba9876543210fedcba9876543210&pvs=4"), "no workspace");
            TestRunner.Eq(Db, NotionLink.DatabaseId("notion.so/" + Db.ToUpperInvariant()), "no scheme, upper case");
            TestRunner.Eq(Db, NotionLink.DatabaseId("https://app.notion.com/p/" + Db), "app.notion.com");
            TestRunner.Eq(Db, NotionLink.DatabaseId("https://myteam.notion.site/Ideas-" + Db + "#top"), "a published site, with a fragment");
            TestRunner.Eq(Db, NotionLink.DatabaseId("01234567-89ab-cdef-0123-456789abcdef"), "a dashed id");
            TestRunner.Eq(Db, NotionLink.DatabaseId("https://www.notion.so/myteam/Ideas-01234567-89ab-cdef-0123-456789abcdef/"), "a dashed id in a link, trailing slash");
            TestRunner.Eq(Db, NotionLink.DatabaseId("  " + Db + "  "), "a bare id with spaces round it");
            TestRunner.Eq(Db, NotionLink.DatabaseId("https://www.notion.so/myteam/Cafe-" + Db), "a title ending in hex letters");
            TestRunner.Eq(null, NotionLink.DatabaseId("https://www.notion.so/myteam/Ideas"), "no id");
            TestRunner.Eq(null, NotionLink.DatabaseId("https://www.notion.so/" + Db + "0"), "33 hex digits is not an id");
            TestRunner.Eq(null, NotionLink.DatabaseId(""), "empty");
            TestRunner.Eq(null, NotionLink.DatabaseId(null), "nothing");

            TestRunner.Check(Opens("https://app.notion.com/p/abc") && Opens("https://www.notion.so/Idea-123"), "Notion's page links may be opened");
            TestRunner.Check(Opens("https://myteam.notion.site/Idea-123"), "and published ones");
            TestRunner.Check(!Opens("http://www.notion.so/x") && !Opens("file:///C:/Windows/notepad.exe"), "but not plain http or files");
            TestRunner.Check(!Opens("https://notion.so.example.com/x") && !Opens("https://example.com/notion.so"), "or other sites");
            TestRunner.Check(!Opens("https://evilnotion.so/x") && !Opens("https://evilnotion.com/x") && !Opens("https://evilnotion.site/x"), "nor a look-alike host that only ends the way Notion's do");
            TestRunner.Check(!Opens("https://www.notion.so@evil.example/x") && !Opens("https://notion.so:443@evil.example/x"), "nor a link with Notion's name before an @: its host is what follows the @");
            TestRunner.Check(!Opens(null) && !Opens("") && !Opens("   "), "and no link at all is no page");

            TestRunner.Eq("https://app.notion.com/p/Idea%20one", NotionLink.PageUrl("https://app.notion.com/p/Idea one"), "a page link is opened in its escaped absolute form, not as the raw string");
            TestRunner.Eq("https://www.notion.so/x?v=1", NotionLink.PageUrl("https://www.notion.so/x?v=1"), "which leaves a good link as it was");
            TestRunner.Check(NotionLink.PageUrl("https://evilnotion.so/x") == null && NotionLink.PageUrl("file:///C:/Windows/notepad.exe") == null && NotionLink.PageUrl(null) == null, "and gives nothing for anything else");
        }

        // Whether the Controller's OpenIdea would open this link: it opens only what PageUrl gives, and nothing for null.
        static bool Opens(string url) { return NotionLink.PageUrl(url) != null; }

        static void DescribesADatabase()
        {
            FakeNotion notion = FakeNotion.Working();
            var client = new NotionClient("secret_test") { Transport = notion.Answer };
            NotionDatabase d = client.Describe(Db);
            TestRunner.Check(d != null, "described");
            if (d == null) return;
            TestRunner.Eq(Source, d.DataSourceId, "the database's data source");
            TestRunner.Eq("Ideas", d.Name, "its name");
            TestRunner.Eq("title", d.TitleProperty, "its title property, whatever it is called");
            TestRunner.Eq("GET databases/" + Db, notion.Requests[0].Method + " " + notion.Requests[0].Path, "first the database");
            TestRunner.Eq("GET data_sources/" + Source, notion.Requests[1].Method + " " + notion.Requests[1].Path, "then its data source");

            // A link to a data source itself: no database has that id, so it is read as a data source.
            var direct = new FakeNotion()
                .Reply("GET", "databases/" + Source, 404, "{\"object\":\"error\",\"status\":404,\"code\":\"object_not_found\"}")
                .Reply("GET", "data_sources/" + Source, 200, TestRunner.Fixture("notion-data-source.json"));
            NotionDatabase viaSource = new NotionClient("secret_test") { Transport = direct.Answer }.Describe(Source);
            TestRunner.Check(viaSource != null && viaSource.DataSourceId == Source, "a data source's own link works too");

            var unshared = new FakeNotion()
                .Reply("GET", "databases/" + Db, 404, "{\"code\":\"object_not_found\"}")
                .Reply("GET", "data_sources/" + Db, 404, "{\"code\":\"object_not_found\"}");
            var c = new NotionClient("secret_test") { Transport = unshared.Answer };
            TestRunner.Check(c.Describe(Db) == null && c.LastFailure.Status == 404, "not shared with Capsule: a 404, kept for the message");
        }

        // A database reply with this title (a rich-text array as JSON) and a data source of this name.
        static string DatabaseReply(string title, string sourceName)
        {
            return "{\"object\":\"database\",\"id\":\"" + Db + "\",\"title\":" + title + ",\"data_sources\":[{\"id\":\"" + Source + "\",\"name\":\"" + sourceName + "\"}]}";
        }

        static NotionDatabase DescribeFrom(string databaseReply)
        {
            FakeNotion notion = new FakeNotion()
                .Reply("GET", "databases/" + Db, 200, databaseReply)
                .Reply("GET", "data_sources/" + Source, 200, TestRunner.Fixture("notion-data-source.json"));
            return new NotionClient("secret_test") { Transport = notion.Answer }.Describe(Db);
        }

        // The settings show the database's name (spec §3), which is the database's own title, not the name of its first data
        // source: a database that was renamed keeps the data source's old name.
        static void NamesTheDatabaseByItsOwnTitle()
        {
            NotionDatabase titled = DescribeFrom(DatabaseReply("[{\"type\":\"text\",\"text\":{\"content\":\"Ideas inbox\"},\"plain_text\":\"Ideas inbox\"}]", "Main data source"));
            TestRunner.Eq("Ideas inbox", titled != null ? titled.Name : null, "the name shown is the database's own title");
            NotionDatabase untitled = DescribeFrom(DatabaseReply("[]", "Main data source"));
            TestRunner.Eq("Main data source", untitled != null ? untitled.Name : null, "a database with no title falls back to its data source's name");
            NotionDatabase bare = DescribeFrom(DatabaseReply("[]", ""));
            TestRunner.Eq("Ideas", bare != null ? bare.Name : null, "and with neither, to the title of the data source itself");
        }

        static void SendsTheDocumentedRequests()
        {
            FakeNotion notion = FakeNotion.Working();
            var client = new NotionClient("secret_test") { Transport = notion.Answer };
            NotionDatabase d = client.Describe(Db);
            NotionPage page = client.Create(d, "Recipe app: add a shopping list");
            NotionRequest create = notion.Requests[2];
            TestRunner.Eq("POST pages", create.Method + " " + create.Path, "an idea is a new page");
            TestRunner.Eq("Bearer secret_test", create.Headers["Authorization"], "the secret as a bearer token");
            TestRunner.Eq("2026-03-11", NotionClient.Version, "pinned to version 2026-03-11");
            TestRunner.Eq(NotionClient.Version, create.Headers["Notion-Version"], "and says so");
            TestRunner.Eq("application/json", create.Headers["Content-Type"], "as JSON");
            object body = Json.Parse(create.Body);
            TestRunner.Eq("data_source_id", Json.Str(Json.Get(body, "parent", "type")), "its parent is the data source");
            TestRunner.Eq(Source, Json.Str(Json.Get(body, "parent", "data_source_id")), "by id");
            object[] title = Json.Arr(Json.Get(body, "properties", "title", "title"));
            TestRunner.Check(title != null && title.Length == 1 && Json.Str(Json.Get(title[0], "text", "content")) == "Recipe app: add a shopping list", "the idea is its title");
            TestRunner.Check(page != null && page.Url.StartsWith("https://app.notion.com/p/"), "the new page's link comes back");
            TestRunner.Eq("Recipe app: add a shopping list", page != null ? page.Title : null, "with its title");

            client.Recent(d, 5);
            NotionRequest query = notion.Requests[3];
            TestRunner.Eq("POST data_sources/" + Source + "/query", query.Method + " " + query.Path, "recent ideas: a data source query");
            object q = Json.Parse(query.Body);
            object[] sorts = Json.Arr(Json.Get(q, "sorts"));
            TestRunner.Check(sorts != null && Json.Str(Json.Get(sorts[0], "timestamp")) == "created_time" && Json.Str(Json.Get(sorts[0], "direction")) == "descending", "newest first");
            TestRunner.Eq(5.0, Json.Num(Json.Get(q, "page_size")), "five of them");
            foreach (NotionRequest r in notion.Requests) TestRunner.Check(r.Headers["Notion-Version"] == NotionClient.Version, "every request pins the version: " + r.Path);
        }

        static void ReadsRecentIdeas()
        {
            List<NotionPage> pages = NotionClient.PagesFrom(TestRunner.Fixture("notion-query.json"));
            TestRunner.Eq(2, pages.Count, "two pages");
            TestRunner.Eq("Garden planner: frost date alerts", pages[0].Title, "a title in two pieces is joined");
            TestRunner.Eq("https://app.notion.com/p/9a2c1d0e000040008000000000000002", pages[0].Url, "its link");
            TestRunner.Eq(Clock.ParseIsoMs("2026-10-01T10:15:00.000Z"), pages[0].CreatedAtMs, "its creation time");
            TestRunner.Eq("Capsule: calendar module", pages[1].Title, "in Notion's order");
            TestRunner.Eq(null, NotionClient.PagesFrom("{\"object\":\"error\"}"), "not a list");
        }

        static void MapsErrors()
        {
            string rejected = TestRunner.Fixture("notion-error-validation.json");
            TestRunner.Eq("Notion secret not accepted — check ⚙", NotionClient.Problem(new HttpResult { Status = 401 }, true), "401");
            TestRunner.Eq("Share the database with Capsule in Notion", NotionClient.Problem(new HttpResult { Status = 403 }, true), "403");
            TestRunner.Eq("Share the database with Capsule in Notion", NotionClient.Problem(new HttpResult { Status = 404 }, false), "404");
            TestRunner.Check(NotionClient.Problem(new HttpResult { Status = 429, RetryAfterSeconds = 30 }, true).Contains("slow down"), "429");
            TestRunner.Eq("Notion rejected the idea (validation_error)", NotionClient.Problem(new HttpResult { Status = 400, Body = rejected }, true), "400 names Notion's code");
            TestRunner.Eq("Notion rejected the request (validation_error)", NotionClient.Problem(new HttpResult { Status = 400, Body = rejected }, false), "400 while setting up");
            TestRunner.Check(NotionClient.Problem(new HttpResult { Status = 503 }, true).Contains("HTTP 503"), "5xx");
            TestRunner.Check(NotionClient.Problem(new HttpResult { Status = 0, Error = "Timeout" }, true).Contains("Timeout"), "no reply");
            TestRunner.Eq("", NotionClient.Problem(null, true), "nothing wrong");
            TestRunner.Eq("validation_error", NotionClient.Code(new HttpResult { Status = 400, Body = rejected }), "Notion's error code");
        }

        static HttpResult WithCode(string codeJson)
        {
            return new HttpResult { Status = 400, Body = "{\"code\":\"" + codeJson + "\"}" };
        }

        // The code from a reply goes into the tile and the log, and a reply can say anything: only what looks like one of
        // Notion's codes (lower-case letters and underscores, at most 64) is passed on.
        static void OnlyRealErrorCodesAreShown()
        {
            TestRunner.Eq("object_not_found", NotionClient.Code(WithCode("object_not_found")), "a real code is passed on");
            TestRunner.Eq(new string('a', 64), NotionClient.Code(WithCode(new string('a', 64))), "up to 64 characters");
            TestRunner.Eq("", NotionClient.Code(WithCode(new string('a', 65))), "not 65");
            TestRunner.Eq("", NotionClient.Code(WithCode("Validation_Error")), "not upper case");
            TestRunner.Eq("", NotionClient.Code(WithCode("not a code, just words")), "not text with spaces");
            TestRunner.Eq("", NotionClient.Code(WithCode("<b>x</b>")), "not markup");
            TestRunner.Eq("", NotionClient.Code(WithCode("validation_error\\n")), "not a code with a line break after it");
            TestRunner.Eq("", NotionClient.Code(WithCode("error_2")), "not digits");
            TestRunner.Eq("", NotionClient.Code(WithCode("")), "not nothing");
            TestRunner.Eq("", NotionClient.Code(new HttpResult { Status = 400, Body = "{\"code\":7}" }), "not a number");
            TestRunner.Eq("Notion rejected the idea", NotionClient.Problem(WithCode("not a code, just words"), true), "so a reply's own words never reach the tile");
        }

        static void NeverThrowsOnOddReplies()
        {
            var odd = new FakeNotion().Reply("GET", "databases/" + Db, 200, "<html>").Reply("POST", "pages", 200, "not json");
            var client = new NotionClient("secret_test") { Transport = odd.Answer };
            TestRunner.Check(client.Describe(Db) == null && client.LastFailure.Status == 200, "an HTML reply is unexpected, not a crash");
            var db = new NotionDatabase { DataSourceId = Source, TitleProperty = "title" };
            TestRunner.Check(client.Create(db, "x") == null && NotionClient.Problem(client.LastFailure, true) == "Unexpected reply from Notion", "so is an unreadable create reply");
            TestRunner.Check(NotionClient.ParseDataSource("{\"id\":\"x\",\"properties\":{}}") == null, "a data source without a title property");
        }
    }
}
