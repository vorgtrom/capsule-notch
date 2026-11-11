using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Capsule
{
    internal static class GitHubMonitorTests
    {
        const string Repo = "owner/capsule";
        const string Fork = "contributor/capsule";
        const string Old = "2026-11-01T00:00:00Z", Recent = "2026-11-02T00:00:00Z", New = "2026-11-03T00:00:00Z";

        public static void Run()
        {
            LinksAndConfig();
            MatchCurrentOpenHead();
            KeepOnlyLatestRun();
            ReportUncertainCoverage();
            ConditionalRequestsAndOutage();
            PayloadBoundsAndCacheSeparation();
            RateLimitsAndUnavailable();
            DiscardAnInFlightRepository();
        }

        static void LinksAndConfig()
        {
            string path = System.IO.Path.Combine(TestRunner.NewTempDir(), "config.json");
            TestRunner.Eq("", Config.Load(path).GitHubRepo, "GitHub monitoring defaults off");
            TestRunner.Check(new Config { GitHubRepo = " owner/capsule " }.Save(path), "save one GitHub repo");
            TestRunner.Eq(Repo, Config.Load(path).GitHubRepo, "saved repo round-trips");
            Files.WriteAtomic(path, "{\"github_repo\":\"owner/../private\"}");
            TestRunner.Eq("", Config.Load(path).GitHubRepo, "invalid config leaves monitoring off");
            foreach (string input in new[] { "https://github.com/owner/capsule", "owner/..", "owner/.", "owner/a?x=y", "owner/a/b", "owner/a#b", "owner/a%2f", "owner/a\\b" })
                TestRunner.Eq("", GitHubMonitor.NormalizeRepo(input), "repo path rejects " + input);
            TestRunner.Eq("https://github.com/owner/capsule/actions/runs/42969367564", GitHubMonitor.RunUrl(Repo, 42969367564), "builds link from repo and large numeric run ID");
            TestRunner.Eq(null, GitHubMonitor.RunUrl(Repo, 0), "a run link needs a positive ID");
        }

        static void MatchCurrentOpenHead()
        {
            object[] pulls = { Pull(1), Pull(2, Fork), Pull(3, Fork, "other"), Pull(4, Repo, "closed", state: "closed") };
            object[] runs =
            {
                Run(41, 1, association: 1, headRepo: Fork),   // wrong fork even with an association
                Run(42, 1, association: 1, sha: "previous"),  // changed PR head
                Run(43, 1, association: 4, branch: "closed"), // closed PR
                Run(44, 1, headRepo: Fork),                    // unique repo/branch/SHA among shared SHAs
                Run(45, 2, association: 3, headRepo: Fork, branch: "other"),
                Run(46, 2, headRepo: "unknown/capsule"),
                Run(47, 3, branch: "other"),                   // branch matches the fork but repo does not
                Run(48, 4, association: 99),                   // never fall back if the association names another PR
                Run(49, 5, date: Old),                         // may belong to an earlier PR with the same head
            };
            var result = GitHubMonitor.Assess(Repo, pulls, false, runs, false, 10);
            TestRunner.Eq("2:44,3:45", Failures(result), "current-head matching uses associations and unique fork/branch identity");

            object[] shared = { Pull(1), Pull(2) };
            var associated = GitHubMonitor.Assess(Repo, shared, false, new[] { Run(51, 1, association: 2) }, false, 10);
            TestRunner.Eq("2:51", Failures(associated), "PR association disambiguates a shared head");
            var ambiguous = GitHubMonitor.Assess(Repo, shared, false, new[] { Run(52, 1) }, false, 10);
            TestRunner.Eq("", Failures(ambiguous), "a shared head without an association never reports a failure");
            TestRunner.Eq("1,2", string.Join(",", ambiguous.Uncertain), "ambiguous PRs show incomplete coverage");
        }

        static void KeepOnlyLatestRun()
        {
            object[] pulls = { Pull(1) };
            object[] runs =
            {
                Run(51, 1, date: Recent), Run(52, 1, date: New, conclusion: "success"),
                Run(53, 2), Run(53, 2, status: "in_progress", conclusion: null, attempt: 2),
                Run(55, 3, conclusion: "timed_out"),
                Run(56, 4, conclusion: "startup_failure"),
                Run(57, 5, conclusion: "cancelled"),
                Run(58, 6, conclusion: "action_required"),
                Run(59, 7),
                Run(60, 8), Run(61, 8, status: "queued", conclusion: null),
            };
            var result = GitHubMonitor.Assess(Repo, pulls, false, runs, false, 10);
            TestRunner.Eq("1:55,1:56,1:59", Failures(result), "success, pending reruns and excluded conclusions suppress old failures");
        }

        static void ReportUncertainCoverage()
        {
            object[] pulls = { Pull(1, date: Old), Pull(2, branch: "new", date: New), Pull(3, branch: "old", date: Old) };
            object[] runs = { Run(11, 1, date: Recent), Run(12, 1, branch: "new", date: New) };
            var result = GitHubMonitor.Assess(Repo, pulls, false, runs, true, 10);
            TestRunner.Eq("2:12", Failures(result), "older PR's visible failure is withheld when the history is incomplete");
            TestRunner.Eq("1,3", string.Join(",", result.Uncertain), "old PRs with or without a match show uncertain coverage");

            var limited = GitHubMonitor.Assess(Repo, pulls, true, new[] { Run(11, 1), Run(12, 1, association: 2, branch: "new", date: New) }, false, 10);
            TestRunner.Check(limited.MorePulls && limited.Uncertain.Contains(1), "truncated pull list cannot establish uniqueness");
            TestRunner.Eq("2:12", Failures(limited), "explicit PR association works with a truncated pull list");
            TestRunner.Check(GitHubMonitor.Assess(Repo, new[] { Pull(1), Pull(1) }, false, runs, false, 10).Status != null,
                "duplicate pull data produces a status instead of a false clean result");
        }

        static void ConditionalRequestsAndOutage()
        {
            long now = Clock.ToMs(new DateTime(2026, 11, 8, 0, 0, 0, DateTimeKind.Utc));
            int scan = 0, calls = 0;
            int caller = Thread.CurrentThread.ManagedThreadId;
            var monitor = new GitHubMonitor(TaskScheduler.Default, (url, headers) =>
            {
                calls++;
                bool actions = IsRuns(url);
                string expectedTag = actions ? "runs" : "pulls";
                TestRunner.Check(caller != Thread.CurrentThread.ManagedThreadId, "GitHub fetch runs off caller thread");
                TestRunner.Check(url.StartsWith("https://api.github.com/repos/" + Repo + "/", StringComparison.Ordinal) &&
                    url.EndsWith("per_page=30", StringComparison.Ordinal), "GitHub fetch uses bounded public endpoints");
                TestRunner.Check(!headers.ContainsKey("Authorization"), "GitHub fetch carries no sign-in");
                string tag;
                headers.TryGetValue("If-None-Match", out tag);
                TestRunner.Eq(scan == 0 ? null : expectedTag, tag, "validators stay with their endpoint");
                if (scan == 1 && actions) return new HttpResult { Status = 0, Error = "Timeout" };
                if (scan >= 1) return new HttpResult { Status = 304 };
                return actions ? Runs("runs", Run(61, 1)) : Pulls("pulls", Pull(1));
            }, () => now);
            TestRunner.Eq(null, monitor.Tick(now), "disabled monitor makes no requests");
            monitor.SetRepo(Repo);
            Check(monitor, now);
            TestRunner.Eq("1:61", Failures(monitor.Current), "initial scan shows the failure");
            TestRunner.Eq(now, monitor.Current.LastChecked, "initial successful scan timestamp");
            TestRunner.Eq(null, monitor.Tick(now + GitHubMonitor.IntervalMs - 1), "scan cannot repeat within three minutes");
            scan++;
            long first = now;
            now += GitHubMonitor.IntervalMs;
            Check(monitor, now);
            TestRunner.Check(monitor.Current.Status.Contains("Couldn't reach") && monitor.Current.Failures.Count == 0, "outage replaces stale rows with network status");
            TestRunner.Eq(first, monitor.Current.LastChecked, "outage retains last successful check time");
            scan++;
            now += GitHubMonitor.IntervalMs;
            Check(monitor, now);
            TestRunner.Eq("1:61", Failures(monitor.Current), "both 304s reuse successful endpoint contents across outage");
            TestRunner.Eq(now, monitor.Current.LastChecked, "successful 304 pair advances last check time");
            TestRunner.Eq(6, calls, "three eligible scans make two calls each");
        }

        static void PayloadBoundsAndCacheSeparation()
        {
            long now = 1000;
            int scan = 0;
            object[] pulls = Enumerable.Range(1, 31).Select(n => Pull(n, branch: "branch" + n)).ToArray();
            HttpResult pullPage = Pulls("pull-page", pulls);
            pullPage.Link = "<https://api.github.com/next>; rel=\"next\"";
            HttpResult runPage = Runs("run-page", Run(11, 1, association: 1, branch: "branch1", date: New),
                Run(12, 1, association: 31, branch: "branch31", date: New));
            var monitor = new GitHubMonitor(TaskScheduler.Default, (url, headers) =>
            {
                if (IsRuns(url))
                {
                    if (scan == 0) return new HttpResult { Status = 503 };
                    TestRunner.Check(!headers.ContainsKey("If-None-Match"), "failed endpoint has no validator");
                    return runPage;
                }
                if (scan == 0) return pullPage;
                TestRunner.Eq("pull-page", headers["If-None-Match"], "success on one endpoint keeps its validator");
                return new HttpResult { Status = 304 };
            }, () => now);
            monitor.SetRepo(Repo);
            Check(monitor, now);
            TestRunner.Check(monitor.Current.Status != null && monitor.Current.LastChecked == 0, "partial scan cannot publish a clean result or successful timestamp");
            scan++;
            now += GitHubMonitor.IntervalMs;
            Check(monitor, now);
            TestRunner.Check(monitor.Current.MorePulls, "pull request pagination survives a 304");
            TestRunner.Eq("1:11", Failures(monitor.Current), "scanner never checks beyond 30 PRs");

            var counts = new GitHubMonitor(TaskScheduler.Default, (url, headers) => IsRuns(url)
                ? new HttpResult { Status = 200, Body = Json.Write(Obj("total_count", 31, "workflow_runs", new[] { Run(11, 1, association: 1, date: New) })) }
                : Pulls("", Pull(1)), () => now);
            counts.SetRepo(Repo);
            Check(counts, now);
            TestRunner.Check(counts.Current.Uncertain.Contains(1) && counts.Current.Failures.Count == 0,
                "run total marks a partial response even without a Link header");
        }

        static void RateLimitsAndUnavailable()
        {
            long now = Clock.ToMs(new DateTime(2026, 11, 8, 0, 0, 0, DateTimeKind.Utc));
            int calls = 0;
            HttpResult reply = new HttpResult { Status = 403, RateLimitRemaining = "0", RateLimitReset = (now / 1000 + 500).ToString() };
            var monitor = new GitHubMonitor(TaskScheduler.Default, (url, headers) => { calls++; return reply; }, () => now);
            monitor.SetRepo(Repo);
            Check(monitor, now);
            TestRunner.Eq(1, calls, "rate-limited first endpoint skips the second request");
            TestRunner.Check(monitor.Current.Status.Contains("rate limit"), "rate-limit status is distinct");
            TestRunner.Eq(null, monitor.Tick(now + 499000), "rate limit backs off to reset");
            now += 500000;
            reply = new HttpResult { Status = 429, RetryAfterSeconds = 420 };
            Check(monitor, now);
            TestRunner.Eq(null, monitor.Tick(now + 419000), "Retry-After backs off a second rate limit");
            now += 420000;
            reply = new HttpResult { Status = 404 };
            Check(monitor, now);
            TestRunner.Check(monitor.Current.Status.Contains("public access"), "private or missing repository shows access status");
            now += GitHubMonitor.IntervalMs;
            reply = new HttpResult { Status = 502 };
            Check(monitor, now);
            TestRunner.Check(monitor.Current.Status.Contains("unavailable"), "server outage shows an unavailable status");
            now += GitHubMonitor.IntervalMs;
            reply = new HttpResult { Status = 200, Body = "[]", Truncated = true };
            Check(monitor, now);
            TestRunner.Check(monitor.Current.Status.Contains("incomplete"), "truncated body shows an incomplete status");
            now += GitHubMonitor.IntervalMs;
            reply = new HttpResult { Status = 200, Body = "invalid" };
            Check(monitor, now);
            TestRunner.Check(monitor.Current.Status.Contains("unreadable"), "invalid JSON shows an unreadable status");
        }

        static void DiscardAnInFlightRepository()
        {
            long now = 1000;
            bool blocked = false;
            using (var entered = new ManualResetEventSlim())
            using (var resume = new ManualResetEventSlim())
            {
                var monitor = new GitHubMonitor(TaskScheduler.Default, (url, headers) =>
                {
                    bool actions = IsRuns(url);
                    bool first = url.Contains("/owner/capsule/");
                    if (first && !actions)
                    {
                        entered.Set();
                        blocked = resume.Wait(5000);
                    }
                    if (!first) TestRunner.Check(!headers.ContainsKey("If-None-Match"), "repo change discards each old validator");
                    if (first && actions) TestRunner.Check(false, "repo change skips an old endpoint that has not started");
                    return actions ? Runs(first ? "old runs" : "new runs", Run(first ? 71 : 72, 1)) :
                        Pulls(first ? "old pulls" : "new pulls", Pull(1));
                }, () => now);
                monitor.SetRepo(Repo);
                Task scan = monitor.Tick(now);
                bool started = entered.Wait(5000);
                monitor.SetRepo("owner/replacement");
                TestRunner.Eq(null, monitor.Tick(now + GitHubMonitor.IntervalMs), "repo change does not start an overlapping scan");
                resume.Set();
                scan.GetAwaiter().GetResult();
                TestRunner.Check(started && blocked, "test changed repository before the old response returned");
                TestRunner.Eq("owner/replacement", monitor.Current.Repo, "in-flight repo change keeps the replacement snapshot");
                TestRunner.Check(monitor.Current.Failures.Count == 0 && monitor.Current.LastChecked == 0, "in-flight result is discarded");
                now += GitHubMonitor.IntervalMs;
                Check(monitor, now);
                TestRunner.Eq("1:72", Failures(monitor.Current), "replacement uses only its own response");
                monitor.SetRepo("");
                TestRunner.Eq(null, monitor.Current, "turning monitoring off drops cached rows");
            }
        }

        static void Check(GitHubMonitor monitor, long now)
        {
            Task scan = monitor.Tick(now);
            TestRunner.Check(scan != null, "eligible GitHub scan starts");
            if (scan != null) scan.GetAwaiter().GetResult();
        }

        static string Failures(GitHubChecks checks) { return string.Join(",", checks.Failures.Select(f => f.Pull + ":" + f.Run)); }

        static bool IsRuns(string url) { return url.Contains("/actions/runs?event=pull_request"); }

        static HttpResult Pulls(string tag, params object[] pulls)
        {
            return new HttpResult { Status = 200, ETag = tag, Body = Json.Write(pulls) };
        }

        static HttpResult Runs(string tag, params object[] runs)
        {
            return new HttpResult { Status = 200, ETag = tag, Body = Json.Write(Obj("total_count", runs.Length, "workflow_runs", runs)) };
        }

        static object Pull(int number, string repo = Repo, string branch = "main", string sha = "commit", string date = Recent, string state = "open")
        {
            return Obj("number", number, "state", state, "created_at", date, "head", Head(repo, branch, sha));
        }

        static object Run(long id, long workflow, int association = 0, string headRepo = Repo, string branch = "main",
            string sha = "commit", string date = Recent, string status = "completed", string conclusion = "failure", long attempt = 1)
        {
            return Obj("id", id, "workflow_id", workflow, "name", "Build", "created_at", date,
                "run_attempt", attempt, "head_sha", sha, "head_branch", branch, "head_repository", Obj("full_name", headRepo),
                "status", status, "conclusion", conclusion, "html_url", "https://example.invalid",
                "pull_requests", association == 0 ? new object[0] : new[] { Obj("number", association, "head", Head(headRepo, branch, sha)) });
        }

        static object Head(string repo, string branch, string sha)
        {
            return Obj("repo", Obj("full_name", repo), "ref", branch, "sha", sha);
        }

        static object Obj(params object[] pairs)
        {
            var result = new Dictionary<string, object>();
            for (int i = 0; i < pairs.Length; i += 2) result.Add((string)pairs[i], pairs[i + 1]);
            return result;
        }
    }
}
