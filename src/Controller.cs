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
        const int CmdRefresh = 1, CmdShow = 2, CmdHooks = 3, CmdAutostart = 4, CmdFolder = 5, CmdQuit = 6, CmdUpdate = 7, CmdCheckUpdates = 8, CmdCodexHooks = 9, CmdLimitAlerts = 10, CmdSounds = 11, CmdFocus = 12, CmdCapture = 13;

        readonly Application app;
        Config config;
        UsageModule claude, codex;
        SessionsModule sessions;
        IdeasModule ideas;
        CalendarModule calendar;
        MusicModule music;
        UpdateCheck updates;   // a release build's once-a-day look for a newer release
        CodexActivity codexActivity;
        UsageHistory history;        // token use per day and model, from the Claude Code and Codex logs
        SampleStore samples;         // the limit readings behind the pace forecast, and the alerts already shown
        UsagePeriod usagePeriod = UsagePeriod.Heat;
        int usageOffset;             // the Usage page's period: 0 now, -1 the one before
        List<IModule> modules;
        NotchWindow notch;
        HoverCard card;
        PanelWindow panel;
        GlobalHotkey shortcut;
        CaptureCoordinator capture;
        bool captureHidden;
        Tray tray;
        MenuOwner menuOwner;
        PromptBroker prompts;        // the requests held, and their rules
        PromptBroker codexPrompts;
        PromptServer promptServer;   // the pipe the hook asks through
        DispatcherTimer tick, screenCheck, openCard, closeCard, codexCheck, promptTick, musicTick, focusTick, clipRead, linkWatchEnd;
        bool linkWatch;   // Get link was clicked: a copied iCal address is saved, for a few minutes
        readonly FocusTimer focus = new FocusTimer();
        readonly ClipboardHistory clips = new ClipboardHistory();   // in memory only
        ClipboardWatcher clipWatcher;
        long clipIgnoreUntil;   // a copy put back from the tile: its own clipboard change isn't read as a new copy
        int clipRetries;        // reads tried again while another app held the clipboard
        SessionTally tally;     // each session's model and tokens, from its transcript
        int heldNeeds, heldDone;                              // sounds held while you focus
        readonly List<string> heldAlerts = new List<string>();   // limit alerts held while you focus
        bool tallyBusy;
        List<UsageModule> shown = new List<UsageModule>();
        List<CellModel> notchCells;
        string lastActivity;
        readonly SessionCues cues = new SessionCues();   // which sound a change in the sessions calls for
        bool fullscreen, signInOpen, menuOpen;
        bool codexWorking, codexChecking;   // a Codex turn is running; a check of Codex's logs is under way
        string codexFault;                  // the exception type the last logged fault of that check had: logged once, not every 2 s
        int notionChecks;   // counts the clicks on Save and test, so that only the latest one's result is shown
        bool blurWorks;   // live blur is available on this PC and its windows could be made
        bool disposed;    // on the way out: nothing is drawn any more
        int hoverCell = -1, cardCell = -1;
        int calendarCell = -1;   // the calendar's cell on the capsule, under the usage cells; -1 while signed out
        int musicCell = -1;      // the music's cell on the capsule; -1 while nothing plays
        int focusCell = -1;      // the focus timer's cell; -1 while it isn't running or paused
        long musicPlayedMs;      // the last time any player was playing
        int monthYear, monthMonth;   // the month page's month, while it shows
        DateTime monthDay;           // and its selected day

        public Controller(Application app) { this.app = app; }

        public void Start()
        {
            config = Config.Load(Paths.ConfigFile);
            if (!config.FirstRunDone)
            {
                if (MessageBox.Show("Start Capsule automatically when you sign in to Windows? You can change this later in the tray menu.",
                    "Capsule", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
                {
                    try { Autostart.Set(true, ExePath()); }
                    catch (Exception e) { Log.Error("start with Windows", e); }
                }
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
            history = new UsageHistory(Paths.UsageHistoryFile, () => UsageScanner.Discover(Paths.ClaudeDir, Paths.CodexHome));
            history.Changed += delegate { Guard("usage", ShowUsageIfOpen); };
            samples = SampleStore.Load(Paths.UsageSamplesFile);
            music = new MusicModule(new WindowsMusicSource(), TaskScheduler.FromCurrentSynchronizationContext());
            music.Changed += delegate { Guard("music render", RenderMusic); };
            modules = new List<IModule> { sessions, claude, codex, calendar, ideas, history, music };
            updates = new UpdateCheck(AppVersion.Current, Clock.NowMs(), TaskScheduler.FromCurrentSynchronizationContext()) { Enabled = config.CheckUpdates };
            updates.Found += delegate { if (!disposed) tray.Balloon("Capsule", "Capsule " + updates.Available + " is available. Choose it in Capsule's menu to open its download page."); };
            codexActivity = new CodexActivity(Paths.CodexHome);
            tally = new SessionTally(Paths.ClaudeDir);
            focus.SetLengths(config.FocusMinutes, config.ShortBreakMinutes, config.LongBreakMinutes);
            lastActivity = sessions.Activity;
            blurWorks = GlassLayer.Supported;   // asked here, on the UI thread, which then runs the blur
            Theme.Refresh(blurWorks);
            Theme.Changed += OnThemeChanged;
            GlassLayer.Failed += OnGlassLayerFailed;

            openCard = NewTimer(110, OpenCard);   // long enough to pass over a ring without opening its card
            closeCard = NewTimer(200, CloseCardIfAway);
            tick = NewTimer(5000, Tick);
            screenCheck = NewTimer(2000, CheckScreen);
            codexCheck = NewTimer(2000, CheckCodex);
            promptTick = NewTimer(250, delegate { Guard("prompts", PromptTick); });   // runs only while a request is held
            musicTick = NewTimer(1000, delegate { music.Tick(Clock.NowMs()); });
            focusTick = NewTimer(1000, delegate { Guard("focus", FocusTick); });   // runs only while the timer does
            clipRead = NewTimer(100, delegate { clipRead.Stop(); Guard("clipboard", ReadClipboard); });   // once the copying app has let go
            linkWatchEnd = NewTimer(LinkWatchMs, delegate { Guard("calendar link", EndLinkWatch); });
            clipWatcher = new ClipboardWatcher();
            clipWatcher.Changed += delegate { clipRead.Stop(); clipRead.Start(); };

            // Capsule steps aside while the Claude app's window is in front, and while the capsule is hidden (turned off,
            // or under a fullscreen app): the card can't be seen then, and holding would only delay the app's own prompt.
            prompts = new PromptBroker(() => captureHidden || !config.RingOn("claude") || !notch.IsVisible || ClaudeApp.IsInFront());
            prompts.Changed += delegate { Guard("prompts", OnPromptsChanged); };
            codexPrompts = new PromptBroker(() => captureHidden || !config.RingOn("codex") || !notch.IsVisible || CodexApp.IsInFront());
            codexPrompts.Changed += delegate { Guard("prompts", OnPromptsChanged); };

            menuOwner = new MenuOwner();
            tray = new Tray();
            tray.LeftClick += delegate { Guard("tray click", ToggleNotch); };
            tray.RightClick += delegate { Guard("tray menu", ShowMenu); };
            card = new HoverCard();
            card.View.Music.Commanded += OnMusicCommand;
            card.View.Music.SourceSelected += delegate(string key) { music.Select(key); };
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
            panel.View.Music.Commanded += OnMusicCommand;
            panel.View.Music.SourceSelected += delegate(string key) { music.Select(key); };
            panel.View.RefreshAllClicked += delegate { Guard("refresh", RefreshAll); };
            panel.View.RefreshClicked += delegate(string provider) { (provider == "codex" ? codex : claude).Refresh(); };
            panel.View.SignInClicked += SignIn;
            panel.View.IdeaSubmitted += OnIdeaSubmitted;
            panel.View.IdeaOpened += OpenIdea;
            panel.View.DiscardClicked += delegate { ideas.Discard(); };
            panel.View.FocusCommand += delegate(string command) { Guard("focus", delegate { OnFocusCommand(command); }); };
            panel.View.ClipCopyRequested += delegate(ClipItem item) { Guard("clipboard", delegate { CopyBack(item); }); };
            panel.View.ClipClearRequested += delegate
            {
                clips.Clear();
                panel.View.UpdateClipboard(ClipTile());
            };
            panel.View.RingToggled += delegate(string name, bool on) { Guard("rings", delegate { OnRingToggled(name, on); }); };
            panel.View.ClipboardToggled += delegate(bool on) { Guard("clipboard", delegate { OnClipboardToggled(on); }); };
            panel.View.FocusLengthChanged += delegate(string which, int minutes) { Guard("focus", delegate { OnFocusLength(which, minutes); }); };
            panel.View.FocusQuietToggled += delegate(bool on) { Guard("focus", delegate { OnFocusQuiet(on); }); };
            card.View.FocusCommand += delegate(string command) { Guard("focus", delegate { OnFocusCommand(command); }); };
            panel.View.SettingsClicked += delegate
            {
                panel.View.ShowRings(config);
                panel.View.ShowSettings(SecretStore.Exists(Paths.NotionSecretFile), config.NotionDatabase, config.Hotkey, config.CaptureHotkey, capture.ShortcutRegistered);
                panel.View.ShowGoogle(calendar.Settings());
            };
            panel.View.NotionSaved += OnNotionSaved;
            panel.View.ShortcutChosen += OnShortcutChosen;
            panel.View.CaptureClicked += delegate { capture.Open(); };
            panel.View.CaptureShortcutChosen += OnCaptureShortcutChosen;
            panel.View.GoogleSignInClicked += delegate(string id, string secret) { Guard("calendar sign-in", delegate { calendar.SignIn(id, secret); }); };
            panel.View.GoogleSignInCancelled += delegate
            {
                calendar.CancelSignIn();
                ShowGoogleIfOpen();
            };
            panel.View.GoogleSignOutClicked += delegate { Guard("calendar sign-out", calendar.SignOut); };
            panel.View.CalendarToggled += OnCalendarToggled;
            panel.View.MonthClicked += delegate { Guard("month", OpenMonth); };
            panel.View.UsageClicked += delegate { Guard("usage", OpenUsage); };
            panel.View.Usage.PeriodChosen += delegate(UsagePeriod p) { Guard("usage", delegate { usagePeriod = p; usageOffset = 0; ShowUsage(); }); };
            panel.View.Usage.PreviousClicked += delegate { Guard("usage", delegate { usageOffset--; ShowUsage(); }); };
            panel.View.Usage.NextClicked += delegate { Guard("usage", delegate { if (usageOffset < 0) usageOffset++; ShowUsage(); }); };
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
            panel.View.GoogleLinkFetch += delegate { Guard("calendar link", StartLinkWatch); };
            panel.View.GoogleClientDraft += delegate(string id, string secret) { Guard("calendar client", delegate { calendar.KeepClient(id, secret); }); };
            shortcut = new GlobalHotkey();
            shortcut.Pressed += delegate { Guard("shortcut", delegate { OpenPanel(true); }); };
            RegisterShortcut(config.Hotkey);
            capture = new CaptureCoordinator(CaptureVisibilityChanged);
            capture.RegisterShortcut(config.CaptureHotkey);
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
            if (config.RingOn("codex")) codexCheck.Start();
            if (config.RingOn("music")) musicTick.Start();
            if (config.ClipboardHistory && !clipWatcher.Start()) Log.Info("clipboard: Windows didn't let Capsule watch the clipboard");
            RestoreFocus();
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
            PlayCue(cues.Next(ClaudeSessionsForCues()));
            UpdateTally();
            Render();
        }

        // Claude's sessions count for sounds only while its hooks are connected and its ring is on.
        List<SessionStatus> ClaudeSessionsForCues()
        {
            return sessions.HooksConnected && config.RingOn("claude") ? sessions.All : new List<SessionStatus>();
        }

        // A module whose ring is off does no work: no checks, no reads.
        bool ModuleOn(IModule m)
        {
            if (m == claude) return config.RingOn("claude");
            if (m == codex) return config.RingOn("codex");
            if (m == calendar) return config.RingOn("calendar");
            if (m == music) return config.RingOn("music");
            if (m == history) return config.RingOn("claude") || config.RingOn("codex");   // the Usage page opens from their tiles
            return true;
        }

        // Reads what is new in the sessions' transcripts, on a worker thread; their rows are drawn again if it changed.
        void UpdateTally()
        {
            if (tallyBusy || !config.RingOn("claude") || !sessions.HooksConnected) return;
            tallyBusy = true;
            var list = new List<SessionStatus>(sessions.All);
            long now = Clock.NowMs();
            Task.Run(() => tally.Update(list, now)).ContinueWith(t =>
            {
                tallyBusy = false;
                if (!t.IsFaulted && t.Result) Guard("render", Render);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        // A sound for what needs you, unless Sounds is off. Claude's sessions count only while its hooks are connected.
        void PlayCue(Cue cue)
        {
            if (cue == Capsule.Cue.None) return;
            if (Quiet)
            {
                if (cue == Capsule.Cue.Attention) heldNeeds++;
                else heldDone++;
                return;
            }
            if (config.Sounds) Sounds.Play(cue, Clock.NowMs());
        }

        // While you focus, Capsule's own sounds and limit alerts wait for the break (the rings still glow and spin).
        bool Quiet { get { return config.FocusQuiet && config.RingOn("focus") && focus.Running && focus.Phase == FocusPhase.Focus; } }

        // What waited while you focused, in one line, or null; the count starts again.
        string HeldSummary()
        {
            var parts = new List<string>();
            if (heldNeeds > 0) parts.Add(heldNeeds == 1 ? "1 request needed you" : heldNeeds + " requests needed you");
            if (heldDone > 0) parts.Add(heldDone == 1 ? "1 session finished" : heldDone + " sessions finished");
            string text = parts.Count > 0 ? "While you focused: " + string.Join(", ", parts) + "." : null;
            if (heldAlerts.Count > 0) text = (text == null ? "" : text + "\n") + string.Join("\n", heldAlerts);
            Cue cue = heldNeeds > 0 ? Capsule.Cue.Attention : heldDone > 0 ? Capsule.Cue.Done : Capsule.Cue.None;
            heldNeeds = heldDone = 0;
            heldAlerts.Clear();
            if (text != null && cue != Capsule.Cue.None && config.Sounds) Sounds.Play(cue, Clock.NowMs());
            return text;
        }

        // Focus is over (paused, skipped, stopped, or switched off): what waited is told now.
        void ReleaseHeld()
        {
            if (Quiet) return;
            string text = HeldSummary();
            if (text != null) tray.Balloon("Capsule", text);
        }

        void SaveFocus()
        {
            if (!Files.WriteAtomic(Paths.FocusFile, Json.Write(focus.State()))) Log.Info("focus: couldn't save the timer");
        }

        // A timer that was running when Capsule closed picks up where it was (one that ran out meanwhile is let go).
        void RestoreFocus()
        {
            if (!config.RingOn("focus")) return;
            long now = Clock.NowMs();
            FocusPhase? ended = focus.Restore(Json.Obj(Json.TryParse(Files.ReadText(Paths.FocusFile))), now);
            if (focus.Running) focusTick.Start();
            if (ended.HasValue) tray.Balloon("Capsule", focus.EndedText(ended.Value));
            if (focus.Active) Log.Info("focus: picked up where it was");
            SaveFocus();
        }

        void OnFocusQuiet(bool on)
        {
            config.FocusQuiet = on;
            config.Save(Paths.ConfigFile);
            panel.View.ShowRings(config);
            ReleaseHeld();
        }

        void OnUsageChecked()
        {
            SaveReadings();
            Guard("limits", CheckLimits);
            Guard("render", Render);
        }

        void Tick()
        {
            long now = Clock.NowMs();
            foreach (IModule m in modules) if (ModuleOn(m)) m.Tick(now);
            Guard("updates", delegate { updates.Tick(now); });
            lastActivity = sessions.Activity;
            prompts.SessionsChanged(sessions.All);
            PlayCue(cues.Next(ClaudeSessionsForCues()));
            UpdateTally();
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
                    PromptBroker broker = request.Provider == "codex" ? codexPrompts : prompts;
                    HeldPrompt held = broker.Arrive(request, delegate(PromptReply reply) { link.Send(reply); }, Clock.NowMs());
                    if (held == null) return;
                    if (broker == codexPrompts) PlayCue(Capsule.Cue.Attention);   // Claude's waiting shows in its session's status, which cues its own sound
                    int id = held.Id;
                    link.WhenGone(delegate { app.Dispatcher.BeginInvoke(new Action(delegate { Guard("prompts", delegate { broker.Gone(id); }); })); });
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
            int count = prompts.Count + codexPrompts.Count;
            if (count == 0) promptTick.Stop();
            else if (!promptTick.IsEnabled) promptTick.Start();
            if (disposed) return;
            card.RequestsHeld = count > 0;
            if (count == 0 && !card.IsVisible) card.View.Prompt.Clear();
            Render();
            if (card.IsOpen && cardCell >= 0 && cardCell < shown.Count) ShowCard(cardCell);
        }

        // Every 250 ms while a request is held: the Claude app coming to the front, or a countdown ending, sends requests to
        // the app; the card's countdown line follows.
        void PromptTick()
        {
            long now = Clock.NowMs();
            prompts.Tick(now);
            codexPrompts.Tick(now);
            ShowCountdown(now);
        }

        void ShowCountdown(long now)
        {
            PromptCardModel model = card.View.Prompt.Model;
            PromptBroker broker = model != null && model.Provider == "codex" ? codexPrompts : prompts;
            if (card.IsOpen && card.View.ShowingPrompt && broker.Current != null)
                card.View.Prompt.SetCountdown(PromptCardModel.CountdownText(broker.MsLeft(now), model.AppName));
        }

        // Something done on the card, passed on exactly as the card raised it: it names the request it was built for, and
        // the broker ignores it when that is no longer the one shown. The broker redraws the card when the request
        // changes; Other… then takes the keyboard, and typing only relights Next or Send. Those two are only for the
        // request the action named, never for whichever request has come to the front since.
        void OnPromptActed(PromptAction action)
        {
            long now = Clock.NowMs();
            PromptCardModel model = card.View.Prompt.Model;
            if (model == null) return;
            PromptBroker broker = model.Provider == "codex" ? codexPrompts : prompts;
            broker.Act(action, now);
            HeldPrompt held = broker.Current;
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
                if (codexWorking && !t.Result) PlayCue(Capsule.Cue.Done);   // a Codex turn finished
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
            shown = new List<UsageModule>();
            if (config.RingOn("claude")) shown.Add(claude);
            if (config.RingOn("codex") && (codex.Current.Status != "none" || codexPrompts.Count > 0)) shown.Add(codex);
            string codexActivityNow = codexPrompts.Count > 0 ? States.Waiting : codexWorking ? States.Working : null;
            List<CellModel> cells = shown.Select(m => m.Cell(m == codex ? codexActivityNow : activity, now)).ToList();
            calendarCell = -1;
            if (config.RingOn("calendar") && calendar.Connected)   // under Claude and Codex, while there is a link or a sign-in (calendar spec §2)
            {
                calendarCell = cells.Count;
                cells.Add(calendar.Cell(now, CultureInfo.CurrentCulture));
            }
            musicCell = -1;
            if (MusicShown(now))
            {
                musicCell = cells.Count;
                cells.Add(music.Current.Cell());
            }
            focusCell = -1;
            if (config.RingOn("focus") && focus.Active)
            {
                focusCell = cells.Count;
                cells.Add(focus.Cell(now));
            }
            if (card.IsVisible && cardCell >= cells.Count) HideCard();   // its cell is gone (signed out, say)
            notchCells = cells;
            notch.SetCells(cells);
            LimitWindow headline = config.RingOn("claude") ? claude.Current.DisplayWindow : null;
            tray.Update(headline != null ? (double?)headline.Used : null, TrayText());
            // Not while it folds: that would turn it round. Nor while it shows a request: OnPromptsChanged redraws that.
            if (card.IsOpen && cardCell >= 0 && !card.View.ShowingPrompt) ShowCard(cardCell);
            if (panel.IsOpen) panel.Refresh(PanelNow());
        }

        // The one-second music update changes only its faces; the rest of the board keeps its controls and focus.
        void RenderMusic()
        {
            if (disposed || notchCells == null) return;
            // Music started, or stopped long enough ago: the cell comes or goes, and the capsule is laid out again.
            if (MusicShown(Clock.NowMs()) != (musicCell >= 0)) { Render(); return; }
            if (panel.IsOpen) panel.RefreshMusic(music.Current);
            if (musicCell < 0 || musicCell >= notchCells.Count) return;
            notchCells[musicCell] = music.Current.Cell();
            notch.SetCells(notchCells);
            if (card.IsOpen && cardCell == musicCell) ShowCard(musicCell);
        }

        bool MusicShown(long now)
        {
            if (!config.RingOn("music")) return false;
            if (music.Current.Tracks.Exists(t => t.Playing)) musicPlayedMs = now;
            return MusicSelection.ShowCell(music.Current, now, musicPlayedMs);
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
                Claude = config.RingOn("claude") ? claude.Tile(now, CultureInfo.CurrentCulture) : null,
                Codex = config.RingOn("codex") ? codex.Tile(now, CultureInfo.CurrentCulture) : null,
                Sessions = config.RingOn("claude") ? sessions.Tile(now, tally.For) : null,
                Calendar = config.RingOn("calendar") ? calendar.Tile(now, CultureInfo.CurrentCulture) : null,
                Music = config.RingOn("music") ? music.Current : null,
                Focus = config.RingOn("focus") ? FocusTile.From(focus, now, CultureInfo.CurrentCulture) : null,
                Clipboard = ClipTile(),
                Ideas = ideas.Tile(),
            };
        }

        ClipboardTile ClipTile()
        {
            return config.ClipboardHistory ? new ClipboardTile { Items = new List<ClipItem>(clips.Items) } : null;
        }

        // ---- Rings, the focus timer and the clipboard ----

        // A switch in Settings. Off, the ring's work stops too, and requests it holds go to the app.
        void OnRingToggled(string name, bool on)
        {
            if (!on && !config.CanTurnOff(name)) return;
            config.Rings[name] = on;
            if (!config.Save(Paths.ConfigFile)) Log.Info("rings: couldn't write config.json; the switch holds only until Capsule restarts");
            Log.Info("rings: " + name + (on ? " on" : " off"));
            switch (name)
            {
                case "claude":
                    if (on) claude.Refresh();
                    else prompts.ReleaseAll();
                    break;
                case "codex":
                    if (on)
                    {
                        codexCheck.Start();
                        codex.Refresh();
                    }
                    else
                    {
                        codexCheck.Stop();
                        codexWorking = false;
                        codexPrompts.ReleaseAll();
                    }
                    break;
                case "calendar":
                    if (on) calendar.Refresh();
                    break;
                case "music":
                    if (on)
                    {
                        musicTick.Start();
                        music.Refresh();
                    }
                    else musicTick.Stop();
                    break;
                case "focus":
                    if (!on)
                    {
                        focus.Stop();
                        focusTick.Stop();
                        SaveFocus();
                        ReleaseHeld();
                    }
                    break;
            }
            panel.View.ShowRings(config);
            Render();
        }

        void OnClipboardToggled(bool on)
        {
            config.ClipboardHistory = on;
            if (!config.Save(Paths.ConfigFile)) Log.Info("clipboard: couldn't write config.json; the switch holds only until Capsule restarts");
            if (on)
            {
                if (!clipWatcher.Start()) Log.Info("clipboard: Windows didn't let Capsule watch the clipboard");
            }
            else
            {
                clipWatcher.Stop();
                clipRead.Stop();
                clips.Clear();
            }
            panel.View.ShowRings(config);
            Render();
        }

        void OnFocusLength(string which, int minutes)
        {
            if (which == "focus") config.FocusMinutes = Math.Max(1, Math.Min(Config.MaxFocusMinutes, minutes));
            else if (which == "short") config.ShortBreakMinutes = Math.Max(1, Math.Min(Config.MaxBreakMinutes, minutes));
            else if (which == "long") config.LongBreakMinutes = Math.Max(1, Math.Min(Config.MaxBreakMinutes, minutes));
            else return;
            config.Save(Paths.ConfigFile);
            focus.SetLengths(config.FocusMinutes, config.ShortBreakMinutes, config.LongBreakMinutes);
            panel.View.ShowRings(config);
            Render();
        }

        void OnFocusCommand(string command)
        {
            long now = Clock.NowMs();
            switch (command)
            {
                case "start": focus.Start(now); break;
                case "pause": focus.Pause(now); break;
                case "resume": focus.Resume(now); break;
                case "skip": focus.Skip(now); break;
                case "stop": focus.Stop(); break;
                default: return;
            }
            if (focus.Running) focusTick.Start();
            else focusTick.Stop();
            SaveFocus();
            ReleaseHeld();
            Render();
            if (card.IsOpen && cardCell >= 0 && cardCell == focusCell) ShowCard(focusCell);
        }

        // Every second while the timer runs: a part that ended is marked by a sound and a notification, and the ring and
        // the open Focus tile count down. Nothing else is redrawn.
        void FocusTick()
        {
            long now = Clock.NowMs();
            FocusPhase? ended = focus.Tick(now);
            if (ended.HasValue)
            {
                // The end of focus also tells what waited meanwhile; its chime is the one sound for both.
                string held = ended.Value == FocusPhase.Focus ? HeldSummary() : null;
                if (held == null) PlayCue(Capsule.Cue.Done);
                tray.Balloon("Capsule", focus.EndedText(ended.Value) + (held != null ? "\n" + held : ""));
                SaveFocus();
            }
            if (notchCells != null && focusCell >= 0 && focusCell < notchCells.Count)
            {
                notchCells[focusCell] = focus.Cell(now);
                notch.SetCells(notchCells);
            }
            if (panel.IsOpen && !panel.View.ShowingSettings) panel.View.UpdateFocus(FocusTile.From(focus, now, CultureInfo.CurrentCulture));
            if (card.IsOpen && cardCell >= 0 && cardCell == focusCell) ShowCard(focusCell);
        }

        // The clipboard changed: what is on it now joins the history, unless it is private or was put back from the tile.
        // Nothing about it is logged but the type of a failure.
        void ReadClipboard() { ReadClipboard(ClipboardHistory.Read); }

        internal void ReadClipboard(Func<long, ClipItem> read)
        {
            if (linkWatch && CatchCalendarLink()) return;
            if (!config.ClipboardHistory) return;
            long now = Clock.NowMs();
            if (now < clipIgnoreUntil) return;
            ClipItem item;
            try { item = read(now); }
            catch (Exception e)
            {
                // Another app still holds it: try again a little later, a few times.
                if (clipRetries++ < 3) clipRead.Start();
                else
                {
                    clipRetries = 0;
                    Log.Info("clipboard: couldn't read a copy (" + e.GetType().Name + ")");
                }
                return;
            }
            clipRetries = 0;
            if (clips.Add(item) && panel.IsOpen) panel.View.UpdateClipboard(ClipTile());
        }

        const string GoogleSettingsPage = "https://calendar.google.com/calendar/r/settings";
        const int LinkWatchMs = 5 * 60 * 1000;

        // Get link: Google Calendar's settings open in the browser, and for the next few minutes the clipboard is watched
        // for the calendar's secret iCal address. Copying it there is all that's left: it is saved at once.
        void StartLinkWatch()
        {
            try { System.Diagnostics.Process.Start(GoogleSettingsPage); }
            catch (Exception e) { Log.Error("open google calendar", e); return; }
            if (!clipWatcher.Start())
            {
                Log.Info("calendar link: Windows didn't let Capsule watch the clipboard");   // pasting into the box still works
                return;
            }
            linkWatch = true;
            linkWatchEnd.Stop();
            linkWatchEnd.Start();
        }

        void EndLinkWatch()
        {
            linkWatchEnd.Stop();
            linkWatch = false;
            if (!config.ClipboardHistory) clipWatcher.Stop();
        }

        // While Get link waits: a copied iCal address is saved, the wait ends, and a balloon says so. True when the copy
        // was handled here (or the clipboard was busy), so it doesn't go on to the history. The address is never logged.
        bool CatchCalendarLink()
        {
            string text;
            try { text = Clipboard.ContainsText() ? Clipboard.GetText() : null; }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                if (clipRetries++ < 3) clipRead.Start();
                else clipRetries = 0;
                return true;
            }
            clipRetries = 0;
            if (IcsLink.Normalize(text) == null) return false;
            EndLinkWatch();
            if (!calendar.SaveLink(text)) return true;   // its status says why, in the settings
            panel.View.LinkKept();
            tray.Balloon("Capsule", config.RingOn("calendar")
                ? "Google Calendar connected. Your events show on the capsule in a moment."
                : "Google Calendar connected. Turn on the Calendar ring in Settings to see it.");
            return true;
        }

        void CopyBack(ClipItem item)
        {
            clipIgnoreUntil = Clock.NowMs() + 500;
            if (!ClipboardHistory.CopyBack(item))
            {
                clipIgnoreUntil = 0;
                tray.Balloon("Capsule", "Another app is using the clipboard. Try again in a moment.");
                return;
            }
            clips.Add(item);   // to the top
            panel.View.UpdateClipboard(ClipTile());
        }

        void TogglePanel()
        {
            if (panel.IsOpen) panel.Dismiss();
            else OpenPanel(false);
        }

        // Beside the capsule, wherever it is (even hidden under a fullscreen app, for the shortcut).
        void OpenPanel(bool byShortcut)
        {
            if (captureHidden) return;
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
            foreach (IModule m in modules) if (ModuleOn(m)) m.Refresh();
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

        // A fresh reading becomes a pace sample, and may bring a limit alert (usage history spec §2).
        void CheckLimits()
        {
            long now = Clock.NowMs();
            bool changed = false;
            foreach (UsageModule m in new[] { claude, codex })
            {
                if (!config.RingOn(m == claude ? "claude" : "codex")) continue;
                Reading r = m.Current;
                if (!samples.Record(r, now)) continue;
                changed = true;
                foreach (string text in LimitAlerts.Check(samples, r, config.LimitAlerts, now, CultureInfo.CurrentCulture))
                {
                    if (Quiet)
                    {
                        Log.Info("limits: alert held for the break");
                        heldAlerts.Add(text);
                        continue;
                    }
                    Log.Info("limits: alert shown");
                    tray.Balloon("Capsule", text);
                }
            }
            if (changed) samples.Save(Paths.UsageSamplesFile);
            ShowUsageIfOpen();
        }

        Forecast PaceFor(Reading r, LimitWindow w) { return Pace.Shown(samples, r, w, Clock.NowMs()); }

        void OpenUsage()
        {
            usageOffset = 0;
            history.Refresh();
            ShowUsage();
        }

        void ShowUsage()
        {
            UsageModel m = UsageModel.Build(history.Snapshot, usagePeriod, usageOffset, DateTime.Today, CultureInfo.CurrentCulture, !Theme.Current.Light);
            long now = Clock.NowMs();
            foreach (UsageModule u in new[] { claude, codex })
            {
                if (!config.RingOn(u == claude ? "claude" : "codex")) continue;
                Reading r = u.Current;
                LimitWindow w = r.DisplayWindow;
                if (r.Status != "ok" || w == null) continue;
                m.Pace.Add(PaceLineFor(r.Provider, w, PaceFor(r, w), now, CultureInfo.CurrentCulture));
            }
            panel.View.ShowUsage(m);
        }

        // "Claude · 5-hour · 73% · full at 4:10 PM".
        internal static PaceLine PaceLineFor(string provider, LimitWindow w, Forecast f, long now, CultureInfo culture)
        {
            string name = LimitAlerts.Name(provider, w);
            if (name.EndsWith(" limit", StringComparison.Ordinal)) name = name.Substring(0, name.Length - " limit".Length);
            string head = (provider == "codex" ? "Codex" : "Claude") + " · " + name + " · " + Format.Percent(w) + " · ";
            bool day = f.LengthMs > Pace.Day;
            switch (f.Kind)
            {
                case ForecastKind.Full: return new PaceLine { Text = head + "full " + (day ? "" : "at ") + Pace.When(f.FullAtMs, day, culture), Warn = true };
                case ForecastKind.AtReset: return new PaceLine { Text = head + "about " + f.AtResetPercent + "% at reset" };
                case ForecastKind.Reached: return new PaceLine { Text = head + "limit reached", Warn = true };
                default:
                    string reset = Format.ResetText(w.ResetsAtMs, now, culture);
                    return new PaceLine { Text = head + (reset == "" ? "no forecast yet" : char.ToLowerInvariant(reset[0]) + reset.Substring(1)) };
            }
        }

        void ShowUsageIfOpen()
        {
            if (!disposed && panel.IsOpen && panel.View.ShowingUsage) ShowUsage();
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

        void OnCaptureShortcutChosen(string text)
        {
            if (capture.RegisterShortcut(text))
            {
                config.CaptureHotkey = text;
                bool saved = config.Save(Paths.ConfigFile);
                panel.View.ShowCaptureShortcutResult(saved, text, saved ? "Saved" : "Works now, but could not save the shortcut.");
            }
            else panel.View.ShowCaptureShortcutResult(false, config.CaptureHotkey, text + " is unavailable. Try another shortcut.");
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
            if (cell >= 0 && cell == focusCell)
            {
                cardCell = cell;
                card.ShowFocusBeside(FocusTile.From(focus, now, CultureInfo.CurrentCulture), notch.CellScreenRect(cell), notch.LeftEdge, notch.Monitor);
                NotchOnTop();
                return;
            }
            if (cell >= 0 && cell == musicCell)
            {
                cardCell = cell;
                card.ShowMusicBeside(music.Current, notch.CellScreenRect(cell), notch.LeftEdge, notch.Monitor);
                NotchOnTop();
                return;
            }
            if (cell >= 0 && cell == calendarCell)
            {
                cardCell = cell;
                card.ShowCalendarBeside(calendar.Card(now, CultureInfo.CurrentCulture), notch.CellScreenRect(cell), notch.LeftEdge, notch.Monitor);
                NotchOnTop();
                return;
            }
            if (cell < 0 || cell >= shown.Count) return;
            cardCell = cell;
            PromptBroker broker = shown[cell] == codex ? codexPrompts : prompts;
            if (broker.Current != null)
                card.ShowPromptBeside(PromptCardModel.From(broker.Current, broker.Count, broker.MsLeft(now)), notch.CellScreenRect(cell), notch.LeftEdge, notch.Monitor);
            else
                card.ShowBeside(shown[cell].Card(sessions, now, CultureInfo.CurrentCulture, w => PaceFor(shown[cell].Current, w), tally.For), notch.CellScreenRect(cell), notch.LeftEdge, notch.Monitor);
            NotchOnTop();
        }

        void OnMusicCommand(string key, MusicCommand command)
        {
            Guard("music control", delegate { music.Command(key, command); });
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
                new MenuItemSpec { Id = CmdCapture, Text = "Capture..." },
                new MenuItemSpec { Id = CmdShow, Text = "Show notch", Checked = config.ShowNotch },
                new MenuItemSpec { Separator = true },
                new MenuItemSpec { Id = CmdHooks, Text = HookSetup.MenuText(sessions.HooksConnected, reconnect) },
                new MenuItemSpec { Id = CmdCodexHooks, Text = HookSetup.IsConnected(Paths.CodexHooks) ? "Disconnect from Codex / Work" : "Connect to Codex / Work" },
                new MenuItemSpec { Id = CmdAutostart, Text = "Start with Windows", Checked = autostart },
                new MenuItemSpec { Id = CmdFolder, Text = "Open data folder" },
            });
            items.Add(new MenuItemSpec { Id = CmdLimitAlerts, Text = "Limit alerts", Checked = config.LimitAlerts });
            items.Add(new MenuItemSpec { Id = CmdSounds, Text = "Sounds", Checked = config.Sounds });
            if (config.RingOn("focus")) items.Add(new MenuItemSpec { Id = CmdFocus, Text = focus.Active ? "Stop focus" : "Start focus" });
            if (updates.CanCheck) items.Add(new MenuItemSpec { Id = CmdCheckUpdates, Text = "Check for updates", Checked = config.CheckUpdates });   // a release build only
            items.Add(new MenuItemSpec { Separator = true });
            items.Add(new MenuItemSpec { Id = CmdQuit, Text = "Quit Capsule" });
            int choice;
            menuOpen = true;   // CheckScreen's KeepOnTop would otherwise lift the notch over its own menu
            try { choice = NativeMenu.Show(menuOwner.Handle, items); }
            finally { menuOpen = false; }
            switch (choice)
            {
                case CmdCapture:
                    capture.Open();
                    break;
                case CmdRefresh:
                    RefreshAll();
                    break;
                case CmdShow:
                    ToggleNotch();
                    break;
                case CmdHooks:
                    ToggleHooks(reconnect);
                    break;
                case CmdCodexHooks:
                    ToggleCodexHooks();
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
                case CmdSounds:
                    config.Sounds = !config.Sounds;
                    config.Save(Paths.ConfigFile);
                    if (config.Sounds) Sounds.Play(Capsule.Cue.Attention, Clock.NowMs());   // so you hear what it sounds like
                    break;
                case CmdFocus:
                    OnFocusCommand(focus.Active ? "stop" : "start");
                    break;
                case CmdLimitAlerts:
                    config.LimitAlerts = !config.LimitAlerts;
                    config.Save(Paths.ConfigFile);
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

        void ToggleCodexHooks()
        {
            string hookExe = Path.Combine(Paths.ExeDir, "capsule-hook.exe");
            if (!HookSetup.IsConnected(Paths.CodexHooks) && !File.Exists(hookExe))
            {
                tray.Balloon("Capsule", "Can't connect: capsule-hook.exe is missing next to Capsule.exe.");
                return;
            }
            try
            {
                bool connected = HookSetup.IsConnected(Paths.CodexHooks);
                string result = connected ? HookSetup.Disconnect(Paths.CodexHooks, DateTime.Now)
                    : HookSetup.ConnectCodex(Paths.CodexHooks, hookExe, DateTime.Now);
                Log.Info("Codex hooks: " + result);
                if (connected) codexPrompts.ReleaseAll();
                tray.Balloon("Capsule", connected ? "Disconnected from Codex / Work."
                    : "Connected. Review and trust Capsule's hooks in Codex / Work, then start a new local chat. Command approvals and questions can use the capsule.");
            }
            catch (Exception e)
            {
                Log.Error("Codex hooks (" + e.GetType().Name + ")", null);
                tray.Balloon("Capsule", "Couldn't change Codex / Work hooks.json: " + e.Message);
            }
        }

        void ToggleNotch()
        {
            config.ShowNotch = !config.ShowNotch;
            config.Save(Paths.ConfigFile);
            ApplyVisibility();
        }

        internal bool CapsuleVisible { get { return config.ShowNotch && !fullscreen && !captureHidden; } }

        internal void CaptureVisibilityChanged(bool hidden)
        {
            captureHidden = hidden;
            if (hidden)
            {
                panel.Dismiss();
                panel.Hide();
                HideCard();
                prompts.ReleaseAll();
                codexPrompts.ReleaseAll();
            }
            if (!disposed) ApplyVisibility();
        }

        void ApplyVisibility()
        {
            bool visible = CapsuleVisible;
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
                Log.Info("notch hidden (" + (captureHidden ? "capture" : config.ShowNotch ? "fullscreen" : "turned off") + ")");
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
            foreach (DispatcherTimer timer in new[] { tick, screenCheck, openCard, closeCard, codexCheck, promptTick, musicTick, focusTick, clipRead, linkWatchEnd }) if (timer != null) timer.Stop();
            if (capture != null) capture.Dispose();
            if (clipWatcher != null) clipWatcher.Dispose();
            if (music != null) music.Dispose();
            if (calendar != null) calendar.CancelSignIn();   // a sign-in waiting for the browser stops listening
            // Every request held goes to the app. A reply still being written when the process ends reaches the hook as a
            // closed pipe, which it takes as a pass too.
            try
            {
                if (prompts != null) prompts.ReleaseAll();
                if (codexPrompts != null) codexPrompts.ReleaseAll();
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
