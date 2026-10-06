using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Capsule
{
    public static class CalendarModuleTests
    {
        public static void Run()
        {
            SecretsKeepTheirPurpose();
            ConfigKeepsTheGoogleSettings();
            SignedOutDoesNothing();
            APassReadsTheChosenCalendars();
            ChoicesOverrideGooglesSelection();
            A401RefreshesOnce();
            ARejectedRefreshTokenSignsOut();
            FailuresBackOff();
            SignOutForgetsAndRevokes();
            APassThatEndsAfterSignOutIsDropped();
            SignInRunsThroughTheBrowser();
            ALinkShowsTheCalendar();
            OnlyGoogleCalendarLinksAreKept();
            LinkFailuresSayWhy();
            RemovingTheLinkDisconnects();
            SigningInWinsOverTheLink();
            TheClientIsKeptWhenThePanelCloses();
        }

        // Pasting the client ID, then clicking away to copy the secret, closes the panel: neither is lost.
        static void TheClientIsKeptWhenThePanelCloses()
        {
            var rig = new Rig(false).Working();
            File.Delete(rig.SecretPath);
            rig.ClientId = "";
            string chosen = null;
            rig.Module.ClientIdChosen += delegate(string id) { chosen = id; rig.ClientId = id; };
            rig.Module.KeepClient(" cid-typed ", "");
            TestRunner.Check(chosen == "cid-typed" && rig.Module.Settings().ClientId == "cid-typed" && !File.Exists(rig.SecretPath), "signed out, a typed client ID is kept at once");
            rig.Module.KeepClient("cid-typed", " csecret-typed ");
            TestRunner.Check(SecretStore.Load(rig.SecretPath, SecretStore.GoogleClientPurpose) == "csecret-typed" && rig.Module.Settings().SecretSaved, "and the secret, encrypted, when it comes");
            TestRunner.Check(rig.Module.SignIn("", "") && rig.Module.LastSignIn.Wait(10000), "Sign in then needs nothing typed again");
            rig.Settle();
            TestRunner.Check(rig.Module.SignedIn && rig.Google.Requests.Any(r => r.Path == "/token" && r.Body.Contains("client_secret=csecret-typed")), "and signs in with them");

            chosen = null;
            rig.Module.KeepClient("cid-other", "csecret-other");
            GoogleSettings s = rig.Module.Settings();
            TestRunner.Check(chosen == null && SecretStore.Load(rig.SecretPath, SecretStore.GoogleClientPurpose) == "csecret-typed" && s.ClientId == "cid-other" && s.SecretPending, "signed in, another client is only held for the next sign-in, not kept yet");
            rig.Module.SignOut();
            TestRunner.Check(rig.Module.Settings().ClientId == "cid-typed" && !rig.Module.Settings().SecretPending, "and signing out drops what was held");
        }

        const string Link = "https://calendar.google.com/calendar/ical/sample.user%40example.com/private-0123456789abcdef0123456789abcdef/basic.ics";

        // Signed out, with the fixture's feed behind the link; fetched counts the fetches.
        static Rig LinkRig(Func<string, HttpResult> feed)
        {
            var rig = new Rig(false);
            rig.Module.FetchLink = feed;
            return rig;
        }

        static HttpResult Feed(string url) { return new HttpResult { Status = 200, Body = TestRunner.Fixture("google-calendar.ics") }; }

        static void ALinkShowsTheCalendar()
        {
            string fetched = null;
            var rig = LinkRig(url => { fetched = url; return Feed(url); });
            TestRunner.Check(!rig.Module.Connected && !rig.Module.HasLink, "no link, no sign-in: nothing to show");
            long before = Clock.NowMs();
            TestRunner.Check(rig.Module.SaveLink("  webcal://calendar.google.com/calendar/ical/sample.user%40example.com/private-0123456789abcdef0123456789abcdef/basic.ics "), "a Google Calendar iCal address is kept");
            TestRunner.Check(File.Exists(rig.LinkPath) && !(Files.ReadText(rig.LinkPath) ?? "").Contains("private-") && SecretStore.Load(rig.LinkPath, SecretStore.GoogleLinkPurpose) == Link, "encrypted, as https");
            TestRunner.Check(rig.Module.Connected && !rig.Module.SignedIn, "the calendar shows without a sign-in");
            rig.Settle();
            CalendarSnapshot s = rig.Module.Snapshot;
            TestRunner.Check(s.SignedIn && s.Loaded && !s.LastFailed && s.Problem == "", "and is read at once");
            TestRunner.Eq(Link, fetched, "from that address");
            GoogleSettings settings = rig.Module.Settings();
            TestRunner.Check(settings.LinkSaved && settings.LinkName == "sample.user@example.com" && !settings.SignedIn, "the settings say a link is saved, and whose calendar it is");
            TestRunner.Check(rig.Module.DueMs >= before + CalendarModule.LinkEveryMs - 1000 && rig.Module.DueMs <= Clock.NowMs() + CalendarModule.LinkEveryMs, "read again in 15 minutes");
            string log = Files.ReadText(Paths.LogFile) ?? "";
            TestRunner.Check(log.Contains("calendar: link saved") && log.Contains("from the calendar link, 1 repeating event shown once"), "the log says so, with counts");
            TestRunner.Check(!log.Contains("private-0123456789abcdef") && !log.Contains("Standup"), "but never the address or a title");
        }

        static void OnlyGoogleCalendarLinksAreKept()
        {
            int fetches = 0;
            var rig = LinkRig(url => { fetches++; return Feed(url); });
            TestRunner.Check(!rig.Module.SaveLink("https://example.com/calendar/ical/x/basic.ics") && !File.Exists(rig.LinkPath) && fetches == 0, "another site's address is refused, and nothing fetched");
            TestRunner.Eq("That isn't a Google Calendar iCal address. Copy “Secret address in iCal format”.", rig.Module.Settings().Status, "the settings say what to copy");
        }

        static void LinkFailuresSayWhy()
        {
            var replies = new Queue<HttpResult>();
            replies.Enqueue(new HttpResult { Status = 200, Body = TestRunner.Fixture("google-calendar.ics") });
            replies.Enqueue(new HttpResult { Status = 404, Body = "Not Found" });
            replies.Enqueue(new HttpResult { Status = 200, Body = "BEGIN:VCALENDAR", Truncated = true });
            replies.Enqueue(new HttpResult { Status = 200, Body = "<html>Sign in</html>" });
            replies.Enqueue(new HttpResult { Status = 0, Error = "NameResolutionFailure" });
            var rig = LinkRig(url => replies.Count > 1 ? replies.Dequeue() : replies.Peek());
            rig.Module.SaveLink(Link);
            rig.Settle();
            rig.Module.Refresh();
            rig.Settle();
            TestRunner.Check(rig.Module.Snapshot.LastFailed && rig.Module.Snapshot.Loaded && rig.Module.Connected && rig.Module.Snapshot.Problem == "Google doesn't know that calendar link any more. Copy it again in ⚙", "a link Google no longer knows: the last events stay, with what to do");
            rig.Module.Refresh();
            rig.Settle();
            TestRunner.Eq("The calendar is too large to read", rig.Module.Snapshot.Problem, "a feed over the limit isn't read half");
            rig.Module.Refresh();
            rig.Settle();
            TestRunner.Eq("Google sent something that isn't a calendar", rig.Module.Snapshot.Problem, "a reply that isn't a calendar");
            long before = Clock.NowMs();
            rig.Module.Refresh();
            rig.Settle();
            TestRunner.Check(rig.Module.Snapshot.Problem == "Couldn't reach Google" && rig.Module.DueMs - before <= 21000 + (Clock.NowMs() - before), "no reply: tried again in 20 s");
        }

        static void RemovingTheLinkDisconnects()
        {
            var rig = LinkRig(Feed);
            rig.Module.SaveLink(Link);
            rig.Settle();
            rig.Module.RemoveLink();
            TestRunner.Check(!rig.Module.Connected && !File.Exists(rig.LinkPath) && rig.Module.Snapshot.Events.Count == 0 && !rig.Module.Settings().LinkSaved, "removing the link forgets it and its events");
            TestRunner.Eq(CalendarDay.SignInHint, rig.Module.Tile(Clock.NowMs(), System.Globalization.CultureInfo.GetCultureInfo("en-US")).Hint, "and the tile says how to connect");
        }

        static void SigningInWinsOverTheLink()
        {
            int fetches = 0;
            var rig = new Rig(true).Working();
            rig.Module.FetchLink = url => { fetches++; return Feed(url); };
            rig.Module.SaveLink(Link);
            rig.Module.Refresh();
            rig.Settle();
            TestRunner.Check(fetches == 0 && rig.Api.Requests.Count > 0 && rig.Module.Snapshot.Events.Count == 11, "signed in, the Calendar API is read and the link isn't");
            rig.Google.Reply("/revoke", 200, "");
            rig.Module.SignOut();
            TestRunner.Check(rig.Module.Connected && !rig.Module.SignedIn && rig.Module.Snapshot.SignedIn, "signed out, the link still shows the calendar");
            rig.Module.Tick(Clock.NowMs());
            rig.Settle();
            TestRunner.Check(fetches == 1 && rig.Module.Snapshot.Loaded, "and is read at the next tick");
        }

        const string Tokens = "{\"access_token\": \"ya29.sample\", \"expires_in\": 3599, \"refresh_token\": \"1//sample\", \"token_type\": \"Bearer\"}";
        const string FamilyEvents = "{\"items\": [{\"id\": \"dinner\", \"status\": \"confirmed\", \"summary\": \"Dinner\", \"start\": {\"dateTime\": \"2026-10-06T18:00:00-07:00\"}, \"end\": {\"dateTime\": \"2026-10-06T19:00:00-07:00\"}}]}";

        // A module on its own folder, signed in (or not), whose Google is the two fakes.
        sealed class Rig
        {
            public string Dir = TestRunner.NewTempDir();
            public FakeHttp Google = new FakeHttp();
            public FakeCalendarApi Api = new FakeCalendarApi();
            public Dictionary<string, bool> Choices = new Dictionary<string, bool>();
            public string ClientId = "cid-sample";
            public Func<CalendarRequest, HttpResult> Transport;
            public CalendarModule Module;
            public string TokenPath { get { return Path.Combine(Dir, "google-token.bin"); } }
            public string SecretPath { get { return Path.Combine(Dir, "google-client-secret.bin"); } }
            public string LinkPath { get { return Path.Combine(Dir, "google-calendar-link.bin"); } }

            public Rig(bool signedIn)
            {
                Transport = Api.Answer;
                SecretStore.Save(SecretPath, "csecret-sample", SecretStore.GoogleClientPurpose, "calendar");
                if (signedIn) SecretStore.Save(TokenPath, "1//sample", SecretStore.GoogleTokenPurpose, "calendar");
                Module = new CalendarModule(TokenPath, SecretPath, LinkPath, () => ClientId, () => Choices, TaskScheduler.Default);
                Module.Zone = () => CalendarDayTests.Zone;
                Module.NewAuth = (id, secret) =>
                {
                    var auth = new GoogleAuth(id, secret);
                    auth.TokenUrl = Google.Url("/token");
                    auth.RevokeUrl = Google.Url("/revoke");
                    auth.OpenBrowser = FollowRedirect;
                    return auth;
                };
                Module.NewClient = token =>
                {
                    var client = new GoogleCalendarClient(token);
                    client.Transport = r => Transport(r);
                    return client;
                };
            }

            // The whole calendar: the list, the primary and Family calendars' events, and a 404 for the odd one.
            public Rig Working()
            {
                Google.Reply("/token", 200, Tokens);
                Api.Reply(FakeCalendarApi.ListPath, 200, TestRunner.Fixture("google-calendar-list.json"))
                    .Reply(FakeCalendarApi.EventsPathOf(GoogleCalendarTests.Primary), 200, TestRunner.Fixture("google-events.json"))
                    .Reply(FakeCalendarApi.EventsPathOf(GoogleCalendarTests.Family), 200, FamilyEvents)
                    .Reply(FakeCalendarApi.EventsPathOf("odd@group.calendar.google.com"), 404, "{\"error\": {\"code\": 404}}");
                return this;
            }

            public int TokenCalls { get { lock (Google.Requests) return Google.Requests.Count(r => r.Path == "/token"); } }

            // Waits for the pass under way, and any pass it started when it ended.
            public void Settle()
            {
                for (int i = 0; i < 10; i++)
                {
                    Task pass = Module.LastPass;
                    if (pass == null) return;
                    pass.Wait(5000);
                    Thread.Sleep(20);
                    if (Module.LastPass == pass) return;
                }
            }

            // The browser, as Google would answer after consent: straight back to the loopback address with a code.
            static void FollowRedirect(string url)
            {
                Dictionary<string, string> q = GoogleOAuth.ParseQuery(url);
                string back = q["redirect_uri"] + "?state=" + Uri.EscapeDataString(q["state"]) + "&code=4%2Fflow-code";
                Task.Run(() => Http.Get(back, new Dictionary<string, string>(), 5000));
            }
        }

        static void SignedOutDoesNothing()
        {
            var rig = new Rig(false).Working();
            rig.Module.Tick(Clock.NowMs());
            TestRunner.Check(!rig.Module.SignedIn && rig.Module.LastPass == null && rig.Api.Requests.Count == 0, "signed out: no pass, no request");
            TestRunner.Eq(CalendarDay.SignInHint, rig.Module.Tile(Clock.NowMs(), System.Globalization.CultureInfo.GetCultureInfo("en-US")).Hint, "and the tile says how to sign in");
        }

        static void APassReadsTheChosenCalendars()
        {
            var rig = new Rig(true).Working();
            TestRunner.Check(rig.Module.SignedIn, "a saved refresh token means signed in");
            long before = Clock.NowMs();
            rig.Module.Tick(before);
            rig.Settle();
            CalendarSnapshot s = rig.Module.Snapshot;
            TestRunner.Check(s.Loaded && !s.LastFailed && s.Events.Count == 11, "the selected calendars' events are read: " + s.Events.Count);
            TestRunner.Check(rig.TokenCalls == 1 && rig.Google.Last != null && rig.Google.Last.Body.Contains("grant_type=refresh_token"), "with an access token from the refresh token first");
            TestRunner.Check(rig.Api.Requests.All(r => r.Headers["Authorization"] == "Bearer ya29.sample"), "and that token on every request");
            TestRunner.Eq(0, rig.Api.Count(FakeCalendarApi.EventsPathOf(GoogleCalendarTests.Holidays)), "a calendar not selected in Google isn't read");
            GoogleSettings settings = rig.Module.Settings();
            TestRunner.Check(settings.Account == GoogleCalendarTests.Primary && settings.SignedIn && settings.ClientId == "cid-sample" && settings.SecretSaved, "the account is the primary calendar's id");
            TestRunner.Eq("True,True,False,True", string.Join(",", settings.Calendars.Select(c => c.Shown.ToString())), "a box per calendar, ticked as Google shows them");
            TestRunner.Check(rig.Module.DueMs >= before + CalendarModule.EveryMs - 1000 && rig.Module.DueMs <= Clock.NowMs() + CalendarModule.EveryMs, "the next pass in 5 minutes");
            Task first = rig.Module.LastPass;
            rig.Module.Tick(Clock.NowMs());
            TestRunner.Check(rig.Module.LastPass == first, "not sooner");
            rig.Module.Tick(rig.Module.DueMs);
            rig.Settle();
            TestRunner.Check(rig.Module.LastPass != first && rig.TokenCalls == 1, "then again, with the access token it already has");
            string log = (Files.ReadText(Paths.LogFile) ?? "");
            TestRunner.Check(log.Contains("calendar: 11 events from 2 calendars"), "the log gets counts");
            TestRunner.Check(!log.Contains("Standup") && !log.Contains("Family") && !log.Contains("ya29") && !log.Contains("1//sample") && !log.Contains("csecret"), "and never a title, a calendar's name, a token or the secret");
        }

        static void ChoicesOverrideGooglesSelection()
        {
            var rig = new Rig(true).Working();
            rig.Api.Reply(FakeCalendarApi.EventsPathOf(GoogleCalendarTests.Holidays), 200, "{\"items\": []}");
            rig.Choices[GoogleCalendarTests.Holidays] = true;
            rig.Choices[GoogleCalendarTests.Family] = false;
            rig.Module.Refresh();
            rig.Settle();
            TestRunner.Check(rig.Api.Count(FakeCalendarApi.EventsPathOf(GoogleCalendarTests.Holidays)) == 1 && rig.Api.Count(FakeCalendarApi.EventsPathOf(GoogleCalendarTests.Family)) == 0, "a box the user ticked or unticked wins over Google's selection");
            TestRunner.Eq(10, rig.Module.Snapshot.Events.Count, "so Family's dinner is gone");
            TestRunner.Check(CalendarModule.Shown(new GoogleCalendar { Id = "x", Selected = true }, new Dictionary<string, bool>()) && !CalendarModule.Shown(new GoogleCalendar { Id = "x", Selected = true }, new Dictionary<string, bool> { { "x", false } }), "an untouched calendar follows Google; a touched one, the user");
        }

        static void A401RefreshesOnce()
        {
            var rig = new Rig(true).Working();
            rig.Api.Reply(FakeCalendarApi.ListPath, 401, "{}").Reply(FakeCalendarApi.ListPath, 200, TestRunner.Fixture("google-calendar-list.json"));
            rig.Module.Refresh();
            rig.Settle();
            TestRunner.Eq(1, rig.TokenCalls, "the first pass refreshes once, for its first access token");
            rig.Module.Refresh();
            rig.Settle();
            TestRunner.Check(rig.TokenCalls == 2 && !rig.Module.Snapshot.LastFailed && rig.Module.Snapshot.Events.Count == 11, "a 401 refreshes the access token once, and the pass goes on");
            var always = new Rig(true).Working();
            always.Api.Reply(FakeCalendarApi.ListPath, 401, "{}");
            always.Api.Requests.Clear();
            always.Transport = r => r.Path.StartsWith(FakeCalendarApi.ListPath, StringComparison.Ordinal) ? new HttpResult { Status = 401 } : always.Api.Answer(r);
            always.Module.Refresh();
            always.Settle();
            always.Module.Refresh();
            always.Settle();
            TestRunner.Check(always.TokenCalls == 2 && always.Module.SignedIn && always.Module.Snapshot.LastFailed && always.Module.Snapshot.Problem == "Google didn't accept the sign-in", "a 401 after a fresh token is a failure, not a loop, and doesn't sign out");
        }

        static void ARejectedRefreshTokenSignsOut()
        {
            var fresh = new Rig(true);
            fresh.Google.Reply("/token", 400, "{\"error\": \"invalid_grant\", \"error_description\": \"Token has been expired or revoked.\"}");
            int told = 0;
            fresh.Module.SignedOutByGoogle += delegate { told++; };
            fresh.Module.Tick(Clock.NowMs());
            fresh.Settle();
            TestRunner.Check(told == 1, "Google rejecting the refresh token is told (Controller shows a balloon)");
            TestRunner.Check(!fresh.Module.SignedIn && !File.Exists(fresh.TokenPath), "and signs out: the token file is gone");
            TestRunner.Eq("Google ended the sign-in. Sign in again.", fresh.Module.Settings().Status, "the settings say why");
            TestRunner.Check(fresh.Module.Tile(Clock.NowMs(), System.Globalization.CultureInfo.GetCultureInfo("en-US")).Hint == CalendarDay.SignInHint && fresh.Api.Requests.Count == 0, "the tile asks for a sign-in, and the Calendar API was never asked");
        }

        static long Wait(Rig rig, long before) { return rig.Module.DueMs - before; }

        static void FailuresBackOff()
        {
            var rig = new Rig(true).Working();
            rig.Api.Reply(FakeCalendarApi.ListPath, 429, "{}").Reply(FakeCalendarApi.ListPath, 0, "").Reply(FakeCalendarApi.ListPath, 503, "{}");
            rig.Module.Refresh();
            rig.Settle();
            long before = Clock.NowMs();
            rig.Module.Refresh();
            rig.Settle();
            CalendarSnapshot s = rig.Module.Snapshot;
            TestRunner.Check(s.LastFailed && s.Problem == "Google asked Capsule to slow down" && s.Events.Count == 11, "a 429 keeps the last events, and says so");
            TestRunner.Check(Wait(rig, before) >= 59000 && Wait(rig, before) <= 61000 + (Clock.NowMs() - before), "and waits a minute, as the usage checks do: " + Wait(rig, before));
            before = Clock.NowMs();
            rig.Module.Refresh();
            rig.Settle();
            TestRunner.Check(rig.Module.Snapshot.Problem == "Couldn't reach Google" && Wait(rig, before) >= 19000 && Wait(rig, before) <= 21000 + (Clock.NowMs() - before), "no reply at all: tried again in 20 s");
            Task last = rig.Module.LastPass;
            rig.Module.NetworkBack();
            TestRunner.Check(rig.Module.LastPass != last, "or as soon as the network is back");
            rig.Settle();
            before = Clock.NowMs();
            rig.Module.Refresh();
            rig.Settle();
            TestRunner.Check(rig.Module.Snapshot.Problem == "Google is having trouble (HTTP 503)" && Wait(rig, before) >= CalendarModule.EveryMs - 1000, "a server error waits the usual 5 minutes");
            last = rig.Module.LastPass;
            rig.Module.NetworkBack();
            TestRunner.Check(rig.Module.LastPass == last, "and the network coming back doesn't hurry it");
        }

        static void SignOutForgetsAndRevokes()
        {
            var rig = new Rig(true).Working();
            rig.Google.Reply("/revoke", 200, "");
            rig.Module.Refresh();
            rig.Settle();
            rig.Module.SignOut();
            TestRunner.Check(!rig.Module.SignedIn && !File.Exists(rig.TokenPath) && rig.Module.Snapshot.Events.Count == 0, "signing out deletes the token and forgets the events");
            TestRunner.Check(rig.Module.Settings().Account == "" && rig.Module.Settings().Calendars.Count == 0, "and the account and calendars");
            TestRunner.Check(rig.Module.LastRevoke != null && rig.Module.LastRevoke.Wait(5000) && rig.Google.Last != null && rig.Google.Last.Path == "/revoke" && rig.Google.Last.Body == "token=1%2F%2Fsample", "Google is asked to revoke the refresh token");
            Task last = rig.Module.LastPass;
            rig.Module.Tick(Clock.NowMs());
            rig.Module.PanelOpened(Clock.NowMs());
            TestRunner.Check(rig.Module.LastPass == last, "and nothing is read any more");
            TestRunner.Check(File.Exists(rig.SecretPath), "the client stays, for the next sign-in");
        }

        static void APassThatEndsAfterSignOutIsDropped()
        {
            var rig = new Rig(true).Working();
            var gate = new ManualResetEvent(false);
            rig.Transport = r =>
            {
                gate.WaitOne(5000);
                return rig.Api.Answer(r);
            };
            rig.Module.Tick(Clock.NowMs());
            rig.Module.SignOut();
            gate.Set();
            rig.Settle();
            TestRunner.Check(!rig.Module.SignedIn && rig.Module.Snapshot.Events.Count == 0 && rig.Module.Settings().Account == "", "a pass that ends after sign-out leaves nothing behind");
        }

        static void SignInRunsThroughTheBrowser()
        {
            var rig = new Rig(false).Working();
            File.Delete(rig.SecretPath);
            rig.ClientId = "";
            TestRunner.Check(!rig.Module.SignIn(" ", "csecret") && rig.Module.Settings().Status == "Enter your OAuth client's ID", "no client id typed or kept: nothing starts");
            TestRunner.Check(!rig.Module.SignIn("cid-sample", "") && rig.Module.Settings().Status == "Enter your OAuth client's secret", "no secret saved or given: nothing starts");
            string kept = null;
            rig.Module.SignedInWith += delegate(string id) { kept = id; rig.ClientId = id; };
            TestRunner.Check(rig.Module.SignIn(" cid-new ", " csecret-new ") && rig.Module.SigningIn && rig.Module.Settings().Status == "Waiting for you in the browser…", "with both, the browser opens and the settings say so");
            TestRunner.Check(!File.Exists(rig.SecretPath), "the new client secret isn't kept before the sign-in works");
            rig.Module.LastSignIn.Wait(10000);
            rig.Settle();
            TestRunner.Eq("csecret-new", SecretStore.Load(rig.SecretPath, SecretStore.GoogleClientPurpose), "then it is kept, encrypted, trimmed");
            TestRunner.Eq("cid-new", kept, "and the client ID is handed over to be kept");
            TestRunner.Check(rig.Module.SignedIn && !rig.Module.SigningIn && SecretStore.Load(rig.TokenPath, SecretStore.GoogleTokenPurpose) == "1//sample", "signed in: the refresh token is kept, encrypted");
            TestRunner.Check(rig.Module.Snapshot.Loaded && rig.Module.Snapshot.Events.Count == 11 && rig.TokenCalls == 1, "and the calendars are read at once with the access token the sign-in brought");
            TestRunner.Check((Files.ReadText(Paths.LogFile) ?? "").Contains("calendar: signed in"), "the log says signed in");

            var other = new Rig(true).Working();
            other.Module.Refresh();
            other.Settle();
            other.Module.NewAuth = (id, secret) =>
            {
                var auth = new GoogleAuth(id, secret);
                auth.OpenBrowser = delegate(string url)
                {
                    Dictionary<string, string> q = GoogleOAuth.ParseQuery(url);
                    Task.Run(() => Http.Get(q["redirect_uri"] + "?error=access_denied&state=" + Uri.EscapeDataString(q["state"]), new Dictionary<string, string>(), 5000));
                };
                return auth;
            };
            bool handed = false;
            other.Module.SignedInWith += delegate { handed = true; };
            other.Module.SignIn("another-client", "another-secret");
            other.Module.LastSignIn.Wait(10000);
            TestRunner.Check(other.Module.SignedIn && !handed && SecretStore.Load(other.SecretPath, SecretStore.GoogleClientPurpose) == "csecret-sample" && SecretStore.Load(other.TokenPath, SecretStore.GoogleTokenPurpose) == "1//sample",
                "a sign-in with another client that fails leaves the working one as it was");
            TestRunner.Eq("Sign-in was cancelled in the browser", other.Module.Settings().Status, "and says why");
        }

        // Google's client secret and refresh token are DPAPI-encrypted like the Notion secret, each under its own
        // purpose, so one file can't be read back as another kind of secret.
        static void SecretsKeepTheirPurpose()
        {
            string dir = TestRunner.NewTempDir();
            string token = Path.Combine(dir, "google-token.bin");
            TestRunner.Check(SecretStore.Save(token, "1//refresh-sample", SecretStore.GoogleTokenPurpose, "calendar"), "a refresh token is saved");
            TestRunner.Check(!File.ReadAllText(token).Contains("refresh-sample"), "and isn't on disk in plain text");
            TestRunner.Eq("1//refresh-sample", SecretStore.Load(token, SecretStore.GoogleTokenPurpose), "it reads back under its own purpose");
            TestRunner.Eq(null, SecretStore.Load(token, SecretStore.GoogleClientPurpose), "but not as a client secret");
            TestRunner.Eq(null, SecretStore.Load(token), "nor as the Notion secret");
            string notion = Path.Combine(dir, "notion-secret.bin");
            SecretStore.Save(notion, "ntn_sample");
            TestRunner.Eq("ntn_sample", SecretStore.Load(notion, SecretStore.NotionPurpose), "the Notion overloads keep the Notion purpose");
            TestRunner.Check(SecretStore.Delete(token) && !SecretStore.Exists(token), "Delete removes the file");
            TestRunner.Check(SecretStore.Delete(token), "and deleting what is already gone is fine");
        }

        static void ConfigKeepsTheGoogleSettings()
        {
            string path = Path.Combine(TestRunner.NewTempDir(), "config.json");
            Config c = Config.Load(path);
            TestRunner.Check(c.GoogleClientId == "" && c.CalendarChoices.Count == 0, "no Google client and no choices by default");
            c.GoogleClientId = "123-abc.apps.googleusercontent.com";
            c.CalendarChoices["work@example.com"] = false;
            c.CalendarChoices["family#group@group.calendar.google.com"] = true;
            TestRunner.Check(c.Save(path), "saved");
            Config back = Config.Load(path);
            TestRunner.Eq("123-abc.apps.googleusercontent.com", back.GoogleClientId, "the client id comes back");
            TestRunner.Check(back.CalendarChoices.Count == 2 && back.CalendarChoices["work@example.com"] == false && back.CalendarChoices["family#group@group.calendar.google.com"], "and so do the choices");
            File.WriteAllText(path, "{\"google_client_id\": 7, \"google_calendars\": {\"a\": true, \"b\": \"yes\"}}");
            Config odd = Config.Load(path);
            TestRunner.Check(odd.GoogleClientId == "" && odd.CalendarChoices.Count == 1 && odd.CalendarChoices["a"], "odd values are ignored, good ones kept");
        }
    }
}
