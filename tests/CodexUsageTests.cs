using System;
using System.IO;
using System.Text;

namespace Capsule
{
    public static class CodexUsageTests
    {
        const long LiveNow = 1790683200000;      // 2026-09-29 12:00 UTC
        const long LineTime = 1790582460000;     // the snapshot line: 2026-09-28 08:01 UTC
        const long BeforeReset = 1790586060000;  // an hour after the snapshot
        const long AfterReset = 1790596860000;   // four hours after: the 5-hour window has reset

        public static void Run()
        {
            ReadsTheAuthFile();
            ParsesTheLiveReply();
            LabelsWindowsByLength();
            ParsesASnapshotLine();
            MarksWindowsThatResetSinceTheSnapshot();
            FindsTheNewestSnapshot();
            ReadsLiveUsageWhenSignedIn();
            FallsBackToLogsWhenTheSignInExpires();
            AbsentWithoutSignInOrLogs();
            PrefersTheNewestFileAndLine();
            LooksOnlyAtTheThreeNewestDays();
            ReadsOnlyTheFileTail();
            WaitsOutARateLimit();
            KeepsAFresherLiveReadingThroughABlip();
            NoReplyIsMarkedUnreachable();
            NewerLogsWinAfterASignInExpires();
            WithoutSignInTheLogsAreTheSource();
        }

        static string[] Lines() { return TestRunner.Fixture("codex-rollout.jsonl").Replace("\r", "").Split('\n'); }

        static string HomeWithAuth()
        {
            string home = TestRunner.NewTempDir();
            File.WriteAllText(Path.Combine(home, "auth.json"), TestRunner.Fixture("codex-auth.json"));
            return home;
        }

        static void ReadsTheAuthFile()
        {
            CodexCredential c = CodexUsage.ParseAuth(TestRunner.Fixture("codex-auth.json"));
            TestRunner.Eq("codex-access-TEST", c.AccessToken, "token");
            TestRunner.Eq("acct-TEST", c.AccountId, "account");
            TestRunner.Check(CodexUsage.ParseAuth("{\"OPENAI_API_KEY\":\"sk-x\",\"tokens\":null}") == null, "an API-key login has no usable token");
            TestRunner.Check(CodexUsage.ParseAuth(null) == null, "no file");
        }

        static void ParsesTheLiveReply()
        {
            Reading r = CodexUsage.ParseLive(TestRunner.Fixture("codex-usage.json"), LiveNow);
            TestRunner.Eq("Plus", r.Plan, "plan");
            TestRunner.Eq(2, r.Windows.Count, "two windows");
            TestRunner.Eq("primary", r.Headline.Id, "primary is the headline");
            TestRunner.Near(21, r.Headline.Used, "used");
            TestRunner.Eq(1790690400000L, r.Headline.ResetsAtMs, "reset_at seconds to ms");
            TestRunner.Eq("Weekly limit", r.Windows[1].Label, "weekly");
            TestRunner.Check(!r.FromLogs, "live");
            TestRunner.Check(CodexUsage.ParseLive("{\"detail\":\"Unauthorized\"}", LiveNow) == null, "no rate_limit");
            TestRunner.Check(CodexUsage.ParseLive("{\"rate_limit\":{\"primary_window\":null,\"secondary_window\":null}}", LiveNow) == null, "a reply with no windows counts as unexpected");
        }

        static void LabelsWindowsByLength()
        {
            TestRunner.Eq("5-hour limit", CodexUsage.WindowLabel(18000), "5 h");
            TestRunner.Eq("Weekly limit", CodexUsage.WindowLabel(604800), "7 d");
            TestRunner.Eq("Monthly limit", CodexUsage.WindowLabel(2592000), "30 d");
            TestRunner.Eq("3-hour limit", CodexUsage.WindowLabel(10800), "other hours");
            TestRunner.Eq("2-day limit", CodexUsage.WindowLabel(172800), "other days");
        }

        static void ParsesASnapshotLine()
        {
            Reading r = CodexUsage.ParseRolloutLine(Lines()[1], BeforeReset);
            TestRunner.Check(r.FromLogs, "from logs");
            TestRunner.Eq(LineTime, r.DataAtMs, "time of the line");
            TestRunner.Near(30, r.Headline.Used, "primary");
            TestRunner.Near(12, r.Windows[1].Used, "secondary");
            TestRunner.Eq("5-hour limit", r.Headline.Label, "label from window_minutes");
            TestRunner.Check(CodexUsage.ParseRolloutLine(Lines()[2], BeforeReset) == null, "null rate_limits skipped");
            TestRunner.Check(CodexUsage.ParseRolloutLine(Lines()[0], BeforeReset) == null, "other events skipped");
        }

        static void MarksWindowsThatResetSinceTheSnapshot()
        {
            Reading r = CodexUsage.ParseRolloutLine(Lines()[1], AfterReset);
            TestRunner.Near(0, r.Headline.Used, "a reset window reads 0");
            TestRunner.Eq("reset since last use", r.Headline.Note, "and says why");
            TestRunner.Near(12, r.Windows[1].Used, "the weekly window is untouched");
        }

        static void FindsTheNewestSnapshot()
        {
            string home = TestRunner.NewTempDir();
            string day = Path.Combine(home, "sessions", "2026", "09", "28");
            Directory.CreateDirectory(day);
            string withSnapshot = Path.Combine(day, "rollout-2026-09-28T08-00-00-a.jsonl");
            File.WriteAllText(withSnapshot, TestRunner.Fixture("codex-rollout.jsonl"));
            File.SetLastWriteTimeUtc(withSnapshot, new DateTime(2026, 9, 28, 8, 3, 0, DateTimeKind.Utc));
            string newerEmpty = Path.Combine(day, "rollout-2026-09-28T09-00-00-b.jsonl");
            File.WriteAllText(newerEmpty, "{\"type\":\"session_meta\",\"payload\":{}}\n");
            File.SetLastWriteTimeUtc(newerEmpty, new DateTime(2026, 9, 28, 9, 0, 0, DateTimeKind.Utc));
            Reading r = CodexUsage.ReadLatestRollout(home, BeforeReset);
            TestRunner.Check(r != null && Math.Abs(r.Headline.Used - 30) < 1e-9, "a newer file without a snapshot is skipped");
            TestRunner.Check(CodexUsage.ReadLatestRollout(TestRunner.NewTempDir(), BeforeReset) == null, "no sessions folder");
        }

        static void ReadsLiveUsageWhenSignedIn()
        {
            var poller = new CodexUsage(HomeWithAuth(), null);
            string account = null;
            poller.Transport = delegate(CodexCredential c)
            {
                account = c.AccountId;
                return new HttpResult { Status = 200, Body = TestRunner.Fixture("codex-usage.json") };
            };
            Reading r = poller.Poll(LiveNow);
            TestRunner.Eq("acct-TEST", account, "account id sent");
            TestRunner.Near(21, r.Headline.Used, "live number");
            TestRunner.Check(!r.FromLogs, "live, not logs");
        }

        static void FallsBackToLogsWhenTheSignInExpires()
        {
            string home = HomeWithAuth();
            string day = Path.Combine(home, "sessions", "2026", "09", "28");
            Directory.CreateDirectory(day);
            File.WriteAllText(Path.Combine(day, "rollout-a.jsonl"), TestRunner.Fixture("codex-rollout.jsonl"));
            var poller = new CodexUsage(home, null);
            poller.Transport = delegate { return new HttpResult { Status = 401 }; };
            Reading r = poller.Poll(BeforeReset);
            TestRunner.Check(r.FromLogs, "log reading used");
            TestRunner.Near(30, r.Headline.Used, "log number");
            TestRunner.Check(r.Note.StartsWith("Codex sign-in expired", StringComparison.Ordinal), "says why: " + r.Note);
        }

        static void AbsentWithoutSignInOrLogs()
        {
            var poller = new CodexUsage(TestRunner.NewTempDir(), null);
            TestRunner.Eq("none", poller.Poll(LiveNow).Status, "nothing to show");
            TestRunner.Eq(0, poller.RequestsSent, "no request without a sign-in");
        }

        // A token_count line whose 5-hour window resets three hours after LineTime (so it's not reset at BeforeReset).
        static string SnapshotLine(string timestamp, double primaryUsed)
        {
            return "{\"timestamp\":\"" + timestamp + "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":null,\"rate_limits\":{\"primary\":{\"used_percent\":"
                + primaryUsed.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ",\"window_minutes\":300,\"resets_at\":1790593260},\"secondary\":null,\"plan_type\":\"plus\"}}}";
        }

        static void PrefersTheNewestFileAndLine()
        {
            string home = TestRunner.NewTempDir();
            string day = Path.Combine(home, "sessions", "2026", "09", "28");
            Directory.CreateDirectory(day);
            // The file modified last is the middle one by name and by creation time. Creation times are set explicitly
            // because stamps of back-to-back creates often tie. Only last-modified order picks it.
            string first = Path.Combine(day, "rollout-a.jsonl");
            string newest = Path.Combine(day, "rollout-b.jsonl");
            string last = Path.Combine(day, "rollout-c.jsonl");
            File.WriteAllText(first, SnapshotLine("2026-09-28T08:01:00.000Z", 30) + "\n");
            File.WriteAllText(newest, SnapshotLine("2026-09-28T08:05:00.000Z", 40) + "\n" + SnapshotLine("2026-09-28T08:06:00.000Z", 45) + "\n");
            File.WriteAllText(last, SnapshotLine("2026-09-28T08:00:00.000Z", 20) + "\n");
            File.SetCreationTimeUtc(first, new DateTime(2026, 9, 28, 7, 0, 0, DateTimeKind.Utc));
            File.SetCreationTimeUtc(newest, new DateTime(2026, 9, 28, 7, 30, 0, DateTimeKind.Utc));
            File.SetCreationTimeUtc(last, new DateTime(2026, 9, 28, 8, 0, 0, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(first, new DateTime(2026, 9, 28, 8, 1, 0, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(newest, new DateTime(2026, 9, 28, 8, 6, 0, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(last, new DateTime(2026, 9, 28, 8, 0, 0, DateTimeKind.Utc));
            Reading r = CodexUsage.ReadLatestRollout(home, BeforeReset);
            TestRunner.Check(r != null && Math.Abs(r.Headline.Used - 45) < 1e-9, "the most recently modified file's last snapshot wins");
        }

        static void LooksOnlyAtTheThreeNewestDays()
        {
            string home = TestRunner.NewTempDir();
            foreach (string d in new[] { "25", "26", "27", "28" }) Directory.CreateDirectory(Path.Combine(home, "sessions", "2026", "09", d));
            File.WriteAllText(Path.Combine(home, "sessions", "2026", "09", "25", "rollout-old.jsonl"), SnapshotLine("2026-09-25T08:00:00.000Z", 60) + "\n");
            TestRunner.Check(CodexUsage.ReadLatestRollout(home, BeforeReset) == null, "a snapshot in the fourth-newest date folder is not searched");
            File.WriteAllText(Path.Combine(home, "sessions", "2026", "09", "26", "rollout-mid.jsonl"), SnapshotLine("2026-09-26T08:00:00.000Z", 50) + "\n");
            Reading r = CodexUsage.ReadLatestRollout(home, BeforeReset);
            TestRunner.Check(r != null && Math.Abs(r.Headline.Used - 50) < 1e-9, "the third-newest date folder is searched");
        }

        static void ReadsOnlyTheFileTail()
        {
            string filler = "{\"type\":\"response_item\",\"payload\":{\"type\":\"message\"}}\n";
            string near = TestRunner.NewTempDir(), far = TestRunner.NewTempDir();
            foreach (string home in new[] { near, far }) Directory.CreateDirectory(Path.Combine(home, "sessions", "2026", "09", "28"));
            var nearText = new StringBuilder(SnapshotLine("2026-09-28T08:01:00.000Z", 35) + "\n");
            while (nearText.Length < 250 * 1024) nearText.Append(filler);
            File.WriteAllText(Path.Combine(near, "sessions", "2026", "09", "28", "rollout-a.jsonl"), nearText.ToString());
            var farText = new StringBuilder(SnapshotLine("2026-09-28T08:01:00.000Z", 35) + "\n");
            while (farText.Length < 262 * 1024) farText.Append(filler);
            File.WriteAllText(Path.Combine(far, "sessions", "2026", "09", "28", "rollout-a.jsonl"), farText.ToString());
            Reading found = CodexUsage.ReadLatestRollout(near, BeforeReset);
            TestRunner.Check(found != null && Math.Abs(found.Headline.Used - 35) < 1e-9, "a snapshot 250 KB from the end is found");
            TestRunner.Check(CodexUsage.ReadLatestRollout(far, BeforeReset) == null, "a snapshot more than 256 KB from the end is not searched for");
        }

        static void WaitsOutARateLimit()
        {
            var poller = new CodexUsage(HomeWithAuth(), null);
            poller.Transport = delegate { return new HttpResult { Status = 429 }; };
            Reading first = poller.Poll(LiveNow);
            TestRunner.Check(first.Note.StartsWith("Rate limited, retrying in 1 min", StringComparison.Ordinal), "rate-limit note: " + first.Note);
            poller.Poll(LiveNow + 30 * 1000);
            TestRunner.Eq(1, poller.RequestsSent, "no request during the 60 s wait");
            poller.Poll(LiveNow + 61 * 1000);
            TestRunner.Eq(2, poller.RequestsSent, "asks again once the wait is over");
        }

        static void KeepsAFresherLiveReadingThroughABlip()
        {
            string home = HomeWithAuth();
            string day = Path.Combine(home, "sessions", "2026", "09", "28");
            Directory.CreateDirectory(day);
            File.WriteAllText(Path.Combine(day, "rollout-a.jsonl"), TestRunner.Fixture("codex-rollout.jsonl"));
            var poller = new CodexUsage(home, null);
            poller.Transport = delegate { return new HttpResult { Status = 200, Body = TestRunner.Fixture("codex-usage.json") }; };
            poller.Poll(LiveNow);
            poller.Transport = delegate { return new HttpResult { Status = 503 }; };
            Reading r = poller.Poll(LiveNow + 5 * 60 * 1000);
            TestRunner.Check(!r.FromLogs && Math.Abs(r.Headline.Used - 21) < 1e-9, "a one-off failure keeps the fresher live numbers, not the older log snapshot");
            TestRunner.Check(r.LastCheckFailed && r.Note == "ChatGPT returned HTTP 503", "and says why: " + r.Note);
        }

        static void NoReplyIsMarkedUnreachable()
        {
            var poller = new CodexUsage(HomeWithAuth(), null);
            poller.Transport = delegate { return new HttpResult { Status = 0, Error = "ConnectFailure" }; };
            poller.Poll(LiveNow);
            TestRunner.Check(poller.LastCheckUnreachable, "no reply at all: unreachable");
            poller.Transport = delegate { return new HttpResult { Status = 503 }; };
            poller.Poll(LiveNow + 60 * 1000);
            TestRunner.Check(!poller.LastCheckUnreachable, "a server error is still a reply");
        }

        static void NewerLogsWinAfterASignInExpires()
        {
            string home = HomeWithAuth();
            string day = Path.Combine(home, "sessions", "2026", "09", "28");
            Directory.CreateDirectory(day);
            File.WriteAllText(Path.Combine(day, "rollout-a.jsonl"), TestRunner.Fixture("codex-rollout.jsonl"));
            var poller = new CodexUsage(home, null);
            poller.Transport = delegate { return new HttpResult { Status = 200, Body = TestRunner.Fixture("codex-usage.json") }; };
            poller.Poll(LineTime - 5 * 60 * 1000);
            poller.Transport = delegate { return new HttpResult { Status = 401 }; };
            Reading r = poller.Poll(BeforeReset);
            TestRunner.Check(r.FromLogs && Math.Abs(r.Headline.Used - 30) < 1e-9, "a newer log snapshot replaces an older live reading");
            TestRunner.Check(r.Note.StartsWith("Codex sign-in expired", StringComparison.Ordinal), "and says why: " + r.Note);
        }

        static void WithoutSignInTheLogsAreTheSource()
        {
            string home = TestRunner.NewTempDir();
            string day = Path.Combine(home, "sessions", "2026", "09", "28");
            Directory.CreateDirectory(day);
            File.WriteAllText(Path.Combine(day, "rollout-a.jsonl"), TestRunner.Fixture("codex-rollout.jsonl"));
            var fresh = new CodexUsage(home, null);
            Reading r = fresh.Poll(BeforeReset);
            TestRunner.Check(r.FromLogs && Math.Abs(r.Headline.Used - 30) < 1e-9, "no sign-in: the log snapshot is shown");
            TestRunner.Eq(0, fresh.RequestsSent, "and nothing is requested");
            var cachedLive = new Reading { Provider = "codex", Status = "ok", HeadlineId = "primary", DataAtMs = LiveNow };
            cachedLive.Windows.Add(new LimitWindow { Id = "primary", Label = "5-hour limit", Used = 21 });
            var restarted = new CodexUsage(home, cachedLive);
            Reading r2 = restarted.Poll(LiveNow + 60 * 1000);
            TestRunner.Check(r2.FromLogs, "no sign-in: the logs replace a cached live reading, even a newer one");
            TestRunner.Eq(0, restarted.RequestsSent, "still nothing requested");
        }
    }
}
