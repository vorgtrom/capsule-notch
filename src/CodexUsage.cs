using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Capsule
{
    public sealed class CodexCredential
    {
        public string AccessToken = "";
        public string AccountId = "";
    }

    // Codex usage (spec 4.2): Codex's own sign-in against chatgpt.com/backend-api/wham/usage, falling back to the
    // usage snapshot Codex writes into its session logs after every turn. Poll runs on a background thread.
    public sealed class CodexUsage : IUsageSource
    {
        public const string Endpoint = "https://chatgpt.com/backend-api/wham/usage";

        readonly string home;
        readonly object gate = new object();
        Reading current;
        int consecutive429;
        long backoffUntilMs;

        public Func<CodexCredential, HttpResult> Transport;   // set by tests; null = the real endpoint
        public int RequestsSent;                               // read by tests
        public bool LastCheckUnreachable { get; private set; }   // the last check got no reply at all (network down)

        public CodexUsage(string codexHome, Reading cached)
        {
            home = codexHome;
            current = cached != null ? cached.Clone() : new Reading();
            current.Provider = "codex";
        }

        public Reading Current { get { lock (gate) return current; } }

        public Reading Poll(long now)
        {
            CodexCredential cred = ParseAuth(Files.ReadText(Path.Combine(home, "auth.json")));
            LastCheckUnreachable = false;
            if (cred != null && backoffUntilMs > now) return Current;   // inside a rate-limit wait
            string note = "";
            if (cred != null)
            {
                HttpResult res = Fetch(cred);
                if (res.Status == 200)
                {
                    Reading live = ParseLive(res.Body, now);
                    if (live != null)
                    {
                        consecutive429 = 0;
                        return Publish(live);
                    }
                    note = "Unexpected reply from ChatGPT";
                }
                else if (res.Status == 401 || res.Status == 403) note = "Codex sign-in expired — open Codex to renew it";
                else if (res.Status == 429)
                {
                    consecutive429++;
                    long wait = ClaudeUsage.BackoffSeconds(consecutive429, res.RetryAfterSeconds);
                    backoffUntilMs = now + wait * 1000;
                    note = "Rate limited, retrying in " + Math.Max(1, (wait + 59) / 60) + " min";
                }
                else
                {
                    LastCheckUnreachable = res.Status == 0;   // no reply at all: the network may not be back yet
                    note = res.Status > 0 ? "ChatGPT returned HTTP " + res.Status : "Network error (" + res.Error + ")";
                }
            }
            Reading fromLogs = ReadLatestRollout(home, now);
            Reading previous = Current;
            // After a failed live call, show whichever is newer, so a one-off failure can't swap fresh numbers for an
            // older log snapshot. Without a sign-in there is no live call, and the logs are the source.
            if (fromLogs != null && (cred == null || previous.Windows.Count == 0 || previous.FromLogs || fromLogs.DataAtMs >= previous.DataAtMs))
            {
                fromLogs.Note = note;
                return Publish(fromLogs);
            }
            if (cred == null && fromLogs == null) return Publish(new Reading { Provider = "codex", Status = "none" });
            Reading kept = previous.Clone();
            kept.LastCheckFailed = true;
            kept.Note = note;
            if (kept.Windows.Count == 0) kept.Status = "error";
            return Publish(kept);
        }

        Reading Publish(Reading r)
        {
            lock (gate) current = r;
            return r;
        }

        HttpResult Fetch(CodexCredential cred)
        {
            RequestsSent++;
            if (Transport != null) return Transport(cred);
            var headers = new Dictionary<string, string>();
            headers["Authorization"] = "Bearer " + cred.AccessToken;
            headers["ChatGPT-Account-Id"] = cred.AccountId;
            headers["Accept"] = "application/json";
            return Http.Get(Endpoint, headers, 15000);
        }

        // auth.json, read only: never refreshed or written back. Null when there is no ChatGPT sign-in in it.
        public static CodexCredential ParseAuth(string json)
        {
            object tokens = Json.Get(Json.TryParse(json), "tokens");
            string access = Json.Str(Json.Get(tokens, "access_token"));
            string account = Json.Str(Json.Get(tokens, "account_id"));
            if (string.IsNullOrWhiteSpace(access) || string.IsNullOrWhiteSpace(account)) return null;
            var c = new CodexCredential();
            c.AccessToken = access;
            c.AccountId = account;
            return c;
        }

        public static Reading ParseLive(string json, long now)
        {
            var root = Json.Obj(Json.TryParse(json));
            var limits = Json.Obj(Json.Get(root, "rate_limit"));
            if (limits == null) return null;
            var r = new Reading { Provider = "codex", Status = "ok", HeadlineId = "primary", DataAtMs = now };
            r.Plan = Format.PlanName(Json.Str(Json.Get(root, "plan_type")));
            foreach (string id in new[] { "primary", "secondary" })
            {
                object window = Json.Get(limits, id + "_window");
                double length = Json.Num(Json.Get(window, "limit_window_seconds")) ?? 0;
                AddWindow(r, id, window, length, ResetMs(window, "reset_at", "reset_after_seconds", now), now, false);
            }
            return r.Windows.Count > 0 ? r : null;
        }

        // One line of a rollout log, or null unless it is a token_count event carrying rate limits.
        public static Reading ParseRolloutLine(string line, long now)
        {
            if (string.IsNullOrEmpty(line) || line.IndexOf("token_count", StringComparison.Ordinal) < 0) return null;
            var root = Json.Obj(Json.TryParse(line));
            object payload = Json.Get(root, "payload");
            if (Json.Str(Json.Get(payload, "type")) != "token_count") return null;
            var limits = Json.Obj(Json.Get(payload, "rate_limits"));
            if (limits == null) return null;
            long at = Clock.ParseIsoMs(Json.Str(Json.Get(root, "timestamp")));
            var r = new Reading { Provider = "codex", Status = "ok", HeadlineId = "primary", FromLogs = true, DataAtMs = at > 0 ? at : now };
            r.Plan = Format.PlanName(Json.Str(Json.Get(limits, "plan_type")));
            foreach (string id in new[] { "primary", "secondary" })
            {
                object window = Json.Get(limits, id);
                double length = (Json.Num(Json.Get(window, "window_minutes")) ?? 0) * 60;
                AddWindow(r, id, window, length, ResetMs(window, "resets_at", "resets_in_seconds", r.DataAtMs), now, true);
            }
            return r.Windows.Count > 0 ? r : null;
        }

        // The newest usage snapshot in the three most recent sessions\YYYY\MM\DD folders, or null.
        public static Reading ReadLatestRollout(string codexHome, long now)
        {
            List<string> days = CodexLogs.AllDayDirs(codexHome);
            days.Sort(StringComparer.Ordinal);
            days.Reverse();
            var files = new List<FileInfo>();
            foreach (string day in days.Take(3))
                foreach (string file in CodexLogs.Rollouts(day))
                    files.Add(new FileInfo(file));
            files.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
            foreach (FileInfo file in files)
            {
                Reading r = LastSnapshot(file.FullName, now);
                if (r != null) return r;
            }
            return null;
        }

        public static string WindowLabel(double seconds)
        {
            double hours = seconds / 3600;
            if (Math.Abs(hours - 5) < 0.01) return "5-hour limit";
            if (Math.Abs(hours - 168) < 0.01) return "Weekly limit";
            if (Math.Abs(hours - 720) < 0.01) return "Monthly limit";
            if (hours <= 0) return "Limit";
            if (hours < 48) return Math.Round(hours).ToString(CultureInfo.InvariantCulture) + "-hour limit";
            return Math.Round(hours / 24).ToString(CultureInfo.InvariantCulture) + "-day limit";
        }

        static Reading LastSnapshot(string path, long now)
        {
            string tail = Files.ReadTail(path, 256 * 1024);
            if (tail == null) return null;
            string[] lines = tail.Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                Reading r = ParseRolloutLine(lines[i].Trim(), now);
                if (r != null) return r;
            }
            return null;
        }

        // A reset time from an absolute field (seconds since 1970) or a relative one (seconds after `since`).
        static long ResetMs(object window, string absoluteKey, string relativeKey, long since)
        {
            double? at = Json.Num(Json.Get(window, absoluteKey));
            if (at.HasValue && at.Value > 0) return (long)(at.Value * 1000);
            double? after = Json.Num(Json.Get(window, relativeKey));
            if (after.HasValue && after.Value >= 0 && after.Value < 1e9) return since + (long)(after.Value * 1000);
            return 0;
        }

        // markReset: for log snapshots, a window whose reset has passed since shows 0% and says so.
        static void AddWindow(Reading r, string id, object window, double lengthSeconds, long resetsAtMs, long now, bool markReset)
        {
            double? used = Json.Num(Json.Get(window, "used_percent"));
            if (!used.HasValue) return;
            var w = new LimitWindow { Id = id, Label = WindowLabel(lengthSeconds), Used = Math.Max(0, Math.Min(100, used.Value)), ResetsAtMs = resetsAtMs };
            if (markReset && resetsAtMs > 0 && resetsAtMs <= now)
            {
                w.Used = 0;
                w.ResetsAtMs = 0;
                w.Note = "reset since last use";
            }
            r.Windows.Add(w);
        }
    }
}
