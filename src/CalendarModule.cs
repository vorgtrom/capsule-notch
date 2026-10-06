using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace Capsule
{
    // What one pass of reading the calendars did. Built on a worker thread; applied on the UI thread.
    public sealed class CalendarPassResult
    {
        public string AccessToken;          // the access token in use at the end (refreshed or not); null if none could be had
        public long AccessExpiresAtMs;
        public string NewRefreshToken;      // Google handed out a new refresh token while refreshing
        public List<GoogleCalendar> Calendars;   // null when they couldn't be read
        public List<CalendarEvent> Events;       // null when they couldn't all be read
        public HttpResult Failure;          // what stopped the pass, or null
        public bool RefreshRejected;        // Google no longer takes the refresh token: sign out
        public int Refreshes;               // access-token refreshes this pass made
        public string LogText = "";         // counts and statuses only
        public bool FromLink;               // read from the calendar's iCal address, not through a sign-in
        public string LinkName;             // the linked calendar's name (shown only), when it was read
    }

    // One pass (calendar spec §3), on a worker thread; touches nothing but its arguments. An access token that is missing
    // or about to expire is refreshed first; a 401 from the Calendar API refreshes it once and tries again. A calendar
    // whose events answer 404 (unshared or deleted since the list was read) is skipped.
    public static class CalendarPass
    {
        public const long RefreshAheadMs = 60 * 1000;

        public static CalendarPassResult Run(GoogleAuth auth, string refreshToken, string accessToken, long accessExpiresAtMs,
            Func<string, GoogleCalendarClient> newClient, Func<GoogleCalendar, bool> chosen, long fromMs, long toMs, long now)
        {
            var r = new CalendarPassResult { AccessToken = accessToken, AccessExpiresAtMs = accessExpiresAtMs };
            if (string.IsNullOrEmpty(accessToken) || accessExpiresAtMs - RefreshAheadMs <= now)
            {
                if (!Refresh(auth, refreshToken, r, now)) return r;
            }
            GoogleCalendarClient client = newClient(r.AccessToken);
            List<GoogleCalendar> calendars = client.Calendars();
            if (calendars == null && Unauthorized(client) && r.Refreshes == 0)
            {
                if (!Refresh(auth, refreshToken, r, now)) return r;
                client = newClient(r.AccessToken);
                calendars = client.Calendars();
            }
            if (calendars == null)
            {
                r.Failure = client.LastFailure;
                r.LogText = "calendar list: " + GoogleCalendarClient.Describe(r.Failure);
                return r;
            }
            r.Calendars = calendars;
            var events = new List<CalendarEvent>();
            int read = 0;
            foreach (GoogleCalendar calendar in calendars.Where(chosen))
            {
                List<CalendarEvent> some = client.Events(calendar, fromMs, toMs);
                if (some == null && Unauthorized(client) && r.Refreshes == 0)
                {
                    if (!Refresh(auth, refreshToken, r, now)) return r;
                    client = newClient(r.AccessToken);
                    some = client.Events(calendar, fromMs, toMs);
                }
                if (some == null && client.LastFailure != null && client.LastFailure.Status == 404) continue;
                if (some == null)
                {
                    r.Failure = client.LastFailure;
                    r.LogText = "events: " + GoogleCalendarClient.Describe(r.Failure);
                    return r;
                }
                read++;
                events.AddRange(some);
            }
            r.Events = events.OrderBy(e => e.AllDay ? 0 : 1).ThenBy(e => e.StartMs).ToList();
            r.LogText = events.Count + " event" + (events.Count == 1 ? "" : "s") + " from " + read + " calendar" + (read == 1 ? "" : "s");
            return r;
        }

        // A pass over the calendar's secret iCal address: one GET to calendar.google.com, then the feed read for the span.
        public static CalendarPassResult RunLink(Func<string, HttpResult> fetch, string link, long fromMs, long toMs, TimeZoneInfo zone)
        {
            var r = new CalendarPassResult { FromLink = true };
            HttpResult reply = fetch(link);
            if (reply.Status != 200)
            {
                r.Failure = reply;
                r.LogText = "calendar link: " + GoogleCalendarClient.Describe(reply);
                return r;
            }
            if (reply.Truncated)
            {
                r.Failure = new HttpResult { Status = 200, Error = TooLarge };
                r.LogText = "calendar link: the calendar is too large to read";
                return r;
            }
            IcsResult ics = Ics.Read(reply.Body, fromMs, toMs, zone);
            if (ics == null)
            {
                r.Failure = new HttpResult { Status = 200, Error = "unexpected reply" };
                r.LogText = "calendar link: the reply isn't a calendar";
                return r;
            }
            r.Events = ics.Events;
            r.LinkName = ics.Name;
            r.LogText = ics.Events.Count + " event" + (ics.Events.Count == 1 ? "" : "s") + " from the calendar link"
                + (ics.NotExpanded > 0 ? ", " + ics.NotExpanded + " repeating event" + (ics.NotExpanded == 1 ? "" : "s") + " shown once" : "");
            return r;
        }

        public const string TooLarge = "too large";

        // What a failed read of the iCal address means, in a line for the tile, the card and the settings.
        public static string LinkProblem(HttpResult reply)
        {
            if (reply == null) return "";
            if (reply.Status == 0) return "Couldn't reach Google";
            if (reply.Status == 400 || reply.Status == 401 || reply.Status == 403 || reply.Status == 404) return "Google doesn't know that calendar link any more. Copy it again in ⚙";
            if (reply.Status == 200 && reply.Error == TooLarge) return "The calendar is too large to read";
            if (reply.Status == 200) return "Google sent something that isn't a calendar";
            return GoogleCalendarClient.Problem(reply);
        }

        static bool Unauthorized(GoogleCalendarClient client) { return client.LastFailure != null && client.LastFailure.Status == 401; }

        // False when the pass can't go on: the refresh token was rejected, or the refresh failed.
        static bool Refresh(GoogleAuth auth, string refreshToken, CalendarPassResult r, long now)
        {
            r.Refreshes++;
            TokenResult t = auth.Refresh(refreshToken, now);
            if (t.Ok)
            {
                r.AccessToken = t.AccessToken;
                r.AccessExpiresAtMs = t.ExpiresAtMs;
                if (!string.IsNullOrEmpty(t.RefreshToken) && t.RefreshToken != refreshToken) r.NewRefreshToken = t.RefreshToken;
                return true;
            }
            r.AccessToken = null;
            r.RefreshRejected = t.Rejected;
            r.Failure = new HttpResult { Status = t.Status, Error = t.NoReply };
            r.LogText = "refresh: " + GoogleAuth.Describe(t);
            return false;
        }
    }

    // A calendar as the settings list it: its check box.
    public sealed class CalendarChoice
    {
        public string Id = "";
        public string Name = "";
        public string Color = GoogleCalendarClient.DefaultColor;
        public bool Shown;
    }

    // What the settings' Google Calendar section shows.
    public sealed class GoogleSettings
    {
        public string ClientId = "";
        public bool SecretSaved;
        public bool SignedIn;
        public bool SigningIn;          // waiting for the browser
        public string Account = "";     // the account's email, once read; "" until then
        public List<CalendarChoice> Calendars = new List<CalendarChoice>();
        public string Status = "";      // the last sign-in's outcome, or what went wrong reading
        public bool SecretPending;      // a new client secret is held for the next sign-in (it is never shown)
        public bool LinkSaved;          // a calendar's iCal address is saved (it is never shown)
        public string LinkName = "";    // that calendar's name, once read
    }

    // Google Calendar (calendar spec §3, §4; link spec): reads one calendar's secret iCal address every 15 minutes, or,
    // once signed in with the user's own OAuth client (which then wins), reads the chosen calendars
    // every 5 minutes (and on Refresh, when the panel opens, after waking, and when the network comes back after a pass
    // that got no reply), and keeps the events in memory only. The refresh token and the client secret are kept
    // DPAPI-encrypted; access tokens live in memory. A 401 refreshes the access token once; a refresh token Google
    // rejects signs the user out (SignedOutByGoogle). A 429 backs off as the usage checks do. The log gets counts and
    // statuses only, never a token, a title or a calendar's name.
    public sealed class CalendarModule : IModule
    {
        public const long EveryMs = 5 * 60 * 1000;
        public const long LinkEveryMs = 15 * 60 * 1000;   // Google itself refreshes an iCal feed only every so often
        public const int LinkMaxChars = 16 * 1024 * 1024;   // a feed holds a calendar's whole history; a larger one isn't read
        public const long PanelFreshMs = 60 * 1000;   // the panel opening reads again only when the list is older than this

        readonly string tokenPath, secretPath, linkPath;
        readonly Func<string> clientId;                       // config.json's google_client_id
        readonly Func<IDictionary<string, bool>> choices;     // config.json's google_calendars
        readonly TaskScheduler ui;
        CalendarSnapshot snap = new CalendarSnapshot();
        List<GoogleCalendar> calendars = new List<GoogleCalendar>();
        bool signedIn;           // a refresh token is saved: the Calendar API is read, not the link
        string pendingId, pendingSecret;   // a new client typed in while signed in: used by the next sign-in, kept only once it works
        string linkName = "";
        string accessToken;
        long accessExpiresAtMs;
        string account = "";
        string status = "";
        long due;
        bool busy, again, unreachable;
        int retries, consecutive429;
        int generation;          // a sign-in or sign-out since a pass began makes that pass's result stale
        int passId;              // the latest pass started: only its result is applied
        GoogleAuth signingIn;    // the sign-in waiting for the browser, or null
        string lastLogged = "";

        public Func<string, string, GoogleAuth> NewAuth = (id, secret) => new GoogleAuth(id, secret);   // tests swap in fakes
        public Func<string, GoogleCalendarClient> NewClient = token => new GoogleCalendarClient(token);
        public Func<TimeZoneInfo> Zone = () => TimeZoneInfo.Local;
        public Func<string, HttpResult> FetchLink = url =>
        {
            var headers = new Dictionary<string, string>();
            headers["Accept"] = "text/calendar";
            return Http.Get(url, headers, 30000, LinkMaxChars);
        };
        public event Action Changed;             // raised on the UI thread
        public event Action SignedOutByGoogle;   // Google rejected the refresh token: Controller shows a balloon
        public event Action<string> SignedInWith;   // a sign-in worked with this client ID: Controller keeps it in config.json
        public event Action<string> ClientIdChosen; // a client ID typed in while signed out: Controller keeps it in config.json
        public Task LastPass { get; private set; }     // for tests to wait on
        public Task LastSignIn { get; private set; }
        public Task LastRevoke { get; private set; }

        // ui: where results are applied (the UI thread's scheduler; tests pass TaskScheduler.Default).
        // linkPath: where the calendar's iCal address is kept, encrypted.
        public CalendarModule(string tokenPath, string secretPath, string linkPath, Func<string> clientId, Func<IDictionary<string, bool>> choices, TaskScheduler ui)
        {
            this.tokenPath = tokenPath;
            this.secretPath = secretPath;
            this.linkPath = linkPath;
            this.clientId = clientId;
            this.choices = choices;
            this.ui = ui;
            signedIn = SecretStore.Exists(tokenPath);
            snap.SignedIn = Connected;
        }

        public bool SignedIn { get { return signedIn; } }        // with the user's own OAuth client
        public bool HasLink { get { return SecretStore.Exists(linkPath); } }
        public bool Connected { get { return signedIn || HasLink; } }   // there is a calendar to show: the cell and the tile's rows
        long Every { get { return signedIn ? EveryMs : LinkEveryMs; } }
        public bool SigningIn { get { return signingIn != null; } }
        public long DueMs { get { return due; } }
        public CalendarSnapshot Snapshot { get { return snap; } }
        public IList<GoogleCalendar> Calendars { get { return calendars; } }

        public void Tick(long now) { if (Connected && !busy && now >= due) Pass(); }
        public void Refresh() { Read(); }
        public void NetworkBack() { if (unreachable) Read(); }
        public void Resumed() { due = 0; }

        // The panel opened: read again unless the list is fresh.
        public void PanelOpened(long now)
        {
            if (Connected && (snap.DataAtMs == 0 || now - snap.DataAtMs > PanelFreshMs)) Read();
        }

        // A check box changed: read again with the new choice.
        public void ChoicesChanged() { Read(); }

        void Read()
        {
            if (!Connected) return;
            due = 0;
            if (busy) again = true;
            else Pass();
        }

        void Pass()
        {
            if (!signedIn)
            {
                LinkPass();
                return;
            }
            string refresh = SecretStore.Load(tokenPath, SecretStore.GoogleTokenPurpose);
            string secret = SecretStore.Load(secretPath, SecretStore.GoogleClientPurpose);
            if (refresh == null || secret == null || clientId() == "")
            {
                SetProblem(refresh == null ? "Can't read the saved sign-in. Sign in again in ⚙" : "Can't read the saved client. Enter it again in ⚙");
                due = Clock.NowMs() + EveryMs;
                return;
            }
            busy = true;
            due = long.MaxValue;
            long now = Clock.NowMs();
            TimeZoneInfo zone = Zone();
            long from = CalendarDay.WindowStartMs(now, zone), to = CalendarDay.WindowEndMs(now, zone);
            GoogleAuth auth = NewAuth(clientId(), secret);
            Func<string, GoogleCalendarClient> make = NewClient;
            var picked = new Dictionary<string, bool>(choices());
            string token = accessToken;
            long expires = accessExpiresAtMs;
            int run = generation, id = ++passId;
            LastPass = Task.Run(() => CalendarPass.Run(auth, refresh, token, expires, make, c => Shown(c, picked), from, to, now))
                .ContinueWith(t => Apply(t, run, id), ui);
        }

        void LinkPass()
        {
            string link = SecretStore.Load(linkPath, SecretStore.GoogleLinkPurpose);
            if (link == null)
            {
                SetProblem("Can't read the saved calendar link. Paste it again in ⚙");
                due = Clock.NowMs() + LinkEveryMs;
                return;
            }
            busy = true;
            due = long.MaxValue;
            long now = Clock.NowMs();
            TimeZoneInfo zone = Zone();
            long from = CalendarDay.WindowStartMs(now, zone), to = CalendarDay.WindowEndMs(now, zone);
            Func<string, HttpResult> fetch = FetchLink;
            int run = generation, id = ++passId;
            LastPass = Task.Run(() => CalendarPass.RunLink(fetch, link, from, to, zone)).ContinueWith(t => Apply(t, run, id), ui);
        }

        // Shown when the user ticked it, or, if they never touched its box, when Google Calendar shows it.
        public static bool Shown(GoogleCalendar c, IDictionary<string, bool> picked)
        {
            bool on;
            return picked.TryGetValue(c.Id, out on) ? on : c.Selected;
        }

        // Runs as a continuation nobody observes, so nothing may escape it.
        void Apply(Task<CalendarPassResult> t, int run, int id)
        {
            if (id != passId) return;   // a newer pass began (a sign-in finished meanwhile): its result is the one that counts
            busy = false;
            try
            {
                if (run != generation) return;   // signed out or in again meanwhile: this pass's tokens and events are stale
                if (t.IsFaulted)
                {
                    Log.Error("calendar: a pass failed (" + t.Exception.GetBaseException().GetType().Name + ")", null);
                    Failed(new HttpResult { Status = 0, Error = t.Exception.GetBaseException().GetType().Name }, !signedIn);
                    return;
                }
                CalendarPassResult r = t.Result;
                if (r.FromLink && r.LinkName != null) linkName = r.LinkName;
                if (!r.FromLink)
                {
                    accessToken = r.AccessToken;
                    accessExpiresAtMs = r.AccessExpiresAtMs;
                }
                if (r.NewRefreshToken != null) SecretStore.Save(tokenPath, r.NewRefreshToken, SecretStore.GoogleTokenPurpose, "calendar");
                if (r.Calendars != null) TakeCalendars(r.Calendars);
                Note(r.LogText);
                if (r.RefreshRejected)
                {
                    Log.Info("calendar: Google no longer accepts the sign-in; signed out");
                    Forget();
                    status = "Google ended the sign-in. Sign in again.";
                    if (SignedOutByGoogle != null) SignedOutByGoogle();
                    return;
                }
                if (r.Events == null)
                {
                    Failed(r.Failure, r.FromLink);
                    return;
                }
                unreachable = false;
                retries = 0;
                consecutive429 = 0;
                snap = new CalendarSnapshot { SignedIn = true, Loaded = true, Events = r.Events, DataAtMs = Clock.NowMs() };
                due = Clock.NowMs() + Every;
            }
            catch (Exception e)
            {
                Log.Error("calendar: applying a pass threw " + e.GetType().Name, null);
            }
            finally
            {
                if (due == long.MaxValue) due = Clock.NowMs() + Every;   // never left without a next pass
                Raise();
                if (again)
                {
                    again = false;
                    Read();   // asked for while this pass ran
                }
            }
        }

        // The pass didn't get the events: the last ones stay, dimmed once old, with what went wrong. No reply at all
        // retries soon (the network may not be back yet); a 429 backs off as the usage checks do; anything else waits
        // the usual wait (5 minutes signed in, 15 for the link).
        void Failed(HttpResult failure, bool fromLink)
        {
            long now = Clock.NowMs();
            unreachable = failure != null && failure.Status == 0;
            long wait;
            if (failure != null && failure.Status == 429)
            {
                consecutive429++;
                wait = ClaudeUsage.BackoffSeconds(consecutive429, failure.RetryAfterSeconds) * 1000;
            }
            else wait = UsageModule.RetryDelay(unreachable, ref retries, Every);
            due = now + wait;
            var s = new CalendarSnapshot { SignedIn = snap.SignedIn, Loaded = snap.Loaded, Events = snap.Events, DataAtMs = snap.DataAtMs, LastFailed = true };
            s.Problem = fromLink ? CalendarPass.LinkProblem(failure) : GoogleCalendarClient.Problem(failure);
            snap = s;
        }

        void SetProblem(string problem)
        {
            var s = new CalendarSnapshot { SignedIn = snap.SignedIn, Loaded = snap.Loaded, Events = snap.Events, DataAtMs = snap.DataAtMs, LastFailed = true, Problem = problem };
            snap = s;
            Raise();
        }

        void TakeCalendars(List<GoogleCalendar> list)
        {
            calendars = list;
            GoogleCalendar primary = list.FirstOrDefault(c => c.Primary);
            if (primary != null) account = primary.Id;
        }

        // The log hears of a change, not of every pass.
        void Note(string text)
        {
            if (text == "" || text == lastLogged) return;
            lastLogged = text;
            Log.Info("calendar: " + text);
        }

        // Sign in with the user's client: the browser opens at Google's consent page, and the module waits for it on a
        // worker thread. A new client ID and secret ("" keeps the saved secret) are used for this sign-in and kept only
        // once it worked, so a failed try with another client leaves the working sign-in as it was. False when it couldn't
        // start; Status says why.
        public bool SignIn(string newClientId, string newSecret)
        {
            string id = (newClientId ?? "").Trim();
            string given = (newSecret ?? "").Trim();
            if (id == "") id = (pendingId ?? clientId() ?? "").Trim();   // nothing typed: the client kept earlier
            if (given == "" && pendingSecret != null) given = pendingSecret;
            if (id == "")
            {
                status = "Enter your OAuth client's ID";
                Raise();
                return false;
            }
            string secret = given != "" ? given : SecretStore.Load(secretPath, SecretStore.GoogleClientPurpose);
            if (secret == null)
            {
                status = "Enter your OAuth client's secret";
                Raise();
                return false;
            }
            CancelSignIn();
            GoogleAuth auth = NewAuth(id, secret);
            signingIn = auth;
            status = "Waiting for you in the browser…";
            int run = ++generation;
            Raise();
            string keep = given != "" ? given : null;
            LastSignIn = Task.Run(() => auth.SignIn(GoogleAuth.SignInTimeoutMs, Clock.NowMs())).ContinueWith(t => FinishSignIn(t, auth, run, id, keep), ui);
            return true;
        }

        // newSecret: the client secret to keep, when the sign-in brought a new one; null to keep the saved one.
        void FinishSignIn(Task<SignInResult> t, GoogleAuth auth, int run, string id, string newSecret)
        {
            try
            {
                if (signingIn == auth) signingIn = null;
                if (run != generation) return;   // cancelled, or another sign-in or a sign-out came since
                SignInResult r = t.IsFaulted ? new SignInResult { Problem = "Sign-in failed", LogText = "sign-in failed (" + t.Exception.GetBaseException().GetType().Name + ")" } : t.Result;
                Log.Info("calendar: " + r.LogText);
                if (!r.Ok)
                {
                    status = r.Problem;
                    if (Connected) Read();   // the sign-in it would have replaced (or the link) goes on
                    return;
                }
                if (newSecret != null && !SecretStore.Save(secretPath, newSecret, SecretStore.GoogleClientPurpose, "calendar"))
                {
                    status = "Couldn't save the client secret. Try again.";
                    RevokeLater(auth, r.RefreshToken);
                    return;
                }
                string old = SecretStore.Load(tokenPath, SecretStore.GoogleTokenPurpose);
                if (!SecretStore.Save(tokenPath, r.RefreshToken, SecretStore.GoogleTokenPurpose, "calendar"))
                {
                    status = "Signed in, but the sign-in couldn't be saved. Try again.";
                    RevokeLater(auth, r.RefreshToken);
                    return;
                }
                if (old != null && old != r.RefreshToken) RevokeLater(auth, old);   // the sign-in this one replaces
                pendingId = null;
                pendingSecret = null;
                if (SignedInWith != null) SignedInWith(id);   // config.json keeps the client ID before the first pass reads it
                signedIn = true;
                accessToken = r.AccessToken;
                accessExpiresAtMs = r.ExpiresAtMs;
                account = "";
                calendars = new List<GoogleCalendar>();
                snap = new CalendarSnapshot { SignedIn = true };
                status = "";
                lastLogged = "";
                busy = false;
                again = false;
                Read();
            }
            catch (Exception e) { Log.Error("calendar: finishing the sign-in threw " + e.GetType().Name, null); }
            finally { Raise(); }
        }

        // A sign-in waiting for the browser stops waiting.
        public void CancelSignIn()
        {
            if (signingIn == null) return;
            signingIn.Cancel();
            signingIn = null;
            generation++;
            status = "";
        }

        // Sign out (calendar spec §3): the token file goes, the events and tokens are forgotten, and Google is asked to
        // revoke the grant in the background.
        public void SignOut()
        {
            CancelSignIn();
            pendingId = null;
            pendingSecret = null;
            string refresh = SecretStore.Load(tokenPath, SecretStore.GoogleTokenPurpose);
            string secret = SecretStore.Load(secretPath, SecretStore.GoogleClientPurpose) ?? "";
            Forget();
            status = "";
            Log.Info("calendar: signed out");
            if (refresh != null) RevokeLater(NewAuth(clientId(), secret), refresh);
            Raise();
        }

        void RevokeLater(GoogleAuth auth, string token)
        {
            LastRevoke = Task.Run(() =>
            {
                bool done = auth.Revoke(token);
                Log.Info("calendar: " + (done ? "access revoked at Google" : "Google didn't confirm the revoke"));
            });
        }

        // Signed out: the token file goes, and everything read with it is forgotten. A saved link is read again at the next
        // tick. The caller raises Changed.
        void Forget()
        {
            generation++;
            again = false;
            SecretStore.Delete(tokenPath);
            signedIn = false;
            accessToken = null;
            accessExpiresAtMs = 0;
            account = "";
            calendars = new List<GoogleCalendar>();
            snap = new CalendarSnapshot { SignedIn = Connected };
            unreachable = false;
            retries = 0;
            consecutive429 = 0;
            lastLogged = "";
            due = 0;
        }

        // What was in the client's fields when the settings closed, or a client file pasted whole, so that clicking away to
        // copy the other one loses nothing. Signed out, it is kept at once: the ID in config.json (through ClientIdChosen),
        // the secret encrypted. Signed in, it is held in memory for the next sign-in, which keeps it only once it works.
        public void KeepClient(string id, string secret)
        {
            string i = (id ?? "").Trim(), s = (secret ?? "").Trim();
            if (i == "" && s == "") return;
            if (signedIn)
            {
                if (i != "" && i != clientId()) pendingId = i;
                if (s != "") pendingSecret = s;
            }
            else
            {
                if (i != "" && i != clientId() && ClientIdChosen != null) ClientIdChosen(i);
                if (s != "" && !SecretStore.Save(secretPath, s, SecretStore.GoogleClientPurpose, "calendar")) status = "Couldn't save the client secret";
            }
            Raise();
        }

        // Keeps the calendar's iCal address, encrypted, and reads it at once (unless signed in, which wins). False when it
        // isn't a Google Calendar iCal address or couldn't be kept; Status says why. The address is never logged or shown.
        public bool SaveLink(string text)
        {
            string link = IcsLink.Normalize(text);
            if (link == null)
            {
                status = "That isn't a Google Calendar iCal address. Copy “Secret address in iCal format”.";
                Raise();
                return false;
            }
            if (!SecretStore.Save(linkPath, link, SecretStore.GoogleLinkPurpose, "calendar"))
            {
                status = "Couldn't save the link";
                Raise();
                return false;
            }
            Log.Info("calendar: link saved");
            status = "";
            linkName = "";
            if (!signedIn) StartOver(true);
            Raise();
            return true;
        }

        // Forgets the iCal address.
        public void RemoveLink()
        {
            SecretStore.Delete(linkPath);
            Log.Info("calendar: link removed");
            status = "";
            linkName = "";
            if (!signedIn) StartOver(false);
            Raise();
        }

        // What was read from the link is dropped, and, when there is one, the new link is read at once.
        void StartOver(bool read)
        {
            generation++;
            busy = false;   // a pass still running for the old link is dropped when it ends (its id is no longer the latest)
            again = false;
            unreachable = false;
            retries = 0;
            consecutive429 = 0;
            lastLogged = "";
            snap = new CalendarSnapshot { SignedIn = Connected };
            due = 0;
            if (read) Read();
        }

        void Raise()
        {
            try { if (Changed != null) Changed(); }
            catch (Exception e) { Log.Error("calendar: a Changed handler threw " + e.GetType().Name, null); }
        }

        public CellModel Cell(long now, CultureInfo culture) { return CalendarDay.Cell(snap, now, Zone(), culture); }
        public CalendarCardModel Card(long now, CultureInfo culture) { return CalendarDay.Card(snap, now, Zone(), culture); }
        public CalendarTile Tile(long now, CultureInfo culture) { return CalendarDay.Tile(snap, now, Zone(), culture); }

        // The settings' section: the client, the account, and a check box per calendar.
        public GoogleSettings Settings()
        {
            IDictionary<string, bool> picked = choices();
            var s = new GoogleSettings
            {
                ClientId = pendingId ?? clientId(),
                SecretSaved = SecretStore.Exists(secretPath),
                SecretPending = pendingSecret != null,
                SignedIn = signedIn,
                SigningIn = signingIn != null,
                LinkSaved = HasLink,
                LinkName = linkName,
                Account = account,
                Status = status != "" ? status : snap.Problem,
            };
            foreach (GoogleCalendar c in calendars)
                s.Calendars.Add(new CalendarChoice { Id = c.Id, Name = c.Name, Color = c.Color, Shown = Shown(c, picked) });
            return s;
        }
    }
}
