using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Capsule
{
    // Capsule.exe --preview <folder>: renders the notch, the cards and the panel with made-up numbers into PNGs at
    // 2x scale, so the look can be checked without live accounts. Stills can't show live blur, so they show the
    // solid glass, dark and light, over a sample wallpaper.
    public static class Preview
    {
        const string Wallpaper = "#3B6E8F";

        public static void Render(string dir)
        {
            Directory.CreateDirectory(dir);
            // A fixed mid-morning today, so the made-up day (a meeting under way, more to come) looks the same whenever
            // it is rendered.
            long now = Clock.ToMs(DateTime.Today.AddHours(10).AddMinutes(40).ToUniversalTime());
            CultureInfo en = CultureInfo.GetCultureInfo("en-US");
            Reading claude = SampleClaude(now), codex = SampleCodex(now);
            Reading dimmed = claude.Clone();
            dimmed.LastCheckFailed = true;
            dimmed.DataAtMs = now - 30 * 60 * 1000;
            var signin = new Reading { Provider = "claude", Status = "signin" };
            Theme.Use(Theme.For(false, false));
            SaveNotch(dir, "notch-normal.png", WithCalendar(Cells(now, null, claude, codex), now, en), false);
            SaveNotch(dir, "notch-working.png", WithCalendar(Cells(now, States.Working, claude, codex), now, en), false);
            SaveNotch(dir, "notch-waiting.png", WithCalendar(Cells(now, States.Waiting, claude, codex), now, en), false);
            SaveNotch(dir, "notch-dimmed.png", Cells(now, null, dimmed, codex), false);
            SaveNotch(dir, "notch-signin.png", Cells(now, null, signin), false);
            SaveNotch(dir, "notch-left.png", Cells(now, null, claude, codex), true);
            SaveCard(dir, "card-claude.png", CardModel.From(claude, SampleSessions(now), true, now, en), true);
            SaveCard(dir, "card-codex.png", CardModel.From(codex, null, true, now, en), true);
            SaveCard(dir, "card-claude-left.png", CardModel.From(claude, new List<SessionStatus>(), false, now, en), false);
            foreach (KeyValuePair<string, PromptCardModel> sample in SamplePrompts()) SavePrompt(dir, "card-" + sample.Key + ".png", sample.Value);
            SavePanel(dir, "panel.png", SamplePanel(claude, codex, now, en), false);
            SavePanel(dir, "panel-settings.png", SamplePanel(claude, codex, now, en), true);
            SaveCalendar(dir, "", claude, codex, now, en);
            SaveMonth(dir, "", now, en);
            Theme.Use(Theme.For(true, false));
            SaveNotch(dir, "notch-light.png", Cells(now, States.Waiting, claude, codex), false);
            SaveCard(dir, "card-light.png", CardModel.From(claude, SampleSessions(now), true, now, en), true);
            SavePrompt(dir, "card-question-light.png", SamplePrompts()["question"]);
            SavePanel(dir, "panel-light.png", SamplePanel(claude, codex, now, en), false);
            SaveCalendar(dir, "-light", claude, codex, now, en);
            SaveMonth(dir, "-light", now, en);
            Theme.Use(Theme.For(false, false));
        }

        // The capsule's cells with the calendar's under them, during the made-up meeting.
        static List<CellModel> WithCalendar(List<CellModel> cells, long now, CultureInfo en)
        {
            cells.Add(CalendarDay.Cell(SampleCalendar(now), now, TimeZoneInfo.Local, en));
            return cells;
        }

        // The month page (month spec §2): the month with today picked, then with + Add event's form open, then with a
        // row asking "Delete this?".
        static void SaveMonth(string dir, string suffix, long now, CultureInfo en)
        {
            DateTime today = DateTime.Today;
            TimeZoneInfo zone = TimeZoneInfo.Local;
            CalendarSnapshot day = SampleCalendar(now);
            var events = new List<CalendarEvent>(day.Events);
            events.Add(Timed(today.AddDays(-6), 9, 30, 60, "Kickoff", "#9fe1e7", zone));
            events.Add(Timed(today.AddDays(-3), 12, 0, 60, "Team lunch", "#9fe1e7", zone));
            events.Add(Timed(today.AddDays(-3), 16, 0, 30, "Pick up the bike", "#f83a22", zone));
            events.Add(Timed(today.AddDays(2), 14, 0, 60, "Interview", "#9fe1e7", zone));
            events.Add(Timed(today.AddDays(4), 19, 0, 120, "Dinner with Alex", "#f83a22", zone));
            events.Add(Timed(today.AddDays(8), 10, 0, 45, "Sprint review", "#9fe1e7", zone));
            events.Add(new CalendarEvent { Title = "Long weekend", AllDay = true, StartDay = today.AddDays(11), EndDay = today.AddDays(14), Color = "#16a765" });
            for (int i = 0; i < events.Count; i++)
            {
                events[i].Id = "sample" + i;
                events[i].CalendarId = "you@example.com";
                events[i].Editable = true;
            }
            var tasks = new List<GoogleTask>(day.Tasks);
            tasks.Add(new GoogleTask { Id = "t9", ListId = "tasks", Title = "Water the plants", Due = today, Done = true });
            tasks.Add(new GoogleTask { Id = "t10", ListId = "tasks", Title = "Renew the passport", Due = today.AddDays(5) });
            MonthModel m = CalendarMonth.Build(today.Year, today.Month, today, events, tasks, now, zone, en);
            m.CanAdd = true;
            m.ShowTasks = true;
            m.Calendars.Add(new CalendarChoice { Id = "you@example.com", Name = "you@example.com", Color = "#9fe1e7" });
            m.Lists.Add(new TaskList { Id = "tasks", Title = "My Tasks" });
            SaveMonthPage(dir, "month" + suffix + ".png", m, null, null);
            SaveMonthPage(dir, "month-add" + suffix + ".png", m, "+ Add event", "Lunch with Sam");
            SaveMonthPage(dir, "month-delete" + suffix + ".png", m, "Ask to delete: 1:1 with Sam", null);
        }

        static CalendarEvent Timed(DateTime day, int hour, int minute, int minutes, string title, string color, TimeZoneInfo zone)
        {
            long start = Clock.ToMs(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(day.Date.AddHours(hour).AddMinutes(minute), DateTimeKind.Unspecified), zone));
            return new CalendarEvent { Title = title, StartMs = start, EndMs = start + minutes * 60 * 1000L, Color = color };
        }

        // click: the automation name or the text of what to click first; title: typed into the form it opens.
        static void SaveMonthPage(string dir, string name, MonthModel m, string click, string title)
        {
            var view = new PanelView();
            view.ShowMonth(m);
            if (click != null) Click(Find(view, click));
            if (title != null) view.Month.TitleBox.Text = title;
            view.Relayout();
            var host = new Grid { Background = NotchView.Brush(Wallpaper) };
            host.Children.Add(view);
            SavePng(host, Path.Combine(dir, name));
        }

        // What carries this automation name, or the button showing this text.
        static FrameworkElement Find(DependencyObject root, string name)
        {
            var e = root as FrameworkElement;
            if (e != null && e.Visibility != Visibility.Visible) return null;
            if (e != null && System.Windows.Automation.AutomationProperties.GetName(e) == name) return e;
            var t = root as TextBlock;
            if (t != null && t.Text == name) return VisualTreeHelper.GetParent(t) as FrameworkElement;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                FrameworkElement found = Find(VisualTreeHelper.GetChild(root, i), name);
                if (found != null) return found;
            }
            return null;
        }

        // A click as the mouse would deliver it, with no window.
        static void Click(UIElement button)
        {
            if (button == null) return;
            button.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent, Source = button });
        }

        static PanelModel SamplePanel(Reading claude, Reading codex, long now, CultureInfo en)
        {
            var ideas = new IdeasTile { Configured = true, Waiting = 1 };
            ideas.Rows.Add(new IdeaRow { Text = "Capsule: calendar module", State = IdeaRow.Saving });
            ideas.Rows.Add(new IdeaRow { Text = "Recipe app: add a shopping list", State = IdeaRow.Saved, Url = "https://app.notion.com/p/1" });
            ideas.Rows.Add(new IdeaRow { Text = "Garden planner: frost date alerts", Url = "https://app.notion.com/p/2" });
            return new PanelModel
            {
                Claude = UsageTile.From(claude, now, en),
                Codex = UsageTile.From(codex, now, en),
                Sessions = SessionsTile.From(SampleSessions(now), true, now),
                Ideas = ideas,
                Calendar = CalendarDay.Tile(SampleCalendar(now), now, TimeZoneInfo.Local, en),
            };
        }

        static void SavePanel(string dir, string name, PanelModel model, bool settings)
        {
            var view = new PanelView();
            view.Update(model);
            if (settings)
            {
                view.ShowSettings(true, "0123456789abcdef0123456789abcdef", HotkeyText.Default);
                view.ShowNotionResult(true, "Connected to “Ideas”");
                view.Relayout();
            }
            var host = new Grid { Background = NotchView.Brush(Wallpaper) };
            host.Children.Add(view);
            SavePng(host, Path.Combine(dir, name));
        }

        // The calendar's faces (calendar spec §2), made up around now: the cell during a meeting and before the next one, the
        // hover card, the panel with its Calendar tile, and the settings signed in and signed out.
        static void SaveCalendar(string dir, string suffix, Reading claude, Reading codex, long now, CultureInfo en)
        {
            CalendarSnapshot day = SampleCalendar(now);
            TimeZoneInfo zone = TimeZoneInfo.Local;
            List<CellModel> during = Cells(now, null, claude, codex);
            during.Add(CalendarDay.Cell(day, now, zone, en));
            SaveNotch(dir, "calendar-notch-now" + suffix + ".png", during, false);
            List<CellModel> before = Cells(now, null, claude, codex);
            before.Add(CalendarDay.Cell(day, now + 30 * 60 * 1000L, zone, en));
            SaveNotch(dir, "calendar-notch-next" + suffix + ".png", before, false);
            var card = new CardView();
            card.ShowCalendar(CalendarDay.Card(day, now, zone, en), true);
            card.SetPointer(48);
            card.Margin = new Thickness(20);
            var host = new Grid { Background = NotchView.Brush(Wallpaper) };
            host.Children.Add(card);
            SavePng(host, Path.Combine(dir, "calendar-card" + suffix + ".png"));
            PanelModel panel = SamplePanel(claude, codex, now, en);
            panel.Calendar = CalendarDay.Tile(day, now, zone, en);
            SavePanel(dir, "calendar-panel" + suffix + ".png", panel, false);
            var settings = new GoogleSettings { ClientId = "123456789012-sample.apps.googleusercontent.com", SecretSaved = true, SignedIn = true, Account = "you@example.com" };
            settings.Calendars.Add(new CalendarChoice { Id = "you@example.com", Name = "you@example.com", Color = "#9fe1e7", Shown = true });
            settings.Calendars.Add(new CalendarChoice { Id = "family", Name = "Family", Color = "#f83a22", Shown = true });
            settings.Calendars.Add(new CalendarChoice { Id = "holidays", Name = "Holidays in United States", Color = "#16a765", Shown = false });
            SaveSettings(dir, "calendar-settings" + suffix + ".png", panel, settings);
            SaveSettings(dir, "calendar-settings-signin" + suffix + ".png", panel, new GoogleSettings { ClientId = "123456789012-sample.apps.googleusercontent.com" });
            SaveSettings(dir, "calendar-settings-link" + suffix + ".png", panel, new GoogleSettings { LinkSaved = true, LinkName = "you@example.com" });
        }

        static void SaveSettings(string dir, string name, PanelModel model, GoogleSettings google)
        {
            var view = new PanelView();
            view.Update(model);
            view.ShowSettings(true, "0123456789abcdef0123456789abcdef", HotkeyText.Default);
            view.ShowGoogle(google);
            view.Relayout();
            var host = new Grid { Background = NotchView.Brush(Wallpaper) };
            host.Children.Add(view);
            SavePng(host, Path.Combine(dir, name));
        }

        static CalendarSnapshot SampleCalendar(long now)
        {
            const long Minute = 60 * 1000;
            DateTime today = DateTime.Today;
            var events = new List<CalendarEvent>
            {
                new CalendarEvent { Title = "Offsite planning", AllDay = true, StartDay = today, EndDay = today.AddDays(1), Color = "#16a765" },
                new CalendarEvent { Title = "Design review", StartMs = now - 20 * Minute, EndMs = now + 25 * Minute, Color = "#9fe1e7" },
                new CalendarEvent { Title = "1:1 with Sam", StartMs = now + 60 * Minute, EndMs = now + 90 * Minute, Color = "#9fe1e7" },
                new CalendarEvent { Title = "Quarterly planning with the whole product and design group", StartMs = now + 150 * Minute, EndMs = now + 210 * Minute, Color = "#9fe1e7" },
                new CalendarEvent { Title = "Standup", StartMs = now + 24 * 60 * Minute, EndMs = now + 24 * 60 * Minute + 15 * Minute, Color = "#9fe1e7" },
                new CalendarEvent { Title = "Dentist", StartMs = now + 26 * 60 * Minute, EndMs = now + 27 * 60 * Minute, Color = "#f83a22" },
            };
            var tasks = new List<GoogleTask>
            {
                new GoogleTask { Id = "t1", ListId = "tasks", Title = "Send the invoice", Due = today },
                new GoogleTask { Id = "t2", ListId = "tasks", Title = "Book flights", Due = today.AddDays(1) },
            };
            return new CalendarSnapshot { SignedIn = true, Loaded = true, Events = events, Tasks = tasks, DataAtMs = now - 2 * Minute };
        }

        static List<CellModel> Cells(long now, string activity, params Reading[] readings)
        {
            var list = new List<CellModel>();
            foreach (Reading r in readings) list.Add(NotchView.CellFor(r, activity, now));
            return list;
        }

        static Reading SampleClaude(long now)
        {
            var r = new Reading { Provider = "claude", Status = "ok", HeadlineId = "five_hour", Plan = "Max", DataAtMs = now - 60 * 1000 };
            r.Windows.Add(new LimitWindow { Id = "five_hour", Label = "Current session", Used = 73, ResetsAtMs = now + 51 * 60 * 1000L });
            r.Windows.Add(new LimitWindow { Id = "seven_day", Label = "Weekly · all models", Used = 7, ResetsAtMs = now + 3 * 24 * 3600 * 1000L });
            r.Windows.Add(new LimitWindow { Id = "seven_day_opus", Label = "Weekly · Opus", Used = 52, ResetsAtMs = now + 3 * 24 * 3600 * 1000L });
            return r;
        }

        static Reading SampleCodex(long now)
        {
            var r = new Reading { Provider = "codex", Status = "ok", HeadlineId = "primary", Plan = "Plus", DataAtMs = now - 2 * 60 * 1000 };
            r.Windows.Add(new LimitWindow { Id = "primary", Label = "5-hour limit", Used = 21, ResetsAtMs = now + 3 * 3600 * 1000L });
            r.Windows.Add(new LimitWindow { Id = "secondary", Label = "Weekly limit", Used = 9, ResetsAtMs = now + 5 * 24 * 3600 * 1000L });
            return r;
        }

        static List<SessionStatus> SampleSessions(long now)
        {
            return new List<SessionStatus>
            {
                new SessionStatus { SessionId = "a", State = States.Waiting, Project = "confetti", Reason = "needs permission: Bash", At = now - 20000, Since = now - 20000 },
                new SessionStatus { SessionId = "d", State = States.Waiting, Project = "a-really-long-project-folder-name", Reason = "Claude needs your permission to use mcp__github__create_pull_request", At = now - 60000, Since = now - 60000 },
                new SessionStatus { SessionId = "b", State = States.Working, Project = "website", At = now - 5000, Since = now - 150000 },
                new SessionStatus { SessionId = "c", State = States.Done, Project = "capsule", At = now - 4 * 60000, Since = now - 4 * 60000 },
            };
        }

        static void SaveNotch(string dir, string name, List<CellModel> cells, bool left)
        {
            var view = new NotchView();
            view.Animate = false;
            view.Update(cells, left);
            view.HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            view.VerticalAlignment = VerticalAlignment.Center;
            // a strip of "desktop" beside the notch, so the shoulders against the screen edge are visible
            var host = new Grid { Width = NotchView.PillWidth + 60, Height = view.ViewSize.Height + 24, Background = NotchView.Brush(Wallpaper) };
            host.Children.Add(view);
            SavePng(host, Path.Combine(dir, name));
        }

        static void SaveCard(string dir, string name, CardModel model, bool pointRight)
        {
            var card = new CardView();
            card.Show(model, pointRight);
            card.SetPointer(48);
            card.Margin = new Thickness(20);
            var host = new Grid { Background = NotchView.Brush(Wallpaper) };
            host.Children.Add(card);
            SavePng(host, Path.Combine(dir, name));
        }

        // Held requests on Claude's card (act-from-notch spec §3), made up: an approval with a long command, a question
        // with one option chosen, a multi-select with Other… open, a plan, and a question the card can't show.
        static Dictionary<string, PromptCardModel> SamplePrompts()
        {
            var samples = new Dictionary<string, PromptCardModel>();
            var bash = new PromptRequest { SessionId = "a", ToolName = "Bash", Project = "confetti", Cwd = @"C:\work\confetti" };
            bash.ToolInput["command"] = "npm test -- --watchAll=false --coverage --reporters=default --reporters=jest-junit && npm run build";
            bash.Rules.Add("Bash(npm test:*)");
            samples["approval"] = PromptCardModel.From(new HeldPrompt { Request = bash }, 3, 45000);
            var codexCommand = new PromptRequest { Provider = "codex", SessionId = "codex", ToolName = "Bash", Project = "capsule",
                Cwd = @"C:\work\capsule", Description = "Run the tests before publishing this change." };
            codexCommand.ToolInput["command"] = "dotnet test --configuration Release";
            samples["codex-approval"] = PromptCardModel.From(new HeldPrompt { Request = codexCommand }, 2, 45000);
            PromptRequest codexQuestion = CodexHook.Request(Json.Obj(Json.Parse("{\"hook_event_name\":\"PreToolUse\",\"tool_name\":\"request_user_input_async\",\"cwd\":\"C:\\\\work\\\\capsule\",\"session_id\":\"codex\",\"tool_input\":{\"questions\":[{\"title\":\"What should the release notes say?\"}]}}")), 0);
            var codexFlow = new QuestionFlow(QuestionFlow.Parse(codexQuestion.ToolInput));
            codexFlow.SetOther("Add Codex and local Work approvals and answers.");
            samples["codex-question"] = PromptCardModel.From(new HeldPrompt { Request = codexQuestion, Flow = codexFlow }, 1, 52000);
            var ask = new PromptRequest { SessionId = "b", ToolName = "AskUserQuestion", Project = "website" };
            ask.ToolInput = Json.Obj(Json.Parse("{\"questions\":[" +
                "{\"question\":\"Which layout should the landing page use?\",\"header\":\"Layout\",\"multiSelect\":false,\"options\":[" +
                "{\"label\":\"Hero and grid\",\"description\":\"A large banner, then three cards\"},{\"label\":\"Single column\",\"description\":\"Text first, one image\"}]}," +
                "{\"question\":\"Which sections should it have?\",\"header\":\"Sections\",\"multiSelect\":true,\"options\":[" +
                "{\"label\":\"Pricing\",\"description\":\"\"},{\"label\":\"FAQ\",\"description\":\"\"},{\"label\":\"Testimonials\",\"description\":\"\"}]}]}"));
            var flow = new QuestionFlow(QuestionFlow.Parse(ask.ToolInput));
            flow.Choose(0);
            samples["question"] = PromptCardModel.From(new HeldPrompt { Request = ask, Flow = flow }, 1, 52000);
            var more = new QuestionFlow(QuestionFlow.Parse(ask.ToolInput));
            more.Choose(0);
            more.Next();
            more.Choose(1);
            more.ToggleOther();
            more.SetOther("A contact form");
            samples["question-other"] = PromptCardModel.From(new HeldPrompt { Request = ask, Flow = more }, 1, 60000);
            var plan = new PromptRequest { SessionId = "c", ToolName = "ExitPlanMode", Project = "capsule" };
            samples["plan"] = PromptCardModel.From(new HeldPrompt { Request = plan }, 1, 31000);
            var odd = new PromptRequest { SessionId = "d", ToolName = "AskUserQuestion", Project = "capsule" };
            samples["asked"] = PromptCardModel.From(new HeldPrompt { Request = odd }, 1, 60000);
            return samples;
        }

        static void SavePrompt(string dir, string name, PromptCardModel model)
        {
            var card = new CardView();
            card.ShowPrompt(model, true);
            card.SetPointer(48);
            card.Margin = new Thickness(20);
            var host = new Grid { Background = NotchView.Brush(Wallpaper) };
            host.Children.Add(card);
            SavePng(host, Path.Combine(dir, name));
        }

        static void SavePng(FrameworkElement element, string path)
        {
            element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            element.Arrange(new Rect(element.DesiredSize));
            element.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * 2), (int)Math.Ceiling(element.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
            bitmap.Render(element);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (FileStream file = File.Create(path)) encoder.Save(file);
        }
    }
}
