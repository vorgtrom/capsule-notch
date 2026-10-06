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
