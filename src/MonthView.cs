using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using WEllipse = System.Windows.Shapes.Ellipse;

namespace Capsule
{
    // The month page (month spec §2), shown in the panel in place of the tiles: a header with the month and ‹ › Today, six
    // weeks of days with dots, and the selected day's events and tasks with + Add event and + Add task. Update redraws all
    // of it but the open form, so what is being typed in it survives the module's redraws.
    public sealed class MonthView : StackPanel
    {
        const string BackGlyph = "\uE72B", PrevGlyph = "\uE76B", NextGlyph = "\uE76C";   // U+E72B Back, U+E76B and U+E76C the chevrons: icon-font glyphs, kept as escapes
        public const double CellHeight = 40;

        readonly DockPanel header = new DockPanel { LastChildFill = false };
        readonly UniformGrid weekdays = new UniformGrid { Columns = 7, Rows = 1 };
        readonly UniformGrid grid = new UniformGrid { Columns = 7, Rows = CalendarMonth.Weeks };
        readonly StackPanel day = new StackPanel();
        readonly StackPanel form = new StackPanel();    // the add form, kept across redraws while open
        readonly TextBox titleBox = new TextBox();
        readonly CheckBox allDayBox = new CheckBox();
        readonly TextBox startBox = new TextBox(), endBox = new TextBox();
        readonly StackPanel timeRow = new StackPanel { Orientation = Orientation.Horizontal };
        readonly ComboBox whereBox = new ComboBox();     // the calendar, or the task list
        readonly TextBlock formError = new TextBlock();
        readonly Border formButtons = new Border();
        MonthModel model = new MonthModel();
        string formKind;   // null: no form; "event" or "task"
        DateTime formDay;
        bool formBusy;

        public event Action BackClicked, PreviousClicked, NextClicked, TodayClicked;
        public event Action<DateTime> DaySelected;
        public event Action<string, bool, string, string, string> AddEventRequested;   // title, all day, start, end, calendar id
        public event Action<string, string> AddTaskRequested;                         // title, list id
        public event Action<string, string, bool> TaskToggled;                        // list id, task id, done
        public event Action Resized;

        public MonthView()
        {
            Width = PanelView.PanelWidth;
            HorizontalAlignment = HorizontalAlignment.Left;
            Margin = new Thickness(0, 0, 0, PanelView.Pad);
            header.Margin = new Thickness(PanelView.Pad + 4, PanelView.Pad, PanelView.Pad, 8);
            Children.Add(header);
            var body = new StackPanel { Margin = new Thickness(PanelView.Pad + 4, 0, PanelView.Pad + 4, 0) };
            weekdays.Margin = new Thickness(0, 0, 0, 2);
            body.Children.Add(weekdays);
            body.Children.Add(grid);
            day.Margin = new Thickness(0, 10, 0, 0);
            body.Children.Add(day);
            form.Margin = new Thickness(0, 8, 0, 0);
            form.Visibility = Visibility.Collapsed;
            body.Children.Add(form);
            Children.Add(body);
            BuildForm();
        }

        public bool FormOpen { get { return formKind != null; } }
        public TextBox TitleBox { get { return titleBox; } }
        public TextBox StartBox { get { return startBox; } }
        public TextBox EndBox { get { return endBox; } }
        public CheckBox AllDayBox { get { return allDayBox; } }

        public void Update(MonthModel m)
        {
            model = m;
            if (formKind != null && (formDay != m.Selected || !m.CanAdd)) CloseForm();   // another day, or no adding any more
            BuildHeader();
            BuildGrid();
            BuildDay();
            if (formKind != null) RefreshChoices();
            Restyle();
        }

        void BuildHeader()
        {
            header.Children.Clear();
            Border back = Icon(BackGlyph, "Back", delegate { if (BackClicked != null) BackClicked(); });
            back.Margin = new Thickness(-6, 0, 4, 0);
            header.Children.Add(back);
            Border today = PanelView.PillButton("Today", delegate { if (TodayClicked != null) TodayClicked(); });
            today.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(today, Dock.Right);
            header.Children.Add(today);
            Border next = Icon(NextGlyph, "Next month", delegate { if (NextClicked != null) NextClicked(); });
            DockPanel.SetDock(next, Dock.Right);
            next.Margin = new Thickness(0, 0, 6, 0);
            header.Children.Add(next);
            Border previous = Icon(PrevGlyph, "Previous month", delegate { if (PreviousClicked != null) PreviousClicked(); });
            DockPanel.SetDock(previous, Dock.Right);
            header.Children.Add(previous);
            TextBlock title = CardView.MakeText(model.Title, 15, Palette.Text, FontWeights.SemiBold);
            title.VerticalAlignment = VerticalAlignment.Center;
            header.Children.Add(title);
        }

        static Border Icon(string glyph, string tip, Action click)
        {
            var icon = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily(PanelView.IconFont),
                FontSize = 12,
                Foreground = NotchView.Brush(Palette.Secondary),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Border b = PanelView.Button(icon, click, Brushes.Transparent, NotchView.Brush(Palette.Track), new Thickness(6, 5, 6, 5));
            b.ToolTip = tip;
            System.Windows.Automation.AutomationProperties.SetName(b, tip);
            return b;
        }

        void BuildGrid()
        {
            weekdays.Children.Clear();
            foreach (string name in model.Weekdays)
            {
                TextBlock t = CardView.MakeText(name, 11, Palette.Secondary, FontWeights.SemiBold);
                t.HorizontalAlignment = HorizontalAlignment.Center;
                weekdays.Children.Add(t);
            }
            grid.Children.Clear();
            foreach (MonthDay d in model.Days) grid.Children.Add(Cell(d));
        }

        // A day: its number, with up to three dots under it (and a small + for more). Today is ringed, the selected day
        // filled; days of the months around it are dimmed.
        UIElement Cell(MonthDay d)
        {
            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            TextBlock number = CardView.MakeText(d.Date.Day.ToString(System.Globalization.CultureInfo.CurrentCulture), 12.5, d.InMonth ? Palette.Text : Palette.Secondary,
                d.Today || d.Selected ? FontWeights.SemiBold : FontWeights.Normal);
            number.HorizontalAlignment = HorizontalAlignment.Center;
            stack.Children.Add(number);
            var dots = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Height = 6, Margin = new Thickness(0, 2, 0, 0) };
            foreach (string color in d.Dots)
                dots.Children.Add(new WEllipse { Width = 4, Height = 4, Margin = new Thickness(1, 0, 1, 0), Fill = NotchView.Brush(color), VerticalAlignment = VerticalAlignment.Center });
            if (d.More)
            {
                TextBlock more = CardView.MakeText("+", 8, Palette.Secondary, FontWeights.SemiBold);
                more.Margin = new Thickness(1, -3, 0, 0);
                dots.Children.Add(more);
            }
            stack.Children.Add(dots);
            Brush fill = d.Selected ? NotchView.Brush(Palette.Track) : Brushes.Transparent;
            DateTime date = d.Date;
            Border cell = PanelView.Button(stack, delegate { if (DaySelected != null) DaySelected(date); }, fill, NotchView.Brush(Palette.Track), new Thickness(0));
            cell.Height = CellHeight;
            cell.Margin = new Thickness(1);
            cell.CornerRadius = new CornerRadius(8);
            if (!d.InMonth) cell.Opacity = 0.55;
            if (d.Today)
            {
                cell.BorderThickness = new Thickness(1.5);
                cell.BorderBrush = NotchView.Brush(Palette.Text);
            }
            System.Windows.Automation.AutomationProperties.SetName(cell, d.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
            return cell;
        }

        void BuildDay()
        {
            day.Children.Clear();
            TextBlock heading = CardView.MakeText(model.SelectedTitle, 12, Palette.Secondary, FontWeights.SemiBold);
            heading.Margin = new Thickness(0, 0, 0, 4);
            day.Children.Add(heading);
            if (model.Loading) day.Children.Add(CardView.MakeText("Checking…", 12, Palette.Secondary, FontWeights.Normal));
            else if (model.Events.Count == 0 && model.Tasks.Count == 0) day.Children.Add(CardView.MakeText(model.Empty, 12, Palette.Secondary, FontWeights.Normal));
            foreach (CalendarRow row in model.Events) day.Children.Add(CardView.EventLine(row));
            foreach (MonthTask task in model.Tasks) day.Children.Add(TaskLine(task));
            if (model.Note != "")
            {
                TextBlock note = CardView.Wrap(CardView.MakeText(model.Note, 11.5, Palette.Amber, FontWeights.Normal));
                note.Margin = new Thickness(0, 6, 0, 0);
                day.Children.Add(note);
            }
            if (model.CanAdd && formKind == null)
            {
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
                buttons.Children.Add(PanelView.PillButton("+ Add event", delegate { OpenForm("event"); }));
                if (model.ShowTasks)
                {
                    Border task = PanelView.PillButton("+ Add task", delegate { OpenForm("task"); });
                    task.Margin = new Thickness(8, 0, 0, 0);
                    buttons.Children.Add(task);
                }
                day.Children.Add(buttons);
            }
        }

        // A task: a check box with its title, struck through once done.
        UIElement TaskLine(MonthTask task)
        {
            TextBlock title = CardView.MakeText(task.Title, 12.5, task.Done ? Palette.Secondary : Palette.Text, FontWeights.Normal);
            if (task.Done) title.TextDecorations = TextDecorations.Strikethrough;
            var box = new CheckBox
            {
                Content = title,
                IsChecked = task.Done,
                Margin = new Thickness(0, 2, 0, 2),
                VerticalContentAlignment = VerticalAlignment.Center,
                FocusVisualStyle = null,
            };
            System.Windows.Automation.AutomationProperties.SetName(box, task.Title);
            string list = task.ListId, id = task.Id;
            box.Click += delegate { if (TaskToggled != null) TaskToggled(list, id, box.IsChecked == true); };
            return box;
        }

        // ---- The add forms ----

        void BuildForm()
        {
            foreach (TextBox b in new[] { titleBox, startBox, endBox }) PanelView.Plain(b);
            titleBox.MaxLength = CalendarMonth.MaxTitle;
            form.Children.Add(Field(titleBox));
            allDayBox.Content = CardView.MakeText("All day", 12.5, Palette.Text, FontWeights.Normal);
            allDayBox.Margin = new Thickness(0, 6, 0, 0);
            allDayBox.FocusVisualStyle = null;
            allDayBox.Click += delegate { timeRow.Visibility = allDayBox.IsChecked == true ? Visibility.Collapsed : Visibility.Visible; RaiseResized(); };
            form.Children.Add(allDayBox);
            startBox.Width = 110;
            endBox.Width = 110;
            timeRow.Margin = new Thickness(0, 6, 0, 0);
            timeRow.Children.Add(Field(startBox));
            TextBlock to = CardView.MakeText("to", 12, Palette.Secondary, FontWeights.Normal);
            to.Margin = new Thickness(8, 0, 8, 0);
            to.VerticalAlignment = VerticalAlignment.Center;
            timeRow.Children.Add(to);
            timeRow.Children.Add(Field(endBox));
            form.Children.Add(timeRow);
            whereBox.Margin = new Thickness(0, 6, 0, 0);
            whereBox.FontSize = 12.5;
            form.Children.Add(whereBox);
            formError.FontSize = 11.5;
            formError.TextWrapping = TextWrapping.Wrap;
            formError.Margin = new Thickness(0, 6, 0, 0);
            form.Children.Add(formError);
            formButtons.Margin = new Thickness(0, 8, 0, 0);
            form.Children.Add(formButtons);
        }

        static Border Field(UIElement content)
        {
            return new Border { Child = content, CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 6, 10, 6), Background = NotchView.Brush(Palette.Track) };
        }

        void OpenForm(string kind)
        {
            formKind = kind;
            formDay = model.Selected;
            formBusy = false;
            titleBox.Clear();
            bool isEvent = kind == "event";
            allDayBox.Visibility = isEvent ? Visibility.Visible : Visibility.Collapsed;
            allDayBox.IsChecked = model.AllDayDefault;
            startBox.Text = model.StartText;
            endBox.Text = model.EndText;
            timeRow.Visibility = isEvent && !model.AllDayDefault ? Visibility.Visible : Visibility.Collapsed;
            formError.Text = "";
            RefreshChoices();
            form.Visibility = Visibility.Visible;
            BuildDay();
            Restyle();
            RaiseResized();
            titleBox.Focus();
        }

        // The calendars (or task lists) the form can put it in, keeping the one picked.
        void RefreshChoices()
        {
            string picked = WhereId();
            whereBox.Items.Clear();
            if (formKind == "event")
                foreach (CalendarChoice c in model.Calendars) whereBox.Items.Add(new ComboBoxItem { Content = c.Name, Tag = c.Id });
            else
                foreach (TaskList l in model.Lists) whereBox.Items.Add(new ComboBoxItem { Content = l.Title, Tag = l.Id });
            whereBox.SelectedIndex = whereBox.Items.Count > 0 ? 0 : -1;
            for (int i = 0; i < whereBox.Items.Count; i++)
                if (picked != "" && (string)((ComboBoxItem)whereBox.Items[i]).Tag == picked) whereBox.SelectedIndex = i;
            whereBox.Visibility = whereBox.Items.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            RenderFormButtons();
        }

        void RenderFormButtons()
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(PanelView.PillButton(formBusy ? "Adding…" : "Add", Submit));
            Border cancel = PanelView.PillButton("Cancel", CloseFormAndRedraw);
            cancel.Margin = new Thickness(8, 0, 0, 0);
            row.Children.Add(cancel);
            formButtons.Child = row;
        }

        string WhereId()
        {
            var item = whereBox.SelectedItem as ComboBoxItem;
            return item != null ? (item.Tag as string ?? "") : "";
        }

        void Submit()
        {
            if (formBusy || formKind == null) return;
            if (formKind == "event")
            {
                if (AddEventRequested != null) AddEventRequested(titleBox.Text, allDayBox.IsChecked == true, startBox.Text, endBox.Text, WhereId());
            }
            else if (AddTaskRequested != null) AddTaskRequested(titleBox.Text, WhereId());
        }

        // The Controller is adding what the form says: the button says so until FormDone or FormError.
        public void FormBusy()
        {
            formBusy = true;
            formError.Text = "";
            RenderFormButtons();
        }

        // What's wrong, under the form; what was typed stays.
        public void FormError(string why)
        {
            formBusy = false;
            formError.Text = why ?? "";
            formError.Foreground = NotchView.Brush(Palette.Amber);
            RenderFormButtons();
            RaiseResized();
        }

        // Added: the form closes.
        public void FormDone() { CloseFormAndRedraw(); }

        void CloseFormAndRedraw()
        {
            CloseForm();
            BuildDay();
            Restyle();
            RaiseResized();
        }

        void CloseForm()
        {
            formKind = null;
            formBusy = false;
            titleBox.Clear();
            formError.Text = "";
            form.Visibility = Visibility.Collapsed;
        }

        public void Restyle()
        {
            foreach (TextBox b in new[] { titleBox, startBox, endBox })
            {
                b.Foreground = NotchView.Brush(Palette.Text);
                b.CaretBrush = NotchView.Brush(Palette.Text);
                var field = b.Parent as Border;
                if (field != null) field.Background = NotchView.Brush(Palette.Track);
            }
            allDayBox.Foreground = NotchView.Brush(Palette.Text);
        }

        void RaiseResized() { if (Resized != null) Resized(); }
    }
}
