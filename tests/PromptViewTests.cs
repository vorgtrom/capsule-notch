using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Capsule
{
    public static class PromptViewTests
    {
        // The view's clock for the tests. By default every read moves it 10 s on, so a press is never inside the window that
        // follows a request first being shown; the tests of that window stop it (Step = 0) and set it themselves.
        static long fakeNow = 1000000, step = 10000;

        public static void Run()
        {
            Func<long> real = PromptView.Clock;
            PromptView.Clock = delegate { fakeNow += step; return fakeNow; };
            try { RunAll(); }
            finally { PromptView.Clock = real; }
        }

        static void RunAll()
        {
            AClickCantLandOnARequestThatJustCameUp();
            AlwaysAllowSaysWhatItDoesOnTheCard();
            ARequestBehindTheShownOneDoesNotRedrawIt();
            ADoubleClickIsNoClick();
            ACommandIsSeenWhole();
            AnApprovalHasItsButtons();
            TheSummaryTakesAtMostTwoLines();
            APlanHasItsOwnButtons();
            AQuestionShowsItsOptions();
            OtherOpensABoxThatSurvivesRedraws();
            AQuestionTheCardCantShowOnlyGoesToClaude();
            ClicksBecomeActions();
            TypingBecomesAnActionButRedrawsDont();
            TheCountdownChangesInPlace();
            TheUsageComesBack();
            EveryActionNamesTheRequestShown();
            AStrayMouseUpDoesNotFireAButton();
            TheOtherBoxStopsGrowing();
            TheOtherBoxIsTallFromTheStart();
            ARedrawOfTheSameModelKeepsTheTimeLeftAndWhatWasTyped();
            ARequestThatLeavesTheCardLeavesNoTextInIt();
            TheDimmedPillIsReadableOnBothThemes();
            ThePillsRowsAndBoxAreNamedForScreenReaders();
            EnterInTheOtherBoxIsNextOrSend();
            LeavingAButtonCancelsItsPress();
            AReleaseAfterTheTargetChangedFiresNothing();
            AReleaseAfterTheStepChangedFiresNothing();
            SendIsLitWhenTheLastQuestionIsAnswered();
            TheOtherBoxHasItsLimit();
        }

        // A click aimed at the request that was on the card must not land on the one that has just taken its place: a press
        // that begins within the window (500 ms, or the system's double-click time if longer) of a new request first being
        // built does nothing. Rebuilding the same request (the countdown, typing, a step) doesn't start the window again.
        static void AClickCantLandOnARequestThatJustCameUp()
        {
            long window = PromptView.PressDelayMs;
            TestRunner.Check(window >= 500, "the window is at least 500 ms (" + window + ")");
            step = 0;
            try
            {
                fakeNow = 5000000;
                var view = new CardView();
                view.ShowPrompt(PromptCardModel.From(Held("Bash", "{\"command\":\"rm a\"}", 1), 1, 60000), true);
                fakeNow += 5000;
                view.ShowPrompt(PromptCardModel.From(Held("Bash", "{\"command\":\"rm b\"}", 2), 1, 60000), true);   // B takes A's place
                view.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                view.Arrange(new Rect(view.DesiredSize));
                List<string> got = ActionsOf(view);
                fakeNow += window - 1;
                Click(ButtonFor(view, "Allow"));
                Click(ButtonFor(view, "Deny"));
                TestRunner.Eq(0, got.Count, "a press just inside the window on the new request: nothing fires");
                fakeNow += 1;
                Click(ButtonFor(view, "Allow"));
                TestRunner.Eq("allow", string.Join(", ", got), "a press once the window has passed fires");

                // A rebuild of the same request (its countdown, a step) doesn't start the window again.
                got.Clear();
                fakeNow += 1000;
                view.ShowPrompt(PromptCardModel.From(Held("Bash", "{\"command\":\"rm b\"}", 2), 1, 59000), true);
                view.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                view.Arrange(new Rect(view.DesiredSize));
                Click(ButtonFor(view, "Deny"));
                TestRunner.Eq("deny", string.Join(", ", got), "the same request drawn again doesn't block a click");

                // Options, Other…, Next and the plan's buttons are decisions too.
                HeldPrompt q = Held("AskUserQuestion", Questions, 3);
                view = new CardView();
                view.ShowPrompt(PromptCardModel.From(q, 1, 60000), true);
                got = ActionsOf(view);
                Click(ButtonFor(view, "Blue"));
                Click(ButtonFor(view, "Other…"));
                Click(ButtonFor(view, "Next"));
                Click(ButtonFor(view, "Answer in Claude"));
                TestRunner.Eq(0, got.Count, "a new question's options, Other…, Next and Answer in Claude are ignored inside the window");
                fakeNow += window;
                Click(ButtonFor(view, "Blue"));
                TestRunner.Eq("choose 1", string.Join(", ", got), "and work after it");
                view = new CardView();
                view.ShowPrompt(PromptCardModel.From(Held("ExitPlanMode", "{}", 4), 1, 60000), true);
                got = ActionsOf(view);
                Click(ButtonFor(view, "Approve plan"));
                Click(ButtonFor(view, "Keep planning"));
                TestRunner.Eq(0, got.Count, "a new plan's Approve plan and Keep planning too");

                // A press that began inside the window doesn't fire on its release after the window.
                view = new CardView();
                view.ShowPrompt(PromptCardModel.From(Held("Bash", "{\"command\":\"rm c\"}", 5), 1, 60000), true);
                got = ActionsOf(view);
                Border allow = ButtonFor(view, "Allow");
                Press(allow);
                fakeNow += window + 100;
                Release(allow);
                TestRunner.Eq(0, got.Count, "pressed inside the window, released after it: nothing fires");
            }
            finally { step = 10000; }
        }

        static string TipText(FrameworkElement e)
        {
            var tip = e.ToolTip as ToolTip;
            if (tip == null) return e.ToolTip as string;
            var scroll = tip.Content as ScrollViewer;
            var block = (scroll == null ? tip.Content : scroll.Content) as TextBlock;
            return block != null ? block.Text : tip.Content as string;
        }

        static TextBlock Summary(CardView view)
        {
            return All(view).OfType<TextBlock>().First(t => t.Text.StartsWith("Bash: ", StringComparison.Ordinal));
        }

        // A command the user approves is seen whole (F7): each line break is a visible mark, and a summary that is cut, by
        // length or by the two lines it has, carries the whole command in a tooltip.
        static void ACommandIsSeenWhole()
        {
            CardView view = Shown(PromptCardModel.From(Held("Bash", "{\"command\":\"echo a\\nrm b\"}"), 1, 60000));
            TextBlock what = Summary(view);
            TestRunner.Eq("Bash: echo a ⏎ rm b", what.Text, "a two-line command shows its break");
            TestRunner.Eq("echo a\nrm b", TipText(what), "even a short command has its exact text in the tooltip");

            string cutByLength = string.Join(" ", Enumerable.Repeat("npm run build", 30));
            view = Shown(PromptCardModel.From(Held("Bash", "{\"command\":\"" + cutByLength + "\"}"), 1, 60000));
            what = Summary(view);
            TestRunner.Eq(cutByLength, TipText(what), "a command cut by length has the whole of it in the summary's tooltip");
            TestRunner.Check(what.Text.EndsWith("…", StringComparison.Ordinal), "and the shown text still ends in …");

            string cutByLines = string.Join(" ", Enumerable.Repeat("word", 30));   // 149 characters: under the length limit, over two lines
            view = Shown(PromptCardModel.From(Held("Bash", "{\"command\":\"" + cutByLines + "\"}"), 1, 60000));
            what = Summary(view);
            TestRunner.Check(what.ActualHeight <= 2 * PromptView.WhatLineHeight + 0.5 && !what.Text.EndsWith("…", StringComparison.Ordinal), "a command that fits the length but not two lines is trimmed by the two lines");
            TestRunner.Eq(cutByLines, TipText(what), "and has the whole command in the tooltip too");

            string lines = "first line\\nsecond line\\n" + new string('x', 200);
            view = Shown(PromptCardModel.From(Held("Bash", "{\"command\":\"" + lines + "\"}"), 1, 60000));
            TestRunner.Eq("first line\nsecond line\n" + new string('x', 200), TipText(Summary(view)), "the tooltip keeps the line breaks as real ones");

            string huge = new string('y', 6000);
            view = Shown(PromptCardModel.From(Held("Bash", "{\"command\":\"" + huge + "\"}"), 1, 60000));
            string tip = TipText(Summary(view));
            TestRunner.Check(tip != null && tip.Length == PromptCardModel.MaxFull && tip.EndsWith("…", StringComparison.Ordinal), "a command over 4000 characters is capped there");
        }

        // A second click of a double click is not a click: it is the press that lands when the card has changed under the
        // first.
        static void ADoubleClickIsNoClick()
        {
            CardView view = Shown(Approval(false));
            List<string> got = ActionsOf(view);
            Border allow = ButtonFor(view, "Allow");
            Press(allow, 2);
            Release(allow);
            TestRunner.Eq(0, got.Count, "a press with ClickCount 2 does nothing");
            Press(allow, 3);
            Release(allow);
            TestRunner.Eq(0, got.Count, "nor does a third");
            Press(allow, 1);
            Release(allow);
            TestRunner.Eq("allow", string.Join(", ", got), "a first click does");
        }

        // The card's window is sized once, when it is shown, with one line typed in the box: the box must already be as
        // tall as it ever gets, or Send and Answer in Claude would be pushed below the window while typing.
        static void TheOtherBoxIsTallFromTheStart()
        {
            HeldPrompt held = Held("AskUserQuestion", Questions);
            held.Flow.ToggleOther();
            held.Flow.SetOther("w");
            CardView one = Shown(PromptCardModel.From(held, 1, 60000));
            TestRunner.Check(one.Prompt.OtherBox.ActualHeight >= PromptView.OtherMaxHeight - 0.5, "the Other… box is three lines tall with one typed (" + one.Prompt.OtherBox.ActualHeight + ")");
            double window = one.DesiredSize.Height;
            held.Flow.SetOther("one two three four five six seven eight nine ten eleven twelve thirteen fourteen fifteen sixteen seventeen eighteen nineteen twenty twenty-one twenty-two twenty-three twenty-four twenty-five");
            CardView three = Shown(PromptCardModel.From(held, 1, 60000));
            TestRunner.Check(three.Prompt.OtherBox.ActualHeight <= PromptView.OtherMaxHeight + 0.5, "three lines typed fit it");
            TestRunner.Near(window, three.DesiredSize.Height, "so typing them doesn't change the card's height");
            Border next = ButtonFor(three, "Next");
            Border inClaude = ButtonFor(three, "Answer in Claude");
            double bottom = Math.Max(next.TranslatePoint(new Point(0, next.ActualHeight), three).Y, inClaude.TranslatePoint(new Point(0, inClaude.ActualHeight), three).Y);
            TestRunner.Check(bottom <= window - CardView.ShadowMargin, "and Next and Answer in Claude stay inside the window sized for one line (" + bottom + " of " + window + ")");
        }

        // The Controller redraws a model after a DPI change: it must show the time left and the text typed since it was built.
        static void ARedrawOfTheSameModelKeepsTheTimeLeftAndWhatWasTyped()
        {
            HeldPrompt held = Held("AskUserQuestion", Questions);
            held.Flow.ToggleOther();
            PromptCardModel m = PromptCardModel.From(held, 1, 60000);
            var view = new CardView();
            view.ShowPrompt(m, true);
            view.Prompt.SetCountdown("Goes to Claude in 12 s");
            view.Prompt.OtherBox.Text = "Teal";
            view.Prompt.SetCanAdvance(true);
            view.ShowPrompt(m, true);
            view.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            view.Arrange(new Rect(view.DesiredSize));
            TestRunner.Check(Texts(view).Contains("Goes to Claude in 12 s") && !Texts(view).Contains("Goes to Claude in 60 s"), "the model drawn again shows the time left now, not when it was built");
            TestRunner.Eq("Teal", view.Prompt.OtherBox.Text, "keeps what was typed");
            Border next = ButtonFor(view, "Next");
            TestRunner.Check(next != null && next.Opacity == 1, "and that the question has an answer");
        }

        static void ARequestThatLeavesTheCardLeavesNoTextInIt()
        {
            HeldPrompt held = Held("AskUserQuestion", Questions);
            held.Flow.ToggleOther();
            held.Flow.SetOther("secret answer");
            CardView view = Shown(PromptCardModel.From(held, 1, 60000));
            TestRunner.Check(Texts(view).Contains("Which colour?") && view.Prompt.OtherBox.Text == "secret answer", "the request is on the card");
            var r = new Reading { Provider = "claude", Status = "ok", Plan = "Max" };
            view.Show(CardModel.From(r, new List<SessionStatus>(), true, 1000, System.Globalization.CultureInfo.GetCultureInfo("en-US")), true);
            TestRunner.Check(Texts(view.Prompt).Count == 0 && view.Prompt.Model == null, "the usage back: the request view holds no text");
            TestRunner.Eq("", view.Prompt.OtherBox.Text, "nor what was typed");
            view = Shown(Approval(true));
            view.Prompt.Clear();
            TestRunner.Check(Texts(view.Prompt).Count == 0 && view.Prompt.Model == null && !Texts(view).Contains("Bash: npm test"), "and cleared on its own, it is empty too");
        }

        static double Channel(byte c)
        {
            double v = c / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        static double Luminance(System.Windows.Media.Color c) { return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B); }

        static System.Windows.Media.Color Over(System.Windows.Media.Color top, double opacity, System.Windows.Media.Color below)
        {
            Func<byte, byte, byte> mix = (a, b) => (byte)Math.Round(a * opacity + b * (1 - opacity));
            return System.Windows.Media.Color.FromRgb(mix(top.R, below.R), mix(top.G, below.G), mix(top.B, below.B));
        }

        // Next or Send before the question has an answer is drawn at PromptView.Dimmed over the card: its text on its own
        // background must still read (3:1), on the light theme as on the dark.
        static void TheDimmedPillIsReadableOnBothThemes()
        {
            foreach (bool light in new[] { true, false })
            {
                Theme theme = Theme.For(light, false);
                System.Windows.Media.Color card = System.Windows.Media.Color.FromRgb(theme.Surface.R, theme.Surface.G, theme.Surface.B);
                System.Windows.Media.Color text = Over(theme.Text, PromptView.Dimmed, card);
                System.Windows.Media.Color pill = Over(theme.Track, PromptView.Dimmed, card);
                double a = Luminance(text), b = Luminance(pill);
                double ratio = (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
                TestRunner.Check(ratio >= 3.0, "the dimmed Next pill reads on the " + (light ? "light" : "dark") + " theme (contrast " + Math.Round(ratio, 2) + ")");
            }
        }

        static void ThePillsRowsAndBoxAreNamedForScreenReaders()
        {
            CardView view = Shown(Approval(true));
            foreach (string b in new[] { "Allow", "Always allow", "Deny", "Answer in Claude" })
                TestRunner.Eq(b, System.Windows.Automation.AutomationProperties.GetName(ButtonFor(view, b)), "the " + b + " button is named");
            HeldPrompt held = Held("AskUserQuestion", Questions);
            held.Flow.Choose(0);
            view = Shown(PromptCardModel.From(held, 1, 60000));
            Func<string, string> name = text => System.Windows.Automation.AutomationProperties.GetName(ButtonFor(view, text));
            TestRunner.Check(name("Red").StartsWith("Red", StringComparison.Ordinal) && name("Red").Contains("Warm") && name("Red").Contains("chosen"), "an option row is named by its label, description and state: " + name("Red"));
            TestRunner.Check(name("Blue").StartsWith("Blue", StringComparison.Ordinal) && !name("Blue").Contains("chosen"), "one not chosen says nothing of it: " + name("Blue"));
            TestRunner.Check(name("Other…").StartsWith("Other…", StringComparison.Ordinal), "so is Other…");
            held.Flow.ToggleOther();
            view = Shown(PromptCardModel.From(held, 1, 60000));
            TestRunner.Check(All(view).Contains(view.Prompt.OtherBox) && !string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetName(view.Prompt.OtherBox)), "and the box you type your own answer in");
            TestRunner.Eq("Next", System.Windows.Automation.AutomationProperties.GetName(ButtonFor(view, "Next")), "Next too");
        }

        static void EnterInTheOtherBoxIsNextOrSend()
        {
            HeldPrompt held = Held("AskUserQuestion", Questions);
            held.Flow.ToggleOther();
            CardView view = Shown(PromptCardModel.From(held, 1, 60000));
            List<string> first = ActionsOf(view);
            TestRunner.Check(view.Prompt.EnterPressed(), "Enter in the box is taken");
            held.Flow.SetOther("Teal");
            held.Flow.Next();
            held.Flow.ToggleOther();
            view = Shown(PromptCardModel.From(held, 1, 60000));
            List<string> last = ActionsOf(view);
            view.Prompt.EnterPressed();
            TestRunner.Eq("next", string.Join(", ", first), "on a question that isn't the last it is Next");
            TestRunner.Eq("send", string.Join(", ", last), "on the last it is Send");
            CardView approval = Shown(Approval(false));
            TestRunner.Check(!approval.Prompt.EnterPressed(), "with no question there is nothing for Enter to do");
        }

        // The pointer leaving a button after a press ends that press: a release somewhere it comes back to doesn't fire it.
        static void LeavingAButtonCancelsItsPress()
        {
            CardView view = Shown(Approval(false));
            List<string> got = ActionsOf(view);
            Border allow = ButtonFor(view, "Allow");
            Press(allow);
            allow.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = UIElement.MouseLeaveEvent });
            Release(allow);
            TestRunner.Eq(0, got.Count, "pressed, left, then released on it again: nothing fires");
            Press(allow);
            Release(allow);
            TestRunner.Eq("allow", string.Join(", ", got), "a press and release that stay on it do");
        }

        // The target check on its own: the same button object, the question on the same step, but another request.
        static void AReleaseAfterTheTargetChangedFiresNothing()
        {
            CardView view = Shown(PromptCardModel.From(Held("Bash", "{\"command\":\"rm a\"}", 1), 1, 60000));
            List<string> got = ActionsOf(view);
            Border allow = ButtonFor(view, "Allow");
            Press(allow);
            view.ShowPrompt(PromptCardModel.From(Held("Bash", "{\"command\":\"rm b\"}", 2), 1, 60000), true);
            Release(allow);
            TestRunner.Eq(0, got.Count, "pressed for one request, released after another took its place: nothing fires");
        }

        // The step check on its own: the same request, the same button object, but the question has moved on.
        static void AReleaseAfterTheStepChangedFiresNothing()
        {
            HeldPrompt held = Held("AskUserQuestion", Questions, 5);
            CardView view = Shown(PromptCardModel.From(held, 1, 60000));
            List<string> got = ActionsOf(view);
            Border red = ButtonFor(view, "Red");
            held.Flow.Choose(1);
            Press(red);
            held.Flow.Next();
            view.ShowPrompt(PromptCardModel.From(held, 1, 60000), true);
            Release(red);
            TestRunner.Eq(0, got.Count, "pressed on one question, released after the next took its place, same request: nothing fires");
        }

        static void SendIsLitWhenTheLastQuestionIsAnswered()
        {
            HeldPrompt held = Held("AskUserQuestion", Questions);
            held.Flow.Choose(0);
            held.Flow.Next();
            CardView view = Shown(PromptCardModel.From(held, 1, 60000));
            Border send = ButtonFor(view, "Send");
            TestRunner.Check(send != null && send.Opacity < 1, "Send is dimmed while the last question has no answer");
            view.Prompt.SetCanAdvance(true);
            TestRunner.Check(send.Opacity == 1, "lit once it has");
            view.Prompt.SetCanAdvance(false);
            TestRunner.Check(send.Opacity < 1, "and dimmed again if the answer goes");
        }

        static void TheOtherBoxHasItsLimit()
        {
            TestRunner.Eq(QuestionFlow.MaxOther, new PromptView().OtherBox.MaxLength, "the box takes no more than an answer can be");
        }

        // Sample requests only.
        static HeldPrompt Held(string tool, string input, int id = 7)
        {
            var r = new PromptRequest { SessionId = "a", ToolName = tool, Project = "confetti", Cwd = @"C:\work\confetti" };
            r.ToolInput = Json.Obj(Json.Parse(input));
            var held = new HeldPrompt { Id = id, Request = r };
            List<PromptQuestion> questions = tool == "AskUserQuestion" ? QuestionFlow.Parse(r.ToolInput) : null;
            if (questions != null) held.Flow = new QuestionFlow(questions);
            return held;
        }

        const string Questions = "{\"questions\":[" +
            "{\"question\":\"Which colour?\",\"header\":\"Colour\",\"multiSelect\":false,\"options\":[{\"label\":\"Red\",\"description\":\"Warm\"},{\"label\":\"Blue\",\"description\":\"Cool\"}]}," +
            "{\"question\":\"Which sizes?\",\"header\":\"Sizes\",\"multiSelect\":true,\"options\":[{\"label\":\"S\"},{\"label\":\"M\"},{\"label\":\"L\"}]}]}";

        static CardView Shown(PromptCardModel m)
        {
            var view = new CardView();
            view.ShowPrompt(m, true);
            view.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            view.Arrange(new Rect(view.DesiredSize));
            return view;
        }

        static IEnumerable<DependencyObject> All(DependencyObject root)
        {
            yield return root;
            foreach (object child in LogicalTreeHelper.GetChildren(root))
            {
                var d = child as DependencyObject;
                if (d == null) continue;
                foreach (DependencyObject below in All(d)) yield return below;
            }
        }

        static List<string> Texts(DependencyObject root) { return All(root).OfType<TextBlock>().Select(t => t.Text).ToList(); }

        // The button (a Border) whose own text is this.
        static Border ButtonFor(DependencyObject root, string text)
        {
            foreach (Border b in All(root).OfType<Border>())
            {
                var t = b.Child as TextBlock;
                if (t != null && t.Text == text) return b;
                var d = b.Child as DockPanel;
                if (d != null && All(d).OfType<TextBlock>().Any(x => x.Text == text)) return b;
            }
            return null;
        }

        static void Click(UIElement element)
        {
            element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseDownEvent });
            element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
        }

        static PromptCardModel Approval(bool withRule)
        {
            HeldPrompt held = Held("Bash", "{\"command\":\"npm test\"}");
            if (withRule) held.Request.Rules.Add("Bash(npm test:*)");
            return PromptCardModel.From(held, 2, 45000);
        }

        static void AnApprovalHasItsButtons()
        {
            CardView view = Shown(Approval(true));
            List<string> texts = Texts(view);
            foreach (string t in new[] { "Claude wants to run a command", "Bash: npm test", "confetti", "Goes to Claude in 45 s", "1 of 2" })
                TestRunner.Check(texts.Contains(t), "an approval shows " + t);
            foreach (string b in new[] { "Allow", "Always allow", "Deny", "Answer in Claude" })
                TestRunner.Check(ButtonFor(view, b) != null, "and has " + b);
            TestRunner.Eq("Don't ask again: Bash(npm test:*)", TipText(ButtonFor(view, "Always allow")), "Always allow names the rule it applies");
            TestRunner.Check(view.ShowingPrompt, "the card shows the request");
            TestRunner.Near(CardView.CardWidth + CardView.PointerWidth + 2 * CardView.ShadowMargin, view.DesiredSize.Width, "at the usage card's width");
            TestRunner.Check(ButtonFor(Shown(Approval(false)), "Always allow") == null, "no Always allow without a suggested rule");
        }

        // The pill says "Allow all edits" when that is what it does, the card says which directories it would open up, and
        // the tooltip says where it is kept (G1, G2).
        static void AlwaysAllowSaysWhatItDoesOnTheCard()
        {
            HeldPrompt held = Held("Bash", "{\"command\":\"npm test\"}");
            held.Request.Rules.Add("Bash(npm test:*)");
            held.Request.Destinations.Add("project");
            CardView view = Shown(PromptCardModel.From(held, 1, 45000));
            Border pill = ButtonFor(view, "Always allow");
            TestRunner.Check(pill != null && ButtonFor(view, "Allow all edits") == null, "a rule alone: Always allow");
            TestRunner.Eq("Don't ask again: Bash(npm test:*) (saved for this project)", pill == null ? null : TipText(pill), "its tooltip names where it is kept");
            TestRunner.Check(!Texts(view).Any(t => t.StartsWith("Also gives access", StringComparison.Ordinal)), "and the card has no extra line");

            held = Held("Bash", "{\"command\":\"npm test\"}");
            held.Request.Rules.Add("Accept all edits");
            held.Request.Rules.Add("Access to C:\\work\\confetti\\docs");
            held.Request.Destinations.Add("session");
            held.Request.Destinations.Add("session");
            view = Shown(PromptCardModel.From(held, 1, 45000));
            pill = ButtonFor(view, "Allow all edits");
            TestRunner.Check(pill != null && ButtonFor(view, "Always allow") == null, "accepting edits: the pill is Allow all edits, not Always allow");
            TestRunner.Eq("Applies: Accept all edits, Access to docs (for this session)", pill == null ? null : TipText(pill), "and its tooltip names what it does, and for how long");
            TestRunner.Check(Texts(view).Contains("Also gives access to docs"), "a directory is on the card itself, under the pills");
            List<string> got = ActionsOf(view);
            Click(pill);
            TestRunner.Eq("always", string.Join(", ", got), "and the pill is still the Always allow action");

            // The hook's own parse: bypassPermissions can't be named, so the pill isn't there at all.
            string bypass = "{\"session_id\":\"s-9\",\"hook_event_name\":\"PermissionRequest\",\"cwd\":\"C:\\\\work\\\\confetti\",\"tool_name\":\"Bash\",\"tool_input\":{\"command\":\"npm test\"}," +
                "\"suggestions\":[{\"type\":\"addRules\",\"rules\":[{\"toolName\":\"Bash\",\"ruleContent\":\"npm test:*\"}],\"behavior\":\"allow\",\"destination\":\"session\"}," +
                "{\"type\":\"setMode\",\"mode\":\"bypassPermissions\",\"destination\":\"session\"}]}";
            PromptRequest request = PromptRequest.FromHookInput(Json.Obj(Json.Parse(bypass)), 1000);
            view = Shown(PromptCardModel.From(new HeldPrompt { Id = 3, Request = request }, 1, 45000));
            TestRunner.Check(request != null && ButtonFor(view, "Always allow") == null && ButtonFor(view, "Allow all edits") == null && ButtonFor(view, "Allow") != null, "bypassPermissions in the suggestions: no Always allow pill, just Allow and Deny");
        }

        // A request that comes in behind the one shown changes only its "1 of N" badge and countdown: the card's elements
        // stay, so a press that is under way on one of them isn't dropped (G3).
        static void ARequestBehindTheShownOneDoesNotRedrawIt()
        {
            HeldPrompt a = Held("Bash", "{\"command\":\"npm test\"}", 11);
            a.Request.Rules.Add("Bash(npm test:*)");
            var view = new CardView();
            view.ShowPrompt(PromptCardModel.From(a, 1, 45000), true);
            List<string> got = ActionsOf(view);
            Border always = ButtonFor(view, "Always allow");
            Press(always);
            view.ShowPrompt(PromptCardModel.From(a, 2, 44000), true);   // B arrived: A is now "1 of 2"
            TestRunner.Check(ButtonFor(view, "Always allow") == always, "the pill is the same element after another request arrived");
            TestRunner.Check(Texts(view).Contains("1 of 2") && Texts(view).Contains("Goes to Claude in 44 s"), "yet the badge and the countdown are current");
            Release(always);
            TestRunner.Eq("always", string.Join(", ", got), "so the release of a press begun before B came still fires A's action");

            view.ShowPrompt(PromptCardModel.From(a, 3, 43000), true);
            TestRunner.Check(ButtonFor(view, "Always allow") == always && Texts(view).Contains("1 of 3") && !Texts(view).Contains("1 of 2"), "another one: the badge changes where it is");
            view.ShowPrompt(PromptCardModel.From(a, 1, 42000), true);
            TestRunner.Check(ButtonFor(view, "Always allow") == always && !Texts(view).Any(t => t.StartsWith("1 of", StringComparison.Ordinal)), "and goes when only one is left");
            view.ShowPrompt(PromptCardModel.From(a, 2, 41000), true);
            TestRunner.Check(ButtonFor(view, "Always allow") == always && Texts(view).Contains("1 of 2"), "and comes back");

            // Anything else that changed still redraws, and so does another request.
            HeldPrompt q = Held("AskUserQuestion", Questions, 12);
            view.ShowPrompt(PromptCardModel.From(q, 1, 45000), true);
            Border red = ButtonFor(view, "Red");
            q.Flow.Choose(0);
            view.ShowPrompt(PromptCardModel.From(q, 1, 44000), true);
            TestRunner.Check(ButtonFor(view, "Red") != red && Texts(view).Contains("✓"), "a choice made redraws the card");
            view.ShowPrompt(PromptCardModel.From(a, 1, 45000), true);
            TestRunner.Check(ButtonFor(view, "Always allow") != always, "and another request replaces it");
        }

        static void TheSummaryTakesAtMostTwoLines()
        {
            HeldPrompt held = Held("Bash", "{\"command\":\"" + string.Join(" ", Enumerable.Repeat("npm run build", 30)) + "\"}");
            CardView view = Shown(PromptCardModel.From(held, 1, 60000));
            TextBlock what = All(view).OfType<TextBlock>().First(t => t.Text.StartsWith("Bash: ", StringComparison.Ordinal));
            TestRunner.Check(what.ActualHeight > PromptView.WhatLineHeight + 1 && what.ActualHeight <= 2 * PromptView.WhatLineHeight + 0.5, "a long command takes two lines, no more (" + what.ActualHeight + ")");
            TestRunner.Check(what.TextTrimming == TextTrimming.CharacterEllipsis, "and the second ends in …");
        }

        static void APlanHasItsOwnButtons()
        {
            CardView view = Shown(PromptCardModel.From(Held("ExitPlanMode", "{\"plan\":\"# Sample plan\"}"), 1, 60000));
            List<string> texts = Texts(view);
            TestRunner.Check(texts.Contains("Claude's plan is ready") && !texts.Any(t => t.Contains("Sample plan")), "a plan says it is ready, and isn't shown");
            foreach (string b in new[] { "Approve plan", "Keep planning", "Answer in Claude" })
                TestRunner.Check(ButtonFor(view, b) != null, "a plan has " + b);
            TestRunner.Check(ButtonFor(view, "Allow") == null && ButtonFor(view, "Deny") == null, "and not Allow or Deny");
        }

        static void AQuestionShowsItsOptions()
        {
            HeldPrompt held = Held("AskUserQuestion", Questions);
            held.Flow.Choose(1);
            CardView view = Shown(PromptCardModel.From(held, 1, 60000));
            List<string> texts = Texts(view);
            foreach (string t in new[] { "Colour", "Which colour?", "Question 1 of 2", "Red", "Warm", "Blue", "Cool", "Other…" })
                TestRunner.Check(texts.Contains(t), "a question shows " + t);
            TestRunner.Check(ButtonFor(view, "Next") != null && ButtonFor(view, "Send") == null, "Next, not Send, before the last question");
            TestRunner.Eq(1, texts.Count(t => t == "✓"), "the chosen option is ticked");
            held.Flow.Next();
            held.Flow.Choose(0);
            held.Flow.Choose(2);
            view = Shown(PromptCardModel.From(held, 1, 60000));
            TestRunner.Check(ButtonFor(view, "Send") != null && ButtonFor(view, "Next") == null, "Send on the last");
            TestRunner.Eq(2, Texts(view).Count(t => t == "✓"), "a multi-select's checked boxes are ticked");
        }

        static void OtherOpensABoxThatSurvivesRedraws()
        {
            HeldPrompt held = Held("AskUserQuestion", Questions);
            var view = new CardView();
            view.ShowPrompt(PromptCardModel.From(held, 1, 60000), true);
            TextBox box = view.Prompt.OtherBox;
            TestRunner.Check(!All(view).Contains(box), "the Other… box is hidden until Other… is chosen");
            held.Flow.ToggleOther();
            held.Flow.SetOther("Teal");
            view.ShowPrompt(PromptCardModel.From(held, 1, 60000), true);
            TestRunner.Check(All(view).Contains(box) && box.Text == "Teal", "then it shows, with what was typed");
            view.ShowPrompt(PromptCardModel.From(held, 1, 60000), true);
            TestRunner.Check(All(view).Contains(box) && view.Prompt.OtherBox == box, "the same box after a redraw");
        }

        static void AQuestionTheCardCantShowOnlyGoesToClaude()
        {
            CardView view = Shown(PromptCardModel.From(Held("AskUserQuestion", "{\"questions\":\"odd\"}"), 1, 60000));
            TestRunner.Check(Texts(view).Contains("Claude asked you a question") && ButtonFor(view, "Answer in Claude") != null, "a question the card can't show: Answer in Claude");
            TestRunner.Check(ButtonFor(view, "Send") == null && ButtonFor(view, "Next") == null && ButtonFor(view, "Allow") == null, "and nothing else");
        }

        static List<string> ActionsOf(CardView view)
        {
            var got = new List<string>();
            view.Prompt.Acted += delegate(PromptAction a)
            {
                if (a.Kind == PromptAction.Touch) return;
                got.Add(a.Kind + (a.Kind == PromptAction.Choose ? " " + a.Option : "") + (a.Kind == PromptAction.OtherText ? " " + a.Text : ""));
            };
            return got;
        }

        static void ClicksBecomeActions()
        {
            CardView view = Shown(Approval(true));
            List<string> got = ActionsOf(view);
            int touches = 0;
            view.Prompt.Acted += delegate(PromptAction a) { if (a.Kind == PromptAction.Touch) touches++; };
            foreach (string b in new[] { "Allow", "Always allow", "Deny", "Answer in Claude" }) Click(ButtonFor(view, b));
            TestRunner.Eq("allow, always, deny, in-claude", string.Join(", ", got), "an approval's buttons");
            TestRunner.Eq(4, touches, "each click also counts as using the card");
            view = Shown(PromptCardModel.From(Held("ExitPlanMode", "{}"), 1, 60000));
            got = ActionsOf(view);
            Click(ButtonFor(view, "Approve plan"));
            Click(ButtonFor(view, "Keep planning"));
            TestRunner.Eq("allow, deny", string.Join(", ", got), "a plan's");
            HeldPrompt held = Held("AskUserQuestion", Questions);
            view = Shown(PromptCardModel.From(held, 1, 60000));
            got = ActionsOf(view);
            Click(ButtonFor(view, "Blue"));
            Click(ButtonFor(view, "Other…"));
            Click(ButtonFor(view, "Next"));
            TestRunner.Eq("choose 1, other, next", string.Join(", ", got), "a question's");
        }

        static void TypingBecomesAnActionButRedrawsDont()
        {
            HeldPrompt held = Held("AskUserQuestion", Questions);
            held.Flow.ToggleOther();
            held.Flow.SetOther("Te");
            var view = new CardView();
            List<string> got = ActionsOf(view);
            view.ShowPrompt(PromptCardModel.From(held, 1, 60000), true);
            TestRunner.Eq(0, got.Count, "a redraw that fills the box isn't typing");
            view.Prompt.OtherBox.Text = "Teal";
            TestRunner.Eq("other-text Teal", string.Join(", ", got), "typing is");
        }

        static void TheCountdownChangesInPlace()
        {
            HeldPrompt held = Held("AskUserQuestion", Questions);
            held.Flow.ToggleOther();
            CardView view = Shown(PromptCardModel.From(held, 1, 60000));
            TextBox box = view.Prompt.OtherBox;
            int before = All(view).Count();
            view.Prompt.SetCountdown("Goes to Claude in 12 s");
            TestRunner.Check(Texts(view).Contains("Goes to Claude in 12 s") && !Texts(view).Contains("Goes to Claude in 60 s"), "the countdown line changes");
            TestRunner.Check(All(view).Count() == before && All(view).Contains(box), "and nothing else is redrawn");
            Border next = ButtonFor(view, "Next");
            TestRunner.Check(next != null && next.Opacity < 1, "Next is dimmed while the question has no answer");
            view.Prompt.SetCanAdvance(true);
            TestRunner.Check(next.Opacity == 1, "and lit once it has");
        }

        static void Press(UIElement element) { Press(element, 1); }

        static void Press(UIElement element, int clickCount)
        {
            var e = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseDownEvent };
            System.Reflection.PropertyInfo count = typeof(MouseButtonEventArgs).GetProperty("ClickCount");
            count.GetSetMethod(true).Invoke(e, new object[] { clickCount });
            element.RaiseEvent(e);
        }

        static void Release(UIElement element)
        {
            element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
        }

        static void EveryActionNamesTheRequestShown()
        {
            HeldPrompt held = Held("AskUserQuestion", Questions, 42);
            held.Flow.ToggleOther();
            CardView view = Shown(PromptCardModel.From(held, 1, 60000));
            var targets = new List<int>();
            view.Prompt.Acted += delegate(PromptAction a) { targets.Add(a.Target); };
            Click(ButtonFor(view, "Blue"));
            Click(ButtonFor(view, "Next"));
            Click(ButtonFor(view, "Answer in Claude"));
            view.Prompt.OtherBox.Text = "Teal";
            TestRunner.Check(targets.Count >= 7 && targets.TrueForAll(t => t == 42), "choosing, Next, Answer in Claude, typing and every click's touch all carry the request's id (" + string.Join(",", targets) + ")");
            view = Shown(PromptCardModel.From(Held("Bash", "{\"command\":\"npm test\"}", 9), 1, 60000));
            targets.Clear();
            view.Prompt.Acted += delegate(PromptAction a) { targets.Add(a.Target); };
            Click(ButtonFor(view, "Allow"));
            TestRunner.Check(targets.Count == 2 && targets.TrueForAll(t => t == 9), "an approval's Allow, and its touch, carry its own");
        }

        // A button fires on the release that ends its own press, not on any release that lands on it: the card may have
        // moved on to the next request between the press and the release.
        static void AStrayMouseUpDoesNotFireAButton()
        {
            CardView view = Shown(PromptCardModel.From(Held("Bash", "{\"command\":\"rm a\"}", 1), 2, 60000));
            List<string> got = ActionsOf(view);
            Press(ButtonFor(view, "Allow"));
            view.ShowPrompt(PromptCardModel.From(Held("Bash", "{\"command\":\"rm b\"}", 2), 1, 60000), true);
            view.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            view.Arrange(new Rect(view.DesiredSize));
            Release(ButtonFor(view, "Allow"));
            TestRunner.Eq(0, got.Count, "pressed on one request's Allow, the card moved on, released on the next one's: nothing fires");
            Release(ButtonFor(view, "Allow"));
            TestRunner.Eq(0, got.Count, "a release that began elsewhere fires nothing");
            Press(ButtonFor(view, "Allow"));
            Release(ButtonFor(view, "Deny"));
            TestRunner.Eq(0, got.Count, "pressed on Allow, released on Deny: nothing fires");
            Press(ButtonFor(view, "Allow"));
            Release(ButtonFor(view, "Allow"));
            TestRunner.Eq("allow", string.Join(", ", got), "pressed and released on the same button: it fires");
            Release(ButtonFor(view, "Allow"));
            TestRunner.Eq("allow", string.Join(", ", got), "once");

            // A multi-question card: the step changed between press and release.
            HeldPrompt held = Held("AskUserQuestion", Questions, 5);
            view = Shown(PromptCardModel.From(held, 1, 60000));
            got = ActionsOf(view);
            Press(ButtonFor(view, "Red"));
            held.Flow.Choose(0);
            held.Flow.Next();
            view.ShowPrompt(PromptCardModel.From(held, 1, 60000), true);
            view.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            view.Arrange(new Rect(view.DesiredSize));
            Release(ButtonFor(view, "S"));
            TestRunner.Eq(0, got.Count, "the same request, another question: a release on its option doesn't choose it");
        }

        static void TheOtherBoxStopsGrowing()
        {
            HeldPrompt held = Held("AskUserQuestion", Questions);
            held.Flow.ToggleOther();
            held.Flow.SetOther("w");
            CardView view = Shown(PromptCardModel.From(held, 1, 60000));
            double one = view.DesiredSize.Height;
            held.Flow.SetOther(new string('w', 1500));
            view = Shown(PromptCardModel.From(held, 1, 60000));
            TextBox box = view.Prompt.OtherBox;
            TestRunner.Check(box.ActualHeight > 40 && box.ActualHeight <= 60, "a long answer takes about three lines of the box, no more (" + box.ActualHeight + ")");
            TestRunner.Check(view.DesiredSize.Height - one < 50, "so the card grows by two lines at most, and the buttons stay in it (" + (view.DesiredSize.Height - one) + ")");
            TestRunner.Eq(ScrollBarVisibility.Auto, box.VerticalScrollBarVisibility, "and the rest scrolls");
        }

        static void TheUsageComesBack()
        {
            CardView view = Shown(Approval(false));
            var r = new Reading { Provider = "claude", Status = "ok", Plan = "Max" };
            view.Show(CardModel.From(r, new List<SessionStatus>(), true, 1000, System.Globalization.CultureInfo.GetCultureInfo("en-US")), true);
            TestRunner.Check(!view.ShowingPrompt && !Texts(view).Contains("Allow"), "the usage comes back once nothing is held");
        }
    }
}
