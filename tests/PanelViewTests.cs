using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace Capsule
{
    public static class PanelViewTests
    {
        const long Now = 1790690940000;
        static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hWnd);

        public static void Run()
        {
            SizesThePanel();
            TilesShowTheirModels();
            SignInReplacesTheNumbers();
            IdeasBoxSurvivesUpdates();
            PastedParagraphsAreJoined();
            SettingsShowWhatIsSaved();
            TheSecretBoxIsWriteOnly();
            SaveKeepsAPastedSecretUntilItIsKept();
            SaveButtonKeepsItsSize();
            ShortcutBoxTakesKeysDirectly();
            ShowsTheRefreshIcon();
            ShowsTheSettingsIcons();
            SurvivesBeingClosed();
            DiskNoteIsAmberEvenBeforeSetup();
        }

        // Polish spec §4.3: what the Ideas tile says about the disk is amber, also while Notion isn't set up and the setup
        // hint beside it is grey.
        static void DiskNoteIsAmberEvenBeforeSetup()
        {
            PanelModel m = Sample(0);
            m.Ideas = new IdeasTile { Configured = false, Waiting = 1, Note = "Set up Notion to send ideas there.", DiskNote = "Couldn't save to disk yet. Retrying." };
            var view = new PanelView();
            view.Update(m);
            Lay(view);
            TextBlock disk = FindText(view, "Couldn't save to disk yet. Retrying.");
            TextBlock setup = FindText(view, "Set up Notion to send ideas there.");
            TestRunner.Check(disk != null && ((SolidColorBrush)disk.Foreground).Color == NotchView.Brush(Palette.Amber).Color, "the note about the disk is amber, though Notion isn't set up");
            TestRunner.Check(setup != null && ((SolidColorBrush)setup.Foreground).Color == NotchView.Brush(Palette.Secondary).Color, "while the setup hint stays grey");
        }

        static void SettingsShowWhatIsSaved()
        {
            var view = new PanelView();
            view.Update(Sample(2));
            int resized = 0;
            view.Resized += delegate { resized++; };
            view.ShowSettings(true, NotionTests.Db, "Ctrl+Alt+N");
            Lay(view);
            TestRunner.Check(view.ShowingSettings && resized == 1, "the settings take the tiles' place, and the window is told to resize");
            TestRunner.Check(HasText(view, "Saved. Paste a new one to replace it."), "a saved secret is mentioned, not shown");
            TestRunner.Check(!HasText(view, "73%"), "the tiles are hidden");
            TestRunner.Check(HasText(view, "Ctrl+Alt+N"), "the shortcut");
            TextBox link = null;
            PasswordBox secret = null;
            Find(view, ref link, ref secret);
            TestRunner.Check(link != null && link.Text == "https://app.notion.com/p/" + NotionTests.Db, "the database as a link");
            view.ShowNotionResult(true, "Connected to “Ideas”");
            Lay(view);
            TestRunner.Check(HasText(view, "✓ Connected to “Ideas”"), "a good test says so");
            view.ShowShortcutResult(false, "Ctrl+Alt+N", "Ctrl+Alt+Space is used by another app. Try another.");
            Lay(view);
            TestRunner.Check(HasText(view, "Ctrl+Alt+Space is used by another app. Try another."), "a taken shortcut says so");
            view.ShowBoard();
            Lay(view);
            TestRunner.Check(!view.ShowingSettings && HasText(view, "73%"), "back to the tiles");
            view.ShowSettings(false, "", "Ctrl+Alt+N");
            Lay(view);
            TestRunner.Check(HasText(view, "Your connection's secret, from app.notion.com/developers (Configuration tab)."), "no secret yet: where to find one");
        }

        // A paste, as WPF raises it on the box before it inserts the text; the text is what the handlers leave in the data.
        static string Paste(TextBox box, string text)
        {
            var args = new DataObjectPastingEventArgs(new DataObject(DataFormats.UnicodeText, text), false, DataFormats.UnicodeText);
            args.RoutedEvent = DataObject.PastingEvent;
            box.RaiseEvent(args);
            return args.DataObject.GetData(DataFormats.UnicodeText) as string;
        }

        // A single-line TextBox cuts a paste off at the first line break, so a pasted paragraph would arrive as its first
        // line only and the rest of the idea would be lost without a word. The box joins the lines first, as the lines of
        // an idea are joined when it is queued.
        static void PastedParagraphsAreJoined()
        {
            var view = new PanelView();
            TestRunner.Eq("first line second line third line", Paste(view.IdeaBox, "first line\r\nsecond line\nthird line"), "the lines of a pasted paragraph are joined with spaces");
            TestRunner.Eq("trailing newline gone", Paste(view.IdeaBox, "trailing newline gone\r\n"), "a copied line's closing line break doesn't leave a space or a cut");
            TestRunner.Eq("already one line", Paste(view.IdeaBox, "already one line"), "a paste that is one line is left as it is");
            string cut = Paste(view.IdeaBox, new string('x', 1500) + "\n" + new string('y', 1500));
            TestRunner.Eq(IdeasModule.MaxChars, cut.Length, "and a paste longer than an idea may be is cut to the same limit");
        }

        // The secret box is write-only: what was typed in it is gone once the settings are shown again or left.
        static void TheSecretBoxIsWriteOnly()
        {
            var view = new PanelView();
            view.Update(Sample(0));
            view.ShowSettings(true, NotionTests.Db, "Ctrl+Alt+N");
            Lay(view);
            TextBox link = null;
            PasswordBox secret = null;
            Find(view, ref link, ref secret);
            if (secret == null)
            {
                TestRunner.Check(false, "the settings have a secret box");
                return;
            }
            secret.Password = "x";
            view.ShowSettings(true, NotionTests.Db, "Ctrl+Alt+N");
            TestRunner.Check(secret.Password == "", "showing the settings starts with an empty secret box");
            secret.Password = "x";
            view.ShowBoard();
            TestRunner.Check(secret.Password == "", "leaving the settings empties the secret box");
        }

        // Save and test hands over the secret and the link, but the secret stays in the box until the Controller says it
        // kept it (SecretSaved): when the save fails, or the link is refused, the pasted secret isn't lost.
        static void SaveKeepsAPastedSecretUntilItIsKept()
        {
            var view = new PanelView();
            view.Update(Sample(0));
            view.ShowSettings(false, "", "Ctrl+Alt+N");
            Lay(view);
            TextBox link = null;
            PasswordBox secret = null;
            Find(view, ref link, ref secret);
            if (link == null || secret == null)
            {
                TestRunner.Check(false, "the settings have a link box and a secret box");
                return;
            }
            int saves = 0, resized = 0;
            string sentSecret = null, sentLink = null;
            view.NotionSaved += delegate(string s, string l)
            {
                saves++;
                sentSecret = s;
                sentLink = l;
            };
            view.Resized += delegate { resized++; };
            TestRunner.Check(!HasText(view, "Saved. Paste a new one to replace it."), "no secret saved yet: the hint doesn't say one is");
            secret.Password = "pasted";
            link.Text = "https://app.notion.com/p/" + NotionTests.Db;
            Click(Holder(view, "Save and test"));
            TestRunner.Check(saves == 1 && sentSecret == "pasted" && sentLink == "https://app.notion.com/p/" + NotionTests.Db, "Save and test hands over the secret and the link");
            TestRunner.Check(secret.Password == "pasted", "the pasted secret stays in the box until it is kept, so a failed save can be tried again");
            resized = 0;
            view.SecretSaved();
            Lay(view);
            TestRunner.Check(secret.Password == "" && HasText(view, "Saved. Paste a new one to replace it."), "once it is kept the box empties and the hint says a secret is saved");
            TestRunner.Check(resized == 1, "and the window is told to resize: the hint changed");
        }

        // The Save and test button keeps its size when the result beside it wraps onto more lines.
        static void SaveButtonKeepsItsSize()
        {
            var view = new PanelView();
            view.Update(Sample(0));
            view.ShowSettings(false, "", "Ctrl+Alt+N");
            Lay(view);
            FrameworkElement save = Holder(view, "Save and test");
            if (save == null)
            {
                TestRunner.Check(false, "the settings have a Save and test button");
                return;
            }
            double height = save.ActualHeight;
            string problem = "Can't reach Notion (The remote name could not be resolved: 'api.notion.com') — will retry";
            view.ShowNotionResult(false, problem);
            Lay(view);
            TextBlock result = FindText(view, problem);
            TestRunner.Check(result != null && result.ActualHeight > height, "a long result wraps onto more lines than the button is tall");
            TestRunner.Near(height, save.ActualHeight, "so the button beside it keeps its height");
        }

        // With an input method on (a Chinese IME, say) key presses reach the box as Key.ImeProcessed, which is no
        // shortcut: the box turns the input method off.
        static void ShortcutBoxTakesKeysDirectly()
        {
            var view = new PanelView();
            view.Update(Sample(0));
            view.ShowSettings(false, "", "Ctrl+Alt+N");
            Lay(view);
            FrameworkElement box = Holder(view, "Ctrl+Alt+N");
            TestRunner.Check(box != null && !InputMethod.GetIsInputMethodEnabled(box), "the shortcut box turns the input method off, so key presses reach it as keys");
        }

        // The settings' database box (the TextBox that isn't the Ideas box) and secret box.
        static void Find(DependencyObject root, ref TextBox link, ref PasswordBox secret)
        {
            if (root is PasswordBox) secret = (PasswordBox)root;
            var box = root as TextBox;
            if (box != null && box.MaxLength != IdeasModule.MaxChars) link = box;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) Find(VisualTreeHelper.GetChild(root, i), ref link, ref secret);
        }

        static PanelModel Sample(int ideas)
        {
            var claude = new Reading { Provider = "claude", Status = "ok", HeadlineId = "five_hour", Plan = "Max", DataAtMs = Now };
            claude.Windows.Add(new LimitWindow { Id = "five_hour", Label = "Current session", Used = 73, ResetsAtMs = Now + 51 * 60 * 1000L });
            var sessions = new List<SessionStatus>
            {
                new SessionStatus { SessionId = "a", State = States.Waiting, Project = "confetti", Reason = "asked you a question", At = Now, Since = Now },
                new SessionStatus { SessionId = "b", State = States.Working, Project = "website", At = Now - 1000, Since = Now - 120000 },
            };
            var tile = new IdeasTile { Configured = true, Waiting = 2 };
            for (int i = 0; i < ideas; i++) tile.Rows.Add(new IdeaRow { Text = "idea " + i, State = i == 0 ? IdeaRow.Saved : "", Url = "https://app.notion.com/p/" + i });
            return new PanelModel
            {
                Claude = UsageTile.From(claude, Now, En),
                Codex = UsageTile.From(new Reading { Provider = "codex", Status = "none" }, Now, En),
                Sessions = SessionsTile.From(sessions, true, Now),
                Ideas = tile,
            };
        }

        static void Lay(FrameworkElement e)
        {
            e.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            e.Arrange(new Rect(e.DesiredSize));
            e.UpdateLayout();
        }

        // The TextBlock showing the text, or null. Collapsed parts of the tree are still in it, so they are skipped.
        static TextBlock FindText(DependencyObject root, string text)
        {
            var e = root as UIElement;
            if (e != null && e.Visibility != Visibility.Visible) return null;
            var t = root as TextBlock;
            if (t != null && t.Text == text) return t;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                TextBlock found = FindText(VisualTreeHelper.GetChild(root, i), text);
                if (found != null) return found;
            }
            return null;
        }

        static bool HasText(DependencyObject root, string text) { return FindText(root, text) != null; }

        // What the text sits in: a button's Border, or the shortcut box.
        static FrameworkElement Holder(DependencyObject root, string text)
        {
            TextBlock t = FindText(root, text);
            return t == null ? null : VisualTreeHelper.GetParent(t) as FrameworkElement;
        }

        // A click on a button as the mouse would deliver it, with no window.
        static void Click(UIElement button)
        {
            if (button == null) return;
            button.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent, Source = button });
        }

        static bool Contains(DependencyObject root, DependencyObject wanted)
        {
            if (root == wanted) return true;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                if (Contains(VisualTreeHelper.GetChild(root, i), wanted)) return true;
            return false;
        }

        static int CountText(DependencyObject root, string text)
        {
            int n = 0;
            var t = root as TextBlock;
            if (t != null && t.Text == text) n++;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                n += CountText(VisualTreeHelper.GetChild(root, i), text);
            return n;
        }

        static void SizesThePanel()
        {
            var view = new PanelView();
            view.Update(Sample(5));
            Lay(view);
            TestRunner.Near(PanelView.PanelWidth + 2 * PanelView.ShadowMargin, view.DesiredSize.Width, "the panel's width, with room for its shadow");
            double height = view.DesiredSize.Height - 2 * PanelView.ShadowMargin;
            TestRunner.Check(height > 250 && height < 700, "a sensible height: " + height);
            Rect b = view.Outline.Bounds;
            TestRunner.Check(Math.Abs(b.Width - PanelView.PanelWidth) < 0.5 && Math.Abs(b.Height - height) < 0.5, "its glass is the panel's outline: " + b);
        }

        static void TilesShowTheirModels()
        {
            var view = new PanelView();
            view.Update(Sample(3));
            Lay(view);
            TestRunner.Check(HasText(view, "Capsule"), "the header");
            TestRunner.Check(HasText(view, "73%") && HasText(view, "Resets in 51 min") && HasText(view, "Max"), "the Claude tile");
            TestRunner.Check(HasText(view, "Codex isn't set up on this PC"), "the Codex tile says why it's empty");
            TestRunner.Check(HasText(view, "confetti") && HasText(view, "waiting · asked you a question") && HasText(view, "working · 2m"), "the Sessions tile");
            TestRunner.Check(HasText(view, "idea 0") && HasText(view, "✓") && HasText(view, "idea 2"), "the Ideas tile's rows");
            TestRunner.Check(HasText(view, "2 waiting to sync"), "and how many wait");
            TestRunner.Check(HasText(view, "Jot an idea…"), "an empty box shows its hint");
        }

        static void SignInReplacesTheNumbers()
        {
            PanelModel m = Sample(0);
            m.Claude = UsageTile.From(new Reading { Provider = "claude", Status = "signin", Note = "Sign in to the Claude CLI to see your usage" }, Now, En);
            var view = new PanelView();
            view.Update(m);
            Lay(view);
            TestRunner.Check(HasText(view, "Sign in") && HasText(view, "Sign in to the Claude CLI to see your usage"), "a Sign in button, with the reason");
        }

        static void IdeasBoxSurvivesUpdates()
        {
            var view = new PanelView();
            view.Update(Sample(1));
            TextBox box = view.IdeaBox;
            box.Text = "half-typed";
            view.Update(Sample(4));
            Lay(view);
            TestRunner.Check(ReferenceEquals(box, view.IdeaBox) && box.Text == "half-typed", "an update keeps the Ideas box and what's in it");
            TestRunner.Check(Contains(view, box), "and the box stays in the panel");
            view.ClearIdea();
            TestRunner.Eq("", box.Text, "cleared after saving");
        }

        // The refresh icon is a private-use glyph (U+E72C) from the Segoe icon fonts. It is invisible in an editor and in
        // a diff, so a rewrite of the source can silently drop it and leave every refresh button blank.
        static void ShowsTheRefreshIcon()
        {
            string glyph = "\uE72C";
            TestRunner.Check(glyph.Length == 1 && glyph[0] == (char)0xE72C, "the test's own glyph is the single character U+E72C");
            var view = new PanelView();
            view.Update(Sample(0));
            Lay(view);
            TestRunner.Eq(3, CountText(view, glyph), "the header, the Claude tile and the Codex tile each have a refresh button showing U+E72C");
        }

        // The settings gear (U+E713) and the back arrow (U+E72B) are private-use glyphs too, and as invisible.
        static void ShowsTheSettingsIcons()
        {
            string gear = "\uE713", back = "\uE72B";
            TestRunner.Check(gear.Length == 1 && gear[0] == (char)0xE713 && back.Length == 1 && back[0] == (char)0xE72B, "the test's own glyphs are the single characters U+E713 and U+E72B");
            var view = new PanelView();
            view.Update(Sample(0));
            Lay(view);
            TestRunner.Check(HasText(view, gear) && !HasText(view, back), "the header has the settings gear, U+E713, and the tiles have no back arrow");
            view.ShowSettings(false, "", "Ctrl+Alt+N");
            Lay(view);
            TestRunner.Check(HasText(view, back), "with the settings shown, the back button has its arrow, U+E72B");
        }

        // Alt+F4 (or any other close request) must only dismiss the panel: a WPF window that has been closed can never be
        // shown again, so the capsule would do nothing until Capsule was restarted. Only the handle is made; nothing is shown.
        static void SurvivesBeingClosed()
        {
            var panel = new PanelWindow();
            bool closed = false;
            panel.Closed += delegate { closed = true; };
            IntPtr handle = new WindowInteropHelper(panel).EnsureHandle();
            panel.Close();
            IntPtr again = IntPtr.Zero;
            try { again = new WindowInteropHelper(panel).EnsureHandle(); }
            catch (InvalidOperationException) { }
            TestRunner.Check(!closed && again == handle && IsWindow(handle), "a close request (Alt+F4) only dismisses the panel: its window survives to be opened again");
        }
    }
}
