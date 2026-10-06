using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Capsule
{
    // A part of Capsule with background work of its own (spec §3). The Controller ticks every module every 5 s and
    // passes on the user's refresh, the network coming back and the PC waking up. Each module also builds what its
    // faces show: a cell on the capsule, a quick-look card, a tile on the panel.
    public interface IModule
    {
        void Tick(long now);     // start background work that is due
        void Refresh();          // the user asked for fresh data
        void NetworkBack();      // Windows reports the network is available again
        void Resumed();          // the PC woke from sleep
    }

    // What a usage module checks: ClaudeUsage or CodexUsage.
    public interface IUsageSource
    {
        Reading Current { get; }
        Reading Poll(long now);
        bool LastCheckUnreachable { get; }
    }

    // Claude's or Codex's usage: checked in the background on a schedule, soon again after a check that got no
    // reply at all.
    public sealed class UsageModule : IModule
    {
        // After a check that got no reply at all (the network not back yet after waking, say): retry every 20 s,
        // up to 6 times, before falling back to the usual interval.
        public const long RetrySoonMs = 20 * 1000;
        public const int RetriesSoon = 6;

        readonly IUsageSource source;
        readonly Func<long> usualInterval;
        readonly TaskScheduler ui;   // the UI thread's: a check's result is handed back there
        long due;   // when the next check is due; long.MaxValue while one runs
        bool busy;
        int retries;

        public event Action Changed;   // a check finished; raised on the UI thread

        // Built on the UI thread, whose scheduler it takes now: asked for later, from whatever thread starts a check, it
        // would throw after the module had marked itself busy, and leave it busy for good.
        public UsageModule(IUsageSource source, Func<long> usualInterval)
        {
            this.source = source;
            this.usualInterval = usualInterval;
            ui = TaskScheduler.FromCurrentSynchronizationContext();
        }

        public Reading Current { get { return source.Current; } }
        public string Provider { get { return source.Current.Provider; } }

        public void Tick(long now) { if (!busy && now >= due) Poll(); }

        // While a check runs, due is long.MaxValue. A refresh asked for meanwhile sets it to 0, and that is kept,
        // so the next tick checks again.
        public void Refresh()
        {
            due = 0;
            if (!busy) Poll();
        }

        // The last check failed for want of a reply: check now rather than waiting for the retry.
        public void NetworkBack() { if (source.LastCheckUnreachable) Refresh(); }

        public void Resumed() { due = 0; }

        // Check no later than this.
        public void CheckBy(long when) { due = Math.Min(due, when); }

        void Poll()
        {
            busy = true;
            due = long.MaxValue;
            Task.Run(() => source.Poll(Clock.NowMs())).ContinueWith(t =>
            {
                busy = false;
                if (t.IsFaulted) Log.Error(Provider + " check", t.Exception.GetBaseException());
                if (due != 0) due = Clock.NowMs() + RetryDelay(source.LastCheckUnreachable, ref retries, usualInterval());
                if (Changed != null) Changed();
            }, ui);
        }

        // The wait before the next check: 20 s after a check that got no reply at all, up to RetriesSoon times in a
        // row, otherwise the usual interval. Any reply resets the count.
        public static long RetryDelay(bool unreachable, ref int retries, long usual)
        {
            if (!unreachable)
            {
                retries = 0;
                return usual;
            }
            if (retries >= RetriesSoon) return usual;
            retries++;
            return RetrySoonMs;
        }

        public CellModel Cell(string activity, long now) { return NotchView.CellFor(Current, activity, now); }

        public CardModel Card(SessionsModule sessions, long now, CultureInfo culture)
        {
            return CardModel.From(Current, sessions.All, sessions.HooksConnected, now, culture);
        }

        public UsageTile Tile(long now, CultureInfo culture) { return UsageTile.From(Current, now, culture); }
    }

    // The Claude Code sessions capsule-hook.exe reports: read back from their status files, watched for changes,
    // and counted only while the hooks are connected.
    public sealed class SessionsModule : IModule, IDisposable
    {
        readonly string dir, settingsPath;
        readonly FileSystemWatcher watcher;
        readonly DispatcherTimer settle;   // one write raises several events: act once they stop
        List<SessionStatus> list;
        bool hooksConnected;

        public event Action Changed;   // the status files changed; raised on the UI thread

        // Made on the UI thread, which the watcher's events are handed to.
        public SessionsModule(string dir, string settingsPath)
        {
            this.dir = dir;
            this.settingsPath = settingsPath;
            hooksConnected = HookSetup.IsConnected(settingsPath);
            list = Sessions.Load(dir, Clock.NowMs());
            settle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            settle.Tick += delegate
            {
                settle.Stop();
                list = Sessions.Load(dir, Clock.NowMs());
                if (Changed != null) Changed();
            };
            Dispatcher ui = Dispatcher.CurrentDispatcher;
            Action restart = delegate
            {
                settle.Stop();
                settle.Start();
            };
            // Raised on a worker thread, often several times per write.
            watcher = new FileSystemWatcher(dir, "*.json");
            watcher.Changed += delegate { ui.BeginInvoke(restart); };
            watcher.Created += delegate { ui.BeginInvoke(restart); };
            watcher.Deleted += delegate { ui.BeginInvoke(restart); };
            watcher.Renamed += delegate { ui.BeginInvoke(restart); };
            watcher.EnableRaisingEvents = true;
        }

        public List<SessionStatus> All { get { return list; } }
        public bool HooksConnected { get { return hooksConnected; } }

        // Working, waiting or null. Leftover status files count for nothing while the hooks are disconnected.
        public string Activity { get { return hooksConnected ? Sessions.Aggregate(list) : null; } }

        public void Tick(long now)
        {
            ReadHookState();
            list = Sessions.Load(dir, now);
        }

        // Re-read each tick, so a connect or disconnect made outside the tray menu (the command line, or by hand)
        // shows within 5 s. While settings.json can't be read (Claude Code rewriting it, say), the last answer stands.
        public void ReadHookState()
        {
            bool? connected = HookSetup.ConnectedState(settingsPath);
            if (connected.HasValue) hooksConnected = connected.Value;
        }

        public void Refresh() { Tick(Clock.NowMs()); }
        public void NetworkBack() { }
        public void Resumed() { }

        public SessionsTile Tile(long now) { return SessionsTile.From(list, hooksConnected, now); }

        public void Dispose()
        {
            settle.Stop();
            watcher.Dispose();
        }
    }
}
