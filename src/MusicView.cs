using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace Capsule
{
    // Kept in place while polling: pressing a button or moving across the card survives a track update.
    public sealed class MusicView : StackPanel
    {
        readonly TextBlock heading = Text("Music", 15, true), title = Text("", 15, true), artist = Text("", 12, false);
        readonly TextBlock time = Text("", 11, false), note = Text("", 11, false);
        readonly Image artwork = new Image { Width = 58, Height = 58, Stretch = Stretch.UniformToFill };
        readonly TextBlock placeholder = Text("\u266B", 27, false);
        readonly Border cover = new Border { Width = 58, Height = 58, CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 12, 0) };
        readonly Grid details = new Grid();
        readonly ProgressBar progress = new ProgressBar { Minimum = 0, Maximum = 100, Height = 3, Margin = new Thickness(0, 10, 0, 5), IsHitTestVisible = false };
        readonly Button source, previous, toggle, next;
        readonly StackPanel controls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) };
        MusicSnapshot model;
        string shownKey = "";
        public event Action<string, MusicCommand> Commanded;
        public event Action<string> SourceSelected;

        public MusicView()
        {
            heading.Margin = new Thickness(0, 0, 0, 10);
            Children.Add(heading);
            details.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            details.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var imageHost = new Grid();
            placeholder.HorizontalAlignment = HorizontalAlignment.Center;
            placeholder.VerticalAlignment = VerticalAlignment.Center;
            imageHost.Children.Add(placeholder);
            imageHost.Children.Add(artwork);
            cover.Child = imageHost;
            cover.Clip = new RectangleGeometry(new Rect(0, 0, 58, 58), 10, 10);
            details.Children.Add(cover);
            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            title.TextTrimming = artist.TextTrimming = TextTrimming.CharacterEllipsis;
            artist.Margin = new Thickness(0, 3, 0, 0);
            info.Children.Add(title);
            info.Children.Add(artist);
            Grid.SetColumn(info, 1);
            details.Children.Add(info);
            Children.Add(details);
            Children.Add(progress);
            Children.Add(time);
            source = MakeButton("", "Switch music source", SelectNext);
            source.HorizontalAlignment = HorizontalAlignment.Left;
            source.Margin = new Thickness(0, 9, 0, 0);
            Children.Add(source);
            previous = MakeButton("\uE892", "Previous track", delegate { Send(MusicCommand.Previous); });
            toggle = MakeButton("\uE768", "Play", delegate { Send(MusicCommand.TogglePlayPause); });
            next = MakeButton("\uE893", "Next track", delegate { Send(MusicCommand.Next); });
            foreach (Button button in new[] { previous, toggle, next })
            {
                ((TextBlock)button.Content).FontFamily = new FontFamily(PanelView.IconFont);
                button.Width = 50;
                button.Height = 36;
                button.Margin = new Thickness(4, 0, 4, 0);
                controls.Children.Add(button);
            }
            Children.Add(controls);
            note.TextWrapping = TextWrapping.Wrap;
            note.Margin = new Thickness(0, 10, 0, 0);
            Children.Add(note);
            Restyle();
        }

        public void Restyle()
        {
            foreach (TextBlock text in new[] { heading, title }) text.Foreground = NotchView.Brush(Palette.Text);
            foreach (TextBlock text in new[] { artist, time, note, placeholder }) text.Foreground = NotchView.Brush(Palette.Secondary);
            cover.Background = progress.Background = NotchView.Brush(Palette.Track);
            foreach (Button button in new[] { source, previous, toggle, next })
            {
                button.Background = NotchView.Brush(Palette.Track);
                ((TextBlock)button.Content).Foreground = NotchView.Brush(Palette.Text);
            }
        }

        public void Update(MusicSnapshot snapshot)
        {
            if (IsMouseCaptureWithin) return;   // finish the press against the player the user saw
            model = snapshot ?? new MusicSnapshot();
            MusicTrack t = model.Selected;
            shownKey = t == null ? "" : t.Key;
            heading.Text = t == null ? "Music" : t.Source;
            details.Visibility = controls.Visibility = t == null ? Visibility.Collapsed : Visibility.Visible;
            title.Text = t == null ? "" : t.Title;
            artist.Text = t == null ? "" : t.Artist;
            title.ToolTip = title.Text;
            artist.ToolTip = artist.Text;
            artwork.Source = t == null ? null : t.Artwork;
            artwork.Visibility = artwork.Source == null ? Visibility.Collapsed : Visibility.Visible;
            bool duration = t != null && t.DurationSeconds > 0;
            progress.Visibility = time.Visibility = duration ? Visibility.Visible : Visibility.Collapsed;
            progress.Value = duration ? Math.Max(0, Math.Min(100, 100 * t.PositionSeconds / t.DurationSeconds)) : 0;
            progress.Foreground = NotchView.Brush(t != null && t.Source == "Spotify" ? "#1DB954" : "#FF5C67");
            time.Text = duration ? MusicSelection.Time(t.PositionSeconds) + " / " + MusicSelection.Time(t.DurationSeconds) : "";
            ((TextBlock)source.Content).Text = t == null ? "" : "Source: " + t.Source + (model.Tracks.Count > 1 ? "  \u21C4" : "");
            source.Visibility = model.Tracks.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            source.IsEnabled = !model.Busy;
            int index = t == null ? -1 : model.Tracks.IndexOf(t);
            source.ToolTip = index >= 0 && model.Tracks.Count > 1 ? "Switch to " + model.Tracks[(index + 1) % model.Tracks.Count].Source + ": " + model.Tracks[(index + 1) % model.Tracks.Count].Title : "Switch music source";
            previous.IsEnabled = t != null && t.CanPrevious && !model.Busy;
            next.IsEnabled = t != null && t.CanNext && !model.Busy;
            toggle.IsEnabled = t != null && t.CanToggle && !model.Busy;
            ((TextBlock)toggle.Content).Text = t != null && t.Playing ? "\uE769" : "\uE768";
            string action = t != null && t.Playing ? "Pause" : "Play";
            toggle.ToolTip = action;
            AutomationProperties.SetName(toggle, action);
            note.Text = model.Note;
            if (note.Text == "" && t != null && t.Source != "Spotify" && t.Source != "YouTube Music")
                note.Text = "Browser media \u00B7 YouTube Music and other tabs use the browser's name.";
            note.Visibility = note.Text == "" ? Visibility.Collapsed : Visibility.Visible;
        }

        void SelectNext()
        {
            if (model == null || model.Busy || model.Tracks.Count < 2 || SourceSelected == null) return;
            int index = model.Tracks.FindIndex(t => t.Key == shownKey);
            SourceSelected(model.Tracks[(index + 1) % model.Tracks.Count].Key);
        }

        void Send(MusicCommand command)
        {
            if (model != null && shownKey != "" && !model.Busy && Commanded != null) Commanded(shownKey, command);
        }

        static TextBlock Text(string text, double size, bool strong)
        {
            return CardView.MakeText(text, size, strong ? Palette.Text : Palette.Secondary, strong ? FontWeights.SemiBold : FontWeights.Normal);
        }

        static Button MakeButton(string text, string name, Action action)
        {
            var button = new Button { Content = Text(text, 13, false), Cursor = Cursors.Hand, Padding = new Thickness(10, 5, 10, 5), ToolTip = name };
            AutomationProperties.SetName(button, name);
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
            border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = RelativeSource.TemplatedParent });
            border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = RelativeSource.TemplatedParent });
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(content);
            var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(UIElement.OpacityProperty, 0.8));
            template.Triggers.Add(hover);
            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.35));
            template.Triggers.Add(disabled);
            button.Template = template;
            button.Click += delegate { action(); };
            return button;
        }
    }
}
