using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Capsule
{
    public static class CardViewTests
    {
        const long Now = 1790690940000;
        static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

        public static void Run()
        {
            SizesTheCard();
            LongTextStaysInsideTheSessionRow();
            OutlineHoldsTheCardAndItsPointer();
            ShadowIsDrawnOutsideTheCard();
            BarsFollowTheirActualWidth();
            AnOpenCardResizesWhenItsContentChanges();
            AQueueBadgeResizesAnOpenCard();
        }

        static void AQueueBadgeResizesAnOpenCard()
        {
            foreach (bool pointRight in new[] { true, false })
            {
                var view = new CardView();
                var request = new PromptRequest { SessionId = "a", ToolName = "Bash" };
                request.ToolInput["command"] = "npm test";
                var held = new HeldPrompt { Id = 1, Request = request };
                view.ShowPrompt(PromptCardModel.From(held, 1, 60000), pointRight);
                ArrangeCard(view);
                double before = view.DesiredSize.Height;
                UIElement title = view.Prompt.Children[0];

                view.ShowPrompt(PromptCardModel.From(held, 2, 59000), pointRight);
                TestRunner.Eq("1 of 2", view.Prompt.Model.Position, "approval #1 shows its new queue badge");
                TestRunner.Check(ReferenceEquals(title, view.Prompt.Children[0]), "the queue badge changes without rebuilding the request");
                CheckCardFits(view, "a queue badge wraps the title");
                TestRunner.Check(view.DesiredSize.Height > before, "the wrapped title makes room for its extra line");

                view.ShowPrompt(PromptCardModel.From(held, 1, 58000), pointRight);
                CheckCardFits(view, "removing a queue badge unwraps the title");
                TestRunner.Near(before, view.DesiredSize.Height, "the card shrinks when the queue badge leaves");
            }
        }

        static void AnOpenCardResizesWhenItsContentChanges()
        {
            foreach (bool pointRight in new[] { true, false })
            {
                var view = new CardView();
                var usage = new CardModel { Provider = "codex", Title = "Codex", Footer = "Checked just now" };
                usage.Rows.Add(new CardRow { Label = "Current session", UsedText = "41% used" });
                usage.Rows.Add(new CardRow { Label = "Weekly", UsedText = "15% used" });
                view.Show(usage, pointRight);
                ArrangeCard(view);

                var approval = new PromptCardModel { Id = 1, Provider = "codex", AppName = "Codex / Work",
                    Title = "Codex / Work wants to run a command", What = "Bash: Write-Output CapsuleNativeTestPassed",
                    Detail = "Live Capsule test. This only prints a message.", Project = "command-allow",
                    Countdown = "Goes to Codex / Work in 57 s" };
                view.ShowPrompt(approval, pointRight);
                CheckCardFits(view, "usage to Codex approval");

                var question = new PromptCardModel { Id = 2, Provider = "codex", AppName = "Codex / Work",
                    Kind = PromptCardModel.Question, Title = "Codex / Work asked you a question",
                    QuestionText = "What should the release notes say?", OtherOpen = true, IsLast = true,
                    OtherText = "Keep every button on the card.", Countdown = "Goes to Codex / Work in 52 s" };
                view.ShowPrompt(question, pointRight);
                CheckCardFits(view, "approval to question with Other open");

                view.Show(usage, pointRight);
                CheckCardFits(view, "question back to usage");
            }
        }

        static void ArrangeCard(CardView view)
        {
            view.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            view.Arrange(new Rect(view.DesiredSize));
            view.UpdateLayout();
        }

        static void CheckCardFits(CardView view, string transition)
        {
            view.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double measured = view.DesiredSize.Height;
            ArrangeCard(view);
            // HoverCard sizes its native window before the queued layout pass.
            var panel = (Border)view.Children[1];
            TestRunner.Near(panel.ActualHeight + 2 * CardView.ShadowMargin, measured, transition + ": measured height includes the new content");
            TestRunner.Check(Math.Abs(panel.ActualHeight - view.Outline.Bounds.Height) < 0.01, transition + ": glass fits the new content");
        }

        static void BarsFollowTheirActualWidth()
        {
            Grid bar = CardView.Bar(50, Palette.Green);
            foreach (double width in new[] { 120.0, 80.0 })
            {
                bar.Measure(new Size(width, 17));
                bar.Arrange(new Rect(0, 0, width, 17));
                bar.UpdateLayout();
                TestRunner.Near(width / 2, ((Border)bar.Children[1]).ActualWidth, "50% stays half the visible track when scrolling reduces the tile width");
            }
        }

        static Reading ClaudeReading()
        {
            var r = new Reading { Provider = "claude", Status = "ok", HeadlineId = "five_hour", Plan = "Max", DataAtMs = Now };
            r.Windows.Add(new LimitWindow { Id = "five_hour", Label = "Current session", Used = 73, ResetsAtMs = Now + 3000000 });
            return r;
        }

        static void SizesTheCard()
        {
            var sessions = new List<SessionStatus> { new SessionStatus { SessionId = "a", State = States.Waiting, At = Now, Since = Now, Project = "confetti", Reason = "asked you a question" } };
            CardModel m = CardModel.From(ClaudeReading(), sessions, true, Now, En);
            var view = new CardView();
            view.Show(m, true);
            view.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double room = 2 * CardView.ShadowMargin;
            TestRunner.Near(CardView.CardWidth + CardView.PointerWidth + room, view.DesiredSize.Width, "card plus pointer width, and room for the shadow");
            TestRunner.Check(view.DesiredSize.Height - room > 150 && view.DesiredSize.Height - room < 400, "a sensible height: " + view.DesiredSize.Height);
            view.Show(m, false);
            view.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            TestRunner.Near(CardView.CardWidth + CardView.PointerWidth + room, view.DesiredSize.Width, "pointer on the other side, same width");
        }

        static void OutlineHoldsTheCardAndItsPointer()
        {
            var view = new CardView();
            view.Show(CardModel.From(ClaudeReading(), new List<SessionStatus>(), true, Now, En), true);
            view.SetPointer(60);
            Rect b = view.Outline.Bounds;
            double height = view.DesiredSize.Height - 2 * CardView.ShadowMargin;
            TestRunner.Near(0, b.Left, "the outline starts at the card's left");
            TestRunner.Near(CardView.CardWidth + CardView.PointerWidth, b.Right, "and ends at the pointer's tip");
            TestRunner.Near(height, b.Height, "it is as tall as the card");
            TestRunner.Check(view.Outline.FillContains(new Point(CardView.CardWidth + CardView.PointerWidth - 2, 60)), "the pointer's tip is at its y");
            TestRunner.Check(!view.Outline.FillContains(new Point(CardView.CardWidth + 2, 20)), "and only there");
            view.Show(CardModel.From(ClaudeReading(), new List<SessionStatus>(), true, Now, En), false);
            view.SetPointer(60);
            TestRunner.Check(view.Outline.FillContains(new Point(2, 60)) && !view.Outline.FillContains(new Point(2, 20)), "a left-edge card points left");

            // Show measures the card before giving its glass the outline: the glass must still be drawn.
            var fresh = new CardView();
            fresh.Show(CardModel.From(ClaudeReading(), new List<SessionStatus>(), true, Now, En), true);
            var host = new Grid();
            host.Children.Add(fresh);
            host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            host.Arrange(new Rect(host.DesiredSize));
            host.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(host.ActualWidth), (int)Math.Ceiling(host.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(host);
            var pixel = new byte[4];
            bitmap.CopyPixels(new Int32Rect((int)(CardView.ShadowMargin + 100), (int)(CardView.ShadowMargin + 4), 1, 1), pixel, 4, 0);
            TestRunner.Check(pixel[3] > 200, "the card's glass is drawn behind its text (alpha " + pixel[3] + ")");
        }

        static void ShadowIsDrawnOutsideTheCard()
        {
            // The shadow sits one canvas deeper than the glass itself, so it is clipped away unless that canvas is measured
            // again with each new outline too. Look for it just outside the card, in its room around, on a transparent host.
            var card = new CardView();
            card.Show(CardModel.From(ClaudeReading(), new List<SessionStatus>(), true, Now, En), true);
            var host = new Grid();
            host.Children.Add(card);
            host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            host.Arrange(new Rect(host.DesiredSize));
            host.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(host.ActualWidth), (int)Math.Ceiling(host.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(host);
            var pixel = new byte[4];
            int below = (int)Math.Round(host.ActualHeight - CardView.ShadowMargin) + 2;   // 2 px under the card's bottom edge
            bitmap.CopyPixels(new Int32Rect((int)(CardView.ShadowMargin + 150), below, 1, 1), pixel, 4, 0);
            TestRunner.Check(pixel[3] > 0, "the card's shadow is drawn just outside its edge (alpha " + pixel[3] + ")");
        }

        static void LongTextStaysInsideTheSessionRow()
        {
            const string LongProject = "a-really-long-project-folder-name-that-goes-on";
            const string LongStatus = "waiting · needs permission: mcp__plugin_claude-mem_mcp-search__smart_search";
            var sessions = new List<SessionStatus>
            {
                new SessionStatus { SessionId = "a", State = States.Waiting, At = Now, Since = Now, Project = "confetti", Reason = "needs permission: mcp__plugin_claude-mem_mcp-search__smart_search" },
                new SessionStatus { SessionId = "b", State = States.Working, At = Now - 1000, Since = Now - 2 * 60000, Project = LongProject },
            };
            var view = new CardView();
            view.Show(CardModel.From(ClaudeReading(), sessions, true, Now, En), true);
            view.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            view.Arrange(new Rect(view.DesiredSize));
            TextBlock project = FindText(view, "confetti");
            TextBlock status = FindText(view, LongStatus);
            TextBlock longProject = FindText(view, LongProject);
            TextBlock working = FindText(view, "working · 2m");
            if (project == null || status == null || longProject == null || working == null)
            {
                TestRunner.Check(false, "the session texts are on the card");
                return;
            }
            TestRunner.Check(project.ActualWidth >= NaturalWidth(project) - 1, "a long status leaves the project name whole: " + project.ActualWidth);
            TestRunner.Check(project.ActualWidth + 8 + status.ActualWidth <= CardView.CardWidth - 2 * CardView.Inset + 1, "and is trimmed to fit the row");
            TestRunner.Check(longProject.ActualWidth <= CardView.ProjectMaxWidth + 1, "a long project name stops at its limit: " + longProject.ActualWidth);
            TestRunner.Check(working.ActualWidth >= NaturalWidth(working) - 1, "leaving its status whole");
        }

        // The TextBlock showing exactly this text, searched through the visual tree.
        static TextBlock FindText(DependencyObject root, string text)
        {
            var t = root as TextBlock;
            if (t != null && t.Text == text) return t;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                TextBlock found = FindText(VisualTreeHelper.GetChild(root, i), text);
                if (found != null) return found;
            }
            return null;
        }

        // The width a TextBlock's text takes with no limit, to tell whether it was squeezed.
        static double NaturalWidth(TextBlock t)
        {
            var copy = new TextBlock { Text = t.Text, FontSize = t.FontSize, FontFamily = t.FontFamily, FontWeight = t.FontWeight };
            copy.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return copy.DesiredSize.Width;
        }
    }
}
