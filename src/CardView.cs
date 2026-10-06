using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WEllipse = System.Windows.Shapes.Ellipse;
using WPath = System.Windows.Shapes.Path;

namespace Capsule
{
    // The hover card: a glass panel listing each limit window (and, for Claude, its sessions), with a small pointer
    // on the side that faces the notch, and room around it for its shadow. While Claude waits on a request Capsule holds,
    // Claude's card shows the request instead (PromptView). The calendar's card lists the rest of today and tomorrow.
    public sealed class CardView : Grid
    {
        public const double CardWidth = 280, PointerWidth = 8, PointerHeight = 16, Radius = 14, Inset = 14;
        public const double ShadowMargin = 16;       // room around the card for its shadow
        public const double ProjectMaxWidth = 120;   // a session row's project name gets at most this; its status takes the rest

        readonly GlassSurface glass = new GlassSurface();
        readonly Border panel = new Border();
        readonly StackPanel content = new StackPanel();
        readonly PromptView prompt = new PromptView();
        bool pointRight = true;

        public CardView()
        {
            UseLayoutRounding = true;   // bar and text edges land on whole pixels, so they stay crisp
            Margin = new Thickness(ShadowMargin);
            panel.Padding = new Thickness(Inset, 12, Inset, 12);
            panel.Width = CardWidth;
            panel.HorizontalAlignment = HorizontalAlignment.Left;
            panel.Child = content;
            Children.Add(glass);
            Children.Add(panel);
        }

        // The card and its pointer, from the card's top-left (inside the shadow margin): the shape of its glass.
        public Geometry Outline { get { return glass.Outline; } }

        public PromptView Prompt { get { return prompt; } }
        public bool ShowingPrompt { get { return panel.Child == prompt; } }

        // pointRight: the notch is to the card's right (a right-edge notch), so the pointer goes on the right.
        public void Show(CardModel model, bool pointRight)
        {
            prompt.Clear();   // a request that was shown leaves no text behind
            Build(model);
            panel.Child = content;
            Point(pointRight);
        }

        // The calendar's card (calendar spec §2).
        public void ShowCalendar(CalendarCardModel model, bool pointRight)
        {
            prompt.Clear();
            BuildCalendar(model);
            panel.Child = content;
            Point(pointRight);
        }

        // A held request in place of the usage (act-from-notch spec §3).
        public void ShowPrompt(PromptCardModel model, bool pointRight)
        {
            prompt.Show(model);
            panel.Child = prompt;
            Point(pointRight);
        }

        void Point(bool pointRight)
        {
            this.pointRight = pointRight;
            panel.Margin = new Thickness(pointRight ? 0 : PointerWidth, 0, pointRight ? PointerWidth : 0, 0);
            SetPointer(Radius + PointerHeight / 2);
        }

        // Moves the pointer so its tip sits y DIPs below the card's top, and redraws the glass around it.
        public void SetPointer(double y)
        {
            Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double height = Math.Max(DesiredSize.Height - 2 * ShadowMargin, 2 * Radius);
            glass.Apply(MakeOutline(height, pointRight, y), null, ShadowMargin);
        }

        public static Geometry MakeOutline(double height, bool pointRight, double pointerY)
        {
            var body = new RectangleGeometry(new Rect(pointRight ? 0 : PointerWidth, 0, CardWidth, height), Radius, Radius);
            double tip = pointRight ? CardWidth + PointerWidth : 0;
            double baseX = pointRight ? CardWidth - 1 : PointerWidth + 1;   // overlapping the body, so they join cleanly
            var pointer = new StreamGeometry();
            using (StreamGeometryContext ctx = pointer.Open())
            {
                ctx.BeginFigure(new Point(baseX, pointerY - PointerHeight / 2), true, true);
                ctx.LineTo(new Point(tip, pointerY), true, false);
                ctx.LineTo(new Point(baseX, pointerY + PointerHeight / 2), true, false);
            }
            var outline = new CombinedGeometry(GeometryCombineMode.Union, body, pointer);
            outline.Freeze();
            return outline;
        }

        void Build(CardModel m)
        {
            content.Children.Clear();

            var title = new DockPanel { Margin = new Thickness(0, 0, 0, 8), LastChildFill = false };
            var logo = new WPath
            {
                Data = Logos.For(m.Provider),
                Fill = NotchView.Brush(Palette.Text),
                Stretch = Stretch.Uniform,
                Width = 16,
                Height = 16,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            title.Children.Add(logo);
            TextBlock name = MakeText(m.Title, 15, Palette.Text, FontWeights.SemiBold);
            name.VerticalAlignment = VerticalAlignment.Center;
            title.Children.Add(name);
            if (m.Plan != "")
            {
                var badge = new Border
                {
                    Background = NotchView.Brush(Palette.Track),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(6, 1, 6, 2),
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                badge.Child = MakeText(m.Plan, 11, Palette.Secondary, FontWeights.Normal);
                title.Children.Add(badge);
            }
            content.Children.Add(title);

            foreach (CardRow row in m.Rows) content.Children.Add(Row(row));

            if (m.SessionRows != null)
            {
                TextBlock header = MakeText("Sessions", 11, Palette.Secondary, FontWeights.SemiBold);
                header.Margin = new Thickness(0, 4, 0, 4);
                content.Children.Add(header);
                if (m.SessionsHint != "") content.Children.Add(Wrap(MakeText(m.SessionsHint, 12, Palette.Secondary, FontWeights.Normal)));
                else if (m.SessionRows.Count == 0) content.Children.Add(MakeText("No active sessions", 12, Palette.Secondary, FontWeights.Normal));
                foreach (SessionRow s in m.SessionRows) content.Children.Add(SessionLine(s));
                if (m.MoreSessions > 0) content.Children.Add(MakeText("+" + m.MoreSessions + " more", 12, Palette.Secondary, FontWeights.Normal));
            }

            TextBlock footer = Wrap(MakeText(m.Footer, 11.5, Palette.Secondary, FontWeights.Normal));
            footer.Margin = new Thickness(0, 10, 0, 0);
            content.Children.Add(footer);
        }

        void BuildCalendar(CalendarCardModel m)
        {
            content.Children.Clear();
            var title = new DockPanel { Margin = new Thickness(0, 0, 0, 8), LastChildFill = false };
            var mark = new TextBlock
            {
                Text = CalendarDay.Glyph,
                FontFamily = new FontFamily(PanelView.IconFont),
                FontSize = 15,
                Foreground = NotchView.Brush(Palette.Text),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            title.Children.Add(mark);
            TextBlock name = MakeText("Calendar", 15, Palette.Text, FontWeights.SemiBold);
            name.VerticalAlignment = VerticalAlignment.Center;
            title.Children.Add(name);
            content.Children.Add(title);
            foreach (CalendarSection section in m.Sections)
            {
                TextBlock header = MakeText(section.Heading, 11, Palette.Secondary, FontWeights.SemiBold);
                header.Margin = new Thickness(0, 4, 0, 4);
                content.Children.Add(header);
                if (section.Rows.Count == 0 && section.Empty != "") content.Children.Add(MakeText(section.Empty, 12, Palette.Secondary, FontWeights.Normal));
                foreach (CalendarRow row in section.Rows) content.Children.Add(EventLine(row));
            }
            if (m.Footer != "")
            {
                TextBlock footer = Wrap(MakeText(m.Footer, 11.5, Palette.Secondary, FontWeights.Normal));
                footer.Margin = new Thickness(0, 10, 0, 0);
                content.Children.Add(footer);
            }
        }

        // One event: its calendar's colour dot, its times (or All day), and its title, cut with "…" when it doesn't fit.
        // The event happening now sits on a highlight. Shared with the panel's Calendar tile.
        public static Border EventLine(CalendarRow row)
        {
            var line = new Grid();
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var dot = new WEllipse
            {
                Width = 8,
                Height = 8,
                Fill = NotchView.Brush(row.Color),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            line.Children.Add(dot);
            TextBlock time = MakeText(row.Time, 12, row.Current ? Palette.Text : Palette.Secondary, FontWeights.Normal);
            time.Margin = new Thickness(0, 0, 8, 0);
            time.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(time, 1);
            line.Children.Add(time);
            TextBlock title = MakeText(row.Title, 12.5, Palette.Text, row.Current ? FontWeights.SemiBold : FontWeights.Normal);
            title.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(title, 2);
            line.Children.Add(title);
            // The highlight reaches into the card's padding, so the dots of every row stay in one column.
            return new Border
            {
                Child = line,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(-6, 0, -6, 0),
                Background = row.Current ? NotchView.Brush(Palette.Track) : Brushes.Transparent,
            };
        }

        static UIElement Row(CardRow row)
        {
            var box = new StackPanel { Margin = new Thickness(0, 2, 0, 10) };
            var top = new DockPanel();
            TextBlock reset = MakeText(row.ResetText, 12, Palette.Secondary, FontWeights.Normal);
            reset.Margin = new Thickness(8, 0, 0, 0);   // keeps a trimmed label off the reset text
            DockPanel.SetDock(reset, Dock.Right);
            top.Children.Add(reset);
            top.Children.Add(MakeText(row.Label, 13, Palette.Text, FontWeights.Normal));
            box.Children.Add(top);
            box.Children.Add(Bar(row.Used, row.Color, CardWidth - 2 * Inset));
            box.Children.Add(MakeText(row.UsedText, 12, Palette.Text, FontWeights.Normal));
            return box;
        }

        // A usage bar: the track, and the used part in its colour. Shared with the panel's tiles.
        public static Grid Bar(double used, string color, double width)
        {
            var bar = new Grid { Height = 6, Margin = new Thickness(0, 6, 0, 5) };
            bar.Children.Add(new Border { Background = NotchView.Brush(Palette.Track), CornerRadius = new CornerRadius(3) });
            double fraction = Math.Max(0, Math.Min(100, used)) / 100;
            if (fraction > 0)
            {
                bar.Children.Add(new Border
                {
                    Background = NotchView.Brush(color),
                    CornerRadius = new CornerRadius(3),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Width = Math.Max(6, fraction * width),
                });
            }
            return bar;
        }

        // The project on the left, at its natural width up to ProjectMaxWidth; the status right-aligned in the
        // rest. Each ends in "…" when it doesn't fit, so a long status can't squeeze out the project name.
        public static UIElement SessionLine(SessionRow s)
        {
            var line = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            TextBlock project = MakeText(s.Project, 12.5, Palette.Text, FontWeights.Normal);
            project.MaxWidth = ProjectMaxWidth;
            TextBlock status = MakeText(s.Status, 12, s.Color, FontWeights.Normal);
            status.Margin = new Thickness(8, 0, 0, 0);
            status.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(status, 1);
            line.Children.Add(project);
            line.Children.Add(status);
            return line;
        }

        public static TextBlock MakeText(string text, double size, string color, FontWeight weight)
        {
            return new TextBlock
            {
                Text = text ?? "",
                FontSize = size,
                Foreground = NotchView.Brush(color),
                FontWeight = weight,
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
        }

        public static TextBlock Wrap(TextBlock t)
        {
            t.TextWrapping = TextWrapping.Wrap;
            t.TextTrimming = TextTrimming.None;
            return t;
        }
    }
}
