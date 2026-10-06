using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Capsule
{
    // Where ideas go (spec §4): the database id inside a Notion link.
    public static class NotionLink
    {
        static readonly Regex Dashed = new Regex("([0-9a-fA-F]{8})-([0-9a-fA-F]{4})-([0-9a-fA-F]{4})-([0-9a-fA-F]{4})-([0-9a-fA-F]{12})$");
        static readonly Regex Plain = new Regex("(?:^|[^0-9a-fA-F])([0-9a-fA-F]{32})$");

        // The id in a database link, as 32 lowercase hex digits, or null. Takes every form Notion's "Copy link" gives:
        // with or without the workspace name and the title, with a ?v= view, with or without dashes in the id, on
        // notion.so, notion.site or app.notion.com, and a bare id.
        public static string DatabaseId(string link)
        {
            if (string.IsNullOrWhiteSpace(link)) return null;
            string s = link.Trim();
            int cut = s.IndexOfAny(new[] { '?', '#' });
            if (cut >= 0) s = s.Substring(0, cut);
            s = s.TrimEnd('/');
            string last = s.Substring(s.LastIndexOf('/') + 1);
            Match m = Dashed.Match(last);
            if (m.Success) return (m.Groups[1].Value + m.Groups[2].Value + m.Groups[3].Value + m.Groups[4].Value + m.Groups[5].Value).ToLowerInvariant();
            m = Plain.Match(last);
            return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
        }

        // A page link from Notion's replies as it may be opened: only https links on Notion's own sites, in their escaped
        // absolute form rather than as the raw string. Null for anything else.
        public static string PageUrl(string url)
        {
            Uri u;
            if (!Uri.TryCreate(url, UriKind.Absolute, out u) || u.Scheme != Uri.UriSchemeHttps) return null;
            string host = u.Host.ToLowerInvariant();
            bool notion = host == "notion.so" || host.EndsWith(".notion.so", StringComparison.Ordinal)
                || host == "notion.com" || host.EndsWith(".notion.com", StringComparison.Ordinal)
                || host.EndsWith(".notion.site", StringComparison.Ordinal);
            return notion ? u.AbsoluteUri : null;
        }
    }

    // One request to Notion, as tests see it.
    public sealed class NotionRequest
    {
        public string Method = "";
        public string Path = "";   // after https://api.notion.com/v1/
        public string Body;        // JSON, or null
        public Dictionary<string, string> Headers = new Dictionary<string, string>();
    }

    // Where ideas are written: a database's first (usually only) data source.
    public sealed class NotionDatabase
    {
        public string DataSourceId = "";
        public string Name = "";
        public string TitleProperty = "";   // the id of its title property, which every data source has exactly one of
    }

    // One idea as Notion has it.
    public sealed class NotionPage
    {
        public string Id = "";
        public string Title = "";
        public string Url = "";
        public long CreatedAtMs;
    }

    // Notion's API (spec §4), pinned to one version: databases hold data sources, ideas are pages in a data source,
    // and the recent ones come from a data-source query. Never throws: a call that fails returns null and leaves the
    // reply in LastFailure. The secret only ever goes into the Authorization header; nothing here logs.
    public sealed class NotionClient
    {
        public const string Base = "https://api.notion.com/v1/";
        public const string Version = "2026-03-11";
        public const int TimeoutMs = 15000;

        readonly string secret;

        public Func<NotionRequest, HttpResult> Transport;   // set by tests; null = api.notion.com
        public HttpResult LastFailure { get; private set; }

        public NotionClient(string secret) { this.secret = secret; }

        // A database's first data source, its name and its title property. The id may also be a data source's own
        // (a "Copy link to data source" link): when no database has it, it is tried as one.
        public NotionDatabase Describe(string id)
        {
            LastFailure = null;
            string sourceId = id, name = null;
            HttpResult database = Send("GET", "databases/" + id, null);
            if (database.Status == 200)
            {
                sourceId = FirstDataSource(database.Body, out name);   // the data source's name...
                string title = PlainText(Json.Get(Json.TryParse(database.Body), "title"));
                if (title != "") name = title;                          // ...unless the database has a title of its own: the name it is known by
                if (sourceId == null) return Fail<NotionDatabase>(Unexpected());
            }
            else if (database.Status != 404) return Fail<NotionDatabase>(database);
            HttpResult source = Send("GET", "data_sources/" + sourceId, null);
            if (source.Status != 200) return Fail<NotionDatabase>(source);
            NotionDatabase d = ParseDataSource(source.Body);
            if (d == null) return Fail<NotionDatabase>(Unexpected());
            if (!string.IsNullOrEmpty(name)) d.Name = name;
            return d;
        }

        // Adds an idea: a page whose title is the text.
        public NotionPage Create(NotionDatabase db, string text)
        {
            LastFailure = null;
            HttpResult r = Send("POST", "pages", CreateBody(db, text));
            if (r.Status != 200) return Fail<NotionPage>(r);
            NotionPage page = PageFrom(Json.TryParse(r.Body));
            if (page == null) return Fail<NotionPage>(Unexpected());
            if (page.Title == "") page.Title = text;
            return page;
        }

        // The newest ideas, newest first.
        public List<NotionPage> Recent(NotionDatabase db, int count)
        {
            LastFailure = null;
            HttpResult r = Send("POST", "data_sources/" + db.DataSourceId + "/query", QueryBody(count));
            if (r.Status != 200) return Fail<List<NotionPage>>(r);
            List<NotionPage> pages = PagesFrom(r.Body);
            return pages ?? Fail<List<NotionPage>>(Unexpected());
        }

        T Fail<T>(HttpResult reply) where T : class
        {
            LastFailure = reply;
            return null;
        }

        // A 200 whose body isn't what Notion documents.
        static HttpResult Unexpected() { return new HttpResult { Status = 200, Error = "unexpected reply" }; }

        HttpResult Send(string method, string path, string body)
        {
            var request = new NotionRequest { Method = method, Path = path, Body = body };
            request.Headers["Authorization"] = "Bearer " + secret;
            request.Headers["Notion-Version"] = Version;
            if (body != null) request.Headers["Content-Type"] = "application/json";
            if (Transport != null) return Transport(request);
            return Http.Send(method, Base + path, request.Headers, body, TimeoutMs);
        }

        public static string CreateBody(NotionDatabase db, string text)
        {
            var piece = new Dictionary<string, object> { { "type", "text" }, { "text", new Dictionary<string, object> { { "content", text } } } };
            var title = new Dictionary<string, object> { { "title", new List<object> { piece } } };
            var parent = new Dictionary<string, object> { { "type", "data_source_id" }, { "data_source_id", db.DataSourceId } };
            var body = new Dictionary<string, object> { { "parent", parent }, { "properties", new Dictionary<string, object> { { db.TitleProperty, title } } } };
            return Json.Write(body);
        }

        public static string QueryBody(int count)
        {
            var newestFirst = new Dictionary<string, object> { { "timestamp", "created_time" }, { "direction", "descending" } };
            return Json.Write(new Dictionary<string, object> { { "sorts", new List<object> { newestFirst } }, { "page_size", count } });
        }

        // A database object's first data source and its name; null when it has none.
        public static string FirstDataSource(string json, out string name)
        {
            name = null;
            object[] sources = Json.Arr(Json.Get(Json.TryParse(json), "data_sources"));
            if (sources == null || sources.Length == 0) return null;
            string id = Json.Str(Json.Get(sources[0], "id"));
            name = Json.Str(Json.Get(sources[0], "name"));
            return string.IsNullOrEmpty(id) ? null : id;
        }

        // A data source object: its id, its name and its title property's id. Null when it isn't one.
        public static NotionDatabase ParseDataSource(string json)
        {
            object root = Json.TryParse(json);
            string id = Json.Str(Json.Get(root, "id"));
            Dictionary<string, object> properties = Json.Obj(Json.Get(root, "properties"));
            if (string.IsNullOrEmpty(id) || properties == null) return null;
            var d = new NotionDatabase { DataSourceId = id, Name = PlainText(Json.Get(root, "title")) };
            foreach (KeyValuePair<string, object> p in properties)
                if (Json.Str(Json.Get(p.Value, "type")) == "title") d.TitleProperty = Json.Str(Json.Get(p.Value, "id")) ?? p.Key;
            if (d.TitleProperty == "") return null;
            if (d.Name == "") d.Name = "Untitled";
            return d;
        }

        // A page object: id, link, creation time and title (whatever its title property is called).
        public static NotionPage PageFrom(object page)
        {
            string id = Json.Str(Json.Get(page, "id"));
            if (string.IsNullOrEmpty(id)) return null;
            var p = new NotionPage
            {
                Id = id,
                Url = Json.Str(Json.Get(page, "url")) ?? "",
                CreatedAtMs = Clock.ParseIsoMs(Json.Str(Json.Get(page, "created_time"))),
            };
            Dictionary<string, object> properties = Json.Obj(Json.Get(page, "properties"));
            if (properties != null)
                foreach (KeyValuePair<string, object> property in properties)
                    if (Json.Str(Json.Get(property.Value, "type")) == "title") p.Title = PlainText(Json.Get(property.Value, "title"));
            return p;
        }

        // A query reply's pages, in Notion's order; null when it isn't a list.
        public static List<NotionPage> PagesFrom(string json)
        {
            object[] results = Json.Arr(Json.Get(Json.TryParse(json), "results"));
            if (results == null) return null;
            var pages = new List<NotionPage>();
            foreach (object item in results)
            {
                NotionPage page = PageFrom(item);
                if (page != null) pages.Add(page);
            }
            return pages;
        }

        static string PlainText(object richText)
        {
            var sb = new StringBuilder();
            foreach (object piece in Json.Arr(richText) ?? new object[0]) sb.Append(Json.Str(Json.Get(piece, "plain_text")) ?? "");
            return sb.ToString();
        }

        // Notion's error code ("validation_error", ...) from an error reply, or "". A reply can say anything, and the code
        // goes into the tile and the log: only lower-case letters and underscores, at most 64, count as a code.
        public static string Code(HttpResult reply)
        {
            if (reply == null) return "";
            string code = Json.Str(Json.Get(Json.TryParse(reply.Body), "code"));
            if (string.IsNullOrEmpty(code) || code.Length > 64) return "";
            foreach (char c in code) if ((c < 'a' || c > 'z') && c != '_') return "";
            return code;
        }

        // What a failed call means, in a line for the Ideas tile or the settings (spec §4's error table).
        // creating: the call was adding an idea.
        public static string Problem(HttpResult reply, bool creating)
        {
            if (reply == null) return "";
            if (reply.Status == 401) return "Notion secret not accepted — check ⚙";
            if (reply.Status == 403 || reply.Status == 404) return "Share the database with Capsule in Notion";
            if (reply.Status == 429) return "Notion asked Capsule to slow down — trying again shortly";
            if (reply.Status == 400)
            {
                string code = Code(reply);
                return (creating ? "Notion rejected the idea" : "Notion rejected the request") + (code != "" ? " (" + code + ")" : "");
            }
            if (reply.Status >= 500) return "Notion is having trouble (HTTP " + reply.Status + ") — will retry";
            if (reply.Status == 0) return "Can't reach Notion (" + reply.Error + ") — will retry";
            if (reply.Status == 200) return "Unexpected reply from Notion";
            return "Notion returned HTTP " + reply.Status;
        }
    }
}
