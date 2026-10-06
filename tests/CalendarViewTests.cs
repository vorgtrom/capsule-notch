using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using WPath = System.Windows.Shapes.Path;

namespace Capsule
{
    // The calendar's faces: its cell on the capsule, its hover card, its tile and its settings (calendar spec §2).
    public static class CalendarViewTests
    {
        static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");
        const string CalendarGlyph = "\uE787";

        public static void Run()
        {
            CellShowsTheCalendarGlyph();
            CardListsTheDay();
            TileShowsTodayAndTomorrow();
            SettingsSignInAndOut();
            SettingsCalendarLink();
            SettingsKeepTheClientWhenClosed();
        }

        // A paste, as WPF raises it on the box before it inserts the text. True when a handler cancelled it.
        static bool Paste(TextBox box, string text)
        {
            var args = new DataObjectPastingEventArgs(new DataObject(DataFormats.UnicodeText, text), false, DataFormats.UnicodeText);
            args.RoutedEvent = DataObject.PastingEvent;
            box.RaiseEvent(args);
            return args.CommandCancelled;
        }

        // Pasting the client ID, then clicking away to copy the secret, closes the panel: what was typed is handed over
        // first. The downloaded client file, pasted whole, fills both.
        static void SettingsKeepTheClientWhenClosed()
        {
            var view = new PanelView();
            view.Update(Model(null));
            view.ShowSettings(false, "", "Ctrl+Alt+N");
            view.ShowGoogle(new GoogleSettings { ClientId = "" });
            string draftId = null, draftSecret = null;
            int drafts = 0;
            view.GoogleClientDraft += delegate(string id, string secret)
            {
                drafts++;
                draftId = id;
                draftSecret = secret;
            };
            Click(Holder(view, "Use your own Google client instead"));
            view.ClientIdBox.Text = "cid-typed";
            view.ShowBoard();
            TestRunner.Check(drafts == 1 && draftId == "cid-typed" && draftSecret == "", "closing the settings hands over the typed client ID");
            view.ShowSettings(false, "", "Ctrl+Alt+N");
            view.ShowGoogle(new GoogleSettings { ClientId = "cid-typed" });
            Lay(view);
            TestRunner.Eq("cid-typed", view.ClientIdBox.Text, "and it is there when they open again");
            bool cancelled = Paste(view.ClientIdBox, "{\"installed\":{\"client_id\":\"cid-file\",\"client_secret\":\"csecret-file\"}}");
            TestRunner.Check(cancelled && view.ClientIdBox.Text == "cid-file" && drafts == 2 && draftId == "cid-file" && draftSecret == "csecret-file", "a pasted client file fills in the ID and hands over both at once");
            TestRunner.Check(!Paste(view.ClientIdBox, "123-plain.apps.googleusercontent.com"), "a plain ID is pasted as it is");
            view.ShowGoogle(new GoogleSettings { ClientId = "cid-other", SecretSaved = true, SecretPending = true, SignedIn = true });
            Lay(view);
            Click(Holder(view, "Change client"));
            Lay(view);
            TestRunner.Check(HasText(view, "Secret kept for the next sign-in. Paste a new one to replace it."), "a secret held for the next sign-in says so");
            view.ShowGoogle(new GoogleSettings { ClientId = "cid", SecretSaved = true, SignedIn = true });
            view.ShowSettings(false, "", "Ctrl+Alt+N");
            view.ShowGoogle(new GoogleSettings { ClientId = "cid", SecretSaved = true, SignedIn = true });
            drafts = 0;
            view.ShowBoard();
            TestRunner.Eq(0, drafts, "signed in with the client's fields folded away, closing hands nothing over");
        }

        static PanelModel Model(CalendarTile calendar)
        {
            long now = CalendarDayTests.At(10, 6, 8, 0);
            return new PanelModel
            {
                Claude = UsageTile.From(new Reading { Provider = "claude", Status = "none" }, now, En),
                Codex = UsageTile.From(new Reading { Provider = "codex", Status = "none" }, now, En),
                Sessions = SessionsTile.From(new List<SessionStatus>(), true, now),
                Calendar = calendar,
                Ideas = new IdeasTile { Configured = true },
            };
        }

        static CalendarTile TileAt(int hour, int minute)
        {
            return CalendarDay.Tile(CalendarDayTests.Snapshot(CalendarDayTests.Fixture(), CalendarDayTests.At(10, 6, 0, 0)), CalendarDayTests.At(10, 6, hour, minute), CalendarDayTests.Zone, En);
        }

        // What the text sits in: a button's Border.
        static FrameworkElement Holder(DependencyObject root, string text)
        {
            TextBlock t = FindText(root, text);
            return t == null ? null : VisualTreeHelper.GetParent(t) as FrameworkElement;
        }

        // A click as the mouse would deliver it, with no window.
        static void Click(UIElement button)
        {
            if (button == null) return;
            button.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent, Source = button });
        }

        static void CheckBoxes(DependencyObject root, List<CheckBox> found)
        {
            var e = root as UIElement;
            if (e != null && e.Visibility != Visibility.Visible) return;
            var box = root as CheckBox;
            if (box != null) found.Add(box);
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) CheckBoxes(VisualTreeHelper.GetChild(root, i), found);
        }

        static bool Dimmed(DependencyObject e)
        {
            for (DependencyObject p = e; p != null; p = VisualTreeHelper.GetParent(p))
            {
                var u = p as UIElement;
                if (u != null && u.Opacity < 0.5) return true;
            }
            return false;
        }

        static void TileShowsTodayAndTomorrow()
        {
            var view = new PanelView();
            view.Update(Model(TileAt(8, 0)));
            Lay(view);
            double with = view.DesiredSize.Height;
            TestRunner.Check(HasText(view, "Calendar") && HasText(view, "Google"), "the Calendar tile, with where it comes from");
            TestRunner.Check(HasText(view, "Standup") && HasText(view, "9:00 AM – 9:30 AM") && HasText(view, "Offsite") && HasText(view, "All day") && HasText(view, "Tomorrow") && HasText(view, "1:1"), "today's events, then tomorrow's under its label");
            view.Update(Model(null));
            Lay(view);
            TestRunner.Check(!HasText(view, "Standup") && !HasText(view, "Tomorrow") && view.DesiredSize.Height < with - 100, "no model, no tile: the panel is as before");
            view.Update(Model(CalendarDay.Tile(new CalendarSnapshot(), CalendarDayTests.At(10, 6, 8, 0), CalendarDayTests.Zone, En)));
            Lay(view);
            TestRunner.Check(HasText(view, "Connect Google Calendar in ⚙"), "not connected: how to connect");
            CalendarSnapshot away = CalendarDayTests.Snapshot(CalendarDayTests.Fixture(), CalendarDayTests.At(10, 6, 7, 0));
            away.LastFailed = true;
            away.Problem = "Couldn't reach Google";
            view.Update(Model(CalendarDay.Tile(away, CalendarDayTests.At(10, 6, 8, 0), CalendarDayTests.Zone, En)));
            Lay(view);
            TextBlock standup = FindText(view, "Standup");
            TestRunner.Check(HasText(view, "Couldn't reach Google") && standup != null && Dimmed(standup), "Google unreachable: the last list, dimmed, with why");
            view.Update(Model(TileAt(0, 30)));
            Lay(view);
            TextBlock deploy = FindText(view, "Night deploy");
            TestRunner.Check(deploy != null && BackgroundOf(deploy) == NotchView.Brush(Palette.Track).Color, "the event happening now is highlighted on the tile too");
        }

        static void SettingsSignInAndOut()
        {
            var view = new PanelView();
            view.Update(Model(null));
            view.ShowSettings(false, "", "Ctrl+Alt+N");
            view.ShowGoogle(new GoogleSettings { ClientId = "cid-sample" });
            Lay(view);
            TestRunner.Check(HasText(view, "Google Calendar") && HasText(view, "Client ID") && HasText(view, "Client secret") && HasText(view, "Sign in with Google"), "signed out: the client's fields and Sign in with Google");
            TestRunner.Eq("cid-sample", view.ClientIdBox.Text, "the saved client ID is filled in");
            TestRunner.Check(HasText(view, "From your own Google Cloud OAuth client (Desktop app). Or paste its downloaded JSON into Client ID: it fills both."), "no secret yet: where it comes from");
            string sentId = null, sentSecret = null;
            int cancels = 0, signOuts = 0;
            string toggled = null;
            view.GoogleSignInClicked += delegate(string id, string secret) { sentId = id; sentSecret = secret; };
            view.GoogleSignInCancelled += delegate { cancels++; };
            view.GoogleSignOutClicked += delegate { signOuts++; };
            view.CalendarToggled += delegate(string id, bool on) { toggled = id + "=" + on; };
            view.ClientIdBox.Text = "cid-new";
            view.ClientSecretBox.Password = "csecret-sample";
            Click(Holder(view, "Sign in with Google"));
            TestRunner.Check(sentId == "cid-new" && sentSecret == "csecret-sample", "Sign in hands over the client ID and the secret");
            TestRunner.Eq("csecret-sample", view.ClientSecretBox.Password, "the secret stays in its box until the sign-in worked");

            view.ShowGoogle(new GoogleSettings { ClientId = "cid-sample", SigningIn = true, Status = "Waiting for you in the browser…" });
            Lay(view);
            TestRunner.Check(HasText(view, "Cancel") && HasText(view, "Waiting for you in the browser…") && !HasText(view, "Sign in with Google"), "while the browser is open: Cancel, and what is going on");
            TestRunner.Eq("cid-new", view.ClientIdBox.Text, "an update doesn't overwrite what was typed");
            Click(Holder(view, "Cancel"));
            TestRunner.Eq(1, cancels, "Cancel gives the sign-in up");

            var signedIn = new GoogleSettings { ClientId = "cid-new", SecretSaved = true, SignedIn = true, Account = GoogleCalendarTests.Primary };
            signedIn.Calendars.Add(new CalendarChoice { Id = GoogleCalendarTests.Primary, Name = GoogleCalendarTests.Primary, Color = "#9fe1e7", Shown = true });
            signedIn.Calendars.Add(new CalendarChoice { Id = GoogleCalendarTests.Family, Name = "Family", Color = "#f83a22", Shown = false });
            view.ShowGoogle(signedIn);
            view.ClientSecretKept();
            Lay(view);
            TestRunner.Eq("", view.ClientSecretBox.Password, "once it worked, the secret box empties");
            TestRunner.Check(HasText(view, "Signed in as " + GoogleCalendarTests.Primary) && HasText(view, "Sign out") && HasText(view, "Change client") && !HasText(view, "Client ID"), "signed in: the account, Sign out and Change client; the client's fields fold away");
            var boxes = new List<CheckBox>();
            CheckBoxes(view, boxes);
            TestRunner.Check(HasText(view, "Calendars") && HasText(view, "Family") && boxes.Count == 2 && boxes[0].IsChecked == true && boxes[1].IsChecked == false, "a box per calendar, ticked as shown");
            if (boxes.Count == 2)
            {
                boxes[1].IsChecked = true;
                boxes[1].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            }
            TestRunner.Eq(GoogleCalendarTests.Family + "=True", toggled, "ticking one hands over its id and its new state");
            Click(Holder(view, "Sign out"));
            TestRunner.Eq(1, signOuts, "Sign out is handed over");
            Click(Holder(view, "Change client"));
            Lay(view);
            TestRunner.Check(HasText(view, "Client ID") && HasText(view, "Sign in with Google") && HasText(view, "Secret saved. Paste a new one to replace it.") && !HasText(view, "Change client") && HasText(view, "Sign out"), "Change client opens the client's fields again, saying a secret is saved");
            view.ClientSecretBox.Password = "x";
            view.ShowBoard();
            TestRunner.Eq("", view.ClientSecretBox.Password, "leaving the settings empties the client secret box");
            view.ShowSettings(false, "", "Ctrl+Alt+N");
            view.ShowGoogle(signedIn);
            Lay(view);
            TestRunner.Check(!HasText(view, "Client ID") && HasText(view, "Change client"), "opened again, the client's fields are folded away");
            view.ShowGoogle(new GoogleSettings { ClientId = "cid-new", SecretSaved = true, Status = "Google ended the sign-in. Sign in again." });
            Lay(view);
            TextBlock why = FindText(view, "Google ended the sign-in. Sign in again.");
            TestRunner.Check(why != null && ((SolidColorBrush)why.Foreground).Color == NotchView.Brush(Palette.Amber).Color && HasText(view, "Client ID"), "signed out by Google: the client's fields, and why, in amber");
        }

        static void Lay(FrameworkElement e)
        {
            e.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            e.Arrange(new Rect(e.DesiredSize));
            e.UpdateLayout();
        }

        // The TextBlock showing the text, or null. Collapsed parts of the tree are skipped.
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

        static Color BackgroundOf(TextBlock t)
        {
            DependencyObject p = t;
            while (p != null && !(p is Border)) p = VisualTreeHelper.GetParent(p);
            var b = p as Border;
            var brush = b != null ? b.Background as SolidColorBrush : null;
            return brush != null ? brush.Color : Colors.Transparent;
        }

        static void CellShowsTheCalendarGlyph()
        {
            TestRunner.Check(CalendarGlyph.Length == 1 && CalendarGlyph[0] == (char)0xE787 && CalendarDay.Glyph == CalendarGlyph, "the test's glyph and the calendar's are the single character U+E787");
            var view = new NotchView { Animate = false };
            var cells = new List<CellModel>
            {
                new CellModel { Provider = "claude" },
                new CellModel { Provider = "codex" },
                new CellModel { Provider = CalendarDay.Provider, Glyph = CalendarDay.Glyph, Text = "now", ShowArc = true, Used = 40, ArcColor = Palette.Text },
            };
            view.Update(cells, false);
            TestRunner.Near(NotchView.HeightFor(3), view.ViewSize.Height, "three cells: the calendar under Claude and Codex");
            TestRunner.Eq(2, view.CellAt(new Point(28, 18 + 10 + 2 * 76 + 30)), "the third cell is found under the mouse");
            TestRunner.Check(HasText(view, CalendarGlyph) && HasText(view, "now"), "the calendar cell shows its glyph and its text");
            int marks = 0, textArcs = 0;
            Color ink = NotchView.Brush(Palette.Text).Color;
            foreach (UIElement child in view.Children)
            {
                var path = child as WPath;
                if (path == null || path.Visibility != Visibility.Visible) continue;
                if (path.Fill != null) marks++;
                var stroke = path.Stroke as SolidColorBrush;
                if (path.StrokeThickness == NotchView.RingStroke && stroke != null && stroke.Color == ink) textArcs++;
            }
            TestRunner.Eq(2, marks, "the provider marks show on Claude's and Codex's cells only");
            TestRunner.Eq(1, textArcs, "the calendar's ring fills in the text colour");
            cells[2] = new CellModel { Provider = "codex", Text = "21%" };
            view.Update(cells, false);
            TestRunner.Check(!HasText(view, CalendarGlyph) && HasText(view, "21%"), "a cell that has no glyph any more shows its mark again");
        }

        static void CardListsTheDay()
        {
            CalendarCardModel m = CalendarDay.Card(CalendarDayTests.Snapshot(CalendarDayTests.Fixture(), CalendarDayTests.At(10, 6, 0, 0)), CalendarDayTests.At(10, 6, 0, 30), CalendarDayTests.Zone, En);
            var card = new CardView();
            card.ShowCalendar(m, true);
            Lay(card);
            TestRunner.Near(CardView.CardWidth + CardView.PointerWidth + 2 * CardView.ShadowMargin, card.DesiredSize.Width, "the calendar's card is as wide as the others");
            TestRunner.Check(card.DesiredSize.Height - 2 * CardView.ShadowMargin < 500, "and not too tall: " + card.DesiredSize.Height);
            TestRunner.Check(HasText(card, CalendarGlyph) && HasText(card, "Calendar") && HasText(card, "Today") && HasText(card, "Tomorrow"), "its title, and a heading per day");
            TestRunner.Check(HasText(card, "Offsite") && HasText(card, "All day") && HasText(card, "11:00 PM – 1:00 AM") && HasText(card, "Lunch") && !HasText(card, "Planning"), "the rest of today and tomorrow's first three");
            TestRunner.Check(HasText(card, "Updated 30 min ago"), "and how old the list is");
            TextBlock now = FindText(card, "Night deploy"), later = FindText(card, "Standup");
            TestRunner.Check(now != null && BackgroundOf(now) == NotchView.Brush(Palette.Track).Color, "the event happening now is highlighted");
            TestRunner.Check(later != null && BackgroundOf(later) == Colors.Transparent, "the others aren't");
            TestRunner.Check(!card.ShowingPrompt, "it isn't a request");

            var longOne = new CalendarCardModel();
            var today = new CalendarSection { Heading = "Today" };
            today.Rows.Add(new CalendarRow { Time = "11:00 AM – 12:00 PM", Title = "A very long meeting title that goes on and on about quarterly planning and more" });
            longOne.Sections.Add(today);
            card.ShowCalendar(longOne, false);
            Lay(card);
            TextBlock title = FindText(card, today.Rows[0].Title);
            double right = title != null ? title.TranslatePoint(new Point(title.ActualWidth, 0), card).X : double.MaxValue;
            TestRunner.Check(right <= CardView.PointerWidth + CardView.CardWidth, "a long title is cut inside the card: " + right);
        }

        // The quick way (link spec): paste the calendar's secret iCal address. It is write-only, like the secrets.
        static void SettingsCalendarLink()
        {
            var view = new PanelView();
            view.Update(Model(null));
            view.ShowSettings(false, "", "Ctrl+Alt+N");
            view.ShowGoogle(new GoogleSettings());
            Lay(view);
            TestRunner.Check(HasText(view, "Calendar link") && HasText(view, "Save link") && HasText(view, "Use your own Google client instead"), "nothing set up: the calendar link first, your own client behind a link");
            TestRunner.Check(!HasText(view, "Client ID") && !HasText(view, "Remove link"), "the client's fields hidden, and nothing to remove");
            TestRunner.Check(HasText(view, "In Google Calendar on the web: ⚙ Settings → your calendar → Integrate calendar → Secret address in iCal format. Capsule only reads it."), "where to find the link");
            string saved = null;
            int removed = 0;
            view.GoogleLinkSaved += delegate(string text) { saved = text; };
            view.GoogleLinkRemoved += delegate { removed++; };
            view.CalendarLinkBox.Password = "https://calendar.google.com/calendar/ical/x/private-y/basic.ics";
            Click(Holder(view, "Save link"));
            TestRunner.Eq("https://calendar.google.com/calendar/ical/x/private-y/basic.ics", saved, "Save link hands over what was pasted");
            TestRunner.Check(view.CalendarLinkBox.Password != "", "which stays in the box until it is kept");
            view.LinkKept();
            view.ShowGoogle(new GoogleSettings { LinkSaved = true, LinkName = GoogleCalendarTests.Primary });
            Lay(view);
            TestRunner.Check(view.CalendarLinkBox.Password == "" && HasText(view, "Link saved: showing " + GoogleCalendarTests.Primary + ". Paste a new one to replace it."), "kept: the box empties, and the hint says whose calendar shows");
            Click(Holder(view, "Remove link"));
            TestRunner.Eq(1, removed, "Remove link is handed over");
            Click(Holder(view, "Use your own Google client instead"));
            Lay(view);
            TestRunner.Check(HasText(view, "Client ID") && HasText(view, "Sign in with Google") && !HasText(view, "Use your own Google client instead"), "your own client's fields open on request");
            view.CalendarLinkBox.Password = "x";
            view.ShowBoard();
            TestRunner.Eq("", view.CalendarLinkBox.Password, "leaving the settings empties the link box");
            view.ShowSettings(false, "", "Ctrl+Alt+N");
            view.ShowGoogle(new GoogleSettings { ClientId = "cid", SecretSaved = true, SignedIn = true, LinkSaved = true });
            Lay(view);
            TestRunner.Check(!HasText(view, "Calendar link") && HasText(view, "Sign out"), "signed in with your own client, the link steps back");
        }
    }
}
