using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using WEllipse = System.Windows.Shapes.Ellipse;

namespace Capsule
{
    // The Usage page (usage history spec §3), shown in the panel in place of the tiles: ← Usage and ‹ ›, the period,
    // Week / Month / Year, the pace lines, the total, stacked bars by model, the legend and Details.
    public sealed class UsageView : StackPanel
    {
        const string BackGlyph = "", PrevGlyph = "", NextGlyph = "", DownGlyph = "", UpGlyph = "";   // icon-font glyphs, kept as escapes
        public const double ChartHeight = 120;

        readonly DockPanel header = new DockPanel { LastChildFill = false };
        readonly ScrollViewer scroll = PanelView.BodyScroll();
        readonly StackPanel body = new StackPanel();
        UsageModel model = new UsageModel();
        bool detailsOpen;

        public event Action BackClicked, PreviousClicked, NextClicked;
        public event Action<UsagePeriod> PeriodChosen;

        public UsageView()
        {
            Width = PanelView.PanelWidth;
            HorizontalAlignment = HorizontalAlignment.Left;
            Margin = new Thickness(0, 0, 0, PanelView.Pad);
            header.Margin = new Thickness(PanelView.Pad + 4, PanelView.Pad, PanelView.Pad, 6);
            Children.Add(header);
            body.Margin = new Thickness(PanelView.Pad + 4, 0, PanelView.Pad + 4, 0);
            scroll.Content = body;
            Children.Add(scroll);
        }

        public UsageModel Model { get { return model; } }
        public bool DetailsOpen { get { return detailsOpen; } }

        public void ConstrainHeight(double height)
        {
            header.Measure(new Size(PanelView.PanelWidth, double.PositiveInfinity));
            scroll.MaxHeight = Math.Max(1, height - header.DesiredSize.Height - PanelView.Pad);
        }

        public void Update(UsageModel m)
        {
            model = m;
            BuildHeader();
            BuildBody();
        }

        public void Restyle() { if (model != null) Update(model); }

        void BuildHeader()
        {
            header.Children.Clear();
            Border back = Icon(BackGlyph, "Back", delegate { if (BackClicked != null) BackClicked(); }, true);
            back.Margin = new Thickness(-6, 0, 4, 0);
            header.Children.Add(back);
            Border next = Icon(NextGlyph, "Next", delegate { if (model.CanNext && NextClicked != null) NextClicked(); }, model.CanNext);
            DockPanel.SetDock(next, Dock.Right);
            header.Children.Add(next);
            Border previous = Icon(PrevGlyph, "Previous", delegate { if (PreviousClicked != null) PreviousClicked(); }, true);
            DockPanel.SetDock(previous, Dock.Right);
            header.Children.Add(previous);
            TextBlock title = CardView.MakeText("Usage", 15, Palette.Text, FontWeights.SemiBold);
            title.VerticalAlignment = VerticalAlignment.Center;
            header.Children.Add(title);
        }

        void BuildBody()
        {
            body.Children.Clear();
            body.Children.Add(CardView.MakeText(model.Title, 12, Palette.Secondary, FontWeights.Normal));
            body.Children.Add(Switch());

            foreach (PaceLine p in model.Pace)
            {
                TextBlock line = CardView.Wrap(CardView.MakeText(p.Text, 11.5, p.Warn ? Palette.Amber : Palette.Secondary, p.Warn ? FontWeights.SemiBold : FontWeights.Normal));
                line.Margin = new Thickness(0, 0, 0, 2);
                body.Children.Add(line);
            }

            var total = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            TextBlock number = CardView.MakeText(Format.Tokens(model.Total), 24, Palette.Text, FontWeights.SemiBold);
            total.Children.Add(number);
            TextBlock unit = CardView.MakeText("tokens", 12, Palette.Secondary, FontWeights.Normal);
            unit.VerticalAlignment = VerticalAlignment.Bottom;
            unit.Margin = new Thickness(6, 0, 0, 5);
            total.Children.Add(unit);
            body.Children.Add(total);
            if (model.Progress != "") body.Children.Add(CardView.MakeText(model.Progress, 11.5, Palette.Secondary, FontWeights.Normal));

            if (model.Empty && model.Progress == "")
            {
                TextBlock empty = CardView.Wrap(CardView.MakeText("No Claude Code or Codex activity found on this PC yet.", 12, Palette.Secondary, FontWeights.Normal));
                empty.Margin = new Thickness(0, 10, 0, 4);
                body.Children.Add(empty);
                return;
            }

            body.Children.Add(Chart());

            var legend = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            foreach (LegendRow row in model.Legend)
            {
                var line = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 2, 0, 2) };
                line.Children.Add(Dot(row.Color));
                TextBlock amount = CardView.MakeText(Format.Tokens(row.Tokens), 12.5, Palette.Secondary, FontWeights.Normal);
                DockPanel.SetDock(amount, Dock.Right);
                line.Children.Add(amount);
                line.Children.Add(CardView.MakeText(row.Name, 12.5, Palette.Text, FontWeights.Normal));
                legend.Children.Add(line);
            }
            body.Children.Add(legend);

            if (model.Details.Count > 0)
            {
                var row = new DockPanel { LastChildFill = true };
                var chevron = new TextBlock { Text = detailsOpen ? UpGlyph : DownGlyph, FontFamily = new FontFamily(PanelView.IconFont), FontSize = 10, Foreground = NotchView.Brush(Palette.Secondary), VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(chevron, Dock.Right);
                row.Children.Add(chevron);
                row.Children.Add(CardView.MakeText("Details", 12.5, Palette.Text, FontWeights.SemiBold));
                Border details = PanelView.Button(row, delegate { detailsOpen = !detailsOpen; BuildBody(); if (Resized != null) Resized(); }, NotchView.Brush(Palette.Track), NotchView.Brush(Palette.Track), new Thickness(10, 7, 10, 7));
                details.Margin = new Thickness(0, 10, 0, 0);
                System.Windows.Automation.AutomationProperties.SetName(details, "Details");
                body.Children.Add(details);
                if (detailsOpen)
                    foreach (string d in model.Details)
                    {
                        TextBlock t = CardView.Wrap(CardView.MakeText(d, 11.5, Palette.Secondary, FontWeights.Normal));
                        t.Margin = new Thickness(2, 4, 0, 0);
                        body.Children.Add(t);
                    }
            }
        }

        public event Action Resized;

        UIElement Switch()
        {
            var pills = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 8) };
            var track = new Border { CornerRadius = new CornerRadius(10), Background = NotchView.Brush(Palette.Track), Padding = new Thickness(2), HorizontalAlignment = HorizontalAlignment.Left, Child = pills };
            foreach (UsagePeriod p in new[] { UsagePeriod.Week, UsagePeriod.Month, UsagePeriod.Year })
            {
                UsagePeriod chosen = p;
                bool on = model.Period == p;
                TextBlock label = CardView.MakeText(p.ToString(), 12, on ? Palette.Text : Palette.Secondary, on ? FontWeights.SemiBold : FontWeights.Normal);
                Brush rest = on ? Theme.Current.Brush(Theme.Current.Light ? Colors.White : Color.FromRgb(0x5A, 0x5A, 0x5E)) : Brushes.Transparent;
                Border pill = PanelView.Button(label, delegate { if (PeriodChosen != null) PeriodChosen(chosen); }, rest, on ? rest : NotchView.Brush(Palette.Track), new Thickness(12, 3, 12, 4));
                System.Windows.Automation.AutomationProperties.SetName(pill, p.ToString());
                pills.Children.Add(pill);
            }
            return track;
        }

        UIElement Chart()
        {
            var grid = new UniformGrid { Rows = 1, Columns = Math.Max(1, model.Bars.Count), Margin = new Thickness(0, 10, 0, 0) };
            long max = Math.Max(1, model.Bars.Max(b => (long?)b.Total) ?? 1);
            double gap = model.Bars.Count > 12 ? 1 : model.Bars.Count > 7 ? 3 : 6;
            foreach (UsageBar bar in model.Bars)
            {
                var column = new StackPanel();
                var inner = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(gap, 0, gap, 0) };
                double height = ChartHeight * bar.Total / max;
                // Bottom to top: added top first.
                for (int i = bar.Segments.Count - 1; i >= 0; i--)
                {
                    double h = Math.Max(2, height * bar.Segments[i].Tokens / Math.Max(1, bar.Total) - 2);
                    inner.Children.Add(new Border { Height = h, CornerRadius = new CornerRadius(model.Bars.Count > 12 ? 1.5 : 3.5), Background = NotchView.Brush(bar.Segments[i].Color), Margin = new Thickness(0, 2, 0, 0) });
                }
                if (bar.Segments.Count == 0)
                    inner.Children.Add(new Border { Height = 2, CornerRadius = new CornerRadius(1), Background = NotchView.Brush(Palette.Track), Opacity = bar.Future ? 0.4 : 1 });
                var holder = new Grid { Height = ChartHeight };
                holder.Children.Add(inner);
                column.Children.Add(holder);
                TextBlock label = CardView.MakeText(bar.Label, model.Bars.Count > 12 ? 10 : 11, bar.IsToday ? Palette.Text : Palette.Secondary, bar.IsToday ? FontWeights.SemiBold : FontWeights.Normal);
                label.HorizontalAlignment = HorizontalAlignment.Center;
                label.TextTrimming = TextTrimming.None;
                label.Margin = new Thickness(0, 4, 0, 0);
                column.Children.Add(label);
                column.Background = Brushes.Transparent;   // the whole column takes the hover, not just the coloured parts
                if (bar.Total > 0) column.ToolTip = bar.Tip;
                grid.Children.Add(column);
            }
            // Month labels sit under every 7th day and may be wider than a bar: let them overflow their column.
            grid.ClipToBounds = false;
            return grid;
        }

        static UIElement Dot(string color)
        {
            var dot = new WEllipse { Width = 8, Height = 8, Fill = NotchView.Brush(color), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 9, 0) };
            DockPanel.SetDock(dot, Dock.Left);
            return dot;
        }

        static Border Icon(string glyph, string tip, Action click, bool enabled)
        {
            var icon = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily(PanelView.IconFont),
                FontSize = 12,
                Foreground = NotchView.Brush(Palette.Secondary),
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = enabled ? 1 : 0.35,
            };
            Border b = PanelView.Button(icon, click, Brushes.Transparent, enabled ? NotchView.Brush(Palette.Track) : Brushes.Transparent, new Thickness(6, 5, 6, 5));
            b.ToolTip = tip;
            System.Windows.Automation.AutomationProperties.SetName(b, tip);
            return b;
        }
    }
}
