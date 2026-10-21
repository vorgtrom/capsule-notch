using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
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
