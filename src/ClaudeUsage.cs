using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Capsule
{
    public sealed class ClaudeCredential
    {
        public string Token = "";
        public long ExpiresAtMs;   // 0 when the file names no expiry
        public string Plan = "";   // "max", "pro", ...
    }

    // Claude usage (spec 4.1): the Claude CLI's sign-in, api/oauth/usage, rate-limit waits, and keeping the
    // sign-in fresh by running `claude -p`. Poll runs on a background thread, never two at once.
    public sealed class ClaudeUsage : IUsageSource
    {
        public const string Endpoint = "https://api.anthropic.com/api/oauth/usage";
        public const long RenewMarginMs = 4 * 60 * 1000;
        public const long RenewRetryBaseMs = 10 * 60 * 1000;
        public const long RenewRetryCapMs = 60 * 60 * 1000;

        // Step 6 of Task 6 sets this to false if `claude -p` turns out not to be a harmless no-op.
        public static readonly bool RenewWithCli = true;

        readonly string credentialPath;
        readonly object gate = new object();
        Reading current;
        int consecutive429;
        long backoffUntilMs;
        long renewAttemptedFor, renewLastAttempt;
        int renewFailures;

        public bool RenewalDisabled;                  // set by tests so they never launch the real CLI
        public Func<string, HttpResult> Transport;    // set by tests; null = the real endpoint
        public int RequestsSent;                      // read by tests
        public bool LastCheckUnreachable { get; private set; }   // the last check got no reply at all (network down)

        public ClaudeUsage(string credentialPath, Reading cached)
        {
            this.credentialPath = credentialPath;
            current = cached != null ? cached.Clone() : new Reading();
            current.Provider = "claude";
            current.HeadlineId = "five_hour";
        }

        public Reading Current { get { lock (gate) return current; } }
        public long BackoffUntilMs { get { return backoffUntilMs; } }

        // One check: renew the sign-in if it is about to expire, then read usage. Returns the new reading,
        // which also becomes Current.
        public Reading Poll(long now)
        {
            Reading r = Current.Clone();
            LastCheckUnreachable = false;
            ClaudeCredential cred = ReadCredential(credentialPath);
            if (cred != null && RenewWithCli && !RenewalDisabled) MaybeRenew(cred, now);
            if (backoffUntilMs > now) return r;   // inside a rate-limit wait: send nothing
            cred = ReadCredential(credentialPath);
            if (cred == null) return Publish(Failed(r, "signin", "Sign in to the Claude CLI to see your usage"));
            r.Plan = Format.PlanName(cred.Plan);
            string keep = r.Windows.Count > 0 ? "ok" : "error";
            // An expired token is never sent: the endpoint answers it with a long 429, not a 401.
            if (cred.ExpiresAtMs > 0 && cred.ExpiresAtMs <= now)
                return Publish(Failed(r, r.Windows.Count > 0 ? "ok" : "signin", "Sign-in expired — run claude once in a terminal to renew it"));

            HttpResult res = Fetch(cred.Token);
            if (res.Status == 401)
            {
                // Claude Code may have just replaced the token: read it again and retry once.
                ClaudeCredential again = ReadCredential(credentialPath);
                if (again != null && again.Token != cred.Token) res = Fetch(again.Token);
            }
            if (res.Status == 200)
            {
                List<LimitWindow> windows = Parse(res.Body);
                if (windows == null || windows.Count == 0) return Publish(Failed(r, keep, "Unexpected reply from Anthropic"));
                consecutive429 = 0;
                backoffUntilMs = 0;
                r.Status = "ok";
                r.Windows = windows;
                r.DataAtMs = now;
                r.LastCheckFailed = false;
                r.Note = "";
                return Publish(r);
            }
            if (res.Status == 401) return Publish(Failed(r, "signin", "Sign-in rejected — sign in again"));
            if (res.Status == 403) return Publish(Failed(r, keep, "Access denied (network or account)"));
            if (res.Status == 429)
            {
                consecutive429++;
                long wait = BackoffSeconds(consecutive429, res.RetryAfterSeconds);
                backoffUntilMs = now + wait * 1000;
                long minutes = Math.Max(1, (wait + 59) / 60);
                // A missing or invalid token is also answered with 429 and a Retry-After of about an hour.
                string note = res.RetryAfterSeconds >= 3000
                    ? "Usage checks paused for " + minutes + " min (if this keeps happening, sign in to the Claude CLI again)"
                    : "Rate limited, retrying in " + minutes + " min";
                return Publish(Failed(r, keep, note));
            }
            LastCheckUnreachable = res.Status == 0;   // no reply at all: the network may not be back yet
            return Publish(Failed(r, keep, res.Status > 0 ? "Anthropic returned HTTP " + res.Status : "Network error (" + res.Error + ")"));
        }

        static Reading Failed(Reading r, string status, string note)
        {
            r.Status = status;
            r.Note = note;
            r.LastCheckFailed = true;
            return r;
        }

        Reading Publish(Reading r)
        {
            lock (gate) current = r;
            return r;
        }

        HttpResult Fetch(string token)
        {
            RequestsSent++;
            if (Transport != null) return Transport(token);
            var headers = new Dictionary<string, string>();
            headers["Authorization"] = "Bearer " + token;
            headers["anthropic-beta"] = "oauth-2025-04-20";
            return Http.Get(Endpoint, headers, 15000);
        }

        void MaybeRenew(ClaudeCredential cred, long now)
        {
            if (!ShouldRenew(cred.ExpiresAtMs, now, renewAttemptedFor, renewLastAttempt, renewFailures)) return;
            if (renewAttemptedFor != cred.ExpiresAtMs) renewFailures = 0;
            renewAttemptedFor = cred.ExpiresAtMs;
            renewLastAttempt = now;
            string cli = FindCli(Paths.Home, Environment.GetEnvironmentVariable("PATH"));
            if (cli == null)
            {
                renewFailures++;
                Log.Info("claude: sign-in about to expire and no standalone claude.exe found to renew it");
                return;
            }
            try { RunRenewal(cli); }
            catch (Exception e) { Log.Error("claude: renewal could not start", e); }
            ClaudeCredential after = ReadCredential(credentialPath);
            if (after != null && after.ExpiresAtMs > cred.ExpiresAtMs)
            {
                renewFailures = 0;
                consecutive429 = 0;
                backoffUntilMs = 0;
                Log.Info("claude: sign-in renewed");
            }
            else
            {
                renewFailures++;
                Log.Info("claude: ran claude -p but the sign-in expiry did not move");
            }
        }

        // Whether to launch `claude -p` now. Claude Code only renews a token within 5 minutes of expiry, so an
        // earlier launch would do nothing. A failed attempt is retried after 10, 20, 40, then every 60 minutes.
        public static bool ShouldRenew(long expiresAt, long now, long attemptedFor, long lastAttempt, int failures)
        {
            if (expiresAt <= 0) return false;
            if (expiresAt > now + RenewMarginMs) return false;
            if (lastAttempt <= 0 || attemptedFor != expiresAt) return true;
            return now - lastAttempt >= RetryWaitMs(failures);
        }

        public static long RetryWaitMs(int failures)
        {
            int doublings = Math.Min(Math.Max(failures, 1) - 1, 10);
            return Math.Min(RenewRetryBaseMs << doublings, RenewRetryCapMs);
        }

        // Rate-limit wait: 60 s, doubling to at most 15 min. A longer Retry-After always wins.
        public static long BackoffSeconds(int consecutive429, long retryAfter)
        {
            int doublings = Math.Min(Math.Max(consecutive429, 1) - 1, 10);
            long wait = Math.Min(60L << doublings, 900);
            return Math.Max(wait, retryAfter);
        }

        public static ClaudeCredential ParseCredential(string json)
        {
            var root = Json.Obj(Json.TryParse(json));
            if (root == null) return null;
            var oauth = Json.Obj(Json.Get(root, "claudeAiOauth")) ?? root;
            string token = Json.Str(Json.Get(oauth, "accessToken"));
            if (string.IsNullOrWhiteSpace(token)) return null;   // an empty token means signed out
            var c = new ClaudeCredential();
            c.Token = token;
            c.ExpiresAtMs = (long)(Json.Num(Json.Get(oauth, "expiresAt")) ?? 0);
            c.Plan = Json.Str(Json.Get(oauth, "subscriptionType")) ?? "";
            return c;
        }

        public static ClaudeCredential ReadCredential(string path) { return ParseCredential(Files.ReadText(path)); }

        // The usage reply: named windows first (five_hour, seven_day, then seven_day_<model>), then any window
        // that only the newer "limits" array has. Null when the reply is not JSON.
        public static List<LimitWindow> Parse(string json)
        {
            var root = Json.Obj(Json.TryParse(json));
            if (root == null) return null;
            var list = new List<LimitWindow>();
            AddNamed(list, root, "five_hour");
            AddNamed(list, root, "seven_day");
            foreach (string key in root.Keys)
                if (key.StartsWith("seven_day_", StringComparison.Ordinal)) AddNamed(list, root, key);
            foreach (object item in Json.Arr(Json.Get(root, "limits")) ?? new object[0])
            {
                string kind = Json.Str(Json.Get(item, "kind"));
                double? percent = Json.Num(Json.Get(item, "percent"));
                long resets = Clock.ParseIsoMs(Json.Str(Json.Get(item, "resets_at")));
                if (kind == null || !percent.HasValue || resets <= 0) continue;
                string id = KindToId(kind);
                string label = LabelFor(id);
                double used = percent.Value;
                bool twin = list.Any(w => w.Id == id || w.Label == label
                    || (w.ResetsAtMs / 1000 == resets / 1000 && Math.Abs(w.Used - used) < 0.5));
                if (!twin) list.Add(new LimitWindow { Id = id, Label = label, Used = Clamp(used), ResetsAtMs = resets });
            }
            return list;
        }

        static void AddNamed(List<LimitWindow> list, Dictionary<string, object> root, string key)
        {
            object window = Json.Get(root, key);
            double? used = Json.Num(Json.Get(window, "utilization"));
            long resets = Clock.ParseIsoMs(Json.Str(Json.Get(window, "resets_at")));
            if (!used.HasValue || resets <= 0 || list.Any(w => w.Id == key)) return;   // no reset time: not shown
            list.Add(new LimitWindow { Id = key, Label = LabelFor(key), Used = Clamp(used.Value), ResetsAtMs = resets });
        }

        static string KindToId(string kind)
        {
            switch (kind)
            {
                case "session":
                case "five_hour":
                    return "five_hour";
                case "weekly":
                case "weekly_all":
                case "seven_day":
                    return "seven_day";
                case "weekly_opus":
                    return "seven_day_opus";
                case "weekly_sonnet":
                    return "seven_day_sonnet";
                default:
                    return kind;
            }
        }

        public static string LabelFor(string id)
        {
            if (id == "five_hour") return "Current session";
            if (id == "seven_day") return "Weekly · all models";
            if (id.StartsWith("seven_day_", StringComparison.Ordinal)) return "Weekly · " + Capitalise(id.Substring("seven_day_".Length).Replace('_', ' '));
            return Capitalise(id.Replace('_', ' '));
        }

        static string Capitalise(string s) { return s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1); }
        static double Clamp(double percent) { return Math.Max(0, Math.Min(100, percent)); }

        // The standalone Claude CLI: the native installer's copy first, then PATH. The desktop app's bundled copy
        // renews its own sign-in elsewhere and would leave .credentials.json untouched, so it is skipped.
        public static string FindCli(string home, string pathVariable)
        {
            var candidates = new List<string>();
            if (!string.IsNullOrEmpty(home)) candidates.Add(Path.Combine(home, ".local", "bin", "claude.exe"));
            foreach (string dir in (pathVariable ?? "").Split(';'))
            {
                string d = dir.Trim().Trim('"');
                if (d.Length == 0) continue;
                try { candidates.Add(Path.Combine(d, "claude.exe")); }
                catch (ArgumentException) { }
            }
            foreach (string candidate in candidates)
                if (!IsDesktopOwned(candidate) && File.Exists(candidate)) return candidate;
            return null;
        }

        public static bool IsDesktopOwned(string path)
        {
            string p = path.Replace('/', '\\').ToLowerInvariant();
            return p.Contains("\\anthropicclaude\\") || p.Contains("\\claude\\claude-code\\") || p.Contains("\\windowsapps\\");
        }

        // `claude -p` with stdin closed starts up, renews an ageing token as it does, then exits for want of a
        // prompt: no conversation, no quota. Its output is thrown away unread.
        static void RunRenewal(string cli)
        {
            var psi = new ProcessStartInfo(cli, "-p");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardInput = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.WorkingDirectory = Paths.Home;
            StripClaudeSessionVariables(psi);
            psi.EnvironmentVariables["CAPSULE_SKIP_HOOK"] = "1";   // its hooks must not report this as a session
            using (Process p = Process.Start(psi))
            {
                p.OutputDataReceived += delegate { };
                p.ErrorDataReceived += delegate { };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                p.StandardInput.Close();
                if (!p.WaitForExit(30000))
                {
                    try { p.Kill(); }
                    catch (Exception) { }
                }
            }
        }

        // Opens a console window running `claude auth login`; onExit runs (on a worker thread) when it closes.
        // Returns false when no standalone CLI was found.
        public static bool StartSignIn(Action onExit)
        {
            string cli = FindCli(Paths.Home, Environment.GetEnvironmentVariable("PATH"));
            if (cli == null) return false;
            var psi = new ProcessStartInfo(cli, "auth login");
            psi.UseShellExecute = false;   // a console program started like this from a windowed app gets its own console
            psi.CreateNoWindow = false;
            psi.WorkingDirectory = Paths.Home;
            StripClaudeSessionVariables(psi);
            var p = new Process();
            p.StartInfo = psi;
            p.EnableRaisingEvents = true;   // set up before Start, so a window closed at once still raises Exited
            p.Exited += delegate
            {
                p.Dispose();
                if (onExit != null) onExit();
            };
            p.Start();
            return true;
        }

        // Variables a Claude Code session passes to its children. Left in place, the CLI would borrow that session's
        // sign-in or use another profile folder instead of the default one Capsule reads.
        public static bool IsSessionVariable(string name)
        {
            return name.Equals("CLAUDECODE", StringComparison.OrdinalIgnoreCase)
                || name.Equals("CLAUDE_CONFIG_DIR", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("CLAUDE_CODE_", StringComparison.OrdinalIgnoreCase);
        }

        public static void StripClaudeSessionVariables(ProcessStartInfo psi)
        {
            var remove = new List<string>();
            foreach (DictionaryEntry entry in psi.EnvironmentVariables)
            {
                string key = (string)entry.Key;
                if (IsSessionVariable(key)) remove.Add(key);
            }
            foreach (string key in remove) psi.EnvironmentVariables.Remove(key);
        }
    }
}
