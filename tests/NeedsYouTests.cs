using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Capsule
{
    internal static class NeedsYouTests
    {
        public static void Run()
        {
            ShowsOnlyTheCurrentRequestForEachProvider();
            ReviewUsesExactIdWithoutExtending();
            PanelShowsEitherSection();
        }

        static void ShowsOnlyTheCurrentRequestForEachProvider()
        {
            var claude = new PromptBroker(() => false);
            var codex = new PromptBroker(() => false);
            Hold(codex, "codex", "CodexProject", 1000, ToolNames.AskUserQuestion);
            Hold(claude, "claude", "ClaudeProject", 2000, ToolNames.ExitPlanMode);
            Hold(claude, "claude", "QueuedProject", 3000);
            NeedsYouTile tile = NeedsYouTile.From(claude, codex, null, 4000);
            TestRunner.Eq(2, tile.Requests.Count, "one row per enabled provider");
            TestRunner.Eq("codex", tile.Requests[0].Provider, "oldest request first across providers");
            TestRunner.Eq("Question", tile.Requests[0].Type, "question type shows without question text");
            TestRunner.Eq("Plan review", tile.Requests[1].Type, "plan type shows without plan text");
            TestRunner.Eq("ClaudeProject", tile.Requests[1].Project, "shows current project");
            TestRunner.Eq(1, tile.Requests[1].Queued, "counts the other held requests for the provider");
            TestRunner.Eq("Waiting 2 s", tile.Requests[1].Wait(tile.Now), "shows wait from arrival time");
            TestRunner.Eq(1, NeedsYouTile.From(null, codex, null, 4000).Requests.Count, "disabled providers are omitted");
        }

        static void ReviewUsesExactIdWithoutExtending()
        {
            int replies = 0;
            bool foreground = false;
            var claude = new PromptBroker(() => false);
            var codex = new PromptBroker(() => foreground);
            var first = Hold(claude, "claude", "First", 1000, reply: r => replies++);
            var next = Hold(claude, "claude", "Next", 2000, reply: r => replies++);
            var other = Hold(codex, "codex", "Other", 2000, reply: r => replies++);
            NeedsYouTile tile = NeedsYouTile.From(claude, codex, null, 4000);
            PendingReview claudeRow = tile.Requests[0], codexRow = tile.Requests[1];
            long firstDeadline = first.Deadline, otherDeadline = other.Deadline;
            TestRunner.Eq(first, NeedsYouTile.Review(claudeRow, claude, codex, 4000), "review opens only the selected request");
            TestRunner.Eq(firstDeadline, first.Deadline, "review does not extend the countdown");
            TestRunner.Eq(otherDeadline, other.Deadline, "review does not extend another provider's countdown");
            TestRunner.Eq(0, replies, "review does not reply");
            TestRunner.Eq(null, NeedsYouTile.Review(new PendingReview { Provider = "codex", Id = first.Id }, claude, codex, 4000),
                "a wrong-provider request ID never opens a card");
            TestRunner.Eq(null, NeedsYouTile.Review(claudeRow, claude, codex, firstDeadline), "expired row cannot open the next request");
            TestRunner.Eq(next, claude.Current, "the next request remains held");
            TestRunner.Eq(1, replies, "only the expired request returns to its host");
            foreground = true;
            TestRunner.Eq(null, NeedsYouTile.Review(codexRow, claude, codex, firstDeadline), "host in front makes the row stale");
            TestRunner.Eq(2, replies, "foreground request returns to its host");
            TestRunner.Eq(next, claude.Current, "foreground fallback does not release the other provider");
        }

        static void PanelShowsEitherSection()
        {
            var claude = new PromptBroker(() => false);
            Hold(claude, "claude", "Example", 1000);
            var view = new PanelView();
            var model = new PanelModel { Ideas = new IdeasTile(), Claude = new UsageTile { Provider = "claude" }, Codex = new UsageTile { Provider = "codex" } };
            model.PanelHome = "calendar";
            model.Calendar = new CalendarTile();
            model.NeedsYou = NeedsYouTile.From(claude, null, null, 6000);
            view.Update(model);
            TextBlock title = Find<TextBlock>(view, t => t.Text == "Needs you");
            TestRunner.Check(title != null && Grid.GetRow((Border)((StackPanel)title.Parent).Parent) == 0, "a pending request puts Needs you in the first row");
            TestRunner.Check(Find<TextBlock>(view, t => t.Text == "Claude · Example") != null, "agent-only tile shows the agent");
            TestRunner.Check(Find<TextBlock>(view, t => t.Text == "GitHub checks") == null, "disabled GitHub section is absent");
            PendingReview clicked = null;
            view.ReviewRequested += row => clicked = row;
            Button button = Find<Button>(view, b => Equals(b.Content, "Review"));
            if (button != null) button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            TestRunner.Eq(claude.Current.Id, clicked == null ? 0 : clicked.Id, "Review forwards the displayed request ID");

            var checks = new GitHubChecks { Repo = "owner/repo", LastChecked = 1000 };
            checks.Failures.Add(new GitHubFailure { Pull = 15, Run = 1234, Workflow = "Build", Conclusion = "failure" });
            checks.Uncertain.Add(16);
            checks.MorePulls = true;
            model.NeedsYou = NeedsYouTile.From(null, null, checks, 6000);
            view.Update(model);
            TestRunner.Check(Find<TextBlock>(view, t => t.Text == "No pending requests") != null, "GitHub-only tile shows the empty agent section");
            TestRunner.Check(Find<TextBlock>(view, t => t.Text == "PR #15 · Build · Failed") != null, "GitHub-only tile shows the failure");
            TestRunner.Check(Find<TextBlock>(view, t => t.Text == "PR #16 · Couldn't see the latest run.") != null, "GitHub-only tile explains uncertain coverage");
            TestRunner.Check(Find<TextBlock>(view, t => t.Text.StartsWith("Last checked ")) != null, "GitHub-only tile shows last successful check");
            TestRunner.Check(Find<TextBlock>(view, t => t.Text == "Capsule checked only the 30 newest open PRs.") != null, "tile explains truncated pull coverage");
            string opened = null;
            view.GitHubRunOpened += (repo, run) => opened = repo + ":" + run;
            Button open = Find<Button>(view, b => Equals(b.Content, "Open run"));
            if (open != null) open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            TestRunner.Eq("owner/repo:1234", opened, "Open run forwards only configured repo and numeric run ID");
            view.ShowReviewExpired();
            TestRunner.Check(Find<TextBlock>(view, t => t.Text == "This request is no longer pending.") != null, "stale review displays feedback");
            Border needsTile = (Border)((StackPanel)title.Parent).Parent;
            TestRunner.Check(Grid.GetRow(needsTile) > 0, "without pending requests the calendar preference takes priority again");
            view.SetPanelHome("needs_you");
            TestRunner.Eq(0, Grid.GetRow(needsTile), "the Needs you preference promotes the tile without a request");

            model.NeedsYou = NeedsYouTile.From(null, null, null, 6000);
            view.Update(model);
            TestRunner.Check(Find<TextBlock>(view, t => t.Text == "No pending requests") != null &&
                Find<TextBlock>(view, t => t.Text == "GitHub checks") == null, "empty tile shows only the agent status with monitoring off");
            model.NeedsYou = NeedsYouTile.From(claude, null, new GitHubChecks { Repo = "owner/repo", Status = "Waiting to check GitHub." }, 6000);
            view.Update(model);
            TestRunner.Check(Find<TextBlock>(view, t => t.Text == "Waiting to check GitHub.") != null &&
                Find<Button>(view, b => Equals(b.Content, "Review")) != null, "agent pending and GitHub status appear together");
        }

        static T Find<T>(DependencyObject root, Func<T, bool> match) where T : UIElement
        {
            UIElement element = root as UIElement;
            if (element != null && element.Visibility != Visibility.Visible) return null;
            T candidate = root as T;
            if (candidate != null && match(candidate)) return candidate;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                T child = Find(VisualTreeHelper.GetChild(root, i), match);
                if (child != null) return child;
            }
            return null;
        }

        static HeldPrompt Hold(PromptBroker broker, string provider, string project, long at, string tool = ToolNames.Bash, Action<PromptReply> reply = null)
        {
            return broker.Arrive(new PromptRequest { Provider = provider, Project = project, At = at, ToolName = tool }, reply, at);
        }
    }
}
