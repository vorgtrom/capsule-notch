using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Capsule
{
    public static class ClaudeUsageTests
    {
        const long Exp = 1790000000000;
        const long Min = 60 * 1000;

        public static void Run()
        {
            ReadsTheCredential();
            ParsesNamedWindows();
            ParsesTheLimitsShapeWithoutTwins();
            RejectsGarbage();
            ReadsRetryAfter();
            ErrorReplyCutOffStillReturnsStatus();
            PostsAUtf8BodyWithoutExpectContinue();
            RenewsOnlyNearExpiry();
            RetryWaitsDoubleToAnHour();
            BacksOffOnRateLimits();
            FindsTheStandaloneCli();
            SignInNeededWithoutACredential();
            ExpiredTokenIsNeverSent();
            ReadsUsageWhenSignedIn();
            RateLimitKeepsTheReadingAndWaits();
            RejectedSignInAsksToSignIn();
            StripsSessionVariablesForTheCli();
            EmptyReplyKeepsTheNumbers();
            NoReplyIsMarkedUnreachable();
        }

        static void ReadsTheCredential()
        {
            ClaudeCredential c = ClaudeUsage.ParseCredential(TestRunner.Fixture("claude-credentials.json"));
            TestRunner.Eq("sk-ant-oat01-TEST", c.Token, "token");
            TestRunner.Eq(1790000000000L, c.ExpiresAtMs, "expiry");
            TestRunner.Eq("max", c.Plan, "plan");
            TestRunner.Check(ClaudeUsage.ParseCredential("{\"claudeAiOauth\":{\"accessToken\":\"\"}}") == null, "empty token = signed out");
            TestRunner.Check(ClaudeUsage.ReadCredential(Path.Combine(TestRunner.NewTempDir(), "nope.json")) == null, "missing file");
        }

        static void ParsesNamedWindows()
        {
            var windows = ClaudeUsage.Parse(TestRunner.Fixture("claude-usage.json"));
            TestRunner.Eq(3, windows.Count, "five_hour, seven_day, seven_day_opus (null ones skipped)");
            TestRunner.Eq("Current session", windows[0].Label, "session first");
            TestRunner.Near(73, windows[0].Used, "session used");
            TestRunner.Eq(1790694000000L, windows[0].ResetsAtMs, "session reset");
            TestRunner.Eq("Weekly · all models", windows[1].Label, "weekly");
            TestRunner.Eq("Weekly · Opus", windows[2].Label, "opus");
            TestRunner.Near(12.5, windows[2].Used, "opus used");
        }

        static void ParsesTheLimitsShapeWithoutTwins()
        {
            var windows = ClaudeUsage.Parse(TestRunner.Fixture("claude-usage-limits.json"));
            TestRunner.Eq(3, windows.Count, "session and weekly from the named fields, scoped from limits");
            TestRunner.Eq("five_hour", windows[0].Id, "named session kept");
            TestRunner.Eq("Weekly scoped", windows[2].Label, "limits-only window added");
        }

        static void RejectsGarbage()
        {
            TestRunner.Check(ClaudeUsage.Parse("<html>") == null, "not JSON");
            TestRunner.Eq(0, ClaudeUsage.Parse("{}").Count, "no windows");
        }

        static void ReadsRetryAfter()
        {
            TestRunner.Eq(3587L, Http.ParseRetryAfter("3587"), "seconds");
            TestRunner.Eq(0L, Http.ParseRetryAfter(null), "missing");
            TestRunner.Eq(0L, Http.ParseRetryAfter("soon"), "junk");
        }

        // Behaviour check: a 429 whose body is cut off still reports its status and Retry-After. (.NET buffers error
        // bodies inside GetResponse, so this passes with or without Http.Get's defensive catch around ReadBody.)
        static void ErrorReplyCutOffStillReturnsStatus()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var server = new Thread(delegate()
            {
                try
                {
                    using (TcpClient client = listener.AcceptTcpClient())
                    {
                        NetworkStream stream = client.GetStream();
                        var reader = new StreamReader(stream, Encoding.ASCII);
                        string line;
                        while (!string.IsNullOrEmpty(line = reader.ReadLine())) { }   // read the request headers
                        byte[] reply = Encoding.ASCII.GetBytes("HTTP/1.1 429 Too Many Requests\r\nRetry-After: 30\r\nContent-Type: application/json\r\nContent-Length: 1000\r\n\r\n{\"error\":");
                        stream.Write(reply, 0, reply.Length);
                        stream.Flush();
                    }
                }
                catch (Exception) { }   // e.g. the listener stopped before a client arrived: the checks below report it
            });
            server.IsBackground = true;
            server.Start();
            HttpResult r = null;
            try { r = Http.Get("http://127.0.0.1:" + port + "/", new Dictionary<string, string>(), 5000); }
            catch (Exception e) { TestRunner.Check(false, "Http.Get never throws (threw " + e.GetType().Name + ")"); }
            finally
            {
                server.Join(5000);
                listener.Stop();
            }
            if (r == null) return;
            TestRunner.Eq(429, r.Status, "status kept when the error body is cut off");
            TestRunner.Eq(30L, r.RetryAfterSeconds, "Retry-After kept");
        }

        // The head of the request that has been read so far ends in a blank line.
        static bool HeadIsComplete(List<byte> bytes)
        {
            int n = bytes.Count;
            return n >= 4 && bytes[n - 4] == 13 && bytes[n - 3] == 10 && bytes[n - 2] == 13 && bytes[n - 1] == 10;
        }

        static int ContentLengthOf(string head)
        {
            foreach (string line in head.Split(new[] { "\r\n" }, StringSplitOptions.None))
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) return int.Parse(line.Substring("Content-Length:".Length).Trim());
            }
            return 0;
        }

        // Http.Send's POST, against a one-request server on the loopback (no network): the body arrives as UTF-8, with a
        // Content-Length counted in bytes (a Chinese idea is three bytes a character), under the headers it was given,
        // and without an Expect: 100-continue handshake, which costs a round trip and which HTTPS-intercepting antivirus
        // (Norton, for one) handles badly.
        static void PostsAUtf8BodyWithoutExpectContinue()
        {
            const string Text = "想法：给“菜谱”应用加一个购物清单";
            string sent = "{\"text\":\"" + Text + "\"}";
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            string head = "";
            byte[] body = new byte[0];
            var server = new Thread(delegate()
            {
                try
                {
                    using (TcpClient client = listener.AcceptTcpClient())
                    {
                        NetworkStream stream = client.GetStream();
                        var headBytes = new List<byte>();
                        int next;
                        while (!HeadIsComplete(headBytes) && (next = stream.ReadByte()) >= 0) headBytes.Add((byte)next);
                        head = Encoding.ASCII.GetString(headBytes.ToArray());
                        body = new byte[ContentLengthOf(head)];
                        int got = 0, n;
                        while (got < body.Length && (n = stream.Read(body, got, body.Length - got)) > 0) got += n;
                        byte[] reply = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 11\r\nConnection: close\r\n\r\n{\"ok\":true}");
                        stream.Write(reply, 0, reply.Length);
                        stream.Flush();
                    }
                }
                catch (Exception) { }   // e.g. the listener stopped before a client arrived: the checks below report it
            });
            server.IsBackground = true;
            server.Start();
            HttpResult r = null;
            try { r = Http.Send("POST", "http://127.0.0.1:" + port + "/v1/pages", new Dictionary<string, string> { { "Authorization", "Bearer loopback-test" } }, sent, 5000); }
            catch (Exception e) { TestRunner.Check(false, "Http.Send never throws (threw " + e.GetType().Name + ")"); }
            finally
            {
                server.Join(5000);
                listener.Stop();
            }
            if (r == null) return;
            TestRunner.Check(r.Status == 200 && r.Body == "{\"ok\":true}", "the reply is read (status " + r.Status + ", error " + r.Error + ")");
            TestRunner.Eq(sent, Encoding.UTF8.GetString(body), "a Chinese body arrives intact");
            TestRunner.Eq(Encoding.UTF8.GetByteCount(sent), ContentLengthOf(head), "with its length counted in bytes");
            TestRunner.Check(ContentLengthOf(head) > sent.Length, "which is more than its characters");
            TestRunner.Check(head.StartsWith("POST /v1/pages HTTP/1.1", StringComparison.Ordinal) && head.Contains("Authorization: Bearer loopback-test"), "as a POST with the headers it was given");
            TestRunner.Check(head.Contains("Content-Type: application/json; charset=utf-8"), "declared as UTF-8 JSON when no Content-Type was named");
            TestRunner.Check(head.IndexOf("Expect:", StringComparison.OrdinalIgnoreCase) < 0, "and with no Expect: 100-continue handshake");
        }

        static void RenewsOnlyNearExpiry()
        {
            TestRunner.Check(!ClaudeUsage.ShouldRenew(0, Exp, 0, 0, 0), "no expiry known: never");
            TestRunner.Check(!ClaudeUsage.ShouldRenew(Exp, Exp - 4 * Min - 1, 0, 0, 0), "more than 4 min left");
            TestRunner.Check(ClaudeUsage.ShouldRenew(Exp, Exp - 4 * Min, 0, 0, 0), "4 min left");
            TestRunner.Check(ClaudeUsage.ShouldRenew(Exp, Exp + 60 * Min, 0, 0, 0), "already expired");
            TestRunner.Check(!ClaudeUsage.ShouldRenew(Exp, Exp, Exp, Exp - 5 * Min, 1), "failed 5 min ago: wait");
            TestRunner.Check(ClaudeUsage.ShouldRenew(Exp, Exp, Exp, Exp - 10 * Min, 1), "failed 10 min ago: retry");
            TestRunner.Check(!ClaudeUsage.ShouldRenew(Exp, Exp, Exp, Exp - 10 * Min, 2), "second failure waits 20 min");
            TestRunner.Check(ClaudeUsage.ShouldRenew(Exp + 1, Exp, Exp, Exp - Min, 3), "a different token is tried at once");
        }

        static void RetryWaitsDoubleToAnHour()
        {
            TestRunner.Eq(10 * Min, ClaudeUsage.RetryWaitMs(1), "after 1 failure");
            TestRunner.Eq(20 * Min, ClaudeUsage.RetryWaitMs(2), "after 2");
            TestRunner.Eq(40 * Min, ClaudeUsage.RetryWaitMs(3), "after 3");
            TestRunner.Eq(60 * Min, ClaudeUsage.RetryWaitMs(4), "capped");
            TestRunner.Eq(60 * Min, ClaudeUsage.RetryWaitMs(40), "stays capped");
        }

        static void BacksOffOnRateLimits()
        {
            TestRunner.Eq(60L, ClaudeUsage.BackoffSeconds(1, 0), "first");
            TestRunner.Eq(120L, ClaudeUsage.BackoffSeconds(2, 0), "second");
            TestRunner.Eq(900L, ClaudeUsage.BackoffSeconds(9, 0), "capped");
            TestRunner.Eq(3587L, ClaudeUsage.BackoffSeconds(1, 3587), "a longer Retry-After wins, even past the cap");
        }

        static void FindsTheStandaloneCli()
        {
            string home = TestRunner.NewTempDir();
            string desktop = Path.Combine(TestRunner.NewTempDir(), "AnthropicClaude", "app-1.0");
            Directory.CreateDirectory(desktop);
            File.WriteAllText(Path.Combine(desktop, "claude.exe"), "");
            TestRunner.Check(ClaudeUsage.FindCli(home, desktop) == null, "the desktop app's bundled copy is refused");
            string local = Path.Combine(home, ".local", "bin");
            Directory.CreateDirectory(local);
            File.WriteAllText(Path.Combine(local, "claude.exe"), "");
            TestRunner.Eq(Path.Combine(local, "claude.exe"), ClaudeUsage.FindCli(home, desktop), "the standalone install is found first");
        }

        static string Credential(string token, long expiresAt)
        {
            string path = Path.Combine(TestRunner.NewTempDir(), ".credentials.json");
            File.WriteAllText(path, "{\"claudeAiOauth\":{\"accessToken\":\"" + token + "\",\"expiresAt\":" + expiresAt + ",\"subscriptionType\":\"max\"}}");
            return path;
        }

        static void SignInNeededWithoutACredential()
        {
            var poller = new ClaudeUsage(Path.Combine(TestRunner.NewTempDir(), ".credentials.json"), null);
            Reading r = poller.Poll(Exp);
            TestRunner.Eq("signin", r.Status, "sign in");
            TestRunner.Check(r.LastCheckFailed, "counts as a failed check");
            TestRunner.Eq(0, poller.RequestsSent, "nothing sent");
        }

        static void ExpiredTokenIsNeverSent()
        {
            var poller = new ClaudeUsage(Credential("expired-test-token", 1000), null);
            poller.RenewalDisabled = true;   // tests never launch the real CLI
            Reading r = poller.Poll(Exp);
            TestRunner.Check(r.Note.StartsWith("Sign-in expired", StringComparison.Ordinal), "expired note: " + r.Note);
            TestRunner.Eq(0, poller.RequestsSent, "no request made");
        }

        static void ReadsUsageWhenSignedIn()
        {
            var poller = new ClaudeUsage(Credential("good-test-token", Exp + 60 * Min), null);
            poller.RenewalDisabled = true;
            string sent = null;
            poller.Transport = delegate(string token)
            {
                sent = token;
                return new HttpResult { Status = 200, Body = TestRunner.Fixture("claude-usage.json") };
            };
            Reading r = poller.Poll(Exp);
            TestRunner.Eq("good-test-token", sent, "token sent");
            TestRunner.Eq("ok", r.Status, "ok");
            TestRunner.Eq("Max", r.Plan, "plan");
            TestRunner.Near(73, r.Headline.Used, "the headline is the current session");
            TestRunner.Eq(Exp, r.DataAtMs, "time of the reading");
            TestRunner.Check(!r.LastCheckFailed && r.Note == "", "clean");
            TestRunner.Check(ReferenceEquals(r, poller.Current), "published as Current");
        }

        static void RateLimitKeepsTheReadingAndWaits()
        {
            var poller = new ClaudeUsage(Credential("good-test-token", Exp + 60 * Min), null);
            poller.RenewalDisabled = true;
            poller.Transport = delegate { return new HttpResult { Status = 200, Body = TestRunner.Fixture("claude-usage.json") }; };
            poller.Poll(Exp);
            poller.Transport = delegate { return new HttpResult { Status = 429, RetryAfterSeconds = 3587 }; };
            Reading r = poller.Poll(Exp + Min);
            TestRunner.Eq("ok", r.Status, "last reading kept");
            TestRunner.Near(73, r.Headline.Used, "numbers kept");
            TestRunner.Check(r.LastCheckFailed, "marked as failed");
            TestRunner.Check(r.Note.StartsWith("Usage checks paused for 60 min", StringComparison.Ordinal), "a long pause is explained: " + r.Note);
            TestRunner.Eq(Exp + Min + 3587000, poller.BackoffUntilMs, "waits out Retry-After");
            int sentBefore = poller.RequestsSent;
            poller.Poll(Exp + 2 * Min);
            TestRunner.Eq(sentBefore, poller.RequestsSent, "no request during the wait");
        }

        static void RejectedSignInAsksToSignIn()
        {
            var poller = new ClaudeUsage(Credential("stale-test-token", Exp + 60 * Min), null);
            poller.RenewalDisabled = true;
            poller.Transport = delegate { return new HttpResult { Status = 401 }; };
            Reading r = poller.Poll(Exp);
            TestRunner.Eq("signin", r.Status, "sign in");
            TestRunner.Eq(1, poller.RequestsSent, "the same token is not retried");
        }

        static void EmptyReplyKeepsTheNumbers()
        {
            var poller = new ClaudeUsage(Credential("good-test-token", Exp + 60 * Min), null);
            poller.RenewalDisabled = true;
            poller.Transport = delegate { return new HttpResult { Status = 200, Body = TestRunner.Fixture("claude-usage.json") }; };
            poller.Poll(Exp);
            poller.Transport = delegate { return new HttpResult { Status = 200, Body = "{}" }; };
            Reading r = poller.Poll(Exp + Min);
            TestRunner.Check(r.Headline != null && Math.Abs(r.Headline.Used - 73) < 1e-9, "an empty reply keeps the last numbers");
            TestRunner.Check(r.LastCheckFailed && r.Note == "Unexpected reply from Anthropic", "and says why: " + r.Note);
        }

        static void NoReplyIsMarkedUnreachable()
        {
            var poller = new ClaudeUsage(Credential("good-test-token", Exp + 60 * Min), null);
            poller.RenewalDisabled = true;
            poller.Transport = delegate { return new HttpResult { Status = 0, Error = "NameResolutionFailure" }; };
            poller.Poll(Exp);
            TestRunner.Check(poller.LastCheckUnreachable, "no reply at all: unreachable");
            poller.Transport = delegate { return new HttpResult { Status = 503 }; };
            poller.Poll(Exp + Min);
            TestRunner.Check(!poller.LastCheckUnreachable, "a server error is still a reply");
        }

        static void StripsSessionVariablesForTheCli()
        {
            TestRunner.Check(ClaudeUsage.IsSessionVariable("CLAUDECODE"), "CLAUDECODE");
            TestRunner.Check(ClaudeUsage.IsSessionVariable("claude_code_entrypoint"), "CLAUDE_CODE_* in any case");
            TestRunner.Check(ClaudeUsage.IsSessionVariable("CLAUDE_CONFIG_DIR"), "CLAUDE_CONFIG_DIR, so the CLI uses the default profile folder");
            TestRunner.Check(!ClaudeUsage.IsSessionVariable("PATH") && !ClaudeUsage.IsSessionVariable("CLAUDE"), "other variables kept");
            var psi = new ProcessStartInfo("unused.exe");
            psi.EnvironmentVariables["CLAUDE_CONFIG_DIR"] = "x";
            psi.EnvironmentVariables["CLAUDECODE"] = "1";
            psi.EnvironmentVariables["CLAUDE_CODE_SOMETHING"] = "1";
            psi.EnvironmentVariables["CLAUDECODEX"] = "keep";
            psi.EnvironmentVariables["CLAUDE_CONFIG_DIRS"] = "keep";
            ClaudeUsage.StripClaudeSessionVariables(psi);
            TestRunner.Check(!psi.EnvironmentVariables.ContainsKey("CLAUDE_CONFIG_DIR") && !psi.EnvironmentVariables.ContainsKey("CLAUDECODE") && !psi.EnvironmentVariables.ContainsKey("CLAUDE_CODE_SOMETHING"), "session variables removed from a real start info");
            TestRunner.Check(psi.EnvironmentVariables.ContainsKey("CLAUDECODEX") && psi.EnvironmentVariables.ContainsKey("CLAUDE_CONFIG_DIRS") && psi.EnvironmentVariables.ContainsKey("PATH"), "near-miss names and PATH are kept");
        }
    }
}
