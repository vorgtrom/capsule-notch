using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Capsule
{
    public sealed class WeatherPlace
    {
        public readonly string Label;
        public readonly double Latitude, Longitude;

        WeatherPlace(string label, double latitude, double longitude)
        {
            Label = label;
            Latitude = Math.Round(latitude, 2);
            Longitude = Math.Round(longitude, 2);
        }

        internal static WeatherPlace From(string label, object latitude, object longitude)
        {
            double? lat = Json.Num(latitude), lon = Json.Num(longitude);
            return !string.IsNullOrWhiteSpace(label) && lat >= -90 && lat <= 90 && lon >= -180 && lon <= 180
                ? new WeatherPlace(label, lat.Value, lon.Value) : null;
        }
    }

    public sealed class WeatherForecast
    {
        public double Temperature, FeelsLike;
        public int Code;
        public bool Daylight;
        public string Unit;
        public readonly List<WeatherHour> Hours = new List<WeatherHour>();
        public readonly List<WeatherDay> Days = new List<WeatherDay>();
    }

    public sealed class WeatherHour
    {
        public DateTime Time;
        public double Temperature;
        public int Code, RainChance;
    }

    public sealed class WeatherDay
    {
        public DateTime Date;
        public double High, Low;
        public int Code;
    }

    internal static class WeatherApi
    {
        public const string UpdateFailed = "Couldn't update the weather";
        public const string Unreadable = "The weather service sent something Capsule couldn't read";
        public const string AttributionUrl = "https://open-meteo.com/";
        static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
        static readonly Dictionary<string, string> NoHeaders = new Dictionary<string, string>();

        public static HttpResult Get(string url) { return Http.Get(url, NoHeaders, 10000, 64 * 1024); }

        public static string SearchUrl(string text, CultureInfo language)
        {
            string code = language.TwoLetterISOLanguageName;
            return "https://geocoding-api.open-meteo.com/v1/search?name=" + Uri.EscapeDataString(text) +
                "&count=5&language=" + (code.Length == 2 ? code : "en") + "&format=json";
        }

        public static string ForecastUrl(WeatherPlace place, string unit)
        {
            return "https://api.open-meteo.com/v1/forecast?latitude=" + place.Latitude.ToString("0.00", Invariant) +
                "&longitude=" + place.Longitude.ToString("0.00", Invariant) +
                "&current=temperature_2m,apparent_temperature,weather_code,is_day" +
                "&hourly=temperature_2m,weather_code,precipitation_probability" +
                "&daily=weather_code,temperature_2m_max,temperature_2m_min&forecast_days=5&timezone=auto&temperature_unit=" + unit;
        }

        public static string NormalizeUnits(string unit) { return unit == "celsius" || unit == "fahrenheit" ? unit : "automatic"; }

        public static string TemperatureUnit(string setting, RegionInfo region)
        {
            setting = NormalizeUnits(setting);
            return setting == "automatic" ? (region.IsMetric ? "celsius" : "fahrenheit") : setting;
        }

        public static List<WeatherPlace> Places(string body)
        {
            var root = Json.Obj(Json.TryParse(body));
            if (root == null || root.ContainsKey("error")) return null;
            var places = new List<WeatherPlace>();
            object value;
            if (!root.TryGetValue("results", out value)) return places;
            object[] results = Json.Arr(value);
            if (results == null) return null;
            foreach (object item in results.Take(5))
            {
                string name = Json.Str(Json.Get(item, "name"));
                object region = Json.Get(item, "admin1"), country = Json.Get(item, "country");
                if (string.IsNullOrWhiteSpace(name) || (region != null && !(region is string)) ||
                    (country != null && !(country is string))) return null;
                string label = string.Join(", ", new[] { name, Json.Str(region), Json.Str(country) }.Where(s => !string.IsNullOrWhiteSpace(s)));
                WeatherPlace place = WeatherPlace.From(label, Json.Get(item, "latitude"), Json.Get(item, "longitude"));
                if (place == null) return null;
                places.Add(place);
            }
            return places;
        }

        public static WeatherForecast Forecast(string body, string unit)
        {
            object root = Json.TryParse(body), current = Json.Get(root, "current");
            try
            {
                DateTime now = Time(Json.Get(current, "time"), "yyyy-MM-dd'T'HH:mm");
                var forecast = new WeatherForecast
                {
                    Unit = unit,
                    Temperature = Number(Json.Get(current, "temperature_2m")),
                    FeelsLike = Number(Json.Get(current, "apparent_temperature")),
                    Code = Integer(Json.Get(current, "weather_code"), 0, int.MaxValue),
                    Daylight = Integer(Json.Get(current, "is_day"), 0, 1) == 1,
                };
                object hourly = Json.Get(root, "hourly");
                object[] hours = Values(hourly, "time", null);
                object[] hourTemps = Values(hourly, "temperature_2m", hours.Length);
                object[] hourCodes = Values(hourly, "weather_code", hours.Length);
                object[] chances = Values(hourly, "precipitation_probability", hours.Length);
                DateTime lastHour = DateTime.MinValue;
                for (int i = 0; i < hours.Length; i++)
                {
                    DateTime time = Time(hours[i], "yyyy-MM-dd'T'HH:mm");
                    if (i > 0 && time - lastHour != TimeSpan.FromHours(1)) return null;
                    lastHour = time;
                    double temp = Number(hourTemps[i]);
                    int code = Integer(hourCodes[i], 0, int.MaxValue), chance = Integer(chances[i], 0, 100);
                    if (time > now && forecast.Hours.Count < 6)
                        forecast.Hours.Add(new WeatherHour { Time = time, Temperature = temp, Code = code, RainChance = chance });
                }
                if (forecast.Hours.Count != 6 || forecast.Hours[0].Time - now > TimeSpan.FromHours(1)) return null;

                object daily = Json.Get(root, "daily");
                object[] days = Values(daily, "time", 5), highs = Values(daily, "temperature_2m_max", 5);
                object[] lows = Values(daily, "temperature_2m_min", 5), dayCodes = Values(daily, "weather_code", 5);
                for (int i = 0; i < days.Length; i++)
                {
                    DateTime day = Time(days[i], "yyyy-MM-dd");
                    double high = Number(highs[i]), low = Number(lows[i]);
                    if ((day - now.Date).TotalDays != i || high < low) return null;
                    forecast.Days.Add(new WeatherDay { Date = day, High = high, Low = low, Code = Integer(dayCodes[i], 0, int.MaxValue) });
                }
                return forecast;
            }
            catch (FormatException) { return null; }
        }

        static object[] Values(object root, string name, int? count)
        {
            object[] values = Json.Arr(Json.Get(root, name));
            if (values == null || (count.HasValue && values.Length != count.Value)) throw new FormatException();
            return values;
        }

        static DateTime Time(object value, string format)
        {
            DateTime time;
            if (!DateTime.TryParseExact(Json.Str(value), format, Invariant, DateTimeStyles.None, out time)) throw new FormatException();
            return time;
        }

        static double Number(object value)
        {
            double? number = Json.Num(value);
            if (!number.HasValue || double.IsNaN(number.Value) || double.IsInfinity(number.Value)) throw new FormatException();
            return number.Value;
        }

        static int Integer(object value, int min, int max)
        {
            double number = Number(value);
            if (number < min || number > max || number != Math.Truncate(number)) throw new FormatException();
            return (int)number;
        }
    }

    internal sealed class WeatherCondition
    {
        public readonly string Label, Glyph;

        WeatherCondition(string label, string glyph)
        {
            Label = label;
            Glyph = glyph;
        }

        // Compose weather symbols from glyphs shared by both Segoe icon fonts.
        const string Sun = "\uE706", Moon = "\uE708", Cloud = "\uE753", Drop = "\uEB42", Ice = "\uEA38";
        const string Fog = "\uE8E4", Lightning = "\uE945", Neutral = "\uE9CE";

        public static WeatherCondition For(int code, bool day)
        {
            switch (code)
            {
                case 0: return new WeatherCondition("Clear", day ? Sun : Moon);
                case 1: return new WeatherCondition("Mainly clear", (day ? Sun : Moon) + Cloud);
                case 2: return new WeatherCondition("Partly cloudy", Cloud + Sun);
                case 3: return new WeatherCondition("Overcast", Cloud);
                case 45: case 48: return new WeatherCondition("Fog", Cloud + Fog);
                case 51: case 53: case 55: return new WeatherCondition("Drizzle", Drop);
                case 56: case 57: return new WeatherCondition("Freezing drizzle", Drop + Ice);
                case 61: case 63: case 65: return new WeatherCondition("Rain", Cloud + Drop);
                case 66: case 67: return new WeatherCondition("Freezing rain", Drop + Ice);
                case 71: case 73: case 75: return new WeatherCondition("Snow", Cloud + Ice);
                case 77: return new WeatherCondition("Snow grains", Ice);
                case 80: case 81: case 82: return new WeatherCondition("Rain showers", Cloud + Drop);
                case 85: case 86: return new WeatherCondition("Snow showers", Cloud + Ice);
                case 95: case 97: return new WeatherCondition("Thunderstorm", Cloud + Lightning);
                case 96: case 99: return new WeatherCondition("Thunderstorm with hail", Lightning + Ice);
                default: return new WeatherCondition("Weather", Neutral);
            }
        }
    }
}
