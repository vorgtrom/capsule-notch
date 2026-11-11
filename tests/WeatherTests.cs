using System;
using System.Globalization;
using System.IO;

namespace Capsule
{
    static class WeatherTests
    {
        public static void Run()
        {
            string places = TestRunner.Fixture("weather-places.json");
            var results = WeatherApi.Places(places);
            TestRunner.Eq(2, results.Count, "weather keeps both places");
            TestRunner.Eq("Zürich, Zurich, Switzerland", results[0].Label, "weather place label");
            TestRunner.Near(47.37, results[0].Latitude, "weather rounds latitude");
            TestRunner.Eq("Zurich, United States", results[1].Label, "weather omits an absent region");
            TestRunner.Eq(0, WeatherApi.Places("{}").Count, "weather accepts no places");
            TestRunner.Check(WeatherApi.Places(places.Replace("47.36667", "\"47.36667\"")) == null, "weather rejects wrong coordinate type");
            TestRunner.Eq("https://geocoding-api.open-meteo.com/v1/search?name=Z%C3%BCrich&count=5&language=de&format=json",
                WeatherApi.SearchUrl("Zürich", CultureInfo.GetCultureInfo("de-CH")), "weather encodes an accented query and UI language");
            string url = WeatherApi.ForecastUrl(results[0], "fahrenheit");
            TestRunner.Check(url.StartsWith("https://api.open-meteo.com/v1/forecast?latitude=47.37&longitude=8.55&", StringComparison.Ordinal), "weather sends rounded invariant coordinates");
            TestRunner.Check(url.EndsWith("forecast_days=5&timezone=auto&temperature_unit=fahrenheit", StringComparison.Ordinal), "weather sends requested forecast days and unit");
            TestRunner.Check(!url.Contains("Zurich"), "weather forecast omits the label");
            TestRunner.Eq("fahrenheit", WeatherApi.TemperatureUnit("automatic", new RegionInfo("US")), "weather Automatic in US");
            TestRunner.Eq("celsius", WeatherApi.TemperatureUnit("automatic", new RegionInfo("MY")), "weather Automatic in Malaysia");
            TestRunner.Eq("fahrenheit", WeatherApi.TemperatureUnit("fahrenheit", new RegionInfo("MY")), "weather manual unit wins");

            string json = TestRunner.Fixture("weather-forecast.json");
            WeatherForecast forecast = WeatherApi.Forecast(json, "celsius");
            TestRunner.Check(forecast != null, "weather parses the saved forecast");
            if (forecast == null) return;
            TestRunner.Near(4.2, forecast.Temperature, "weather reads current temperature");
            TestRunner.Near(1.4, forecast.FeelsLike, "weather reads feels-like temperature");
            TestRunner.Check(!forecast.Daylight, "weather is day flag");
            TestRunner.Eq(6, forecast.Hours.Count, "weather takes six future hours");
            TestRunner.Eq(new DateTime(2026, 11, 11, 23, 0, 0), forecast.Hours[0].Time, "weather skips hours before current weather");
            TestRunner.Eq(new DateTime(2026, 11, 12), forecast.Hours[1].Time, "weather uses place-local midnight");
            TestRunner.Eq(19, forecast.Hours[1].RainChance, "weather reads chance below threshold");
            TestRunner.Eq(20, forecast.Hours[2].RainChance, "weather reads chance at threshold");
            TestRunner.Eq(5, forecast.Days.Count, "weather reads today and four later days");
            TestRunner.Near(9.1, forecast.Days[0].High, "weather reads today's high");
            TestRunner.Check(WeatherApi.Forecast(json.Replace("apparent_temperature", "unexpected"), "celsius") == null, "weather rejects missing current field");
            TestRunner.Check(WeatherApi.Forecast(json.Replace("4.2", "\"4.2\""), "celsius") == null, "weather rejects string temperature");
            TestRunner.Check(WeatherApi.Forecast(json.Replace("\"weather_code\"", "\"unexpected\""), "celsius") == null, "weather rejects missing code array");
            TestRunner.Check(WeatherApi.Forecast(json.Replace("\"2026-11-12T01:00\"", "\"invalid\""), "celsius") == null, "weather rejects invalid forecast times");
            TestRunner.Check(WeatherApi.Forecast(json.Replace("\"2026-11-12T01:00\",", ""), "celsius") == null, "weather rejects arrays with mismatched lengths");

            TestRunner.Eq("Partly cloudy", WeatherCondition.For(2, true).Label, "weather partly cloudy");
            TestRunner.Eq("Thunderstorm", WeatherCondition.For(97, true).Label, "weather heavy thunderstorm");
            TestRunner.Eq("Thunderstorm with hail", WeatherCondition.For(99, true).Label, "weather hail");
            TestRunner.Eq("Weather", WeatherCondition.For(123, true).Label, "weather unknown code");
            TestRunner.Check(WeatherCondition.For(0, true).Glyph != WeatherCondition.For(0, false).Glyph, "weather clear changes glyph at night");
            TestRunner.Check(WeatherCondition.For(1, true).Glyph != WeatherCondition.For(1, false).Glyph, "weather mainly clear changes glyph at night");

            var state = new WeatherRefresh();
            const long minute = 60000;
            long now = 100 * minute;
            TestRunner.Check(state.Due(now), "weather is due on first selection");
            state.Start(now);
            state.Finish(now, forecast, "", 0);
            TestRunner.Check(!state.Due(now + 29 * minute) && state.Due(now + 30 * minute), "weather refreshes every 30 minutes");
            state.PanelOpened(now + 15 * minute);
            TestRunner.Check(!state.Due(now + 15 * minute), "weather panel opening keeps a 15 minute old forecast");
            state.PanelOpened(now + 16 * minute);
            TestRunner.Check(state.Due(now + 16 * minute), "weather panel opening refreshes a forecast older than 15 minutes");
            state.Start(now + 16 * minute);
            state.Finish(now + 16 * minute, null, WeatherApi.Unreadable, 0);
            state.Request();
            TestRunner.Check(!state.Due(now + 16 * minute) && state.Due(now + 17 * minute), "weather allows at most one request per minute");
            TestRunner.Eq(forecast, state.Forecast, "weather keeps the last forecast when parsing fails");
            TestRunner.Eq(WeatherApi.Unreadable, state.Error, "weather exposes parsing error");
            TestRunner.Check(!state.Dimmed(now + 90 * minute) && state.Dimmed(now + 91 * minute), "weather dims data after 90 minutes");
            state.Start(now + 17 * minute);
            state.Finish(now + 17 * minute, null, WeatherApi.UpdateFailed, 1);
            state.Request();
            TestRunner.Check(!state.Due(now + 46 * minute) && state.Due(now + 47 * minute), "weather uses a minimum 30-minute 429 backoff");
            state.Start(now + 47 * minute);
            state.Finish(now + 47 * minute, null, WeatherApi.UpdateFailed, 2400);
            state.ClearForecast();
            state.Request();
            TestRunner.Check(!state.Due(now + 86 * minute) && state.Due(now + 87 * minute), "weather respects a longer Retry-After across place changes");
            TestRunner.Check(state.Forecast == null, "weather clears old data when the place changes");

            string dir = TestRunner.NewTempDir();
            try
            {
                string path = Path.Combine(dir, "config.json");
                TestRunner.Check(Config.Load(path).WeatherPlace == null, "weather is off in default config");
                var config = new Config { WeatherPlace = results[0], WeatherUnits = "fahrenheit", PanelHome = "weather" };
                TestRunner.Check(config.Save(path), "weather config saves");
                Config loaded = Config.Load(path);
                TestRunner.Eq(results[0].Label, loaded.WeatherPlace.Label, "weather config restores label");
                TestRunner.Near(47.37, loaded.WeatherPlace.Latitude, "weather config restores rounded coordinate");
                TestRunner.Eq("fahrenheit", loaded.WeatherUnits, "weather config restores units");
                TestRunner.Eq("weather", loaded.PanelHome, "weather can be the panel home");
                config.WeatherPlace = null;
                config.Save(path);
                TestRunner.Check(Json.Get(Json.TryParse(File.ReadAllText(path)), "weather_lat") == null && Config.Load(path).WeatherPlace == null,
                    "weather turn off removes coordinates");
            }
            finally { Directory.Delete(dir, true); }
        }
    }
}
