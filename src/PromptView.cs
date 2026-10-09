using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;
using WPath = System.Windows.Shapes.Path;

namespace Capsule
{
    // The hover card's request view (act-from-notch spec §3): what Claude wants, or its question, and the buttons, in the
    // card's own style. Everything done on it is raised as a PromptAction; PromptBroker decides what it means. The Other…
    // box is kept across redraws, as the panel keeps its Ideas box, so what is typed in it is never lost to one.
    public sealed class PromptView : StackPanel
    {
        public const double WhatLineHeight = 17;   // the summary's lines: at most two of them
        public const double Dimmed = 0.6;          // Next or Send while the question has no answer yet: still readable on either theme
        public const double OtherMaxHeight = 54;   // three lines of the Other… box, then it scrolls: Send and Answer in Claude stay on the card

        // How long after a new request is first drawn a press on the card is ignored (see Button): at least this long, and
        // at least the system's double-click time.
        public const int MinPressDelayMs = 500;

        // Milliseconds on a steady clock; the tests replace it.
        internal static Func<long> Clock = delegate { return Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency; };

        readonly TextBox otherBox = new TextBox();
        readonly Border otherField = new Border();
        TextBlock countdown = new TextBlock();
        Border advance;   // Next or Send
        PromptCardModel model;
        bool building;
        bool drawn;        // a request has been drawn since the view was last cleared
        int drawnId;       // which one, the first time it was drawn
        long drawnAt;      // and when
        DockPanel top;             // the title row, whose "1 of N" badge comes and goes without a redraw
        Border positionBadge;
        Theme builtWith;           // the theme the card was last built in

        public event Action<PromptAction> Acted;

        public PromptView()
        {
            PanelView.Plain(otherBox);
            otherBox.MaxLength = QuestionFlow.MaxOther;
            otherBox.TextWrapping = TextWrapping.Wrap;
            otherBox.MaxHeight = OtherMaxHeight;   // about three lines; the card is sized when it is shown and typing doesn't redraw it
            otherBox.MinHeight = OtherMaxHeight;   // so it is that tall from the start: the window is sized once, and 3 lines of typing must not push Send out of it
            AutomationProperties.SetName(otherBox, "Your own answer");
            otherBox.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            otherBox.TextChanged += delegate
            {
                if (building) return;
                if (model != null) model.OtherText = PromptCardModel.Typed(otherBox.Text);   // a redraw (a new DPI) keeps what was typed
                Raise(PromptAction.Typed(otherBox.Text, Target));
            };
            otherBox.PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.Enter && EnterPressed()) e.Handled = true;
            };
            otherField.CornerRadius = new CornerRadius(10);
            otherField.Padding = new Thickness(10, 6, 10, 6);
            otherField.Margin = new Thickness(0, 0, 0, 6);
            otherField.Child = otherBox;
            // Any click on the card counts as using it: its countdown starts again.
            PreviewMouseDown += delegate { Raise(Make(PromptAction.Touch)); };
        }

        public TextBox OtherBox { get { return otherBox; } }
        public PromptCardModel Model { get { return model; } }

        // The window after a new request comes up in which presses are ignored.
        public static int PressDelayMs
        {
            get
            {
                int doubleClick = 0;
                try { doubleClick = WinForms.SystemInformation.DoubleClickTime; }
                catch (Exception) { }
                return Math.Max(MinPressDelayMs, doubleClick);
            }
        }

        // False for the first PressDelayMs after a request was first drawn: a click aimed at the request that was on the
        // card a moment ago must not land on this one, which the user hasn't seen. Drawing the same request again (its
        // countdown, typing, another step) doesn't start the window again; another request, or one drawn after Clear, does.
        bool Settled { get { return model != null && Clock() - drawnAt >= PressDelayMs; } }

        public void Show(PromptCardModel m)
        {
            if (!drawn || drawnId != m.Id || (model != null && model.Provider != m.Provider))
            {
                drawn = true;
                drawnId = m.Id;
                drawnAt = Clock();
            }
            // The same request on the same step, with nothing new to show but its place in the queue and its time left (a
            // request that came in behind it): the card's elements stay, so a press in progress on one of them isn't lost.
            if (model != null && top != null && !ReferenceEquals(model, m) && builtWith == Theme.Current && SameButQueue(model, m))
            {
                model = m;
                SetPosition(m.Position);
                countdown.Text = m.Countdown;
                return;
            }
            model = m;
            building = true;
            try { Build(m); }
            finally { building = false; }
        }

        // Everything the card draws is the same but the "1 of N" badge and the countdown.
        static bool SameButQueue(PromptCardModel a, PromptCardModel b)
        {
            if (a.Id != b.Id || a.Provider != b.Provider || a.Step != b.Step || a.Kind != b.Kind) return false;
            if (a.Detail != b.Detail || a.DetailFull != b.DetailFull || a.ProjectFull != b.ProjectFull) return false;
            if (a.Title != b.Title || a.What != b.What || a.Project != b.Project || a.WhatFull != b.WhatFull || a.WhatCut != b.WhatCut) return false;
            if (a.CanAlwaysAllow != b.CanAlwaysAllow || a.AlwaysAllowLabel != b.AlwaysAllowLabel || a.AlwaysAllowTip != b.AlwaysAllowTip || a.AlwaysAllowAlso != b.AlwaysAllowAlso) return false;
            if (a.Header != b.Header || a.QuestionText != b.QuestionText || a.MultiSelect != b.MultiSelect) return false;
            if (a.OtherOpen != b.OtherOpen || a.OtherText != b.OtherText || a.IsLast != b.IsLast || a.CanAdvance != b.CanAdvance) return false;
            if (a.Options.Count != b.Options.Count) return false;
            for (int i = 0; i < a.Options.Count; i++)
            {
                PromptOptionRow x = a.Options[i], y = b.Options[i];
                if (x.Label != y.Label || x.Description != y.Description || x.Chosen != y.Chosen) return false;
            }
            return true;
        }

        // The "1 of N" badge: made, changed or taken away where it is.
        void SetPosition(string text)
        {
            if (text == "")
            {
                if (positionBadge != null) top.Children.Remove(positionBadge);
                positionBadge = null;
                return;
            }
            if (positionBadge == null)
            {
                positionBadge = Badge(text);
                positionBadge.Margin = new Thickness(8, 1, 0, 0);
                positionBadge.VerticalAlignment = VerticalAlignment.Top;
                DockPanel.SetDock(positionBadge, Dock.Right);
                top.Children.Insert(1, positionBadge);   // after the logo, before the title that fills the rest
                return;
            }
            ((TextBlock)positionBadge.Child).Text = text;
        }

        // The countdown line alone, every second, without a redraw.
        public void SetCountdown(string text)
        {
            countdown.Text = text ?? "";
            if (model != null) model.Countdown = countdown.Text;   // so a redraw of this model (a new DPI) shows the time left, not the first one
        }

        // Next or Send, lit once the question has an answer (typing in the box doesn't redraw the card).
        public void SetCanAdvance(bool can)
        {
            if (model != null) model.CanAdvance = can;
            if (advance != null) advance.Opacity = can ? 1 : Dimmed;
        }

        // Enter in the Other… box is Next, or Send on the last question. False when there is no question to go on from.
        internal bool EnterPressed()
        {
            if (model == null || model.Kind != PromptCardModel.Question) return false;
            Raise(Make(model.IsLast ? PromptAction.Send : PromptAction.Next));
            return true;
        }

        // The request leaves the card: nothing of it stays in the view (its text, its question, what was typed).
        public void Clear()
        {
            var oldParent = otherField.Parent as Panel;
            if (oldParent != null) oldParent.Children.Remove(otherField);
            Children.Clear();
            advance = null;
            top = null;
            positionBadge = null;
            countdown = new TextBlock();
            model = null;
            drawn = false;
            building = true;
            try { otherBox.Text = ""; }
            finally { building = false; }
        }

        // The caret into the Other… box, once the card is laid out and can take the keyboard.
        public void FocusOther()
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(delegate
            {
                if (otherField.Parent == null) return;
                otherBox.Focus();
                Keyboard.Focus(otherBox);
                otherBox.CaretIndex = otherBox.Text.Length;
            }));
        }

        // Every action names the request this view was built for, so one that arrives after the card has moved on
        // to another request can't be applied to it.
        int Target { get { return model == null ? 0 : model.Id; } }
        string Step { get { return model == null ? "" : model.Step; } }

        PromptAction Make(string kind) { return PromptAction.Of(kind, Target); }

        void Raise(PromptAction action)
        {
            if (Acted != null) Acted(action);
        }

        void Build(PromptCardModel m)
        {
            var oldParent = otherField.Parent as Panel;
            if (oldParent != null) oldParent.Children.Remove(otherField);
            Children.Clear();
            advance = null;
            builtWith = Theme.Current;

            top = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            positionBadge = null;
            var logo = new WPath
            {
                Data = Logos.For(m.Provider),
                Fill = NotchView.Brush(Palette.Text),
                Stretch = Stretch.Uniform,
                Width = 16,
                Height = 16,
                Margin = new Thickness(0, 2, 8, 0),
                VerticalAlignment = VerticalAlignment.Top,
            };
            DockPanel.SetDock(logo, Dock.Left);
            top.Children.Add(logo);
            top.Children.Add(CardView.Wrap(CardView.MakeText(m.Title, 15, Palette.Text, FontWeights.SemiBold)));
            SetPosition(m.Position);
            Children.Add(top);

            if (m.Kind == PromptCardModel.Approval)
            {
                bool overflows;
                TextBlock summary = TwoLines(CardView.MakeText(m.What, 12.5, Palette.Text, FontWeights.Normal), out overflows);
                var what = new Border
                {
                    Background = NotchView.Brush(Palette.Track),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(8, 5, 8, 6),
                    Margin = new Thickness(0, 0, 0, 8),
                    Child = summary,
                };
                // Hover shows the exact command, including spaces and line breaks that the summary compresses.
                if (m.WhatFull != "")
                {
                    summary.ToolTip = WholeCommand(m.WhatFull);
                    what.ToolTip = WholeCommand(m.WhatFull);
                    foreach (FrameworkElement e in new FrameworkElement[] { summary, what })
                    {
                        ToolTipService.SetShowDuration(e, 60000);
                        ToolTipService.SetInitialShowDelay(e, 300);
                    }
                }
                Children.Add(what);
            }
            else if (m.Kind == PromptCardModel.Question) BuildQuestion(m);
            else
            {
                string hint = m.Kind == PromptCardModel.Plan ? "Read it in the Claude app, or approve it here." : "Answer it in the Claude app.";
                TextBlock note = CardView.Wrap(CardView.MakeText(hint, 12.5, Palette.Secondary, FontWeights.Normal));
                note.Margin = new Thickness(0, 0, 0, 8);
                Children.Add(note);
            }

            if (m.Detail != "")
            {
                bool overflows;
                TextBlock detail = TwoLines(CardView.MakeText(m.Detail, 12, Palette.Secondary, FontWeights.Normal), out overflows);
                detail.ToolTip = WholeCommand(m.DetailFull);
                Children.Add(detail);
            }
            if (m.Project != "")
            {
                TextBlock project = CardView.MakeText(m.Project, 12, Palette.Secondary, FontWeights.Normal);
                project.ToolTip = WholeCommand(m.ProjectFull);
                Children.Add(project);
            }
            countdown = CardView.MakeText(m.Countdown, 11.5, Palette.Secondary, FontWeights.Normal);
            countdown.Margin = new Thickness(0, 2, 0, 10);
            Children.Add(countdown);

            var buttons = new WrapPanel();
            if (m.Kind == PromptCardModel.Approval)
            {
                buttons.Children.Add(Pill("Allow", PromptAction.Allow));
                if (m.CanAlwaysAllow)
                {
                    Border always = Pill(m.AlwaysAllowLabel, PromptAction.AlwaysAllow);
                    always.ToolTip = WholeCommand(m.AlwaysAllowTip);
                    buttons.Children.Add(always);
                }
                buttons.Children.Add(Pill("Deny", PromptAction.Deny));
            }
            else if (m.Kind == PromptCardModel.Plan)
            {
                buttons.Children.Add(Pill("Approve plan", PromptAction.Allow));
                buttons.Children.Add(Pill("Keep planning", PromptAction.Deny));
            }
            else if (m.Kind == PromptCardModel.Question)
            {
                advance = Pill(m.IsLast ? "Send" : "Next", m.IsLast ? PromptAction.Send : PromptAction.Next);
                buttons.Children.Add(advance);
                SetCanAdvance(m.CanAdvance);
            }
            string answerIn = "Answer in " + m.AppName;
            Border inClaude = Button(CardView.MakeText(answerIn, 12.5, Palette.Secondary, FontWeights.Normal),
                delegate { Raise(Make(PromptAction.InClaude)); }, Brushes.Transparent, NotchView.Brush(Palette.Track), new Thickness(10, 4, 10, 5));
            inClaude.Margin = new Thickness(0, 0, 6, 6);
            AutomationProperties.SetName(inClaude, answerIn);
            buttons.Children.Add(inClaude);
            Children.Add(buttons);
            // What else Always allow would do, in plain sight and not only in its tooltip.
            if (m.Kind == PromptCardModel.Approval && m.CanAlwaysAllow && m.AlwaysAllowAlso != "")
            {
                TextBlock also = CardView.Wrap(CardView.MakeText(m.AlwaysAllowAlso, 11.5, Palette.Secondary, FontWeights.Normal));
                also.Margin = new Thickness(0, 0, 0, 2);
                Children.Add(also);
            }
        }

        void BuildQuestion(PromptCardModel m)
        {
            var line = new DockPanel { Margin = new Thickness(0, 0, 0, 6), LastChildFill = false };
            if (m.Step != "")
            {
                TextBlock step = CardView.MakeText(m.Step, 11.5, Palette.Secondary, FontWeights.Normal);
                step.VerticalAlignment = VerticalAlignment.Center;
                DockPanel.SetDock(step, Dock.Right);
                line.Children.Add(step);
            }
            if (m.Header != "") line.Children.Add(Badge(m.Header));
            if (line.Children.Count > 0) Children.Add(line);
            TextBlock question = CardView.Wrap(CardView.MakeText(m.QuestionText, 13.5, Palette.Text, FontWeights.Normal));
            question.Margin = new Thickness(0, 0, 0, 8);
            Children.Add(question);
            for (int i = 0; i < m.Options.Count; i++)
            {
                int option = i;
                PromptOptionRow o = m.Options[i];
                Children.Add(Choice(o.Label, o.Description, o.Chosen, m.MultiSelect, delegate { Raise(PromptAction.Chose(option, Target)); }));
            }
            Border other = Choice("Other…", "", m.OtherOpen, m.MultiSelect, delegate { Raise(Make(PromptAction.Other)); });
            Children.Add(other);
            if (!m.OtherOpen) return;
            if (otherBox.Text != m.OtherText) otherBox.Text = m.OtherText;
            otherField.Background = NotchView.Brush(Palette.Track);
            otherBox.Foreground = NotchView.Brush(Palette.Text);
            otherBox.CaretBrush = NotchView.Brush(Palette.Text);
            Children.Add(otherField);
        }

        // One of Claude's options, or Other…: a row that lights up under the mouse. A multi-select's rows have check boxes;
        // a single choice's chosen row is lit and ticked.
        Border Choice(string label, string description, bool chosen, bool multi, Action click)
        {
            Theme theme = Theme.Current;
            Color ink = theme.Text;
            Brush lit = theme.Brush(Color.FromArgb(0x40, ink.R, ink.G, ink.B));
            var inner = new DockPanel();
            if (multi)
            {
                var box = new Border
                {
                    Width = 16,
                    Height = 16,
                    CornerRadius = new CornerRadius(4),
                    BorderThickness = new Thickness(1.5),
                    BorderBrush = NotchView.Brush(chosen ? Palette.Text : Palette.Secondary),
                    Margin = new Thickness(0, 1, 8, 0),
                    VerticalAlignment = VerticalAlignment.Top,
                };
                if (chosen)
                {
                    TextBlock check = CardView.MakeText("✓", 11, Palette.Text, FontWeights.Bold);
                    check.HorizontalAlignment = HorizontalAlignment.Center;
                    check.VerticalAlignment = VerticalAlignment.Center;
                    box.Child = check;
                }
                DockPanel.SetDock(box, Dock.Left);
                inner.Children.Add(box);
            }
            else if (chosen)
            {
                TextBlock tick = CardView.MakeText("✓", 12, Palette.Text, FontWeights.Bold);
                tick.Margin = new Thickness(8, 0, 0, 0);
                tick.VerticalAlignment = VerticalAlignment.Top;
                DockPanel.SetDock(tick, Dock.Right);
                inner.Children.Add(tick);
            }
            var texts = new StackPanel();
            texts.Children.Add(CardView.Wrap(CardView.MakeText(label, 13, Palette.Text, FontWeights.Normal)));
            if (description != "") texts.Children.Add(CardView.Wrap(CardView.MakeText(description, 11.5, Palette.Secondary, FontWeights.Normal)));
            inner.Children.Add(texts);
            Brush rest = chosen && !multi ? lit : theme.Brush(theme.Tile);
            Border row = Button(inner, click, rest, chosen && !multi ? lit : NotchView.Brush(Palette.Track), new Thickness(10, 6, 10, 7));
            row.Margin = new Thickness(0, 0, 0, 4);
            AutomationProperties.SetName(row, (description != "" ? label + ", " + description : label) + (chosen ? ", chosen" : ""));
            return row;
        }

        // The panel's pill, with this view's own button behaviour.
        Border Pill(string text, string kind)
        {
            Border b = PanelView.PillButton(text, delegate { Raise(Make(kind)); }, Button);
            b.Margin = new Thickness(0, 0, 6, 6);
            return b;
        }

        // The panel's button, but one that fires only on the release that ends its own press, on the request and question
        // that were shown at the press. The card can move on to the next request between a press and its release (the
        // countdown ends, or another request comes first), and that release must not land on the new request's button.
        Border Button(UIElement content, Action click, Brush rest, Brush hover, Thickness padding)
        {
            bool down = false;
            int pressedTarget = 0;
            string pressedStep = "";
            Border b = PanelView.Button(content, delegate
            {
                bool fire = down && pressedTarget == Target && pressedStep == Step;
                down = false;
                if (fire) click();
            }, rest, hover, padding);
            b.PreviewMouseDown += delegate(object sender, MouseButtonEventArgs e)
            {
                if (e.ChangedButton != MouseButton.Left) return;
                // The second press of a double click, or any press while the request is new on the card: not a click on it.
                if (e.ClickCount > 1 || !Settled)
                {
                    down = false;
                    return;
                }
                down = true;
                pressedTarget = Target;
                pressedStep = Step;
            };
            b.MouseLeave += delegate { down = false; };
            return b;
        }

        static Border Badge(string text)
        {
            return new Border
            {
                Background = NotchView.Brush(Palette.Track),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(6, 1, 6, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Child = CardView.MakeText(text, 11, Palette.Secondary, FontWeights.Normal),
            };
        }

        // The card's width less its padding and the summary box's: the room a summary's lines have.
        const double WhatWidth = CardView.CardWidth - 2 * CardView.Inset - 16;
        const double WhatTipWidth = 380;

        // Wrapped onto at most two lines, the second ending in … when it doesn't fit. overflows: it needed more.
        static TextBlock TwoLines(TextBlock t, out bool overflows)
        {
            t.TextWrapping = TextWrapping.Wrap;
            t.TextTrimming = TextTrimming.CharacterEllipsis;
            t.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            t.LineHeight = WhatLineHeight;
            t.Measure(new Size(WhatWidth, double.PositiveInfinity));
            overflows = t.DesiredSize.Height > 2 * WhatLineHeight + 0.5;
            t.MaxHeight = 2 * WhatLineHeight;
            return t;
        }

        // The whole command in a tooltip: its own line breaks, wrapped to a width that fits on the screen.
        static ToolTip WholeCommand(string text)
        {
            TextBlock content = CardView.MakeText(text, 12, Palette.Text, FontWeights.Normal);
            content.TextWrapping = TextWrapping.Wrap;
            content.MaxWidth = WhatTipWidth;
            return new ToolTip { Content = new ScrollViewer { Content = content, MaxHeight = 240,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } };
        }
    }
}
