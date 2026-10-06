using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Capsule
{
    // A request the loopback fake received.
    public sealed class FakeHttpRequest
    {
        public string Method = "";
        public string Path = "";
        public string Body = "";
        public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    // A stand-in for Google's endpoints on 127.0.0.1: one request per connection, answered by path from the replies set for
    // it (the last one keeps answering once the earlier ones are used up), 404 otherwise. Keeps every request.
    public sealed class FakeHttp : IDisposable
    {
        readonly TcpListener listener;
        readonly Dictionary<string, Queue<HttpResult>> replies = new Dictionary<string, Queue<HttpResult>>();
        public readonly List<FakeHttpRequest> Requests = new List<FakeHttpRequest>();

        public FakeHttp()
        {
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var thread = new Thread(Loop);
            thread.IsBackground = true;
            thread.Start();
        }

        public int Port { get { return ((IPEndPoint)listener.LocalEndpoint).Port; } }
        public string Url(string path) { return "http://127.0.0.1:" + Port + path; }

        public FakeHttp Reply(string path, int status, string body)
        {
            lock (replies)
            {
                if (!replies.ContainsKey(path)) replies[path] = new Queue<HttpResult>();
                replies[path].Enqueue(new HttpResult { Status = status, Body = body ?? "" });
            }
            return this;
        }

        public FakeHttpRequest Last
        {
            get { lock (Requests) return Requests.Count > 0 ? Requests[Requests.Count - 1] : null; }
        }

        // A port nothing listens on: a request there gets no reply at all.
        public static int ClosedPort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        void Loop()
        {
            while (true)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); }
                catch (Exception) { return; }   // stopped
                using (client)
                {
                    try { Handle(client.GetStream()); }
                    catch (Exception) { }
                }
            }
        }

        void Handle(NetworkStream stream)
        {
            stream.ReadTimeout = 5000;
            var head = new List<byte>();
            while (head.Count < 4 || !(head[head.Count - 4] == 13 && head[head.Count - 3] == 10 && head[head.Count - 2] == 13 && head[head.Count - 1] == 10))
            {
                int b = stream.ReadByte();
                if (b < 0) return;
                head.Add((byte)b);
            }
            string[] lines = Encoding.ASCII.GetString(head.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] first = lines[0].Split(' ');
            var request = new FakeHttpRequest { Method = first[0], Path = first.Length > 1 ? first[1] : "" };
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon > 0) request.Headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
            }
            string lengthText;
            int length = request.Headers.TryGetValue("Content-Length", out lengthText) ? int.Parse(lengthText) : 0;
            var body = new byte[length];
            int read = 0;
            while (read < length)
            {
                int n = stream.Read(body, read, length - read);
                if (n <= 0) break;
                read += n;
            }
            request.Body = Encoding.UTF8.GetString(body, 0, read);
            lock (Requests) Requests.Add(request);
            HttpResult reply = new HttpResult { Status = 404, Body = "" };
            int q = request.Path.IndexOf('?');
            string path = q < 0 ? request.Path : request.Path.Substring(0, q);
            lock (replies)
            {
                Queue<HttpResult> queue;
                if (replies.TryGetValue(path, out queue) && queue.Count > 0) reply = queue.Count > 1 ? queue.Dequeue() : queue.Peek();
            }
            byte[] content = Encoding.UTF8.GetBytes(reply.Body);
            byte[] answer = Encoding.ASCII.GetBytes("HTTP/1.1 " + reply.Status + " Fake\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: " + content.Length + "\r\nConnection: close\r\n\r\n");
            stream.Write(answer, 0, answer.Length);
            stream.Write(content, 0, content.Length);
            stream.Flush();
        }

        public void Dispose()
        {
            try { listener.Stop(); }
            catch (Exception) { }
        }
    }

    public static class GoogleAuthTests
    {
        const long Now = 1790690940000;
        const string Tokens = "{\"access_token\": \"ya29.sample\", \"expires_in\": 3599, \"refresh_token\": \"1//sample\", \"scope\": \"x\", \"token_type\": \"Bearer\"}";

        public static void Run()
        {
            PkceFollowsTheRfc();
            AuthUrlCarriesEverything();
            FormsAndQueriesAreEscaped();
            OnlySafeErrorCodesAreKept();
            CallbackListensOnLoopback();
            CallbackTakesOnlyItsOwnState();
            CallbackCarriesGooglesError();
            CallbackGivesUp();
            ExchangeSendsTheDocumentedForm();
            RefreshAndItsFailures();
            RevokeSendsTheToken();
            SignInRunsTheWholeFlow();
            SignInEndsCleanly();
            ReadsAClientFile();
        }

        // The JSON Google Cloud downloads for an OAuth client fills in both fields when pasted whole.
        static void ReadsAClientFile()
        {
            string id, secret;
            const string Desktop = "{\"installed\":{\"client_id\":\"123-abc.apps.googleusercontent.com\",\"project_id\":\"capsule\",\"auth_uri\":\"https://accounts.google.com/o/oauth2/auth\",\"token_uri\":\"https://oauth2.googleapis.com/token\",\"client_secret\":\"GOCSPX-sample\",\"redirect_uris\":[\"http://localhost\"]}}";
            TestRunner.Check(GoogleClientFile.Parse("  " + Desktop + "\r\n", out id, out secret) && id == "123-abc.apps.googleusercontent.com" && secret == "GOCSPX-sample", "a Desktop app's file gives its ID and secret");
            TestRunner.Check(GoogleClientFile.Parse("{\"web\":{\"client_id\":\"w\",\"client_secret\":\"s\"}}", out id, out secret) && id == "w" && secret == "s", "a web client's file too");
            TestRunner.Check(!GoogleClientFile.Parse("123-abc.apps.googleusercontent.com", out id, out secret) && id == null && secret == null, "a plain client ID isn't a file");
            TestRunner.Check(!GoogleClientFile.Parse("{\"installed\":{\"client_id\":\"x\"}}", out id, out secret) && !GoogleClientFile.Parse("{not json", out id, out secret), "a file without a secret, or broken JSON, isn't one");
        }

        // RFC 7636, appendix B: this verifier has exactly this S256 challenge.
        static void PkceFollowsTheRfc()
        {
            TestRunner.Eq("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", Pkce.ChallengeFor("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"), "the RFC's sample challenge");
            Pkce p = Pkce.Make();
            TestRunner.Check(p.Verifier.Length == 43 && UrlSafe(p.Verifier), "a made verifier is 43 URL-safe characters: " + p.Verifier.Length);
            TestRunner.Eq(Pkce.ChallengeFor(p.Verifier), p.Challenge, "and its challenge is its S256");
            TestRunner.Check(Pkce.Make().Verifier != p.Verifier, "each sign-in gets a new verifier");
        }

        static bool UrlSafe(string s)
        {
            foreach (char c in s)
                if (!(c >= 'A' && c <= 'Z') && !(c >= 'a' && c <= 'z') && !(c >= '0' && c <= '9') && c != '-' && c != '_') return false;
            return true;
        }

        static void AuthUrlCarriesEverything()
        {
            string url = GoogleOAuth.AuthUrl("123-abc.apps.googleusercontent.com", "http://127.0.0.1:5555/", "challenge-sample", "state-sample");
            TestRunner.Check(url.StartsWith("https://accounts.google.com/o/oauth2/v2/auth?"), "Google's consent page");
            Dictionary<string, string> q = GoogleOAuth.ParseQuery(url);
            TestRunner.Eq("123-abc.apps.googleusercontent.com", Get(q, "client_id"), "the client id");
            TestRunner.Eq("http://127.0.0.1:5555/", Get(q, "redirect_uri"), "the loopback redirect");
            TestRunner.Eq("code", Get(q, "response_type"), "a code comes back");
            TestRunner.Eq(GoogleOAuth.Scopes, Get(q, "scope"), "the read-only scopes");
            TestRunner.Eq("challenge-sample", Get(q, "code_challenge"), "the PKCE challenge");
            TestRunner.Eq("S256", Get(q, "code_challenge_method"), "hashed with SHA-256");
            TestRunner.Eq("state-sample", Get(q, "state"), "the state");
            TestRunner.Eq("offline", Get(q, "access_type"), "a refresh token is asked for");
            TestRunner.Eq("consent", Get(q, "prompt"), "every time");
            TestRunner.Check(url.Contains("redirect_uri=http%3A%2F%2F127.0.0.1%3A5555%2F"), "the redirect is escaped");
            TestRunner.Check(url.Contains("scope=https%3A%2F%2Fwww.googleapis.com%2Fauth%2Fcalendar.events.readonly%20https%3A%2F%2Fwww.googleapis.com%2Fauth%2Fcalendar.calendarlist.readonly&"), "the two scopes, read-only, space-separated and escaped");
        }

        static string Get(Dictionary<string, string> q, string name)
        {
            string value;
            return q.TryGetValue(name, out value) ? value : null;
        }

        static void FormsAndQueriesAreEscaped()
        {
            var fields = new List<KeyValuePair<string, string>> { GoogleOAuth.Field("a", "1+2/3="), GoogleOAuth.Field("b", "x y"), GoogleOAuth.Field("c", "é") };
            TestRunner.Eq("a=1%2B2%2F3%3D&b=x%20y&c=%C3%A9", GoogleOAuth.Form(fields), "a form body escapes +, /, =, spaces and non-ASCII");
            Dictionary<string, string> q = GoogleOAuth.ParseQuery("/?a=1%2B2&b=x+y&a=second");
            TestRunner.Eq("1+2", Get(q, "a"), "a query is unescaped, and a name given twice keeps its first value");
            TestRunner.Eq("x y", Get(q, "b"), "+ is a space");
            TestRunner.Eq(0, GoogleOAuth.ParseQuery("/").Count, "no query, no values");
        }

        static void OnlySafeErrorCodesAreKept()
        {
            TestRunner.Eq("access_denied", GoogleOAuth.SafeCode("access_denied"), "a real code is kept");
            TestRunner.Eq("", GoogleOAuth.SafeCode("<script>alert(1)</script>"), "anything else is dropped");
            TestRunner.Eq("", GoogleOAuth.SafeCode(new string('a', 65)), "and so is a long one");
            TestRunner.Eq("invalid_grant", GoogleOAuth.OAuthError("{\"error\": \"invalid_grant\", \"error_description\": \"Bad Request\"}"), "the code from an error reply");
            TestRunner.Eq("", GoogleOAuth.OAuthError("<html>not json</html>"), "and none from a reply that isn't JSON");
        }

        // A GET as a browser would send it; the reply's status line, or "" when nothing answered.
        static string RawGet(int port, string target)
        {
            try
            {
                using (var client = new TcpClient("127.0.0.1", port))
                {
                    client.ReceiveTimeout = 3000;
                    NetworkStream stream = client.GetStream();
                    byte[] request = Encoding.ASCII.GetBytes("GET " + target + " HTTP/1.1\r\nHost: 127.0.0.1:" + port + "\r\nConnection: close\r\n\r\n");
                    stream.Write(request, 0, request.Length);
                    string reply = new StreamReader(stream, Encoding.ASCII).ReadToEnd();
                    int end = reply.IndexOf("\r\n", StringComparison.Ordinal);
                    return end < 0 ? reply : reply.Substring(0, end);
                }
            }
            catch (Exception) { return ""; }
        }

        static bool Answers(int port)
        {
            try
            {
                using (new TcpClient("127.0.0.1", port)) return true;
            }
            catch (SocketException) { return false; }
        }

        static void CallbackListensOnLoopback()
        {
            using (var callback = new LoopbackCallback())
            {
                TestRunner.Check(callback.Port > 0 && callback.RedirectUri == "http://127.0.0.1:" + callback.Port + "/", "the redirect is 127.0.0.1 at the listener's own port: " + callback.RedirectUri);
            }
        }

        // Only the callback with this sign-in's state is taken: anything else is answered and ignored, and the listener
        // closes once the right one came.
        static void CallbackTakesOnlyItsOwnState()
        {
            var callback = new LoopbackCallback();
            int port = callback.Port;
            Task<CallbackResult> wait = Task.Run(() => callback.Wait("right-state", 10000));
            TestRunner.Eq("HTTP/1.1 400 Bad Request", RawGet(port, "/?code=4%2Fforged&state=wrong-state"), "a callback with another state is refused");
            TestRunner.Eq("HTTP/1.1 404 Not Found", RawGet(port, "/favicon.ico"), "so is anything that isn't the callback");
            TestRunner.Check(!wait.IsCompleted, "and the sign-in goes on waiting");
            TestRunner.Eq("HTTP/1.1 200 OK", RawGet(port, "/?state=right-state&code=4%2Fsample-code&scope=x"), "the right one gets the closing page");
            TestRunner.Check(wait.Wait(3000) && wait.Result.Code == "4/sample-code" && wait.Result.Error == null, "and its code, unescaped");
            TestRunner.Check(!Answers(port), "the listener is closed afterwards");
        }

        static void CallbackCarriesGooglesError()
        {
            var callback = new LoopbackCallback();
            Task<CallbackResult> wait = Task.Run(() => callback.Wait("s", 10000));
            TestRunner.Eq("HTTP/1.1 200 OK", RawGet(callback.Port, "/?error=access_denied&state=s"), "a refusal gets a page too");
            TestRunner.Check(wait.Wait(3000) && wait.Result.Error == "access_denied" && wait.Result.Code == null, "and Google's error code comes back");
            var odd = new LoopbackCallback();
            Task<CallbackResult> oddWait = Task.Run(() => odd.Wait("s", 10000));
            RawGet(odd.Port, "/?error=%3Cb%3Ehi%3C%2Fb%3E&state=s");
            TestRunner.Check(oddWait.Wait(3000) && oddWait.Result.Error == "unknown", "an error code that can't be shown is \"unknown\"");
        }

        static void CallbackGivesUp()
        {
            var callback = new LoopbackCallback();
            CallbackResult late = callback.Wait("s", 200);
            TestRunner.Check(late.TimedOut && late.Code == null && !Answers(callback.Port), "nothing in time: a timeout, and the listener is closed");
            var cancelled = new LoopbackCallback();
            Task<CallbackResult> wait = Task.Run(() => cancelled.Wait("s", 60000));
            Thread.Sleep(100);
            cancelled.Cancel();
            TestRunner.Check(wait.Wait(2000) && wait.Result.Cancelled, "Cancel ends the wait at once");
        }

        static GoogleAuth AuthAt(FakeHttp fake)
        {
            var auth = new GoogleAuth("cid-sample", "csecret-sample");
            auth.TokenUrl = fake.Url("/token");
            auth.RevokeUrl = fake.Url("/revoke");
            return auth;
        }

        static Dictionary<string, string> FormOf(FakeHttpRequest r) { return GoogleOAuth.ParseQuery("?" + (r != null ? r.Body : "")); }

        static void ExchangeSendsTheDocumentedForm()
        {
            using (var fake = new FakeHttp().Reply("/token", 200, Tokens))
            {
                TokenResult t = AuthAt(fake).Exchange("4/code-sample", "verifier-sample", "http://127.0.0.1:5555/", Now);
                TestRunner.Check(t.Ok && t.AccessToken == "ya29.sample" && t.RefreshToken == "1//sample", "the exchange gives an access token and a refresh token");
                TestRunner.Eq(Now + 3599000L, t.ExpiresAtMs, "and when the access token expires");
                FakeHttpRequest r = fake.Last;
                TestRunner.Check(r != null && r.Method == "POST" && r.Path == "/token", "a POST to the token endpoint");
                TestRunner.Eq("application/x-www-form-urlencoded", r != null && r.Headers.ContainsKey("Content-Type") ? r.Headers["Content-Type"] : null, "as a form");
                Dictionary<string, string> form = FormOf(r);
                TestRunner.Eq("4/code-sample", Get(form, "code"), "with the code");
                TestRunner.Eq("cid-sample", Get(form, "client_id"), "the client id");
                TestRunner.Eq("csecret-sample", Get(form, "client_secret"), "the client secret");
                TestRunner.Eq("http://127.0.0.1:5555/", Get(form, "redirect_uri"), "the same redirect");
                TestRunner.Eq("authorization_code", Get(form, "grant_type"), "the grant type");
                TestRunner.Eq("verifier-sample", Get(form, "code_verifier"), "and the PKCE verifier");
            }
        }

        static void RefreshAndItsFailures()
        {
            using (var fake = new FakeHttp())
            {
                fake.Reply("/token", 200, "{\"access_token\": \"ya29.new\", \"expires_in\": 3600, \"token_type\": \"Bearer\"}")
                    .Reply("/token", 400, "{\"error\": \"invalid_grant\", \"error_description\": \"Token has been expired or revoked.\"}")
                    .Reply("/token", 401, "{\"error\": \"invalid_client\", \"error_description\": \"Unauthorized\"}")
                    .Reply("/token", 200, "{}");
                GoogleAuth auth = AuthAt(fake);
                TokenResult t = auth.Refresh("1//sample", Now);
                TestRunner.Check(t.Ok && t.AccessToken == "ya29.new" && t.RefreshToken == null && t.ExpiresAtMs == Now + 3600000L, "a refresh gives a new access token");
                Dictionary<string, string> form = FormOf(fake.Last);
                TestRunner.Check(Get(form, "grant_type") == "refresh_token" && Get(form, "refresh_token") == "1//sample" && Get(form, "client_id") == "cid-sample" && Get(form, "client_secret") == "csecret-sample", "sent as a refresh_token grant with the client");
                TokenResult rejected = auth.Refresh("1//sample", Now);
                TestRunner.Check(rejected.Rejected && !rejected.Ok && rejected.Status == 400, "invalid_grant: the refresh token is no longer good");
                TestRunner.Eq("HTTP 400 invalid_grant", GoogleAuth.Describe(rejected), "the log gets the status and the code");
                TokenResult client = auth.Refresh("1//sample", Now);
                TestRunner.Check(!client.Rejected && GoogleAuth.Problem(client) == "Google didn't accept the client ID or secret", "a bad client isn't a rejected token, and says so");
                TokenResult odd = auth.Refresh("1//sample", Now);
                TestRunner.Check(!odd.Ok && odd.Error == "unexpected_reply" && !odd.Rejected, "a 200 without a token is no token");
            }
            var away = new GoogleAuth("cid", "secret");
            away.TokenUrl = "http://127.0.0.1:" + FakeHttp.ClosedPort() + "/token";
            TokenResult none = away.Refresh("1//sample", Now);
            TestRunner.Check(none.Status == 0 && !none.Ok && !none.Rejected && none.NoReply != "", "no reply at all: status 0, and the token isn't judged");
            TestRunner.Check(GoogleAuth.Problem(none).StartsWith("Couldn't reach Google"), "and the settings say Google couldn't be reached");
        }

        static void RevokeSendsTheToken()
        {
            using (var fake = new FakeHttp().Reply("/revoke", 200, "").Reply("/revoke", 400, "{\"error\": \"invalid_token\"}"))
            {
                GoogleAuth auth = AuthAt(fake);
                TestRunner.Check(auth.Revoke("1//sample"), "Google confirms the revoke");
                FakeHttpRequest r = fake.Last;
                TestRunner.Check(r != null && r.Path == "/revoke" && Get(FormOf(r), "token") == "1//sample", "the token is posted to the revoke endpoint");
                TestRunner.Check(!auth.Revoke("1//sample"), "a refused revoke is false");
            }
        }

        // The browser stand-in follows the consent page's redirect itself, as Google would after consent.
        static void SignInRunsTheWholeFlow()
        {
            using (var fake = new FakeHttp().Reply("/token", 200, Tokens))
            {
                GoogleAuth auth = AuthAt(fake);
                string opened = null;
                auth.OpenBrowser = delegate(string url)
                {
                    opened = url;
                    Dictionary<string, string> q = GoogleOAuth.ParseQuery(url);
                    string back = Get(q, "redirect_uri") + "?state=" + Uri.EscapeDataString(Get(q, "state")) + "&code=4%2Fflow-code";
                    Task.Run(() => Http.Get(back, new Dictionary<string, string>(), 5000));
                };
                SignInResult r = auth.SignIn(10000, Now);
                TestRunner.Check(r.Ok && r.RefreshToken == "1//sample" && r.AccessToken == "ya29.sample" && r.Problem == "", "a whole sign-in gives the tokens");
                TestRunner.Eq("signed in", r.LogText, "and the log says only that");
                Dictionary<string, string> form = FormOf(fake.Last);
                Dictionary<string, string> asked = GoogleOAuth.ParseQuery(opened ?? "");
                TestRunner.Check(Get(form, "code") == "4/flow-code" && Get(form, "redirect_uri") == Get(asked, "redirect_uri"), "the code is exchanged with the redirect it came back to");
                string verifier = Get(form, "code_verifier") ?? "";
                TestRunner.Check(verifier.Length == 43 && Pkce.ChallengeFor(verifier) == Get(asked, "code_challenge"), "with the verifier whose challenge the consent page got");
            }
        }

        static void SignInEndsCleanly()
        {
            var auth = new GoogleAuth("cid", "secret");
            auth.TokenUrl = "http://127.0.0.1:" + FakeHttp.ClosedPort() + "/token";
            auth.OpenBrowser = delegate(string url)
            {
                Dictionary<string, string> q = GoogleOAuth.ParseQuery(url);
                Task.Run(() => Http.Get(Get(q, "redirect_uri") + "?error=access_denied&state=" + Uri.EscapeDataString(Get(q, "state")), new Dictionary<string, string>(), 5000));
            };
            SignInResult refused = auth.SignIn(10000, Now);
            TestRunner.Check(!refused.Ok && refused.Problem == "Sign-in was cancelled in the browser" && refused.LogText == "Google said access_denied", "a refusal in the browser says so");
            auth.OpenBrowser = delegate(string url) { throw new InvalidOperationException("no browser"); };
            SignInResult noBrowser = auth.SignIn(10000, Now);
            TestRunner.Check(!noBrowser.Ok && noBrowser.Problem == "Couldn't open the browser" && noBrowser.LogText.Contains("InvalidOperationException"), "no browser: said so, the exception named by its type");
            auth.OpenBrowser = delegate(string url) { };
            SignInResult late = auth.SignIn(200, Now);
            TestRunner.Check(!late.Ok && late.Problem == "Sign-in wasn't finished in time. Try again.", "nothing came back in time");
            Task<SignInResult> waiting = Task.Run(() => auth.SignIn(60000, Now));
            for (int i = 0; i < 30 && !waiting.IsCompleted; i++)   // until the sign-in is listening, Cancel has nothing to stop
            {
                Thread.Sleep(100);
                auth.Cancel();
            }
            TestRunner.Check(waiting.Wait(2000) && !waiting.Result.Ok && waiting.Result.Problem == "" && waiting.Result.LogText == "cancelled", "Cancel ends a sign-in quietly");
        }
    }
}
