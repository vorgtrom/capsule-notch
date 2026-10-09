using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Capsule
{
    // Runs the app on the UI thread: ticks the modules (Claude and Codex usage, Claude Code sessions, Google Calendar,
    // ideas), and keeps the notch, the hover card and the tray icon up to date. It also holds Claude Code's permission
    // prompts that the hook passes on, and shows the oldest on Claude's card (act-from-notch spec §3).
    public sealed class Controller : IDisposable
    {
        const long ClaudeActiveMs = 60 * 1000, ClaudeIdleMs = 300 * 1000, CodexEveryMs = 300 * 1000;
        const int CmdRefresh = 1, CmdShow = 2, CmdHooks = 3, CmdAutostart = 4, CmdFolder = 5, CmdQuit = 6, CmdUpdate = 7, CmdCheckUpdates = 8;

        readonly Application app;
        Config config;
        UsageModule claude, codex;
        SessionsModule sessions;
        IdeasModule ideas;
        CalendarModule calendar;
        UpdateCheck updates;   // a release build's once-a-day look for a newer release
        CodexActivity codexActivity;
        List<IModule> modules;
        NotchWindow notch;
        HoverCard card;
        PanelWindow panel;
        GlobalHotkey shortcut;
        Tray tray;
        MenuOwner menuOwner;
        PromptBroker prompts;        // the requests held, and their rules
        PromptServer promptServer;   // the pipe the hook asks through
        DispatcherTimer tick, screenCheck, openCard, closeCard, codexCheck, promptTick;
        List<UsageModule> shown = new List<UsageModule>();
        string lastActivity;
        bool fullscreen, signInOpen, menuOpen;
        bool codexWorking, codexChecking;   // a Codex turn is running; a check of Codex's logs is under way
        string codexFault;                  // the exception type the last logged fault of that check had: logged once, not every 2 s
        int notionChecks;   // counts the clicks on Save and test, so that only the latest one's result is shown
        bool blurWorks;   // live blur is available on this PC and its windows could be made
        bool disposed;    // on the way out: nothing is drawn any more
        int hoverCell = -1, cardCell = -1;
        int calendarCell = -1;   // the calendar's cell on the capsule, under the usage cells; -1 while signed out
        int monthYear, monthMonth;   // the month page's month, while it shows
        DateTime monthDay;           // and its selected day

        public Controller(Application app) { this.app = app; }

        public void Start()
        {
            config = Config.Load(Paths.ConfigFile);
            if (!config.FirstRunDone)
            {
                try { Autostart.Set(true, ExePath()); }
                catch (Exception e) { Log.Error("start with Windows", e); }
                config.FirstRunDone = true;
                config.Save(Paths.ConfigFile);
            }
            Dictionary<string, Reading> cached = ReadingsCache.Load(Paths.ReadingsFile);
            Reading cachedClaude, cachedCodex;
            cached.TryGetValue("claude", out cachedClaude);
            cached.TryGetValue("codex", out cachedCodex);
            sessions = new SessionsModule(Paths.SessionsDir, Paths.ClaudeSettings);
            sessions.Changed += OnSessionsChanged;
            claude = new UsageModule(new ClaudeUsage(Paths.ClaudeCredentials, cachedClaude), () => sessions.Activity != null ? ClaudeActiveMs : ClaudeIdleMs);
            codex = new UsageModule(new CodexUsage(Paths.CodexHome, cachedCodex), () => CodexEveryMs);
            claude.Changed += OnUsageChecked;
            codex.Changed += OnUsageChecked;
            ideas = new IdeasModule(Paths.IdeasQueueFile, Paths.IdeasDiscardedFile, Paths.NotionSecretFile, () => config.NotionDatabase, TaskScheduler.FromCurrentSynchronizationContext());
            ideas.Changed += delegate { if (panel.IsOpen) panel.Refresh(PanelNow()); };
            calendar = new CalendarModule(Paths.GoogleTokenFile, Paths.GoogleClientSecretFile, Paths.GoogleLinkFile, () => config.GoogleClientId, () => config.CalendarChoices, TaskScheduler.FromCurrentSynchronizationContext());
            calendar.Changed += delegate
            {
                Guard("render", Render);
                ShowGoogleIfOpen();
                Guard("month", ShowMonthIfOpen);
            };
            calendar.SignedOutByGoogle += delegate { tray.Balloon("Capsule", "Google ended Capsule's access to your calendar. Sign in again in Capsule's settings (⚙ in the panel)."); };
            calendar.SignedInWith += OnGoogleSignedIn;
            calendar.ClientIdChosen += OnGoogleClientIdChosen;
            modules = new List<IModule> { sessions, claude, codex, calendar, ideas };
            updates = new UpdateCheck(AppVersion.Current, Clock.NowMs(), TaskScheduler.FromCurrentSynchronizationContext()) { Enabled = config.CheckUpdates };
            updates.Found += delegate { if (!disposed) tray.Balloon("Capsule", "Capsule " + updates.Available + " is available. Choose it in Capsule's menu to open its download page."); };
            codexActivity = new CodexActivity(Paths.CodexHome);
            lastActivity = sessions.Activity;
            blurWorks = GlassLayer.Supported;   // asked here, on the UI thread, which then runs the blur
            Theme.Refresh(blurWorks);
            Theme.Changed += OnThemeChanged;
            GlassLayer.Failed += OnGlassLayerFailed;

            openCard = NewTimer(150, OpenCard);
            closeCard = NewTimer(200, CloseCardIfAway);
            tick = NewTimer(5000, Tick);
            screenCheck = NewTimer(2000, CheckScreen);
            codexCheck = NewTimer(2000, CheckCodex);
            promptTick = NewTimer(250, delegate { Guard("prompts", PromptTick); });   // runs only while a request is held

            // Capsule steps aside while the Claude app's window is in front, and while the capsule is hidden (turned off,
            // or under a fullscreen app): the card can't be seen then, and holding would only delay the app's own prompt.
            prompts = new PromptBroker(() => !notch.IsVisible || ClaudeApp.IsInFront());
            prompts.Changed += delegate { Guard("prompts", OnPromptsChanged); };

            menuOwner = new MenuOwner();
            tray = new Tray();
            tray.LeftClick += delegate { Guard("tray click", ToggleNotch); };
            tray.RightClick += delegate { Guard("tray menu", ShowMenu); };
            card = new HoverCard();
            card.MouseEnter += delegate
            {
                closeCard.Stop();
                if (card.IsClosing && cardCell >= 0) Guard("card", delegate { ShowCard(cardCell); });   // back onto a folding card: it turns round
            };
            card.MouseLeave += delegate { closeCard.Start(); };
            card.Deactivated += delegate { closeCard.Start(); };   // it had the keyboard for Other…, and you clicked elsewhere
            card.View.Prompt.Acted += delegate(PromptAction action) { Guard("prompts", delegate { OnPromptActed(action); }); };
            notch = new NotchWindow();
            notch.Clicked += delegate { Guard("panel", TogglePanel); };
            notch.HoverChanged += OnHover;
            notch.RightClicked += ShowMenu;
            notch.Moved += OnMoved;
            panel = new PanelWindow();
            panel.View.RefreshAllClicked += delegate { Guard("refresh", RefreshAll); };
            panel.View.RefreshClicked += delegate(string provider) { (provider == "codex" ? codex : claude).Refresh(); };
            panel.View.SignInClicked += SignIn;
            panel.View.IdeaSubmitted += OnIdeaSubmitted;
            panel.View.IdeaOpened += OpenIdea;
            panel.View.DiscardClicked += delegate { ideas.Discard(); };
            panel.View.SettingsClicked += delegate
            {
                panel.View.ShowSettings(SecretStore.Exists(Paths.NotionSecretFile), config.NotionDatabase, config.Hotkey);
                panel.View.ShowGoogle(calendar.Settings());
            };
            panel.View.NotionSaved += OnNotionSaved;
            panel.View.ShortcutChosen += OnShortcutChosen;
            panel.View.GoogleSignInClicked += delegate(string id, string secret) { Guard("calendar sign-in", delegate { calendar.SignIn(id, secret); }); };
            panel.View.GoogleSignInCancelled += delegate
            {
                calendar.CancelSignIn();
                ShowGoogleIfOpen();
            };
            panel.View.GoogleSignOutClicked += delegate { Guard("calendar sign-out", calendar.SignOut); };
            panel.View.CalendarToggled += OnCalendarToggled;
            panel.View.MonthClicked += delegate { Guard("month", OpenMonth); };
            panel.View.MonthClosed += delegate { calendar.CloseMonth(); };
            MonthView page = panel.View.Month;
            page.PreviousClicked += delegate { Guard("month", delegate { MoveMonth(-1); }); };
            page.NextClicked += delegate { Guard("month", delegate { MoveMonth(1); }); };
            page.TodayClicked += delegate { Guard("month", OpenMonth); };
            page.DaySelected += delegate(DateTime d) { Guard("month", delegate { SelectDay(d); }); };
            page.AddEventRequested += delegate(string title, bool allDay, string start, string end, string calendarId) { Guard("month", delegate { AddEvent(title, allDay, start, end, calendarId); }); };
            page.AddTaskRequested += delegate(string title, string listId) { Guard("month", delegate { AddTask(title, listId); }); };
            page.TaskToggled += delegate(string listId, string taskId, bool done) { Guard("month", delegate { calendar.SetTaskDone(listId, taskId, done, RowResult); }); };
            page.EditEventRequested += delegate(string calendarId, string eventId, string title, bool allDay, string start, string end) { Guard("month", delegate { EditEvent(calendarId, eventId, title, allDay, start, end); }); };
            page.EditTaskRequested += delegate(string listId, string taskId, string title) { Guard("month", delegate { if (calendar.RenameTask(listId, taskId, title, FormResult)) panel.View.Month.FormBusy(); }); };
            page.DeleteEventRequested += delegate(string calendarId, string eventId) { Guard("month", delegate { calendar.DeleteEvent(calendarId, eventId, RowResult); }); };
            page.DeleteTaskRequested += delegate(string listId, string taskId) { Guard("month", delegate { calendar.DeleteTask(listId, taskId, RowResult); }); };
            panel.View.GoogleLinkSaved += delegate(string text) { Guard("calendar link", delegate { if (calendar.SaveLink(text)) panel.View.LinkKept(); }); };
            panel.View.GoogleLinkRemoved += delegate { Guard("calendar link", calendar.RemoveLink); };
            panel.View.GoogleClientDraft += delegate(string id, string secret) { Guard("calendar client", delegate { calendar.KeepClient(id, secret); }); };
            shortcut = new GlobalHotkey();
            shortcut.Pressed += delegate { Guard("shortcut", delegate { OpenPanel(true); }); };
            RegisterShortcut(config.Hotkey);
            if (sessions.HooksConnected && HookSetup.NeedsUpdate(Paths.ClaudeSettings))
            {
                // Its permission hook is stopped after 5 s: a request would leave the card before it could be answered.
                Log.Info("hooks: connected by an earlier Capsule; Reconnect to answer Claude from the capsule");
                tray.Balloon("Capsule", "To answer Claude from the capsule, choose Reconnect to Claude Code in Capsule's menu.");
            }

            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;

            notch.Create();
            Render();
            notch.Place(config.Edge, config.Monitor, config.Y);
            ApplyGlass();
            ApplyVisibility();
            tick.Start();
            screenCheck.Start();
            codexCheck.Start();
            Tick();
            promptServer = new PromptServer(PromptPipe.Name, OnPromptArrived);
            Log.Info(promptServer.Start() ? "prompts: listening" : "prompts: not listening; Claude asks in the app as usual");
            Log.Info("started");
        }

        static DispatcherTimer NewTimer(int ms, Action action)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            timer.Tick += delegate { action(); };
            return timer;
        }

        // Tray clicks arrive through NotifyIcon's window procedure, and check results through a task continuation;
        // an exception in either would vanish without a log line.
        internal static void Guard(string what, Action action)
        {
            try { action(); }
            catch (Exception e) { Log.Error(what, e); }
        }

        void OnSessionsChanged()
        {
            // A session just started working: check Claude every minute from now on, not in five.
            string activity = sessions.Activity;
            if (lastActivity == null && activity != null) claude.CheckBy(Clock.NowMs() + ClaudeActiveMs);
            lastActivity = activity;
            prompts.SessionsChanged(sessions.All);   // a session that moved on was answered in the app: Capsule lets go
            Render();
        }

        void OnUsageChecked()
        {
            SaveReadings();
            Guard("render", Render);
        }

        void Tick()
        {
            long now = Clock.NowMs();
            foreach (IModule m in modules) m.Tick(now);
            Guard("updates", delegate { updates.Tick(now); });
            lastActivity = sessions.Activity;
            prompts.SessionsChanged(sessions.All);
            Render();
        }

        // A hook's request, on the pipe's worker thread: held (or passed straight back) on the UI thread. If anything
        // fails on the way, it goes back to the app.
        void OnPromptArrived(PromptRequest request, PromptLink link)
        {
            app.Dispatcher.BeginInvoke(new Action(delegate
            {
                try
                {
                    if (disposed)
                    {
                        link.Send(PromptReply.Passing());
                        return;
                    }
                    HeldPrompt held = prompts.Arrive(request, delegate(PromptReply reply) { link.Send(reply); }, Clock.NowMs());
                    if (held == null) return;
                    int id = held.Id;
                    link.WhenGone(delegate { app.Dispatcher.BeginInvoke(new Action(delegate { Guard("prompts", delegate { prompts.Gone(id); }); })); });
                }
                catch (Exception e)
                {
                    Log.Error("prompts: a request couldn't be held (" + e.GetType().Name + ")", null);
                    link.Send(PromptReply.Passing());
                }
            }));
        }

        // The requests held changed: the countdown runs while there are any, and Claude's open card shows the oldest, or
        // its usage again once none is left.
        void OnPromptsChanged()
        {
            if (prompts.Count == 0) promptTick.Stop();
            else if (!promptTick.IsEnabled) promptTick.Start();
            if (disposed) return;
            card.RequestsHeld = prompts.Count > 0;
            if (prompts.Count == 0 && !card.IsVisible) card.View.Prompt.Clear();   // released while the card was away: no text left in it
            if (card.IsOpen && cardCell >= 0 && cardCell < shown.Count && shown[cardCell] == claude) ShowCard(cardCell);
        }

        // Every 250 ms while a request is held: the Claude app coming to the front, or a countdown ending, sends requests to
        // the app; the card's countdown line follows.
        void PromptTick()
        {
            long now = Clock.NowMs();
            prompts.Tick(now);
            ShowCountdown(now);
        }

        void ShowCountdown(long now)
        {
            if (card.IsOpen && card.View.ShowingPrompt && prompts.Current != null)
                card.View.Prompt.SetCountdown(PromptCardModel.CountdownText(prompts.MsLeft(now)));
        }

        // Something done on the card, passed on exactly as the card raised it: it names the request it was built for, and
        // the broker ignores it when that is no longer the one shown. The broker redraws the card when the request
        // changes; Other… then takes the keyboard, and typing only relights Next or Send. Those two are only for the
        // request the action named, never for whichever request has come to the front since.
        void OnPromptActed(PromptAction action)
        {
            long now = Clock.NowMs();
            prompts.Act(action, now);
            HeldPrompt held = prompts.Current;
            QuestionFlow flow = held != null && held.Id == action.Target ? held.Flow : null;
            if (action.Kind == PromptAction.Other && flow != null && flow.OtherOpen) card.TakeFocus();
            if (action.Kind == PromptAction.OtherText && flow != null) card.View.Prompt.SetCanAdvance(flow.CanAdvance);
            // Every other action redraws the card (a new request, the next question, a choice), which brings its own
            // countdown; only these two restart it without a redraw.
            if (action.Kind == PromptAction.Touch || action.Kind == PromptAction.OtherText) ShowCountdown(now);
        }

        // Every 2 s, on a worker thread: whether a Codex turn is running, from the turn events in Codex's session logs
        // (polish spec §3). Codex's ring spins while one is.
        void CheckCodex()
        {
            if (codexChecking) return;   // the last check is still reading
            codexChecking = true;
            long now = Clock.NowMs();
            Task.Run(() => codexActivity.Check(now)).ContinueWith(t =>
            {
                codexChecking = false;
                if (t.IsFaulted)   // Check doesn't throw; if it ever did, the log gets the type only, the first time and when it changes
                {
                    string type = t.Exception.GetBaseException().GetType().Name;
                    if (type != codexFault)
                    {
                        codexFault = type;
                        Log.Error("codex activity: " + type, null);
                    }
                    return;   // the last answer stands
                }
                if (t.Result == codexWorking) return;
                codexWorking = t.Result;
                Guard("render", Render);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        // The network is back (Wi-Fi reconnecting after sleep, say): modules whose last try got no reply try again now.
        void OnNetworkAvailabilityChanged(object sender, NetworkAvailabilityEventArgs e)
        {
            if (!e.IsAvailable) return;
            app.Dispatcher.BeginInvoke(new Action(delegate
            {
                foreach (IModule m in modules) m.NetworkBack();
            }));
        }

        void SaveReadings()
        {
            try { ReadingsCache.Save(Paths.ReadingsFile, new[] { claude.Current, codex.Current }); }
            catch (Exception e) { Log.Error("save readings", e); }
        }

        void Render()
        {
            if (disposed) return;   // a check that ends while the way out pumps the dispatcher (GlassLayer.Stop)
            long now = Clock.NowMs();
            string activity = sessions.Activity;
            shown = new List<UsageModule> { claude };
            if (codex.Current.Status != "none") shown.Add(codex);
            string codexActivityNow = codexWorking ? States.Working : null;
            List<CellModel> cells = shown.Select(m => m.Cell(m == codex ? codexActivityNow : activity, now)).ToList();
            calendarCell = -1;
            if (calendar.Connected)   // under Claude and Codex, while there is a link or a sign-in (calendar spec §2)
            {
                calendarCell = cells.Count;
                cells.Add(calendar.Cell(now, CultureInfo.CurrentCulture));
            }
            if (card.IsVisible && cardCell >= cells.Count) HideCard();   // its cell is gone (signed out, say)
            notch.SetCells(cells);
            LimitWindow headline = claude.Current.DisplayWindow;
            tray.Update(headline != null ? (double?)headline.Used : null, TrayText());
            // Not while it folds: that would turn it round. Nor while it shows a request: OnPromptsChanged redraws that.
            if (card.IsOpen && cardCell >= 0 && !card.View.ShowingPrompt) ShowCard(cardCell);
            if (panel.IsOpen) panel.Refresh(PanelNow());
        }

        // The capsule back above the panel and the card. Their shadow room reaches over its inner edge, and a window that
        // was just shown or activated sits above the capsule until this runs, so a click on that edge would hit the
        // shadow instead of the capsule. Not while its menu is open: that would lift it over the menu.
        void NotchOnTop()
        {
            if (notch.IsVisible && !menuOpen) notch.KeepOnTop();
        }

        PanelModel PanelNow()
        {
            long now = Clock.NowMs();
            return new PanelModel
            {
                Claude = claude.Tile(now, CultureInfo.CurrentCulture),
                Codex = codex.Tile(now, CultureInfo.CurrentCulture),
                Sessions = sessions.Tile(now),
                Calendar = calendar.Tile(now, CultureInfo.CurrentCulture),
                Ideas = ideas.Tile(),
            };
        }

        void TogglePanel()
        {
            if (panel.IsOpen) panel.Dismiss();
            else OpenPanel(false);
        }

        // Beside the capsule, wherever it is (even hidden under a fullscreen app, for the shortcut).
        void OpenPanel(bool byShortcut)
        {
            HideCard();
            RECT capsule;
            Native.GetWindowRect(notch.Handle, out capsule);
            panel.Open(PanelNow(), capsule, notch.LeftEdge, notch.Monitor, byShortcut);
            NotchOnTop();
            ideas.PanelOpened();
            calendar.PanelOpened(Clock.NowMs());
        }

        void RefreshAll()
        {
            foreach (IModule m in modules) m.Refresh();
            Render();
        }

        // Enter in the Ideas box: queue the idea. A quick capture (the panel was opened by the shortcut) then closes
        // the panel; opened by a click, it stays open.
        void OnIdeaSubmitted(string text)
        {
            if (!ideas.Add(text)) return;
            panel.View.ClearIdea();
            if (panel.OpenedByShortcut) panel.Dismiss();
        }

        // Settings' Save and test: keep a new secret (encrypted) and the database, then check them with Notion. The secret
        // is kept first and on its own: a link that is refused must not lose a secret pasted along with it.
        void OnNotionSaved(string secret, string link)
        {
            int check = ++notionChecks;   // a slower test that ends after a newer click mustn't show its result over that click's
            bool newSecret = secret.Trim() != "";
            if (newSecret)
            {
                if (!SecretStore.Save(Paths.NotionSecretFile, secret.Trim()))
                {
                    panel.View.ShowNotionResult(false, "Couldn't save the secret");   // the box keeps what was pasted
                    return;
                }
                Log.Info("notion: secret saved");
                panel.View.SecretSaved();
            }
            string id = NotionLink.DatabaseId(link);
            if (link.Trim() != "" && id == null)
            {
                if (newSecret) ideas.SettingsChanged();   // the new secret goes with the database that was already set up
                panel.View.ShowNotionResult(false, "That isn't a link to a Notion database");
                return;
            }
            if (id != null && id != config.NotionDatabase)
            {
                string previous = config.NotionDatabase;
                config.NotionDatabase = id;
                if (!config.Save(Paths.ConfigFile))
                {
                    config.NotionDatabase = previous;   // not kept on disk, so not used either: the sheet and the next start must agree
                    Log.Info("notion: couldn't write config.json; the database was not saved");
                    if (newSecret) ideas.SettingsChanged();
                    panel.View.ShowNotionResult(false, "Couldn't save the database link. Try again");
                    return;
                }
                Log.Info("notion: database saved");
            }
            string saved = SecretStore.Load(Paths.NotionSecretFile);
            if (saved == null)
            {
                panel.View.ShowNotionResult(false, "Paste your Notion connection's secret");
                return;
            }
            if (config.NotionDatabase == "")
            {
                panel.View.ShowNotionResult(false, "Paste the link to your ideas database");
                return;
            }
            ideas.SettingsChanged();
            panel.View.ShowNotionResult(null, "Testing…");
            string databaseId = config.NotionDatabase;
            Task.Run(() => TestNotion(saved, databaseId)).ContinueWith(t =>
            {
                Guard("notion test", delegate   // an exception in a continuation would vanish without a log line
                {
                    if (check != notionChecks) return;   // clicked again since: that click's result is the one to show
                    Tuple<bool, string> result = t.IsFaulted ? Tuple.Create(false, "Couldn't test the connection") : t.Result;
                    Log.Info("notion: test " + (result.Item1 ? "ok" : "failed: " + result.Item2));
                    panel.View.ShowNotionResult(result.Item1, result.Item2);
                });
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        // Reads the database's name with the secret: proof both work. The message is for the settings sheet.
        static Tuple<bool, string> TestNotion(string secret, string databaseId)
        {
            var client = new NotionClient(secret);
            NotionDatabase db = client.Describe(databaseId);
            if (db != null) return Tuple.Create(true, "Connected to “" + db.Name + "”");
            return Tuple.Create(false, NotionClient.Problem(client.LastFailure, false));
        }

        // ---- The month page (month spec) ----

        // Opens the month page on today, or goes back to today's month on it.
        void OpenMonth()
        {
            DateTime today = DateTime.Today;
            monthYear = today.Year;
            monthMonth = today.Month;
            monthDay = today;
            calendar.OpenMonth(monthYear, monthMonth, CultureInfo.CurrentCulture);
            ShowMonth();
        }

        void MoveMonth(int by)
        {
            var first = new DateTime(monthYear, monthMonth, 1).AddMonths(by);
            monthYear = first.Year;
            monthMonth = first.Month;
            monthDay = first.Year == DateTime.Today.Year && first.Month == DateTime.Today.Month ? DateTime.Today : first;
            calendar.OpenMonth(monthYear, monthMonth, CultureInfo.CurrentCulture);
            ShowMonth();
        }

        // A day of another month (dimmed in the grid) goes to its month.
        void SelectDay(DateTime d)
        {
            monthDay = d.Date;
            if (d.Year != monthYear || d.Month != monthMonth)
            {
                monthYear = d.Year;
                monthMonth = d.Month;
                calendar.OpenMonth(monthYear, monthMonth, CultureInfo.CurrentCulture);
            }
            ShowMonth();
        }

        void ShowMonth()
        {
            if (monthYear == 0) return;
            panel.View.ShowMonth(calendar.Month(monthYear, monthMonth, monthDay, Clock.NowMs(), CultureInfo.CurrentCulture));
        }

        void ShowMonthIfOpen()
        {
            if (!disposed && panel.IsOpen && panel.View.ShowingMonth) ShowMonth();
        }

        void AddEvent(string title, bool allDay, string start, string end, string calendarId)
        {
            TimeSpan from, to;
            string why = CalendarMonth.CheckEvent(title, allDay, start, end, CultureInfo.CurrentCulture, out from, out to);
            if (why != null)
            {
                panel.View.Month.FormError(why);
                return;
            }
            string body = CalendarMonth.EventBody(title, monthDay, allDay, from, to, TimeZoneInfo.Local);
            if (calendar.AddEvent(calendarId, body, FormResult)) panel.View.Month.FormBusy();
        }

        void EditEvent(string calendarId, string eventId, string title, bool allDay, string start, string end)
        {
            TimeSpan from, to;
            string why = CalendarMonth.CheckEvent(title, allDay, start, end, CultureInfo.CurrentCulture, out from, out to);
            if (why != null)
            {
                panel.View.Month.FormError(why);
                return;
            }
            string body = CalendarMonth.EventBody(title, monthDay, allDay, from, to, TimeZoneInfo.Local, true);
            CalendarEvent changed = CalendarMonth.EditedEvent(title, monthDay, allDay, from, to, TimeZoneInfo.Local);
            if (calendar.EditEvent(calendarId, eventId, body, changed, FormResult)) panel.View.Month.FormBusy();
        }

        void AddTask(string title, string listId)
        {
            if (calendar.AddTask(listId, title, monthDay, FormResult)) panel.View.Month.FormBusy();
        }

        // How the form's add or save went: done closes the form; otherwise a line says why not, in that form, under the day,
        // or in a balloon once the page is gone.
        void FormResult(string why)
        {
            if (disposed) return;
            MonthView page = panel.View.Month;
            if (why == null) page.ChangeDone(true);
            else if (page.FormOpen) page.FormError(why);
            else if (panel.IsOpen && panel.View.ShowingMonth) page.ShowProblem(why);
            else tray.Balloon("Capsule", why);
            ShowMonthIfOpen();
        }

        // How a tick or a delete went: a form open meanwhile is left alone; why not goes under the day, or in a balloon.
        void RowResult(string why)
        {
            if (disposed) return;
            MonthView page = panel.View.Month;
            if (why == null) page.ChangeDone(false);
            else if (panel.IsOpen && panel.View.ShowingMonth) page.ShowProblem(why);
            else tray.Balloon("Capsule", why);
            ShowMonthIfOpen();
        }

        // The settings' Google Calendar section follows the module while the settings show.
        void ShowGoogleIfOpen()
        {
            if (!disposed && panel.IsOpen && panel.View.ShowingSettings) panel.View.ShowGoogle(calendar.Settings());
        }

        // A sign-in worked with this client: config.json keeps its ID (its secret is kept, encrypted, by the module).
        void OnGoogleSignedIn(string clientId)
        {
            config.GoogleClientId = clientId;
            if (!config.Save(Paths.ConfigFile)) Log.Info("calendar: couldn't write config.json; the client ID is kept only until Capsule restarts");
            panel.View.ClientSecretKept();
        }

        // A client ID typed in while signed out: config.json keeps it, so it is there when the settings open again.
        void OnGoogleClientIdChosen(string clientId)
        {
            config.GoogleClientId = clientId;
            if (!config.Save(Paths.ConfigFile)) Log.Info("calendar: couldn't write config.json; the client ID is kept only until Capsule restarts");
        }

        // A calendar's box in the settings: kept in config.json (its id only), and the calendars read again.
        void OnCalendarToggled(string id, bool on)
        {
            config.CalendarChoices[id] = on;
            if (!config.Save(Paths.ConfigFile)) Log.Info("calendar: couldn't write config.json; the choice is kept only until Capsule restarts");
            calendar.ChoicesChanged();
        }

        // Settings' shortcut box: use the new shortcut if Windows lets Capsule have it, otherwise keep the old one.
        void OnShortcutChosen(string text)
        {
            uint modifiers, key;
            if (!HotkeyText.TryParse(text, out modifiers, out key)) return;
            if (shortcut.Register(modifiers, key))
            {
                config.Hotkey = text;
                bool kept = config.Save(Paths.ConfigFile);
                Log.Info("shortcut: " + text + (kept ? "" : " (couldn't write config.json: only until Capsule restarts)"));
                if (kept) panel.View.ShowShortcutResult(true, text, "Saved");
                else panel.View.ShowShortcutResult(false, text, "Works now, but couldn't be saved: it will be gone when Capsule restarts");
                return;
            }
            Log.Info("shortcut: " + text + " is taken by another app");
            uint oldModifiers, oldKey;
            if (HotkeyText.TryParse(config.Hotkey, out oldModifiers, out oldKey)) shortcut.Register(oldModifiers, oldKey);
            panel.View.ShowShortcutResult(false, config.Hotkey, text + " is used by another app. Try another.");
        }

        // The configured shortcut, registered with Windows. A balloon says so when another app has it.
        bool RegisterShortcut(string text)
        {
            uint modifiers, key;
            if (!HotkeyText.TryParse(text, out modifiers, out key))
            {
                Log.Info("shortcut: the saved shortcut isn't one Capsule accepts, so none is registered");
                return false;
            }
            if (shortcut.Register(modifiers, key))
            {
                Log.Info("shortcut: " + text);
                return true;
            }
            Log.Info("shortcut: " + text + " is taken by another app");
            tray.Balloon("Capsule", text + " is already used by another app. Pick another shortcut in Capsule's settings (⚙ in the panel).");
            return false;
        }

        // An idea's page, in the browser or the Notion app. Only Notion's own https links are opened, in their escaped
        // absolute form, not as the raw string.
        void OpenIdea(string url)
        {
            string page = NotionLink.PageUrl(url);
            if (page == null) return;
            try { System.Diagnostics.Process.Start(page); }
            catch (Exception e) { Log.Error("open idea", e); }
        }

        string TrayText()
        {
            var parts = new List<string>();
            foreach (UsageModule m in shown)
            {
                Reading r = m.Current;
                parts.Add((r.Provider == "codex" ? "Codex " : "Claude ") + (r.Status == "signin" ? "sign in" : Format.Percent(r.DisplayWindow)));
            }
            return string.Join(" · ", parts);
        }

        void OnHover(int cell)
        {
            hoverCell = cell;
            if (panel.IsOpen) return;   // the panel already shows everything
            if (cell < 0)
            {
                openCard.Stop();
                closeCard.Start();
                return;
            }
            closeCard.Stop();
            if (card.IsVisible) ShowCard(cell);
            else
            {
                openCard.Stop();
                openCard.Start();
            }
        }

        void OpenCard()
        {
            openCard.Stop();
            if (hoverCell >= 0) ShowCard(hoverCell);
        }

        // The mouse left the ring and the card: the card folds back into its ring. cardCell stays, so coming back during the
        // fold (onto a ring, or onto the card) turns it round.
        void CloseCardIfAway()
        {
            closeCard.Stop();
            if (hoverCell >= 0 || card.IsMouseOver || card.IsTyping) return;   // not while you type in Other…
            openCard.Stop();
            card.Fold();
        }

        void ShowCard(int cell)
        {
            if (!notch.IsVisible) return;
            long now = Clock.NowMs();
            if (cell >= 0 && cell == calendarCell)
            {
                cardCell = cell;
                card.ShowCalendarBeside(calendar.Card(now, CultureInfo.CurrentCulture), notch.CellScreenRect(cell), notch.LeftEdge, notch.Monitor);
                NotchOnTop();
                return;
            }
            if (cell >= shown.Count) return;
            cardCell = cell;
            if (shown[cell] == claude && prompts.Current != null)   // Claude waits on a request Capsule holds: the card shows it
                card.ShowPromptBeside(PromptCardModel.From(prompts.Current, prompts.Count, prompts.MsLeft(now)), notch.CellScreenRect(cell), notch.LeftEdge, notch.Monitor);
            else
                card.ShowBeside(shown[cell].Card(sessions, now, CultureInfo.CurrentCulture), notch.CellScreenRect(cell), notch.LeftEdge, notch.Monitor);
            NotchOnTop();
        }

        // At once, without folding: the panel opens, the capsule moves or hides, its menu opens.
        void HideCard()
        {
            openCard.Stop();
            card.HideNow();
            cardCell = -1;
        }

        // The Claude tile's Sign in: a console window running `claude auth login`; Claude is checked again when it closes.
        void SignIn()
        {
            if (signInOpen) return;   // its console window is still open
            try
            {
                signInOpen = ClaudeUsage.StartSignIn(delegate
                {
                    app.Dispatcher.BeginInvoke(new Action(delegate
                    {
                        signInOpen = false;
                        claude.Refresh();
                    }));
                });
                if (!signInOpen) tray.Balloon("Capsule", "Couldn't find the Claude CLI (claude.exe) to sign in with.");
            }
            catch (Exception e)
            {
                signInOpen = false;
                Log.Error("sign in", e);
                tray.Balloon("Capsule", "Couldn't start the Claude CLI to sign in: " + e.Message);
            }
        }

        void OnMoved(string edge, string device, double y)
        {
            HideCard();
            panel.Dismiss();
            config.Edge = edge;
            config.Monitor = device;
            config.Y = y;
            config.Save(Paths.ConfigFile);
        }

        void ShowMenu()
        {
            HideCard();
            sessions.ReadHookState();
            bool reconnect = sessions.HooksConnected && HookSetup.NeedsUpdate(Paths.ClaudeSettings);
            bool autostart = false;
            try { autostart = Autostart.IsEnabled(); }
            catch (Exception) { }
            var items = new List<MenuItemSpec>();
            if (updates.Available != null)
            {
                items.Add(new MenuItemSpec { Id = CmdUpdate, Text = "Capsule " + updates.Available + " is available…" });
                items.Add(new MenuItemSpec { Separator = true });
            }
            items.AddRange(new List<MenuItemSpec>
            {
                new MenuItemSpec { Id = CmdRefresh, Text = "Refresh now" },
                new MenuItemSpec { Id = CmdShow, Text = "Show notch", Checked = config.ShowNotch },
                new MenuItemSpec { Separator = true },
                new MenuItemSpec { Id = CmdHooks, Text = HookSetup.MenuText(sessions.HooksConnected, reconnect) },
                new MenuItemSpec { Id = CmdAutostart, Text = "Start with Windows", Checked = autostart },
                new MenuItemSpec { Id = CmdFolder, Text = "Open data folder" },
            });
            if (updates.CanCheck) items.Add(new MenuItemSpec { Id = CmdCheckUpdates, Text = "Check for updates", Checked = config.CheckUpdates });   // a release build only
            items.Add(new MenuItemSpec { Separator = true });
            items.Add(new MenuItemSpec { Id = CmdQuit, Text = "Quit Capsule" });
            int choice;
            menuOpen = true;   // CheckScreen's KeepOnTop would otherwise lift the notch over its own menu
            try { choice = NativeMenu.Show(menuOwner.Handle, items); }
            finally { menuOpen = false; }
            switch (choice)
            {
                case CmdRefresh:
                    RefreshAll();
                    break;
                case CmdShow:
                    ToggleNotch();
                    break;
                case CmdHooks:
                    ToggleHooks(reconnect);
                    break;
                case CmdAutostart:
                    try { Autostart.Set(!autostart, ExePath()); }
                    catch (Exception e) { Log.Error("start with Windows", e); }
                    break;
                case CmdFolder:
                    System.Diagnostics.Process.Start("explorer.exe", "\"" + Paths.DataDir + "\"");
                    break;
                case CmdUpdate:
                    try { System.Diagnostics.Process.Start(updates.PageUrl); }   // github.com, built from the checked version
                    catch (Exception e) { Log.Error("updates: opening the release page", e); }
                    break;
                case CmdCheckUpdates:
                    config.CheckUpdates = !config.CheckUpdates;
                    config.Save(Paths.ConfigFile);
                    updates.Enabled = config.CheckUpdates;
                    break;
                case CmdQuit:
                    app.Shutdown();
                    break;
            }
        }

        // Connect, disconnect, or connect again over an earlier Capsule's entries (reconnect), which Connect replaces.
        void ToggleHooks(bool reconnect)
        {
            string hookExe = Path.Combine(Paths.ExeDir, "capsule-hook.exe");
            bool connect = !sessions.HooksConnected || reconnect;
            if (connect && !File.Exists(hookExe))
            {
                // A hook pointing at a missing file would make every Claude Code session report hook errors.
                Log.Info("hooks: not connected, capsule-hook.exe is missing");
                tray.Balloon("Capsule", "Can't connect: capsule-hook.exe is missing next to Capsule.exe.");
                return;
            }
            try
            {
                string result = connect
                    ? HookSetup.Connect(Paths.ClaudeSettings, hookExe, HookEvents.Wiring, new string[0], HookSetup.UseShellForm, DateTime.Now)
                    : HookSetup.Disconnect(Paths.ClaudeSettings, DateTime.Now);
                Log.Info("hooks: " + result);
                sessions.ReadHookState();
                tray.Balloon("Capsule", sessions.HooksConnected
                    ? "Connected. Claude Code sessions will show when they're working, and you can answer Claude from the capsule."
                    : "Disconnected from Claude Code.");
            }
            catch (Exception e)
            {
                Log.Error("hooks", e);
                tray.Balloon("Capsule", "Couldn't change Claude Code's settings.json: " + e.Message);
            }
            Render();
        }

        void ToggleNotch()
        {
            config.ShowNotch = !config.ShowNotch;
            config.Save(Paths.ConfigFile);
            ApplyVisibility();
        }

        void ApplyVisibility()
        {
            bool visible = config.ShowNotch && !fullscreen;
            if (visible && !notch.IsVisible)
            {
                notch.Show();
                notch.Reposition();
                Log.Info("notch shown");
            }
            if (!visible && notch.IsVisible)
            {
                HideCard();
                notch.Hide();
                Log.Info("notch hidden (" + (config.ShowNotch ? "fullscreen" : "turned off") + ")");
            }
        }

        // Every 2 s: hide under a fullscreen app on the notch's monitor, and stay above other topmost windows.
        void CheckScreen()
        {
            if (notch.Handle == IntPtr.Zero) return;
            bool? covered = Native.IsFullscreenOn(notch.Monitor, notch.Handle, card.Handle, panel.Handle, menuOwner.Handle);
            if (covered.HasValue && covered.Value != fullscreen)
            {
                fullscreen = covered.Value;
                Log.Info(fullscreen ? "fullscreen window: " + Native.ForegroundDescription() : "fullscreen window gone");
                ApplyVisibility();
            }
            NotchOnTop();
        }

        void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode != PowerModes.Resume) return;
            app.Dispatcher.BeginInvoke(new Action(delegate
            {
                foreach (IModule m in modules) m.Resumed();
                Tick();
            }));
        }

        void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            app.Dispatcher.BeginInvoke(new Action(delegate
            {
                HideCard();
                panel.Dismiss();
                notch.Reposition();
            }));
        }

        // Light or dark mode, or Transparency effects, may have changed: Theme raises Changed if so.
        void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            app.Dispatcher.BeginInvoke(new Action(delegate { Theme.Refresh(blurWorks); }));
        }

        void OnThemeChanged()
        {
            Log.Info("theme: " + (Theme.Current.Light ? "light" : "dark") + (Theme.Current.Glass ? ", glass" : ", solid"));
            ApplyGlass();
            notch.Restyle();
            panel.Restyle(PanelNow());
            Render();
        }

        // Live blur on or off under every glass window, as the theme says. If a blur window can't be made, live
        // blur is given up until the next start, and the theme falls back to solid glass.
        void ApplyGlass()
        {
            bool on = Theme.Current.Glass;
            if (notch.SetGlass(on) && card.SetGlass(on) && panel.SetGlass(on)) return;
            Log.Info("glass: couldn't make a blur window; using solid glass");
            FallBackToSolid();
        }

        // A blur window broke while it was in use (a shape couldn't be applied; the layer has hidden itself). That can
        // happen in the middle of a move or a redraw, so the fallback waits until that call is over. If ApplyGlass has
        // already given the blur up (a first shape failing at start-up does both), there is nothing left to do.
        void OnGlassLayerFailed()
        {
            app.Dispatcher.BeginInvoke(new Action(delegate
            {
                Guard("glass fallback", delegate
                {
                    if (!blurWorks || disposed) return;
                    Log.Info("glass: a blur window failed; using solid glass");
                    FallBackToSolid();
                });
            }));
        }

        // Live blur is given up until the next start: every glass window loses its blur, and the theme turns solid.
        void FallBackToSolid()
        {
            blurWorks = false;
            notch.SetGlass(false);
            card.SetGlass(false);
            panel.SetGlass(false);
            Theme.Refresh(false);   // raises Changed, which restyles everything as solid
        }

        static string ExePath() { return Assembly.GetEntryAssembly().Location; }

        // The way out, from Application.Exit: WPF has closed the windows and its dispatcher still runs. The blur windows and
        // the visual layer go now (spec §4.7): left until the dispatcher had stopped, they made Quit end the process with
        // 0xC000041D instead of 0. Also after a start that failed halfway. A second call does nothing.
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            foreach (DispatcherTimer timer in new[] { tick, screenCheck, openCard, closeCard, codexCheck, promptTick }) if (timer != null) timer.Stop();
            if (calendar != null) calendar.CancelSignIn();   // a sign-in waiting for the browser stops listening
            // Every request held goes to the app. A reply still being written when the process ends reaches the hook as a
            // closed pipe, which it takes as a pass too.
            try
            {
                if (prompts != null) prompts.ReleaseAll();
                if (promptServer != null) promptServer.Dispose();
            }
            catch (Exception e) { Log.Error("stop prompts (" + e.GetType().Name + ")", null); }
            Theme.Changed -= OnThemeChanged;
            GlassLayer.Failed -= OnGlassLayerFailed;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
            try
            {
                if (sessions != null) sessions.Dispose();
                if (shortcut != null) shortcut.Dispose();
                if (tray != null) tray.Dispose();
                if (menuOwner != null) menuOwner.Dispose();
            }
            finally
            {
                // A throw above must not skip the blur's teardown: left running, the visual layer makes Quit end the process badly.
                try
                {
                    if (notch != null) notch.SetGlass(false);
                    if (card != null) card.SetGlass(false);
                    if (panel != null) panel.SetGlass(false);
                }
                finally { GlassLayer.Stop(); }
            }
        }
    }
}
