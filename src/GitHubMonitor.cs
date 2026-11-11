using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Capsule
{
    internal sealed class GitHubFailure
    {
        public long Pull, Run;
        public string Workflow, Conclusion;
    }

    internal sealed class GitHubChecks
    {
        public string Repo, Status;
        public long LastChecked;
        public bool MorePulls;
        public readonly List<long> Uncertain = new List<long>();
        public readonly List<GitHubFailure> Failures = new List<GitHubFailure>();
    }

    // The scheduler owns all state. The worker reads a snapshot and returns both endpoint results.
    internal sealed class GitHubMonitor
    {
        internal const int Limit = 30;
        internal const long IntervalMs = 3 * 60 * 1000;
        const string Pulls = "pulls?state=open&sort=created&direction=desc&per_page=30";
        const string Runs = "actions/runs?event=pull_request&per_page=30";
        const string Waiting = "Waiting to check GitHub.";

        sealed class Page
        {
            public object[] Items;
            public bool More;
            public string Tag;
            public string Error;
            public long RetryAt;
        }

        readonly Func<string, IDictionary<string, string>, HttpResult> fetch;
        readonly TaskScheduler scheduler;
        readonly Func<long> clock;
        string repo = "";
        int generation;
        long next;
        bool busy;
        Page pulls, runs;

        public GitHubMonitor(TaskScheduler scheduler)
            : this(scheduler, (url, headers) => Http.Get(url, headers, 8000), Clock.NowMs) { }

        internal GitHubMonitor(TaskScheduler scheduler, Func<string, IDictionary<string, string>, HttpResult> fetch, Func<long> clock)
        {
            this.scheduler = scheduler;
            this.fetch = fetch;
            this.clock = clock;
        }

        public GitHubChecks Current { get; private set; }
        public event Action Changed;

        public static string NormalizeRepo(string text)
        {
            string value = (text ?? "").Trim();
            if (!Regex.IsMatch(value, @"\A[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?/[A-Za-z0-9_.-]{1,100}\z")) return "";
            return value.EndsWith("/.", StringComparison.Ordinal) || value.EndsWith("/..", StringComparison.Ordinal) ? "" : value;
        }

        public static string RunUrl(string repo, long run)
        {
            string name = NormalizeRepo(repo);
            return name == "" || run <= 0 ? null : "https://github.com/" + name + "/actions/runs/" + run.ToString(CultureInfo.InvariantCulture);
        }

        public void SetRepo(string value)
        {
            string name = NormalizeRepo(value);
            if (Same(name, repo)) return;
            Interlocked.Increment(ref generation);
            repo = name;
            pulls = runs = null;
            SetCurrent(name == "" ? null : new GitHubChecks { Repo = name, Status = Waiting });
        }

        void SetCurrent(GitHubChecks value)
        {
            Current = value;
            if (Changed != null) Changed();
        }

        public Task Tick(long now)
        {
            if (repo == "" || busy || now < next) return null;
            busy = true;
            next = now + IntervalMs;
            int started = generation;
            string name = repo;
            Page oldPulls = pulls, oldRuns = runs;
            return Task.Run(() =>
            {
                Page open = Get(name, Pulls, oldPulls, false);
                Page actions = open.RetryAt > 0 || Volatile.Read(ref generation) != started ? null : Get(name, Runs, oldRuns, true);
                return new[] { open, actions };
            }).ContinueWith(t =>
            {
                busy = false;
                if (t.IsFaulted) { var ignored = t.Exception; }
                // A server backoff still applies when another repository is selected.
                if (t.Status == TaskStatus.RanToCompletion)
                    foreach (Page page in t.Result)
                        if (page != null) next = Math.Max(next, page.RetryAt);
                if (started != generation) return;
                long checkedAt = Current.LastChecked;
                if (t.Status != TaskStatus.RanToCompletion)
                {
                    SetCurrent(new GitHubChecks { Repo = name, LastChecked = checkedAt, Status = "GitHub checks are unavailable." });
                    return;
                }
                Page open = t.Result[0], actions = t.Result[1];
                if (open.Error == null) pulls = open;
                if (actions != null && actions.Error == null) runs = actions;
                string error = open.RetryAt > 0 ? open.Error : actions.RetryAt > 0 ? actions.Error : open.Error ?? actions.Error;
                if (error != null)
                {
                    SetCurrent(new GitHubChecks { Repo = name, LastChecked = checkedAt, Status = error });
                    return;
                }
                GitHubChecks result = Assess(name, open.Items, open.More, actions.Items, actions.More, clock());
                if (result.Status != null) result.LastChecked = checkedAt;
                SetCurrent(result);
            }, scheduler);
        }

        Page Get(string name, string endpoint, Page cached, bool actions)
        {
            var headers = new Dictionary<string, string> { { "Accept", "application/vnd.github+json" } };
            if (cached != null && !string.IsNullOrEmpty(cached.Tag)) headers["If-None-Match"] = cached.Tag;
            HttpResult response = fetch("https://api.github.com/repos/" + name + "/" + endpoint, headers);
            if (response.Status == 304 && cached != null) return cached;
            if (response.Status != 200) return Unavailable(response);
            if (response.Truncated) return new Page { Error = "GitHub sent an incomplete response." };
            object root = Json.TryParse(response.Body);
            object[] items = Json.Arr(actions ? Json.Get(root, "workflow_runs") : root);
            object count = actions ? Json.Get(root, "total_count") : 0;
            long total = Number(count);
            if (items == null || (actions && (!(count is int || count is long) || total < items.Length)))
                return new Page { Error = "GitHub returned an unreadable response." };
            bool more = items.Length > Limit || total > items.Length || response.Link.IndexOf("rel=\"next\"", StringComparison.Ordinal) >= 0;
            return new Page { Items = items.Take(Limit).ToArray(), More = more, Tag = response.ETag };
        }

        Page Unavailable(HttpResult response)
        {
            string message = Text(Json.TryParse(response.Body), "message");
            if (response.Status == 429 || (response.Status == 403 && (response.RetryAfterSeconds > 0 ||
                response.RateLimitRemaining == "0" || message.IndexOf("rate limit", StringComparison.OrdinalIgnoreCase) >= 0)))
            {
                long now = clock();
                long reset;
                long.TryParse(response.RateLimitReset, NumberStyles.None, CultureInfo.InvariantCulture, out reset);
                long retry = Math.Min(response.RetryAfterSeconds, 365L * 24 * 60 * 60);
                return new Page
                {
                    Error = "GitHub rate limit reached. Capsule will retry later.",
                    RetryAt = Math.Max(Math.Min(reset, DateTimeOffset.MaxValue.ToUnixTimeSeconds()) * 1000, now + Math.Max(IntervalMs, retry * 1000)),
                };
            }
            if (response.Status == 401 || response.Status == 403 || response.Status == 404 || response.Status == 410 || response.Status == 451)
                return new Page { Error = "GitHub cannot provide public access to this repository or its Actions runs." };
            if (response.Status == 0) return new Page { Error = "Couldn't reach GitHub. Capsule will retry later." };
            return new Page { Error = "GitHub checks are unavailable. Capsule will retry later." };
        }

        sealed class Pull
        {
            public long Number, Created;
            public string Repo, Branch, Sha;
        }

        sealed class Run
        {
            public long Id, Workflow, Created, Attempt;
            public string Name, Status, Conclusion, Repo, Branch, Sha;
            public object[] Associations;
        }

        internal static GitHubChecks Assess(string repo, object[] pullItems, bool morePulls, object[] runItems, bool moreRuns, long checkedAt)
        {
            var result = new GitHubChecks { Repo = repo, LastChecked = checkedAt, MorePulls = morePulls };
            List<Pull> open = pullItems.Where(item => Text(item, "state") != "closed").Select(item => new Pull
            {
                Number = Number(Json.Get(item, "number")),
                Created = Date(item),
                Repo = Text(item, "head", "repo", "full_name"),
                Branch = Text(item, "head", "ref"),
                Sha = Text(item, "head", "sha"),
            }).ToList();
            List<Run> runs = runItems.Select(item => new Run
            {
                Id = Number(Json.Get(item, "id")),
                Workflow = Number(Json.Get(item, "workflow_id")),
                Created = Date(item),
                Attempt = Number(Json.Get(item, "run_attempt")),
                Name = Text(item, "name"),
                Status = Text(item, "status"),
                Conclusion = Text(item, "conclusion"),
                Repo = Text(item, "head_repository", "full_name"),
                Branch = Text(item, "head_branch"),
                Sha = Text(item, "head_sha"),
                Associations = Associations(item),
            }).ToList();
            if (open.Any(p => p.Number <= 0) || open.Select(p => p.Number).Distinct().Count() != open.Count || runs.Any(r => r.Id <= 0 || r.Workflow <= 0 || r.Created <= 0 ||
                r.Sha == "" || r.Status == "" || r.Associations == null))
            {
                result.Status = "GitHub returned incomplete pull request or workflow data.";
                return result;
            }

            // GitHub returns runs newest first. If an open PR predates the page, it may have an omitted workflow.
            long oldest = runs.Count == 0 ? 0 : runs.Min(r => r.Created);
            var matches = open.ToDictionary(p => p.Number, p => new List<Run>());
            var ambiguous = new HashSet<long>();
            foreach (Run run in runs)
            {
                if (run.Associations.Length != 0)
                {
                    foreach (Pull pull in open.Where(p => Same(p.Sha, run.Sha)))
                        if (run.Associations.Any(a => Number(Json.Get(a, "number")) == pull.Number && AssociationMatches(a, run, pull)))
                            matches[pull.Number].Add(run);
                    continue;
                }
                List<Pull> candidates = open.Where(p => p.Repo != "" && p.Branch != "" &&
                    Same(p.Repo, run.Repo) && p.Branch == run.Branch && Same(p.Sha, run.Sha)).ToList();
                if (candidates.Count == 1 && !morePulls)
                {
                    // An older PR could have used the same head before this PR opened.
                    Pull pull = candidates[0];
                    if (run.Created >= pull.Created) matches[pull.Number].Add(run);
                }
                else foreach (Pull candidate in candidates) ambiguous.Add(candidate.Number);
            }

            foreach (Pull pull in open)
            {
                if (pull.Sha == "" || pull.Repo == "" || pull.Branch == "" || pull.Created <= 0 ||
                    (moreRuns && (oldest == 0 || pull.Created <= oldest)) || ambiguous.Contains(pull.Number))
                {
                    result.Uncertain.Add(pull.Number);
                    continue;
                }
                foreach (var group in matches[pull.Number].GroupBy(r => r.Workflow))
                {
                    Run latest = group.OrderByDescending(r => r.Created).ThenByDescending(r => r.Id).ThenByDescending(r => r.Attempt).First();
                    if (latest.Status != "completed" || !IsFailure(latest.Conclusion)) continue;
                    result.Failures.Add(new GitHubFailure { Pull = pull.Number, Run = latest.Id, Workflow = latest.Name, Conclusion = latest.Conclusion });
                }
            }
            return result;
        }

        static object[] Associations(object item)
        {
            object value = Json.Get(item, "pull_requests");
            return value == null ? new object[0] : Json.Arr(value);
        }

        static bool AssociationMatches(object association, Run run, Pull pull)
        {
            string associatedSha = Text(association, "head", "sha");
            string associatedBranch = Text(association, "head", "ref");
            string associatedRepo = Text(association, "head", "repo", "full_name");
            return (associatedSha == "" || Same(associatedSha, pull.Sha)) &&
                (associatedBranch == "" || associatedBranch == pull.Branch) &&
                (associatedRepo == "" || Same(associatedRepo, pull.Repo)) &&
                (run.Repo == "" || Same(run.Repo, pull.Repo)) && (run.Branch == "" || run.Branch == pull.Branch);
        }

        static bool IsFailure(string conclusion)
        {
            return conclusion == "failure" || conclusion == "timed_out" || conclusion == "startup_failure";
        }

        static bool Same(string a, string b) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        static string Text(object value, params string[] path) { return Json.Str(Json.Get(value, path)) ?? ""; }
        static long Number(object value) { return value is int ? (int)value : value is long ? (long)value : 0; }

        static long Date(object value)
        {
            DateTime parsed;
            return DateTime.TryParse(Text(value, "created_at"), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out parsed) ? Clock.ToMs(parsed) : 0;
        }
    }
}
