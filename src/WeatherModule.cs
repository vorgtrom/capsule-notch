using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Capsule
{
    public sealed class WeatherTile
    {
        public string Place, Note;
        public WeatherForecast Forecast;
        public long UpdatedAt;
        public bool Dimmed;
    }

    internal sealed class WeatherRefresh
    {
        const long Minute = 60000;
        long due, nextAllowed, rateLimitedUntil;
        bool busy;

        public WeatherForecast Forecast { get; private set; }
        public long UpdatedAt { get; private set; }
        public string Error { get; private set; }

        public bool Due(long now) { return !busy && now >= due && now >= nextAllowed && now >= rateLimitedUntil; }
        public bool Dimmed(long now) { return Forecast != null && now - UpdatedAt > 90 * Minute; }
        public void Request() { due = 0; }

        public void PanelOpened(long now)
        {
            if (!busy && (Forecast == null || now - UpdatedAt > 15 * Minute)) Request();
        }

        public void Start(long now)
        {
            busy = true;
            nextAllowed = now + Minute;
            due = now + 30 * Minute;
        }

        public void Finish(long now, WeatherForecast forecast, string error, long retryAfterSeconds)
        {
            busy = false;
            if (retryAfterSeconds > 0)
                rateLimitedUntil = now + Math.Min(Math.Max(1800, retryAfterSeconds), (long.MaxValue - now) / 1000) * 1000;
            if (error == null) return;   // the place or units changed while this request was running
            Error = error;
            if (forecast == null) return;
            Forecast = forecast;
            UpdatedAt = now;
        }

        public void ClearForecast()
        {
            Forecast = null;
            UpdatedAt = 0;
            Error = "";
        }
    }

    // The controller owns this module on the UI thread. Only the HTTP work and parsing run on a worker.
    internal sealed class WeatherModule : IModule, IDisposable
    {
        readonly TaskScheduler ui;
        readonly WeatherRefresh refresh = new WeatherRefresh();
        WeatherPlace place;
        string units;
        CancellationTokenSource forecastRequest, searchRequest;
        bool disposed;

        public event Action Changed;

        public WeatherModule(WeatherPlace place, string units, TaskScheduler ui)
        {
            this.place = place;
            this.units = units;
            this.ui = ui;
        }

        public void Configure(WeatherPlace nextPlace, string nextUnits)
        {
            if (place == nextPlace && units == nextUnits) return;
            if (place != nextPlace) refresh.ClearForecast();
            place = nextPlace;
            units = nextUnits;
            if (forecastRequest != null) forecastRequest.Cancel();
            if (place == null) CancelSearch();
            Refresh();
            NotifyChanged();
        }

        public void Refresh()
        {
            refresh.Request();
            Tick(Clock.NowMs());
        }

        public void NetworkBack() { Refresh(); }
        public void Resumed() { Refresh(); }

        public void PanelOpened(long now)
        {
            refresh.PanelOpened(now);
            Tick(now);
        }

        public void Tick(long now)
        {
            if (disposed || place == null || !refresh.Due(now)) return;
            string unit = WeatherApi.TemperatureUnit(units, RegionInfo.CurrentRegion);
            string url = WeatherApi.ForecastUrl(place, unit);
            refresh.Start(now);
            var request = new CancellationTokenSource();
            forecastRequest = request;
            Task.Run(delegate
            {
                HttpResult reply = WeatherApi.Get(url);
                WeatherForecast forecast = reply.Status == 200 && !reply.Truncated ? WeatherApi.Forecast(reply.Body, unit) : null;
                string error = forecast != null ? "" : reply.Status == 200 ? WeatherApi.Unreadable : WeatherApi.UpdateFailed;
                return Tuple.Create(forecast, error, reply.Status == 429 ? Math.Max(1, reply.RetryAfterSeconds) : 0);
            }, request.Token).ContinueWith(task =>
            {
                bool current = !request.IsCancellationRequested && !disposed;
                forecastRequest = null;
                request.Dispose();
                if (task.IsFaulted) Log.Error("weather", task.Exception.GetBaseException());
                var result = task.Status == TaskStatus.RanToCompletion ? task.Result : null;
                refresh.Finish(Clock.NowMs(), current && result != null ? result.Item1 : null,
                    current ? (result != null ? result.Item2 : WeatherApi.UpdateFailed) : null, result != null ? result.Item3 : 0);
                if (current) NotifyChanged();
            }, ui);
        }

        public WeatherTile Tile(long now)
        {
            return place == null ? null : new WeatherTile
            {
                Place = place.Label,
                Note = refresh.Error,
                Forecast = refresh.Forecast,
                UpdatedAt = refresh.UpdatedAt,
                Dimmed = refresh.Dimmed(now),
            };
        }

        public void Search(string text, CultureInfo language, Action<List<WeatherPlace>, string> show)
        {
            CancelSearch();
            string url = WeatherApi.SearchUrl(text, language);
            var request = new CancellationTokenSource();
            searchRequest = request;
            Task.Run(delegate
            {
                HttpResult reply = WeatherApi.Get(url);
                if (reply.Status != 200) return Tuple.Create<List<WeatherPlace>, string>(null, "Couldn't find places");
                List<WeatherPlace> places = reply.Truncated ? null : WeatherApi.Places(reply.Body);
                return Tuple.Create(places, places == null ? WeatherApi.Unreadable : places.Count == 0 ? "No places found" : "");
            }, request.Token).ContinueWith(task =>
            {
                bool current = !request.IsCancellationRequested && !disposed;
                if (searchRequest == request) searchRequest = null;
                request.Dispose();
                if (task.IsFaulted) Log.Error("weather search", task.Exception.GetBaseException());
                if (!current) return;
                if (task.Status == TaskStatus.RanToCompletion) show(task.Result.Item1, task.Result.Item2);
                else show(null, "Couldn't find places");
            }, ui);
        }

        public void CancelSearch()
        {
            if (searchRequest != null) searchRequest.Cancel();
        }

        public void Dispose()
        {
            disposed = true;
            if (forecastRequest != null) forecastRequest.Cancel();
            CancelSearch();
        }

        void NotifyChanged() { if (Changed != null) Changed(); }
    }
}
