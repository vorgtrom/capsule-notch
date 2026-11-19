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
    // The track's progress: a wave up to where it has got while it plays, flat while paused, then the rest of the track as
    // a plain line. Drawn only when the position changes (once a second), so it costs nothing in between.
    internal sealed class WaveBar : FrameworkElement
    {
        double fraction;
        bool playing;
        Brush played = Brushes.White;
        public Brush Rest = Brushes.Gray;
        public double Fraction { get { return fraction; } }

        public void Set(double value, bool wavy, Brush colour)
        {
            if (value == fraction && wavy == playing && colour == played) return;
            fraction = value;
            playing = wavy;
            played = colour;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, mid = ActualHeight / 2, x = Math.Max(0, Math.Min(w, w * fraction));
            if (w <= 0) return;
            var rest = new Pen(Rest, 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawLine(rest, new Point(x, mid), new Point(w, mid));
            var line = new Pen(played, 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            if (x > 0)
            {
                var g = new StreamGeometry();
                using (StreamGeometryContext c = g.Open())
                {
                    c.BeginFigure(new Point(0, mid), false, false);
                    for (double px = 2; px <= x; px += 2) c.LineTo(new Point(px, playing ? mid - 3 * Math.Sin(px * 2 * Math.PI / 16) : mid), true, true);
                    c.LineTo(new Point(x, mid), true, true);
                }
                g.Freeze();
                dc.DrawGeometry(null, line, g);
            }
            dc.DrawEllipse(played, null, new Point(x, mid), 5, 5);
        }
    }

    // Kept in place while polling: pressing a button or moving across the card survives a track update.
    public sealed class MusicView : StackPanel
    {
        const double Cover = 92;
        readonly TextBlock heading = Text("Music", 15, true), title = Text("", 18, true), artist = Text("", 13, false);
        readonly TextBlock time = Text("", 11, false), note = Text("", 11, false);
        readonly Image artwork = new Image { Width = Cover, Height = Cover, Stretch = Stretch.UniformToFill };
        readonly TextBlock placeholder = Text("\u266B", 36, false);
        readonly Border cover = new Border { Width = Cover, Height = Cover, CornerRadius = new CornerRadius(14), Margin = new Thickness(0, 0, 14, 0) };
        readonly Grid details = new Grid();
        readonly WaveBar progress = new WaveBar { Height = 16, Margin = new Thickness(0, 14, 0, 4), IsHitTestVisible = false };
        ImageSource tintedFrom;   // the artwork Tint was worked out from: once per artwork, not on every tick

        // The artwork's colour, for the tile behind it; null without artwork.
        public Color? Tint { get; private set; }
        readonly Button source, previous, toggle, next;
        readonly StackPanel controls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        readonly ISystemVolume systemVolume;
        readonly Button speaker;
        readonly Slider volume;
        bool refreshingVolume, muted;
        MusicSnapshot model;
        string shownKey = "";
        public event Action<string, MusicCommand> Commanded;
        public event Action<string> SourceSelected;

        public MusicView() : this(null) { }

        internal MusicView(ISystemVolume systemVolume)
        {
            this.systemVolume = systemVolume;
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
            cover.Clip = new RectangleGeometry(new Rect(0, 0, Cover, Cover), 14, 14);
            details.Children.Add(cover);
            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            title.TextTrimming = artist.TextTrimming = TextTrimming.CharacterEllipsis;
            title.TextWrapping = TextWrapping.Wrap;   // a long title gets two lines beside the big cover
            title.MaxHeight = 50;
            artist.Margin = new Thickness(0, 4, 0, 0);
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
                button.Width = button == toggle ? 56 : 48;
                button.Height = button == toggle ? 44 : 40;
                ((TextBlock)button.Content).FontSize = button == toggle ? 18 : 15;
                button.Margin = new Thickness(4, 10, 4, 0);
                controls.Children.Add(button);
            }
            if (systemVolume != null)
            {
                speaker = MakeButton("", "Mute", ToggleMute);
                ((TextBlock)speaker.Content).FontFamily = new FontFamily(PanelView.IconFont);
                speaker.Width = speaker.Height = 36;
                speaker.Margin = new Thickness(4, 10, 4, 0);
                volume = new Slider
                {
                    Width = 84, Height = 36, VerticalContentAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 10, 0, 0), Minimum = 0, Maximum = 100,
                    SmallChange = 2, LargeChange = 10, TickFrequency = 1, IsSnapToTickEnabled = true,
                    IsMoveToPointEnabled = true
                };
                AutomationProperties.SetName(volume, "Volume");
                speaker.Visibility = volume.Visibility = Visibility.Collapsed;
                volume.ValueChanged += delegate
                {
                    if (refreshingVolume || volume.Visibility != Visibility.Visible) return;
                    if (systemVolume.TrySetLevel(volume.Value)) UpdateVolumeDisplay();
                    else RefreshVolume();
                };
                volume.PreviewMouseWheel += delegate(object sender, MouseWheelEventArgs e)
                {
                    volume.Value += Math.Sign(e.Delta) * volume.SmallChange;
                    e.Handled = true;
                };
                IsVisibleChanged += delegate { if (IsVisible) RefreshVolume(); };
                controls.Children.Add(speaker);
                controls.Children.Add(volume);
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
            artist.Foreground = NotchView.Brush(Palette.Text);   // over the artwork's tint, grey was too faint
            artist.Opacity = 0.75;
            cover.Background = progress.Rest = NotchView.Brush(Palette.Track);
            foreach (Button button in new[] { source, previous, toggle, next, speaker })
            {
                if (button == null) continue;
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
            details.Visibility = previous.Visibility = toggle.Visibility = next.Visibility =
                t == null ? Visibility.Collapsed : Visibility.Visible;
            title.Text = t == null ? "" : t.Title;
            artist.Text = t == null ? "" : t.Artist;
            title.ToolTip = title.Text;
            artist.ToolTip = artist.Text;
            artwork.Source = t == null ? null : t.Artwork;
            if (artwork.Source != tintedFrom)
            {
                tintedFrom = artwork.Source;
                Tint = AverageColour(artwork.Source);
            }
            artwork.Visibility = artwork.Source == null ? Visibility.Collapsed : Visibility.Visible;
            bool duration = t != null && t.DurationSeconds > 0;
            progress.Visibility = time.Visibility = duration ? Visibility.Visible : Visibility.Collapsed;
            progress.Set(duration ? Math.Max(0, Math.Min(1, t.PositionSeconds / t.DurationSeconds)) : 0, t != null && t.Playing,
                NotchView.Brush(t != null && t.Source == "Spotify" ? "#1DB954" : "#FF5C67"));
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

        // The average colour of a picture, drawn at 12 x 12; null when there is none, or it can't be drawn.
        internal static Color? AverageColour(ImageSource picture)
        {
            if (picture == null) return null;
            try
            {
                var visual = new DrawingVisual();
                using (DrawingContext dc = visual.RenderOpen()) dc.DrawImage(picture, new Rect(0, 0, 12, 12));
                var small = new System.Windows.Media.Imaging.RenderTargetBitmap(12, 12, 96, 96, PixelFormats.Pbgra32);
                small.Render(visual);
                var px = new byte[12 * 12 * 4];
                small.CopyPixels(px, 12 * 4, 0);
                long r = 0, g = 0, b = 0, n = 0;
                for (int i = 0; i < px.Length; i += 4)
                {
                    if (px[i + 3] < 128) continue;
                    b += px[i]; g += px[i + 1]; r += px[i + 2]; n++;
                }
                if (n == 0) return null;
                return Color.FromRgb((byte)(r / n), (byte)(g / n), (byte)(b / n));
            }
            catch (Exception) { return null; }
        }

        internal void RefreshVolume()
        {
            if (systemVolume == null) return;
            double level;
            bool available = systemVolume.TryRead(out level, out muted);
            speaker.Visibility = volume.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
            if (!available) return;
            if (!volume.IsMouseCaptureWithin)
            {
                refreshingVolume = true;
                volume.Value = Math.Round(level);
                refreshingVolume = false;
            }
            UpdateVolumeDisplay();
        }

        void ToggleMute()
        {
            if (systemVolume.TryToggleMute(out muted)) UpdateVolumeDisplay();
            else RefreshVolume();
        }

        void UpdateVolumeDisplay()
        {
            double level = volume.Value;
            string action = muted ? "Unmute" : "Mute";
            AutomationProperties.SetName(speaker, action);
            speaker.ToolTip = action;
            ((TextBlock)speaker.Content).Text = muted ? "\uE74F" :
                level == 0 ? "\uE992" : level <= 33 ? "\uE993" : level <= 66 ? "\uE994" : "\uE995";
            volume.ToolTip = level + "%";
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
