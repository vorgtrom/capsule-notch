using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Capsule
{
    // A PKCE pair (RFC 7636) for one sign-in (calendar spec §3): the verifier stays in memory until the code is exchanged;
    // only the challenge goes into the consent URL. Never logged.
    public sealed class Pkce
    {
        public string Verifier = "";
        public string Challenge = "";

        // 32 random bytes: a verifier of 43 URL-safe characters.
        public static Pkce Make()
        {
            string verifier = GoogleOAuth.RandomUrlSafe(32);
            return new Pkce { Verifier = verifier, Challenge = ChallengeFor(verifier) };
        }

        // S256: the verifier's SHA-256, in base64url without padding.
        public static string ChallengeFor(string verifier)
        {
            using (SHA256 sha = SHA256.Create()) return GoogleOAuth.Base64Url(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
        }
    }

    // Google's OAuth 2.0 for installed apps (calendar spec §3): the endpoints, the read-only scopes, the consent URL, and
    // the small encoding helpers the sign-in needs. The browser opens accounts.google.com; Capsule itself only talks to
    // oauth2.googleapis.com (GoogleAuth).
    public static class GoogleOAuth
    {
        public const string AuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
        public const string TokenEndpoint = "https://oauth2.googleapis.com/token";
        public const string RevokeEndpoint = "https://oauth2.googleapis.com/revoke";
        // Read-only and as narrow as Google offers: the events, and the list of calendars.
        public const string Scopes = "https://www.googleapis.com/auth/calendar.events.readonly https://www.googleapis.com/auth/calendar.calendarlist.readonly";

        // The consent page the browser opens. access_type=offline and prompt=consent make Google hand out a refresh token
        // every time, also to an account that signed in before.
        public static string AuthUrl(string clientId, string redirectUri, string challenge, string state)
        {
            var fields = new List<KeyValuePair<string, string>>
            {
                Field("client_id", clientId),
                Field("redirect_uri", redirectUri),
                Field("response_type", "code"),
                Field("scope", Scopes),
                Field("code_challenge", challenge),
                Field("code_challenge_method", "S256"),
                Field("state", state),
                Field("access_type", "offline"),
                Field("prompt", "consent"),
            };
            return AuthEndpoint + "?" + Form(fields);
        }

        public static KeyValuePair<string, string> Field(string name, string value) { return new KeyValuePair<string, string>(name, value ?? ""); }

        // name=value pairs joined with &, each side escaped as RFC 3986 asks: fit for a query string and for an
        // application/x-www-form-urlencoded body alike.
        public static string Form(IEnumerable<KeyValuePair<string, string>> fields)
        {
            var sb = new StringBuilder();
            foreach (KeyValuePair<string, string> f in fields)
            {
                if (sb.Length > 0) sb.Append('&');
                sb.Append(Uri.EscapeDataString(f.Key)).Append('=').Append(Uri.EscapeDataString(f.Value ?? ""));
            }
            return sb.ToString();
        }

        // The query of a request target or URL ("/?code=…&state=…") as names and values, unescaped ('+' is a space).
        // A name given twice keeps its first value. Empty when there is no query.
        public static Dictionary<string, string> ParseQuery(string target)
        {
            var result = new Dictionary<string, string>();
            int q = (target ?? "").IndexOf('?');
            if (q < 0) return result;
            string query = target.Substring(q + 1);
            int hash = query.IndexOf('#');
            if (hash >= 0) query = query.Substring(0, hash);
            foreach (string pair in query.Split('&'))
            {
                if (pair == "") continue;
                int eq = pair.IndexOf('=');
                string name = Unescape(eq < 0 ? pair : pair.Substring(0, eq));
                string value = eq < 0 ? "" : Unescape(pair.Substring(eq + 1));
                if (name != null && value != null && !result.ContainsKey(name)) result[name] = value;
            }
            return result;
        }

        static string Unescape(string s)
        {
            try { return Uri.UnescapeDataString(s.Replace('+', ' ')); }
            catch (Exception) { return null; }
        }

        // n random bytes from the system's cryptographic generator, in base64url without padding.
        public static string RandomUrlSafe(int bytes)
        {
            var data = new byte[bytes];
            using (var random = new RNGCryptoServiceProvider()) random.GetBytes(data);
            return Base64Url(data);
        }

        public static string Base64Url(byte[] data)
        {
            return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        // An OAuth error code ("access_denied", "invalid_grant") as it may be shown and logged: only lower-case letters
        // and underscores, at most 64 of them. Anything else is "".
        public static string SafeCode(string code)
        {
            if (string.IsNullOrEmpty(code) || code.Length > 64) return "";
            foreach (char c in code) if ((c < 'a' || c > 'z') && c != '_') return "";
            return code;
        }

        // The error code in an OAuth error reply's body ({"error": "invalid_grant", ...}), or "".
        public static string OAuthError(string body)
        {
            return SafeCode(Json.Str(Json.Get(Json.TryParse(body), "error")));
        }
    }

    // What came back to the loopback address.
    public sealed class CallbackResult
    {
        public string Code;     // the authorization code, when Google sent one
        public string Error;    // Google's error code ("access_denied"), when it sent one instead; "unknown" if it can't be shown
        public bool TimedOut;   // nothing came back in time
        public bool Cancelled;  // Cancel, or the listener was stopped
    }

    // Where the browser comes back after consent (calendar spec §3): http://127.0.0.1:<free port>/, on a listener bound to
    // the loopback address only, so nothing on another machine can reach it. Requests are taken one at a time. One that
    // isn't the callback with this sign-in's state (another page, a favicon, a stale or forged callback) is answered with
    // an error page and otherwise ignored; the first that is gets "You can close this tab", and the listener closes.
    // A TcpListener rather than HttpListener: http.sys wants a URL reservation, or admin rights, for a 127.0.0.1 prefix.
    public sealed class LoopbackCallback : IDisposable
    {
        const int MaxHeadBytes = 16 * 1024, ReadTimeoutMs = 3000, PollMs = 50;
        const string DonePage = "Capsule is signed in to Google Calendar. You can close this tab.";
        const string FailedPage = "Capsule wasn't signed in. You can close this tab and try again from Capsule's settings.";
        const string NotOursPage = "This isn't the sign-in Capsule is waiting for.";

        readonly TcpListener listener;
        readonly object gate = new object();
        volatile bool cancelled;
        bool stopped;

        public LoopbackCallback()
        {
            listener = new TcpListener(IPAddress.Loopback, 0);   // port 0: Windows picks a free one
            // No other program may bind the same address and port while Capsule listens. Not every platform has the option;
            // the listener is still loopback-only without it.
            try { listener.ExclusiveAddressUse = true; }
            catch (Exception) { }
            listener.Start();
        }

        public int Port { get { return ((IPEndPoint)listener.LocalEndpoint).Port; } }
        public string RedirectUri { get { return "http://127.0.0.1:" + Port + "/"; } }

        // Ends a Wait on another thread within PollMs.
        public void Cancel() { cancelled = true; }

        // Blocks until the callback with this state arrives, timeoutMs pass, or Cancel. The listener is closed afterwards.
        public CallbackResult Wait(string state, int timeoutMs)
        {
            Stopwatch clock = Stopwatch.StartNew();
            try
            {
                while (true)
                {
                    if (cancelled) return new CallbackResult { Cancelled = true };
                    if (clock.ElapsedMilliseconds >= timeoutMs) return new CallbackResult { TimedOut = true };
                    bool pending;
                    try { pending = listener.Pending(); }
                    catch (Exception) { return new CallbackResult { Cancelled = true }; }   // stopped meanwhile
                    if (!pending)
                    {
                        Thread.Sleep(PollMs);
                        continue;
                    }
                    CallbackResult result = Serve(state);
                    if (result != null) return result;
                }
            }
            finally { Dispose(); }
        }

        // One request: the result when it was the callback, otherwise null. Nothing here throws, and nothing from the
        // request is kept or logged but the code or error it carries.
        CallbackResult Serve(string state)
        {
            TcpClient client;
            try { client = listener.AcceptTcpClient(); }
            catch (Exception) { return null; }
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = ReadTimeoutMs;
                    client.SendTimeout = ReadTimeoutMs;
                    NetworkStream stream = client.GetStream();
                    string target = ReadTarget(stream);
                    if (target == null)
                    {
                        Respond(stream, 400, NotOursPage);
                        return null;
                    }
                    int q = target.IndexOf('?');
                    string path = q < 0 ? target : target.Substring(0, q);
                    Dictionary<string, string> query = GoogleOAuth.ParseQuery(target);
                    string got, code, error;
                    bool ours = path == "/" && query.TryGetValue("state", out got) && got == state;
                    query.TryGetValue("code", out code);
                    query.TryGetValue("error", out error);
                    if (!ours || (string.IsNullOrEmpty(code) && error == null))
                    {
                        Respond(stream, path == "/" ? 400 : 404, NotOursPage);
                        return null;
                    }
                    if (error != null)
                    {
                        Respond(stream, 200, FailedPage);
                        string safe = GoogleOAuth.SafeCode(error);
                        return new CallbackResult { Error = safe != "" ? safe : "unknown" };
                    }
                    Respond(stream, 200, DonePage);
                    return new CallbackResult { Code = code };
                }
                catch (Exception) { return null; }   // a connection that broke or said nothing in time
            }
        }

        // The request target of a GET ("/?code=…"), or null for anything else. Reads the head only.
        static string ReadTarget(Stream stream)
        {
            var head = new MemoryStream();
            var buffer = new byte[2048];
            while (head.Length < MaxHeadBytes)
            {
                int n = stream.Read(buffer, 0, buffer.Length);
                if (n <= 0) break;
                head.Write(buffer, 0, n);
                string sofar = Encoding.ASCII.GetString(head.GetBuffer(), 0, (int)head.Length);
                if (sofar.Contains("\r\n\r\n") || sofar.Contains("\n\n")) break;
            }
            string text = Encoding.ASCII.GetString(head.ToArray());
            int end = text.IndexOf('\n');
            if (end < 0) return null;
            string[] parts = text.Substring(0, end).TrimEnd('\r').Split(' ');
            if (parts.Length != 3 || parts[0] != "GET" || !parts[1].StartsWith("/", StringComparison.Ordinal)) return null;
            return parts[1];
        }

        // A short fixed page: nothing from the request is echoed back into it.
        static void Respond(Stream stream, int status, string message)
        {
            byte[] body = Files.Utf8.GetBytes("<!doctype html><html><head><meta charset=\"utf-8\"><title>Capsule</title></head>"
                + "<body style=\"font-family: 'Segoe UI', sans-serif; margin: 3em\"><p>" + message + "</p></body></html>");
            string reason = status == 200 ? "OK" : status == 404 ? "Not Found" : "Bad Request";
            byte[] head = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + " " + reason + "\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: "
                + body.Length + "\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
            stream.Write(head, 0, head.Length);
            stream.Write(body, 0, body.Length);
            stream.Flush();
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (stopped) return;
                stopped = true;
                try { listener.Stop(); }
                catch (Exception) { }
            }
        }
    }

    // A reply from Google's token endpoint.
    public sealed class TokenResult
    {
        public int Status;               // the HTTP status; 0 when no reply arrived
        public string AccessToken;       // null when it failed
        public string RefreshToken;      // from the exchange; a refresh may also hand out a new one
        public long ExpiresAtMs;
        public string Error = "";        // Google's OAuth error code ("invalid_grant"), or ""
        public string NoReply = "";      // why no reply arrived ("Timeout"), when none did

        public bool Ok { get { return AccessToken != null; } }
        // The code, or the refresh token, is no longer any good: only signing in again helps.
        public bool Rejected { get { return Error == "invalid_grant"; } }
    }

    // How a sign-in ended.
    public sealed class SignInResult
    {
        public string RefreshToken;     // null unless it worked
        public string AccessToken;
        public long ExpiresAtMs;
        public string Problem = "";     // a line for the settings when it didn't; "" when it worked or was cancelled
        public string LogText = "";     // statuses and codes only, for the log

        public bool Ok { get { return RefreshToken != null; } }
    }

    // The sign-in, token refresh and revoke (calendar spec §3), with the user's own OAuth client. Capsule talks to
    // oauth2.googleapis.com only; the browser handles accounts.google.com. Tests point TokenUrl and RevokeUrl at a loopback
    // fake and replace OpenBrowser. Nothing here throws or logs: tokens, codes, the verifier and the client secret are
    // held in memory and sent only to Google.
    public sealed class GoogleAuth
    {
        public const int TimeoutMs = 15000;
        public const int SignInTimeoutMs = 5 * 60 * 1000;   // a sign-in not finished in 5 minutes is given up

        readonly string clientId, clientSecret;
        readonly object gate = new object();
        LoopbackCallback waiting;

        public string TokenUrl = GoogleOAuth.TokenEndpoint;
        public string RevokeUrl = GoogleOAuth.RevokeEndpoint;
        // The system browser, at Google's consent page. The URL is always AuthUrl's, on accounts.google.com.
        public Action<string> OpenBrowser = delegate(string url) { using (Process.Start(url)) { } };

        public GoogleAuth(string clientId, string clientSecret)
        {
            this.clientId = clientId ?? "";
            this.clientSecret = clientSecret ?? "";
        }

        // The whole installed-app flow: listen on the loopback address, open the consent page, wait for the callback,
        // exchange the code (with the PKCE verifier) for tokens. Blocks for up to timeoutMs, so it runs on a worker
        // thread; Cancel ends it from another.
        public SignInResult SignIn(int timeoutMs, long now)
        {
            var result = new SignInResult();
            try
            {
                Pkce pkce = Pkce.Make();
                string state = GoogleOAuth.RandomUrlSafe(16);
                string redirect;
                CallbackResult callback;
                var listener = new LoopbackCallback();
                lock (gate) waiting = listener;
                try
                {
                    redirect = listener.RedirectUri;
                    try { OpenBrowser(GoogleOAuth.AuthUrl(clientId, redirect, pkce.Challenge, state)); }
                    catch (Exception e) { return Failed(result, "Couldn't open the browser", "the browser couldn't be opened (" + e.GetType().Name + ")"); }
                    callback = listener.Wait(state, timeoutMs);
                }
                finally
                {
                    listener.Dispose();
                    lock (gate) waiting = null;
                }
                if (callback.Cancelled) return Failed(result, "", "cancelled");
                if (callback.TimedOut) return Failed(result, "Sign-in wasn't finished in time. Try again.", "timed out");
                if (callback.Error != null)
                    return Failed(result, callback.Error == "access_denied" ? "Sign-in was cancelled in the browser" : "Google refused the sign-in (" + callback.Error + ")", "Google said " + callback.Error);
                TokenResult token = Exchange(callback.Code, pkce.Verifier, redirect, now);
                if (!token.Ok) return Failed(result, Problem(token), "the code exchange failed: " + Describe(token));
                if (string.IsNullOrEmpty(token.RefreshToken)) return Failed(result, "Google didn't hand out a refresh token. Try again.", "no refresh token");
                result.RefreshToken = token.RefreshToken;
                result.AccessToken = token.AccessToken;
                result.ExpiresAtMs = token.ExpiresAtMs;
                result.LogText = "signed in";
                return result;
            }
            catch (Exception e) { return Failed(result, "Sign-in failed", "sign-in failed (" + e.GetType().Name + ")"); }
        }

        static SignInResult Failed(SignInResult result, string problem, string log)
        {
            result.Problem = problem;
            result.LogText = log;
            return result;
        }

        // A sign-in waiting for the browser stops waiting.
        public void Cancel()
        {
            lock (gate) if (waiting != null) waiting.Cancel();
        }

        // The authorization code, for an access token and a refresh token.
        public TokenResult Exchange(string code, string verifier, string redirectUri, long now)
        {
            return ParseToken(Post(TokenUrl, new List<KeyValuePair<string, string>>
            {
                GoogleOAuth.Field("code", code),
                GoogleOAuth.Field("client_id", clientId),
                GoogleOAuth.Field("client_secret", clientSecret),
                GoogleOAuth.Field("redirect_uri", redirectUri),
                GoogleOAuth.Field("grant_type", "authorization_code"),
                GoogleOAuth.Field("code_verifier", verifier),
            }), now);
        }

        // A new access token. Rejected when Google no longer takes the refresh token (revoked, expired, or the client
        // changed): only signing in again helps then.
        public TokenResult Refresh(string refreshToken, long now)
        {
            return ParseToken(Post(TokenUrl, new List<KeyValuePair<string, string>>
            {
                GoogleOAuth.Field("client_id", clientId),
                GoogleOAuth.Field("client_secret", clientSecret),
                GoogleOAuth.Field("refresh_token", refreshToken),
                GoogleOAuth.Field("grant_type", "refresh_token"),
            }), now);
        }

        // Ends Capsule's access: Google forgets the grant. True when Google confirmed it.
        public bool Revoke(string token)
        {
            return Post(RevokeUrl, new List<KeyValuePair<string, string>> { GoogleOAuth.Field("token", token) }).Status == 200;
        }

        static HttpResult Post(string url, List<KeyValuePair<string, string>> fields)
        {
            var headers = new Dictionary<string, string>();
            headers["Content-Type"] = "application/x-www-form-urlencoded";
            return Http.Send("POST", url, headers, GoogleOAuth.Form(fields), TimeoutMs);
        }

        public static TokenResult ParseToken(HttpResult reply, long now)
        {
            var t = new TokenResult { Status = reply.Status, NoReply = reply.Status == 0 ? reply.Error : "" };
            if (reply.Status != 200)
            {
                t.Error = GoogleOAuth.OAuthError(reply.Body);
                return t;
            }
            object root = Json.TryParse(reply.Body);
            string access = Json.Str(Json.Get(root, "access_token"));
            if (string.IsNullOrEmpty(access))
            {
                t.Error = "unexpected_reply";
                return t;
            }
            t.AccessToken = access;
            string refresh = Json.Str(Json.Get(root, "refresh_token"));
            if (!string.IsNullOrEmpty(refresh)) t.RefreshToken = refresh;
            double seconds = Json.Num(Json.Get(root, "expires_in")) ?? 3600;
            t.ExpiresAtMs = now + (long)(Math.Max(60, seconds) * 1000);
            return t;
        }

        // A failed token call, for the log: its status and Google's error code, never a body.
        public static string Describe(TokenResult t)
        {
            if (t.Status == 0) return "no reply (" + t.NoReply + ")";
            return "HTTP " + t.Status + (t.Error != "" ? " " + t.Error : "");
        }

        // A failed token call, in a line for the settings.
        public static string Problem(TokenResult t)
        {
            if (t.Status == 0) return "Couldn't reach Google (" + t.NoReply + ")";
            if (t.Error == "invalid_client" || t.Error == "unauthorized_client") return "Google didn't accept the client ID or secret";
            if (t.Error == "invalid_grant") return "Google didn't accept the sign-in. Try again.";
            if (t.Status == 200) return "Unexpected reply from Google";
            return "Google returned HTTP " + t.Status + (t.Error != "" ? " (" + t.Error + ")" : "");
        }
    }

    // The JSON file Google Cloud gives for an OAuth client ("Download JSON"): {"installed": {"client_id", "client_secret",
    // …}} for a Desktop app, "web" for a web one. Pasted whole, it fills in both fields at once.
    public static class GoogleClientFile
    {
        public static bool Parse(string text, out string clientId, out string clientSecret)
        {
            clientId = null;
            clientSecret = null;
            if (string.IsNullOrWhiteSpace(text) || text.TrimStart()[0] != '{') return false;
            object root = Json.TryParse(text);
            object client = Json.Get(root, "installed") ?? Json.Get(root, "web");
            string id = (Json.Str(Json.Get(client, "client_id")) ?? "").Trim();
            string secret = (Json.Str(Json.Get(client, "client_secret")) ?? "").Trim();
            if (id == "" || secret == "") return false;
            clientId = id;
            clientSecret = secret;
            return true;
        }
    }
}
