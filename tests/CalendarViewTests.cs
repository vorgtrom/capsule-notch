using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
            TheTileOpensTheMonth();
            TheMonthPageShowsAMonth();
            TheMonthPageAddsAndTicks();
            TheGlassFollowsTheMonthPage();
            RowsCanBeEditedAndDeleted();
            TheButtonsShowOnHover();
            ThePageTakesItsKeys();
            ATickLeavesASavingFormOpen();
            OlderSignInsAreAskedToSignInAgain();
        }

        static MonthModel MonthSample(bool canAdd)
        {
            var tasks = new List<GoogleTask>
            {
                new GoogleTask { Id = "t1", ListId = "l1", Title = "Send the invoice", Due = new DateTime(2026, 10, 6) },
                new GoogleTask { Id = "t2", ListId = "l1", Title = "Water the plants", Due = new DateTime(2026, 10, 6), Done = true },
            };
            MonthModel m = CalendarMonth.Build(2026, 10, new DateTime(2026, 10, 6), CalendarDayTests.Fixture(), tasks, CalendarDayTests.At(10, 6, 8, 0), CalendarDayTests.Zone, En);
            m.CanAdd = canAdd;
            m.ShowTasks = canAdd;
            m.Note = canAdd ? "" : CalendarModule.AddNeedsSignIn;
            m.Calendars.Add(new CalendarChoice { Id = "primary@example.com", Name = "primary@example.com" });
            m.Calendars.Add(new CalendarChoice { Id = "family", Name = "Family" });
            m.Lists.Add(new TaskList { Id = "l1", Title = "My Tasks" });
            return m;
        }

        // The element the automation name is on, or null.
        static FrameworkElement Named(DependencyObject root, string name)
        {
            var e = root as FrameworkElement;
            if (e != null && e.Visibility != Visibility.Visible) return null;
            if (e != null && System.Windows.Automation.AutomationProperties.GetName(e) == name) return e;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                FrameworkElement found = Named(VisualTreeHelper.GetChild(root, i), name);
                if (found != null) return found;
            }
            return null;
        }

        static void TheTileOpensTheMonth()
        {
            var view = new PanelView();
            view.Update(Model(TileAt(8, 0)));
            Lay(view);
            int clicks = 0;
            view.MonthClicked += delegate { clicks++; };
            TestRunner.Check(HasText(view, CalendarGlyph), "a connected Calendar tile has the month button, U+E787");
            Click(Holder(view, CalendarGlyph));
            TestRunner.Eq(1, clicks, "which asks for the month page");
            view.Update(Model(CalendarDay.Tile(new CalendarSnapshot(), CalendarDayTests.At(10, 6, 8, 0), CalendarDayTests.Zone, En)));
            Lay(view);
            TestRunner.Check(!HasText(view, CalendarGlyph), "not connected, there's no month to open");
        }

        static void TheMonthPageShowsAMonth()
        {
            var view = new PanelView();
            view.Update(Model(TileAt(8, 0)));
            int closed = 0, back = 0, previous = 0, next = 0, today = 0;
            DateTime picked = DateTime.MinValue;
            view.MonthClosed += delegate { closed++; };
            view.Month.BackClicked += delegate { back++; };
            view.Month.PreviousClicked += delegate { previous++; };
            view.Month.NextClicked += delegate { next++; };
            view.Month.TodayClicked += delegate { today++; };
            view.Month.DaySelected += delegate(DateTime d) { picked = d; };
            view.ShowMonth(MonthSample(true));
            Lay(view);
            TestRunner.Check(view.ShowingMonth && HasText(view, "October 2026") && HasText(view, "Sun") && HasText(view, "Tue 6 Oct"), "the month page in the tiles' place: its month, the weekdays, the selected day");
            TestRunner.Check(Named(view, "2026-09-27") != null && Named(view, "2026-11-07") != null && Named(view, "2026-11-08") == null, "six weeks of days");
            TestRunner.Check(HasText(view, "Night deploy") && HasText(view, "11:00 PM – 1:00 AM") && HasText(view, "Send the invoice") && HasText(view, "Water the plants"), "the day's events and tasks");
            TextBlock allDay = FindText(view, "All day"), task = FindText(view, CalendarDay.TaskTime);
            TestRunner.Check(allDay != null && task != null && Math.Abs(allDay.TranslatePoint(new Point(0, 0), view).X - task.TranslatePoint(new Point(0, 0), view).X) < 0.5,
                "a task's row lines up with an event's: its box where the dot is, \"Task\" where the time is");
            Click(Named(view, "2026-10-09"));
            Click(Named(view, "Previous month"));
            Click(Named(view, "Next month"));
            Click(Holder(view, "Today"));
            TestRunner.Check(picked == new DateTime(2026, 10, 9) && previous == 1 && next == 1 && today == 1, "a day, ‹, › and Today are handed over");
            Click(Named(view, "Back"));
            TestRunner.Check(back == 1 && closed == 1 && !view.ShowingMonth && HasText(view, "Standup"), "← goes back to the tiles");
            view.ShowMonth(MonthSample(false));
            Lay(view);
            TestRunner.Check(!HasText(view, "+ Add event") && HasText(view, CalendarModule.AddNeedsSignIn), "without a sign-in that can add: no Add, and why");
        }

        static void TheMonthPageAddsAndTicks()
        {
            var view = new PanelView();
            view.Update(Model(TileAt(8, 0)));
            view.ShowMonth(MonthSample(true));
            Lay(view);
            string[] sent = null;
            string[] task = null;
            string ticked = null;
            view.Month.AddEventRequested += delegate(string title, bool allDay, string start, string end, string calendarId) { sent = new[] { title, allDay.ToString(), start, end, calendarId }; };
            view.Month.AddTaskRequested += delegate(string title, string listId) { task = new[] { title, listId }; };
            view.Month.TaskToggled += delegate(string list, string id, bool done) { ticked = list + "/" + id + "=" + done; };
            var boxes = new List<CheckBox>();
            CheckBoxes(view, boxes);
            CheckBox invoice = boxes.FirstOrDefault(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Send the invoice");
            if (invoice != null)
            {
                invoice.IsChecked = true;
                invoice.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            }
            TestRunner.Eq("l1/t1=True", ticked, "ticking a task hands over its list, id and state");
            Click(Holder(view, "+ Add event"));
            Lay(view);
            TestRunner.Check(view.Month.FormOpen && view.Month.AllDayBox.IsChecked == false && view.Month.StartBox.Text == "9:00 AM" && view.Month.EndBox.Text == "10:00 AM" && !HasText(view, "+ Add event"), "today's event form: not all-day, from the next whole hour");
            view.Month.TitleBox.Text = "Lunch with Sam";
            view.Month.StartBox.Text = "12:30 PM";
            view.Month.EndBox.Text = "1:30 PM";
            Click(Holder(view, "Add"));
            TestRunner.Check(sent != null && sent[0] == "Lunch with Sam" && sent[1] == "False" && sent[2] == "12:30 PM" && sent[3] == "1:30 PM" && sent[4] == "primary@example.com", "Add hands over what was typed, into the first calendar");
            view.Month.FormBusy();
            view.ShowMonth(MonthSample(true));
            Lay(view);
            TestRunner.Check(view.Month.FormOpen && view.Month.TitleBox.Text == "Lunch with Sam" && HasText(view, "Adding…"), "a redraw keeps the form and what was typed");
            view.Month.FormError("Couldn't reach Google");
            Lay(view);
            TextBlock error = FindText(view, "Couldn't reach Google");
            TestRunner.Check(error != null && ((SolidColorBrush)error.Foreground).Color == NotchView.Brush(Palette.Amber).Color && view.Month.TitleBox.Text == "Lunch with Sam" && HasText(view, "Add"), "a failure says why in amber, and keeps the form");
            view.Month.FormDone();
            Lay(view);
            TestRunner.Check(!view.Month.FormOpen && HasText(view, "+ Add event"), "added: the form closes");
            Click(Holder(view, "+ Add task"));
            view.Month.TitleBox.Text = "Call the dentist";
            Click(Holder(view, "Add"));
            TestRunner.Check(task != null && task[0] == "Call the dentist" && task[1] == "l1" && view.Month.AllDayBox.Visibility != Visibility.Visible, "a task: its title and list");
            view.Month.TitleBox.Text = "x";
            MonthModel other = MonthSample(true);
            other.Selected = new DateTime(2026, 10, 9);
            view.ShowMonth(other);
            TestRunner.Check(!view.Month.FormOpen, "picking another day closes the form");
        }

        // Redrawn in place (the month read, the sign-in known), the page's height changes: the glass and the window follow.
        static void TheGlassFollowsTheMonthPage()
        {
            var view = new PanelView();
            view.Update(Model(TileAt(8, 0)));
            int resized = 0;
            view.Resized += delegate { resized++; };
            MonthModel before = MonthSample(true);
            before.CanAdd = false;
            view.ShowMonth(before);
            double low = view.Outline.Bounds.Height;
            view.ShowMonth(MonthSample(true));
            TestRunner.Check(view.Outline.Bounds.Height > low + 20 && resized == 2, "the Add buttons appearing grow the glass, and the window is told both times");
        }

        // The user's own events and tasks have a pencil and a bin at the right; a delete asks first, on the row.
        static void RowsCanBeEditedAndDeleted()
        {
            var view = new PanelView();
            view.Update(Model(TileAt(8, 0)));
            view.ShowMonth(MonthSample(true));
            Lay(view);
            string deleted = null, renamed = null;
            string[] edited = null;
            view.Month.DeleteEventRequested += delegate(string calendarId, string eventId) { deleted = calendarId + "/" + eventId; };
            view.Month.DeleteTaskRequested += delegate(string listId, string taskId) { deleted = listId + "/" + taskId; };
            view.Month.EditEventRequested += delegate(string calendarId, string eventId, string title, bool allDay, string start, string end) { edited = new[] { calendarId, eventId, title, allDay.ToString(), start, end }; };
            view.Month.EditTaskRequested += delegate(string listId, string taskId, string title) { renamed = listId + "/" + taskId + "=" + title; };
            TestRunner.Check(Named(view, "Edit: Offsite") != null && Named(view, "Ask to delete: Offsite") != null && Named(view, "Edit: Send the invoice") != null && Named(view, "Ask to delete: Send the invoice") != null,
                "the user's own events and tasks have edit and delete");
            TestRunner.Check(Named(view, "Edit: Standup") == null && Named(view, "Ask to delete: Standup") == null && Named(view, "Edit: Night deploy") == null && Named(view, "Ask to delete: Night deploy") != null,
                "one someone else organised has neither; one over two days, delete only");
            Click(Named(view, "Ask to delete: Offsite"));
            Lay(view);
            TestRunner.Check(HasText(view, "Delete this?") && Named(view, "Delete: Offsite") != null && deleted == null, "the bin asks first, on the row");
            Click(Holder(view, "Cancel"));
            Lay(view);
            TestRunner.Check(!HasText(view, "Delete this?") && Named(view, "Ask to delete: Offsite") != null, "Cancel leaves it be");
            Click(Named(view, "Ask to delete: Offsite"));
            Lay(view);
            Click(Named(view, "Delete: Offsite"));
            Lay(view);
            TestRunner.Check(deleted == GoogleCalendarTests.Primary + "/offsite" && HasText(view, "Deleting…"), "Delete hands over the event's calendar and id, and says it is deleting");
            view.Month.ShowProblem("Couldn't reach Google");
            Lay(view);
            TextBlock problem = FindText(view, "Couldn't reach Google");
            TestRunner.Check(problem != null && ((SolidColorBrush)problem.Foreground).Color == NotchView.Brush(Palette.Amber).Color && !HasText(view, "Deleting…"), "refused, the row is back and the day says why, in amber");
            Click(Named(view, "Edit: Weekly review"));
            Lay(view);
            TestRunner.Check(view.Month.FormOpen && view.Month.TitleBox.Text == "Weekly review" && view.Month.AllDayBox.IsChecked == false && view.Month.StartBox.Text == "1:00 PM" && view.Month.EndBox.Text == "2:00 PM"
                && HasText(view, "Save") && !HasText(view, "Couldn't reach Google"), "the pencil opens the form filled in with the event, to Save");
            view.Month.TitleBox.Text = "Weekly review (moved)";
            view.Month.StartBox.Text = "3pm";
            view.Month.EndBox.Text = "4pm";
            Click(Holder(view, "Save"));
            Lay(view);
            TestRunner.Check(edited != null && edited[0] == GoogleCalendarTests.Primary && edited[1] == "weekly_20261006T200000Z" && edited[2] == "Weekly review (moved)" && edited[3] == "False" && edited[4] == "3pm" && edited[5] == "4pm"
                && HasText(view, "Saving…") && view.Month.FormWaiting, "Save hands over the event's ids and what was typed, and waits");
            view.Month.ChangeDone();
            Lay(view);
            TestRunner.Check(!view.Month.FormOpen, "done, the form closes");
            Click(Named(view, "Edit: Send the invoice"));
            view.Month.TitleBox.Text = "Send the invoice today";
            Click(Holder(view, "Save"));
            TestRunner.Check(renamed == "l1/t1=Send the invoice today" && view.Month.AllDayBox.Visibility != Visibility.Visible, "a task's pencil renames it");
            view.ShowMonth(MonthSample(false));
            Lay(view);
            TestRunner.Check(Named(view, "Edit: Offsite") == null && Named(view, "Ask to delete: Send the invoice") == null, "without a sign-in that can change them, no buttons");
        }

        static void TheButtonsShowOnHover()
        {
            var view = new PanelView();
            view.ShowMonth(MonthSample(true));
            Lay(view);
            FrameworkElement pencil = Named(view, "Edit: Offsite"), bin = Named(view, "Ask to delete: Offsite");
            var row = pencil != null ? VisualTreeHelper.GetParent(pencil) as UIElement : null;
            TestRunner.Check(pencil != null && bin != null && row != null && pencil.Opacity == 0 && bin.Opacity == 0, "a row's pencil and bin are hidden until it is hovered");
            if (row == null) return;
            row.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
            bool shown = pencil.Opacity == 1 && bin.Opacity == 1;
            row.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseLeaveEvent });
            TestRunner.Check(shown && pencil.Opacity == 0, "they show while it is hovered, and hide again");
        }

        static void ThePageTakesItsKeys()
        {
            var view = new PanelView();
            view.ShowMonth(MonthSample(true));
            Lay(view);
            MonthView page = view.Month;
            var picked = new List<DateTime>();
            int previous = 0, next = 0;
            string[] sent = null;
            page.DaySelected += delegate(DateTime d) { picked.Add(d); };
            page.PreviousClicked += delegate { previous++; };
            page.NextClicked += delegate { next++; };
            page.AddEventRequested += delegate(string title, bool allDay, string start, string end, string calendarId) { sent = new[] { title, start, end }; };
            bool taken = page.HandleKey(Key.Right, null) && page.HandleKey(Key.Left, null) && page.HandleKey(Key.Up, null) && page.HandleKey(Key.Down, null);
            TestRunner.Check(taken && string.Join(",", picked.Select(d => d.ToString("MM-dd", CultureInfo.InvariantCulture))) == "10-07,10-05,09-29,10-13", "the arrows move the day, up and down a week");
            TestRunner.Check(page.HandleKey(Key.PageUp, null) && page.HandleKey(Key.PageDown, null) && previous == 1 && next == 1, "Page Up and Page Down change the month");
            TestRunner.Check(!page.HandleKey(Key.Escape, null) && !page.HandleKey(Key.A, null), "with nothing to cancel, Esc is left to close the panel");
            Click(Named(view, "Ask to delete: Offsite"));
            Lay(view);
            TestRunner.Check(page.HandleKey(Key.Escape, null) && !HasText(view, "Delete this?"), "Esc lets go of a Delete this?");
            Click(Holder(view, "+ Add event"));
            Lay(view);
            page.TitleBox.Text = "Lunch with Sam";
            picked.Clear();
            TestRunner.Check(!page.HandleKey(Key.Left, page.TitleBox) && picked.Count == 0, "typing in the form, the arrows stay the box's");
            TestRunner.Check(page.HandleKey(Key.Enter, page.TitleBox) && sent != null && sent[0] == "Lunch with Sam" && page.FormWaiting, "Enter in the form adds");
            page.FormError("Couldn't reach Google");
            TestRunner.Check(page.HandleKey(Key.Escape, page.TitleBox) && !page.FormOpen && HasText(view, "+ Add event"), "Esc cancels the form, not the panel");
        }

        // A tick or a delete that finishes while the form's own add is with Google leaves the form waiting on it.
        static void ATickLeavesASavingFormOpen()
        {
            var view = new PanelView();
            view.ShowMonth(MonthSample(true));
            Lay(view);
            Click(Holder(view, "+ Add event"));
            view.Month.TitleBox.Text = "Lunch with Sam";
            Click(Holder(view, "Add"));
            view.Month.ChangeDone(false);
            TestRunner.Check(view.Month.FormOpen && view.Month.FormWaiting && view.Month.TitleBox.Text == "Lunch with Sam", "a tick done meanwhile leaves the saving form open");
            view.Month.ChangeDone(true);
            TestRunner.Check(!view.Month.FormOpen, "its own add closes it");
        }

        static void OlderSignInsAreAskedToSignInAgain()
        {
            var view = new PanelView();
            view.Update(Model(null));
            view.ShowSettings(false, "", "Ctrl+Alt+N");
            string id = null, secret = null;
            view.GoogleSignInClicked += delegate(string i, string s2) { id = i; secret = s2; };
            view.ShowGoogle(new GoogleSettings { ClientId = "cid", SecretSaved = true, SignedIn = true, Account = "a@example.com", NeedsSignInAgain = true });
            Lay(view);
            TestRunner.Check(HasText(view, "Signed in as a@example.com. Sign in again to let Capsule add events and tasks.") && HasText(view, "Sign in again"), "a sign-in from before adding says to sign in again, with a button");
            Click(Holder(view, "Sign in again"));
            TestRunner.Check(id == "" && secret == "", "which signs in with the saved client");
        }

        // A paste, as WPF raises it on the box before it inserts the text. True when a handler cancelled it.
        static bool Paste(TextBox box, string text)
        {
            var args = new DataObjectPastingEventArgs(new DataObject(DataFormats.UnicodeText, text), false, DataFormats.UnicodeText);
            args.RoutedEvent = DataObject.PastingEvent;
            box.RaiseEvent(args);
            return args.CommandCancelled;
        }

        // Explicitly leaving settings keeps the client draft. A downloaded client file fills both fields.
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
            Lay(view);
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
