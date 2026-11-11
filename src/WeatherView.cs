using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace Capsule
{
    public sealed partial class PanelView
    {
        readonly Border weatherTile = new Border();
        WeatherTile shownWeather;
        readonly TextBox weatherPlace = new TextBox { MaxLength = 160 };
        readonly Button weatherFind = new Button { Content = "Find" }, weatherOff = new Button { Content = "Turn off" };
        readonly ComboBox weatherUnits = new ComboBox();
        readonly StackPanel weatherPlaces = new StackPanel();
        TextBlock weatherStatus;

        internal event Action<string> WeatherSearch, WeatherUnitsChosen;
        internal event Action<WeatherPlace> WeatherPlaceChosen;
        internal event Action WeatherAttributionClicked, WeatherSearchCancelled;

        UIElement BuildWeatherSettings()
        {
            var section = new StackPanel { Margin = new Thickness(Pad + 4, 0, Pad + 4, 8) };
            section.Children.Add(Section("Weather"));
            section.Children.Add(Label("Place"));
            Plain(weatherPlace);
            AutomationProperties.SetName(weatherPlace, "Place");
            var placeField = new Border();
            Field(placeField, weatherPlace);
            section.Children.Add(placeField);
            weatherFind.Click += delegate { FindWeatherPlaces(); };
            weatherPlace.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                if (weatherFind.IsEnabled) FindWeatherPlaces();
            };
            weatherOff.Click += delegate { if (WeatherPlaceChosen != null) WeatherPlaceChosen(null); };
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            actions.Children.Add(weatherFind);
            actions.Children.Add(weatherOff);
            section.Children.Add(actions);
            weatherStatus = Hint("");
            weatherPlace.TextChanged += delegate { ClearWeatherSearch(); };
            section.Children.Add(weatherStatus);
            section.Children.Add(weatherPlaces);

            TextBlock units = Label("Units");
            units.Margin = new Thickness(0, 6, 0, 3);
            section.Children.Add(units);
            AutomationProperties.SetName(weatherUnits, "Weather units");
            weatherUnits.Items.Add(new ComboBoxItem { Content = "Automatic", Tag = "automatic" });
            weatherUnits.Items.Add(new ComboBoxItem { Content = "°C", Tag = "celsius" });
            weatherUnits.Items.Add(new ComboBoxItem { Content = "°F", Tag = "fahrenheit" });
            section.Children.Add(weatherUnits);
            ShowWeatherSettings(null, "automatic", true);
            return section;
        }

        internal void ShowWeatherSettings(WeatherPlace place, string units, bool saved)
        {
            ClearWeatherSearch();
            weatherPlace.Text = place != null ? place.Label : "";
            weatherOff.IsEnabled = place != null;
            weatherUnits.SelectionChanged -= OnWeatherUnitsChanged;
            foreach (ComboBoxItem choice in weatherUnits.Items)
                if ((string)choice.Tag == units) { weatherUnits.SelectedItem = choice; break; }
            weatherUnits.SelectionChanged += OnWeatherUnitsChanged;
            ShowWeatherPlaces(null, saved ? (place != null ? "" : "Off") : "Couldn't save the weather settings");
        }

        void ClearWeatherSearch()
        {
            if (WeatherSearchCancelled != null) WeatherSearchCancelled();
            ShowWeatherPlaces(null, "");
        }

        void OnWeatherUnitsChanged(object sender, SelectionChangedEventArgs e)
        {
            var choice = weatherUnits.SelectedItem as ComboBoxItem;
            if (choice != null && WeatherUnitsChosen != null) WeatherUnitsChosen((string)choice.Tag);
        }

        void FindWeatherPlaces()
        {
            string text = weatherPlace.Text.Trim();
            if (text.Length < 2)
            {
                ShowWeatherPlaces(null, "Enter at least two characters");
                return;
            }
            if (WeatherSearch == null) return;
            ShowWeatherPlaces(null, "Finding places…");
            weatherFind.IsEnabled = false;
            WeatherSearch(text);
        }

        internal void ShowWeatherPlaces(List<WeatherPlace> places, string message)
        {
            weatherStatus.Text = message;
            weatherFind.IsEnabled = true;
            weatherPlaces.Children.Clear();
            if (places != null)
                foreach (WeatherPlace place in places)
                {
                    var match = new Button
                    {
                        Content = new TextBlock { Text = place.Label, TextWrapping = TextWrapping.Wrap },
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Left,
                    };
                    AutomationProperties.SetName(match, place.Label);
                    StyleAction(match);
                    match.Click += delegate { if (WeatherPlaceChosen != null) WeatherPlaceChosen(place); };
                    weatherPlaces.Children.Add(match);
                }
            RaiseResized();
        }

        void RestyleWeather()
        {
            ((Border)weatherPlace.Parent).Background = NotchView.Brush(Palette.Track);
            weatherPlace.Foreground = NotchView.Brush(Palette.Text);
            weatherPlace.CaretBrush = weatherPlace.Foreground;
            StyleAction(weatherFind);
            StyleAction(weatherOff);
            foreach (Button match in weatherPlaces.Children) StyleAction(match);
            if (shownWeather != null) weatherTile.Child = WeatherContent(shownWeather);
        }

        void UpdateWeather(WeatherTile model)
        {
            if (model == null && shownWeather == null) return;
            if (model != null && shownWeather != null && model.Forecast == shownWeather.Forecast &&
                model.Place == shownWeather.Place && model.Note == shownWeather.Note &&
                model.UpdatedAt == shownWeather.UpdatedAt && model.Dimmed == shownWeather.Dimmed) return;
            shownWeather = model;
            Show(weatherTile, model != null);
            weatherTile.Child = model != null ? WeatherContent(model) : null;
        }

        UIElement WeatherContent(WeatherTile model)
        {
            CultureInfo culture = CultureInfo.CurrentCulture;
            var box = new StackPanel();
            var header = new DockPanel();
            TextBlock title = TileHeader("Weather");
            title.Margin = new Thickness(0, 0, 8, 4);
            header.Children.Add(title);
            TextBlock place = CardView.MakeText(model.Place, 11.5, Palette.Secondary, FontWeights.Normal);
            place.TextAlignment = TextAlignment.Right;
            place.TextTrimming = TextTrimming.CharacterEllipsis;
            place.ToolTip = model.Place;
            header.Children.Add(place);
            box.Children.Add(header);

            WeatherForecast forecast = model.Forecast;
            if (forecast != null)
            {
                string unit = forecast.Unit == "fahrenheit" ? "F" : "C";
                var data = new StackPanel { Opacity = model.Dimmed ? 0.45 : 1 };
                var now = new StackPanel { Orientation = Orientation.Horizontal };
                TextBlock temperature = CardView.MakeText(WeatherTemperature(forecast.Temperature, culture) + unit, 29, Palette.Text, FontWeights.SemiBold);
                temperature.Margin = new Thickness(0, 0, 8, 0);
                now.Children.Add(temperature);
                WeatherCondition condition = WeatherCondition.For(forecast.Code, forecast.Daylight);
                now.Children.Add(WeatherIcon(condition, 17));
                TextBlock description = CardView.MakeText(condition.Label, 12, Palette.Text, FontWeights.Normal);
                description.Margin = new Thickness(5, 0, 0, 0);
                description.VerticalAlignment = VerticalAlignment.Center;
                now.Children.Add(description);
                data.Children.Add(now);
                WeatherDay today = forecast.Days[0];
                data.Children.Add(CardView.MakeText("Feels like " + WeatherTemperature(forecast.FeelsLike, culture) + "  ·  " +
                    "High " + WeatherTemperature(today.High, culture) + "  ·  Low " + WeatherTemperature(today.Low, culture),
                    11, Palette.Secondary, FontWeights.Normal));

                var hours = new UniformGrid { Rows = 1, Columns = 6 };
                foreach (WeatherHour hour in forecast.Hours)
                {
                    string rain = hour.RainChance >= 20 ? hour.RainChance.ToString(culture) + "%" : "";
                    hours.Children.Add(WeatherColumn(hour.Time.ToString("t", culture), WeatherCondition.For(hour.Code, hour.Daylight),
                        WeatherTemperature(hour.Temperature, culture), rain));
                }
                data.Children.Add(WeatherHeading("Next 6 hours"));
                data.Children.Add(hours);

                var days = new UniformGrid { Rows = 1, Columns = 4 };
                for (int i = 1; i < forecast.Days.Count; i++)
                {
                    WeatherDay day = forecast.Days[i];
                    days.Children.Add(WeatherColumn(day.Date.ToString("ddd", culture), WeatherCondition.For(day.Code, true),
                        WeatherTemperature(day.High, culture) + " / " + WeatherTemperature(day.Low, culture), ""));
                }
                data.Children.Add(WeatherHeading("Next 4 days"));
                data.Children.Add(days);
                box.Children.Add(data);
            }

            string note = model.Note;
            if (!string.IsNullOrEmpty(note) && model.UpdatedAt > 0)
            {
                DateTime updated = CalendarDay.Local(model.UpdatedAt, TimeZoneInfo.Local);
                note += " · Last updated " + updated.ToString("g", culture);
            }
            if (string.IsNullOrEmpty(note) && forecast == null) note = "Checking weather…";
            if (!string.IsNullOrEmpty(note))
            {
                TextBlock status = CardView.Wrap(CardView.MakeText(note, 11, Palette.Secondary, FontWeights.Normal));
                status.Margin = new Thickness(0, 5, 0, 0);
                box.Children.Add(status);
            }

            var attribution = CardView.MakeText("", 10, Palette.Secondary, FontWeights.Normal);
            attribution.Margin = new Thickness(0, 6, 0, 0);
            var link = new Hyperlink(new Run("Weather data by Open-Meteo.com"))
            {
                Foreground = attribution.Foreground,
                NavigateUri = new Uri(WeatherApi.AttributionUrl),
            };
            link.RequestNavigate += delegate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
            {
                e.Handled = true;
                if (WeatherAttributionClicked != null) WeatherAttributionClicked();
            };
            attribution.Inlines.Add(link);
            box.Children.Add(attribution);
            return box;
        }

        static TextBlock WeatherHeading(string text)
        {
            TextBlock heading = CardView.MakeText(text, 11, Palette.Secondary, FontWeights.SemiBold);
            heading.Margin = new Thickness(0, 8, 0, 3);
            return heading;
        }

        static UIElement WeatherColumn(string label, WeatherCondition condition, string temperature, string rain)
        {
            var cell = new StackPanel();
            TextBlock time = CardView.MakeText(label, 10, Palette.Secondary, FontWeights.Normal);
            time.TextAlignment = TextAlignment.Center;
            time.TextWrapping = TextWrapping.Wrap;
            cell.Children.Add(time);
            TextBlock icon = WeatherIcon(condition, 12);
            icon.Margin = new Thickness(0, 2, 0, 2);
            cell.Children.Add(icon);
            TextBlock degrees = CardView.MakeText(temperature, 11, Palette.Text, FontWeights.Normal);
            degrees.TextAlignment = TextAlignment.Center;
            cell.Children.Add(degrees);
            if (rain != "")
            {
                TextBlock chance = CardView.MakeText(rain, 10, Palette.Secondary, FontWeights.Normal);
                chance.TextAlignment = TextAlignment.Center;
                chance.ToolTip = "Chance of rain";
                AutomationProperties.SetName(chance, "Chance of rain " + rain);
                cell.Children.Add(chance);
            }
            return cell;
        }

        static TextBlock WeatherIcon(WeatherCondition condition, double size)
        {
            TextBlock icon = CardView.MakeText(condition.Glyph, size, Palette.Secondary, FontWeights.Normal);
            icon.FontFamily = new FontFamily(IconFont);
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            icon.VerticalAlignment = VerticalAlignment.Center;
            icon.ToolTip = condition.Label;
            AutomationProperties.SetName(icon, condition.Label);
            return icon;
        }

        static string WeatherTemperature(double temperature, CultureInfo culture)
        {
            return temperature.ToString("0", culture) + "°";
        }
    }
}
