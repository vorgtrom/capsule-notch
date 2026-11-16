using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Capsule
{
    // v1.7.1: the focus timer picked up after a restart, its card on the capsule, quiet while focusing, and the Usage
    // page's heatmap.
    public static class FocusHeatTests
    {
        static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");
        const long Now = 1792000000000;
        const long Min = 60000;

        public static void Run()
        {
            Restore();
            QuietSetting();
            FocusCard();
            Heat();
            HeatPage();
            ProjectGroupingAndApiValue();
            UsageGroupingSetting();
        }

        static FocusTimer Back(FocusTimer t, long now, out FocusPhase? ended)
        {
            var json = Json.Write(t.State());
            var again = new FocusTimer();
            ended = again.Restore(Json.Obj(Json.Parse(json)), now);
            return again;
        }

        static void Restore()
        {
            FocusPhase? ended;
            var t = new FocusTimer();
            t.Start(Now);
            t.Skip(Now);
            t.Skip(Now);   // focus 2 of 4, running
            FocusTimer b = Back(t, Now + 5 * Min, out ended);
            TestRunner.Check(b.Running && b.Phase == FocusPhase.Focus && b.Round == 2, "a running timer picks up where it was");
            TestRunner.Eq(t.EndsAtMs, b.EndsAtMs, "with the same end");
            TestRunner.Check(ended == null, "nothing ended meanwhile");

            b = Back(t, t.EndsAtMs + 2 * Min, out ended);
            TestRunner.Eq(FocusPhase.Focus, ended.Value, "it ran out while Capsule was closed, a little while ago: that part ended");
            TestRunner.Check(b.Running && b.Phase == FocusPhase.ShortBreak, "and the break has started");

            b = Back(t, t.EndsAtMs + FocusTimer.RestoreGraceMs + Min, out ended);
            TestRunner.Check(!b.Active && ended == null, "long gone: let go");

            t.Pause(Now + 3 * Min);
            b = Back(t, Now + 90 * Min, out ended);
            TestRunner.Check(b.Paused && b.LeftMs(Now + 999 * Min) == t.LeftMs(Now), "a paused timer keeps what it had left, however long it was closed");

            var stopped = new FocusTimer();
            b = Back(stopped, Now, out ended);
            TestRunner.Check(!b.Active, "a stopped timer stays stopped");
            var none = new FocusTimer();
            TestRunner.Check(none.Restore(null, Now) == null && !none.Active, "no file: nothing to pick up");
            TestRunner.Check(none.Restore(Json.Obj(Json.Parse("{\"running\":true,\"phase\":\"Focus\",\"round\":9,\"ends_at\":1}")), Now) == null && !none.Active, "a round out of range is let go");
            TestRunner.Check(none.Restore(Json.Obj(Json.Parse("{\"running\":true,\"phase\":\"Nap\",\"round\":1,\"ends_at\":1}")), Now) == null && !none.Active, "so is a part it doesn't know");
        }

        static void QuietSetting()
        {
            string path = Path.Combine(TestRunner.NewTempDir(), "config.json");
            var c = new Config();
            TestRunner.Check(c.FocusQuiet, "quiet while focusing is on by default");
            c.FocusQuiet = false;
            c.Save(path);
            TestRunner.Check(!Config.Load(path).FocusQuiet, "and off survives a restart");
        }

        static void FocusCard()
        {
            var t = new FocusTimer();
            t.Start(Now);
            var card = new CardView();
            card.ShowFocus(FocusTile.From(t, Now, En), true);
            Lay(card);
            List<string> texts = Texts(card);
            TestRunner.Check(texts.Contains("Focus") && texts.Contains("25:00") && texts.Contains("Pause") && texts.Contains("Skip") && texts.Contains("Stop"), "the Focus ring's card: the time and the buttons");
            string asked = null;
            card.FocusCommand += delegate(string c) { asked = c; };
            Click(card, "Skip");
            TestRunner.Eq("skip", asked, "its Skip asks the timer to skip");
            card.ShowFocus(FocusTile.From(new FocusTimer(), Now, En), true);
            Lay(card);
            TestRunner.Check(Texts(card).Contains("Start") && !Texts(card).Contains("Pause"), "stopped: just Start");
        }

        static UsageStore Store(params object[] dayTokens)
        {
            var s = new UsageStore { BackfillDone = true };
            for (int i = 0; i < dayTokens.Length; i += 3) s.Add((string)dayTokens[i], (string)dayTokens[i + 1], (long)(int)dayTokens[i + 2], 0);
            return s;
        }

        static void Heat()
        {
            DateTime today = new DateTime(2026, 10, 21);   // a Wednesday
            var store = Store(
                "2026-10-21", "claude-opus-4-8", 400000,
                "2026-10-20", "claude-opus-4-8", 100000,
                "2026-10-19", "gpt-5-codex", 50000,
                "2026-10-17", "claude-sonnet-4-5", 900000,
                "2026-10-16", "claude-sonnet-4-5", 10000,
                "2026-10-15", "claude-sonnet-4-5", 10000,
                "2026-10-14", "claude-sonnet-4-5", 10000,
                "2026-10-13", "claude-sonnet-4-5", 10000,
                "2025-01-01", "claude-opus-4-1", 5000);
            UsageModel m = UsageModel.Build(store, UsagePeriod.Heat, 0, today, En, true);
            HeatModel h = m.Heat;
            TestRunner.Check(h != null, "the heatmap period builds a heatmap");
            TestRunner.Eq(HeatModel.Weeks, h.Columns.Count, "26 weeks");
            TestRunner.Check(h.Columns.All(c => c.Count == 7), "of 7 days each");
            HeatCell first = h.Columns[0][0], last = h.Columns[HeatModel.Weeks - 1][6];
            TestRunner.Eq(DayOfWeek.Sunday, first.Day.DayOfWeek, "weeks start on the culture's first day");
            TestRunner.Eq(new DateTime(2026, 10, 24), last.Day, "the last week is this one, to its end");
            TestRunner.Check(last.Future && !h.Columns[HeatModel.Weeks - 1][3].Future, "days after today are future");
            HeatCell todayCell = h.Columns[HeatModel.Weeks - 1][3];
            TestRunner.Check(todayCell.Today && todayCell.Tokens == 400000, "today, with its tokens");
            TestRunner.Eq(4, h.Columns[HeatModel.Weeks - 2][6].Level, "the busiest day (Oct 17) is the darkest");
            TestRunner.Eq(1, h.Columns[HeatModel.Weeks - 2][2].Level, "a quiet day (Oct 13) is the lightest");
            TestRunner.Eq(0, h.Columns[HeatModel.Weeks - 1][0].Level, "a day with nothing has no shade");
            TestRunner.Check(todayCell.Tip.StartsWith("Wed, Oct 21 · 400K tokens", StringComparison.Ordinal) && todayCell.Tip.Contains("Opus 4.8"), "a day's tip: its tokens and models");
            TestRunner.Check(h.Months.Any(p => p.Value == "Oct"), "months are named over their columns");
            TestRunner.Eq("Sun", h.DayNames[0], "rows named from the week's first day");
            Func<string, string> stat = label => h.Stats.First(s => s.Label == label).Value;
            TestRunner.Eq("186K", stat("Per active day"), "tokens per active day over the weeks shown (the 2025 day is outside them)");
            TestRunner.Eq(1490000L, m.Total, "and the page's total is theirs");
            TestRunner.Check(stat("Active days").StartsWith("8 of ", StringComparison.Ordinal), "active days of those counted so far");
            TestRunner.Eq("3 days", stat("Current streak"), "current streak: today and the two days before");
            TestRunner.Eq("5 days", stat("Longest streak"), "longest streak: Oct 13 to 17");
            TestRunner.Eq("Oct 17", stat("Most active day"), "the busiest day");
            TestRunner.Eq("Sonnet 4.5", stat("Favourite model"), "the model with the most tokens");
            TestRunner.Check(m.Title.Contains("Oct 24, 2026"), "the title spans the weeks shown");

            int current, longest;
            HeatModel.Streaks(Store("2026-10-20", "claude-opus-4-8", 1, "2026-10-19", "claude-opus-4-8", 1), today, out current, out longest);
            TestRunner.Eq(2, current, "nothing yet today: the streak up to yesterday still counts");
            HeatModel.Streaks(Store("2026-10-18", "claude-opus-4-8", 1), today, out current, out longest);
            TestRunner.Eq(0, current, "a gap of two days ends it");
            TestRunner.Eq(1, longest, "a single day is a streak of one");

            UsageModel back = UsageModel.Build(store, UsagePeriod.Heat, -1, today, En, true);
            TestRunner.Eq(h.Columns[0][0].Day.AddDays(-1), back.Heat.Columns[HeatModel.Weeks - 1][6].Day, "one step back: the 26 weeks before");
            TestRunner.Check(back.CanNext && !m.CanNext, "and forward again from there, but not past now");
            TestRunner.Check(UsageModel.Build(store, UsagePeriod.Week, 0, today, En, true).Heat == null, "the bar periods have no heatmap");
        }

        static void HeatPage()
        {
            DateTime today = new DateTime(2026, 10, 21);
            var store = Store("2026-10-21", "claude-opus-4-8", 400000, "2026-10-17", "claude-sonnet-4-5", 900000);
            var view = new UsageView();
            view.Update(UsageModel.Build(store, UsagePeriod.Heat, 0, today, En, true));
            Lay(view);
            List<string> texts = Texts(view);
            TestRunner.Check(texts.Contains("Longest streak") && texts.Contains("Favourite model") && texts.Contains("Active days"), "the page shows the figures");
            TestRunner.Check(!texts.Contains("Week") && !texts.Contains("Month"), "and no period switch");
            var squares = All(view).OfType<System.Windows.Shapes.Rectangle>().Where(r => r.ToolTip is string).ToList();
            TestRunner.Eq(26 * 7 - 3, squares.Count, "a square for every day so far this half-year");
            TestRunner.Check(squares.Count(r => r.Stroke != null) == 1, "today is ringed");
            TestRunner.Check(squares.All(r => r.Width < 12 && r.Width > 7), "squares fit the panel (" + squares[0].Width + ")");
        }

        static void ProjectGroupingAndApiValue()
        {
            DateTime today = new DateTime(2026, 10, 21);
            const string sonnet = "claude-sonnet-4-5", opus = "claude-opus-4-1-20250805";
            const string day = "2026-10-21";
            var store = new UsageStore { BackfillDone = true };
            var detail = new UsageDetailStore();
            detail.Rows.AddRange(new[]
            {
                new UsageDetailRow { Day = day, Model = sonnet, ProjectKey = "b", ProjectName = "demo", Input = 500000 },
                new UsageDetailRow { Day = day, Model = opus, ProjectKey = "a", ProjectName = "demo", Input = 600000, CacheRead = 1000000 },
                new UsageDetailRow { Day = day, Model = sonnet, ProjectKey = "c", ProjectName = "farm", Input = 400000 },
                new UsageDetailRow { Day = day, Model = sonnet, ProjectKey = "d", ProjectName = "atlas", Input = 300000 },
                new UsageDetailRow { Day = day, Model = sonnet, ProjectKey = "e", ProjectName = "moons", Input = 200000 },
                new UsageDetailRow { Day = day, Model = sonnet, ProjectKey = "f", ProjectName = "harbor", Input = 100000 },
                new UsageDetailRow { Day = day, Model = "unpriced-fixture", ProjectKey = "g", ProjectName = "park", Input = 50000 },
                new UsageDetailRow { Day = day, Model = sonnet, ProjectKey = "h", ProjectName = "sample", Input = 40000 },
                new UsageDetailRow { Day = day, Model = sonnet, Input = 20000 },
                new UsageDetailRow { Day = "2025-01-01", Model = opus, ProjectKey = "i", ProjectName = "old project", Input = 10000000 },
            });
            foreach (UsageDetailRow row in detail.Rows) store.Add(row.Day, row.Model, row.Tokens, row.CacheRead);
            store.Add("2026-10-20", sonnet, 1234, 0);   // The legacy history has a day whose logs are gone.

            UsageModel byModel = UsageModel.Build(store, detail, UsagePeriod.Heat, 0, today, En, true, false);
            UsageModel byProject = UsageModel.Build(store, detail, UsagePeriod.Heat, 0, today, En, true, true);
            var modelCells = byModel.Heat.Columns.SelectMany(week => week).ToList();
            var projectCells = byProject.Heat.Columns.SelectMany(week => week).ToList();
            TestRunner.Check(modelCells.Select(c => c.Level).SequenceEqual(projectCells.Select(c => c.Level)), "the grouping cannot change heatmap shades");
            TestRunner.Eq(byModel.Total, byProject.Total, "the grouping cannot change the legacy total");
            TestRunner.Check(byProject.Legend.Select(p => p.Name).SequenceEqual(new[] { "demo", "demo (2)", "farm", "atlas", "moons", "harbor", "Other", "Other projects" }),
                "the six named projects in token order, unknown cwd, then the rest");
            TestRunner.Eq(90000L, byProject.Legend.Last().Tokens, "Other projects combines lower-ranked named projects");
            TestRunner.Check(byProject.Details.Contains("demo (2) · 500K") && byProject.Details.Contains("Other projects · 90K"), "Details shows the same project totals as the legend");
            TestRunner.Check(projectCells.First(c => c.Today).Tip.EndsWith("\ndemo · 600K\ndemo (2) · 500K\nfarm · 400K", StringComparison.Ordinal),
                "the day's hover shares the duplicate labels and shows its three biggest projects");
            TestRunner.Check(modelCells.First(c => c.Today).Tip.Contains("Opus 4.1"), "the model hover retains the model names");
            TestRunner.Check(!projectCells.First(c => c.Day == today.AddDays(-1)).Tip.Contains("\n"), "the legacy-only day has no project attribution");

            TestRunner.Eq("≈ $15.18 API value", byModel.ApiValue, "cache reads are priced and an out-of-period row and missing prices are excluded");
            TestRunner.Eq(byModel.ApiValue, byProject.ApiValue, "the estimate does not change with the grouping");
            TestRunner.Check(byProject.MissingPrices && byModel.MissingPrices, "an unpriced model triggers the hint in both modes");
            TestRunner.Check(byModel.ApiValueTip.Contains("Opus 4.1 · $10.50") && byProject.ApiValueTip.Contains("demo · $10.50") &&
                byProject.ApiValueTip.Contains("Other projects · $0.12") && byProject.ApiValueTip.EndsWith("Estimate at API prices. Your plan is billed differently.", StringComparison.Ordinal),
                "the estimate hover follows the grouping and explains the billing difference");

            UsageModel german = UsageModel.Build(store, detail, UsagePeriod.Heat, 0, today, CultureInfo.GetCultureInfo("de-DE"), true, false);
            TestRunner.Check(german.ApiValue.Contains("15,18") && german.ApiValue.Contains("$"), "local currency formatting retains the USD symbol");

            UsageModel noBreakdown = UsageModel.Build(store, detail, UsagePeriod.Heat, -1, today, En, true, true);
            TestRunner.Check(noBreakdown.ApiValue == "" && !noBreakdown.MissingPrices, "a period with no breakdown has no estimate or hint");

            var view = new UsageView();
            view.Update(byModel);
            Lay(view);
            bool? picked = null;
            view.GroupingChosen += delegate(bool project) { picked = project; };
            var buttons = All(view).OfType<RadioButton>().ToDictionary(b => AutomationProperties.GetName(b));
            TestRunner.Check(buttons["By model"].IsChecked == true && buttons["By project"].IsChecked == false, "the model switch has the model option selected");
            buttons["By project"].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            TestRunner.Eq(true, picked, "the project switch asks the controller for the project view");
            view.Update(byProject);
            Lay(view);
            TestRunner.Check(All(view).OfType<RadioButton>().Any(b => b.IsChecked == true && AutomationProperties.GetName(b) == "By project"), "the project option is selected");
            var estimate = All(view).OfType<TextBlock>().First(t => t.Text == byProject.ApiValue);
            TestRunner.Eq(byProject.ApiValueTip, estimate.ToolTip as string, "the estimate has its group split on hover");
            TestRunner.Check(Texts(view).Contains("Some models have no listed price"), "the estimate shows the missing-price hint");
            Click(view, "Details");
            Lay(view);
            TestRunner.Check(Texts(view).Contains("demo (2) · 500K"), "expanded Details draws the project figures");

            var legacyOnly = UsageModel.Build(store, new UsageDetailStore(), UsagePeriod.Heat, 0, today, En, true, true);
            view.Update(legacyOnly);
            Lay(view);
            TestRunner.Check(Texts(view).Contains("No project breakdown for these dates.") && !Texts(view).Any(t => t.Contains("API value")), "legacy-only usage has a project status without an estimate");
            TestRunner.Eq(2, All(view).OfType<RadioButton>().Count(), "the grouping switch works before a breakdown is available");
        }

        static void UsageGroupingSetting()
        {
            string path = Path.Combine(TestRunner.NewTempDir(), "config.json");
            var config = Config.Load(path);
            TestRunner.Check(!config.UsageByProject, "the Usage page defaults to By model");
            config.UsageByProject = true;
            TestRunner.Check(config.Save(path) && Config.Load(path).UsageByProject, "By project survives a restart");
            config.UsageByProject = false;
            TestRunner.Check(config.Save(path) && !Config.Load(path).UsageByProject, "switching back to By model survives a restart");
        }

        static void Click(DependencyObject root, string text)
        {
            TextBlock t = All(root).OfType<TextBlock>().FirstOrDefault(x => x.Text == text);
            DependencyObject at = t;
            while (at != null && !(at is Border && ((Border)at).Cursor == System.Windows.Input.Cursors.Hand)) at = VisualTreeHelper.GetParent(at);
            var b = at as Border;
            if (b == null) { TestRunner.Check(false, text + " is a button"); return; }
            b.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
        }

        static void Lay(FrameworkElement e)
        {
            e.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            e.Arrange(new Rect(e.DesiredSize));
            e.UpdateLayout();
        }

        static List<string> Texts(DependencyObject root) { return All(root).OfType<TextBlock>().Select(t => t.Text).ToList(); }

        static IEnumerable<DependencyObject> All(DependencyObject root)
        {
            yield return root;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                foreach (DependencyObject d in All(VisualTreeHelper.GetChild(root, i))) yield return d;
        }
    }
}
