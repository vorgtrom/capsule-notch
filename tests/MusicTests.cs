using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Capsule
{
    public static class MusicTests
    {
        public static void Run()
        {
            SelectionAndSources();
            TheCellHidesWhenNothingPlays();
            ModelsAndViews();
            CommandsStayWithTheirPlayer();
        }

        public static int Smoke()
        {
            try
            {
                MusicSnapshot snapshot = Task.Run(() => new WindowsMusicSource().ReadAsync()).GetAwaiter().GetResult();
                Console.WriteLine("Windows music reader: " + snapshot.Tracks.Count + " sessions; " + snapshot.Note);
                foreach (MusicTrack t in snapshot.Tracks)
                    Console.WriteLine(t.Source + ": " + (t.Playing ? "playing" : "paused") + ", artwork=" + (t.Artwork != null) + ", toggle=" + t.CanToggle);
                return 0;
            }
            catch (Exception e) { Console.WriteLine("Windows music reader failed: " + e); return 1; }
        }

        static void TheCellHidesWhenNothingPlays()
        {
            const long now = 1000000000;
            var none = new MusicSnapshot();
            TestRunner.Check(!MusicSelection.ShowCell(none, now, now), "no player: no Music cell");
            var playing = new MusicSnapshot { Tracks = Tracks(), SelectedKey = "spotify" };
            playing.Tracks[1].Playing = true;
            TestRunner.Check(MusicSelection.ShowCell(playing, now, 0), "something playing: the Music cell shows");
            var paused = new MusicSnapshot { Tracks = Tracks(), SelectedKey = "spotify" };
            paused.Tracks.ForEach(t => t.Playing = false);
            TestRunner.Check(MusicSelection.ShowCell(paused, now, now - 60 * 1000), "paused a minute ago: still shown, so Play is in reach");
            TestRunner.Check(!MusicSelection.ShowCell(paused, now, now - MusicSelection.LingerMs), "paused 2 minutes ago: hidden");
            TestRunner.Check(!MusicSelection.ShowCell(paused, now, 0), "paused since before Capsule started: hidden");
        }

        static List<MusicTrack> Tracks()
        {
            return new List<MusicTrack>
            {
                new MusicTrack { Key = "spotify", Source = "Spotify", Title = "A song", Artist = "An artist", DurationSeconds = 240, PositionSeconds = 60, CanToggle = true },
                new MusicTrack { Key = "browser1", Source = "Chrome", Title = "Another song", Playing = true, CanToggle = true, CanNext = true },
                new MusicTrack { Key = "browser2", Source = "Chrome", Title = "Another tab", CanToggle = true },
            };
        }

        static void SelectionAndSources()
        {
            TestRunner.Eq("Spotify", MusicSelection.SourceName("SpotifyAB.SpotifyMusic_abc!Spotify"), "Store Spotify is identified");
            TestRunner.Eq("YouTube Music", MusicSelection.SourceName("com.github.th-ch.youtube-music"), "YouTube Music desktop is identified");
            TestRunner.Eq("YouTube Music", MusicSelection.SourceName("ytmdesktop.exe"), "YTM Desktop is identified");
            TestRunner.Eq("Chrome", MusicSelection.SourceName("chrome.exe"), "browser sessions aren't falsely labelled YouTube Music");
            TestRunner.Eq("Edge", MusicSelection.SourceName("msedge.exe"), "Edge sessions are available");
            TestRunner.Eq(null, MusicSelection.SourceName("unrelated.exe"), "unrelated apps are excluded");
            var tracks = Tracks();
            TestRunner.Eq("browser1", MusicSelection.Choose(tracks, "", "", "spotify"), "playing media wins over a paused system session");
            TestRunner.Eq("spotify", MusicSelection.Choose(tracks, "spotify", "browser1", "browser1"), "an explicit source stays selected while paused");
            tracks[0].Playing = true;
            TestRunner.Eq("spotify", MusicSelection.Choose(tracks, "", "spotify", "browser1"), "playing selection does not flicker to another player");
            TestRunner.Eq("browser2", MusicSelection.Choose(tracks, "browser2", "spotify", "browser1"), "two tabs from one browser are selectable independently");
            tracks.RemoveAt(2);
            TestRunner.Eq("spotify", MusicSelection.Choose(tracks, "browser2", "spotify", "browser1"), "a closed pinned tab falls back to a live session");
            TestRunner.Eq("", MusicSelection.Choose(new List<MusicTrack>(), "spotify", "spotify", "spotify"), "all players closed clears the selection");
            TestRunner.Eq("1:05", MusicSelection.Time(65), "minutes");
            TestRunner.Eq("1:01:05", MusicSelection.Time(3665), "hours");
        }

        static void ModelsAndViews()
        {
            var m = new MusicSnapshot { Tracks = Tracks(), SelectedKey = "spotify" };
            TestRunner.Near(25, m.Cell().Used, "ring shows playback progress");
            TestRunner.Eq("Paused", m.Cell().Text, "pause state");
            m.Tracks[0].PositionSeconds = 500;
            TestRunner.Near(100, m.Cell().Used, "progress is bounded");
            m.Tracks[0].DurationSeconds = 0;
            TestRunner.Check(!m.Cell().ShowArc, "unknown durations have no progress ring");
            var view = new MusicView();
            view.Update(m);
            view.Measure(new Size(252, double.PositiveInfinity));
            view.Arrange(new Rect(0, 0, 252, view.DesiredSize.Height));
            Button previous = Find(view, "Previous track"), play = Find(view, "Play"), next = Find(view, "Next track");
            TestRunner.Check(!previous.IsEnabled && !next.IsEnabled && play.IsEnabled, "unsupported controls are disabled");
            string clickedKey = "";
            MusicCommand clickedCommand = MusicCommand.Next;
            view.Commanded += delegate(string key, MusicCommand command) { clickedKey = key; clickedCommand = command; };
            play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            TestRunner.Eq("spotify", clickedKey, "click carries the displayed session's key");
            TestRunner.Eq(MusicCommand.TogglePlayPause, clickedCommand, "play sends the correct command");
            // Mutating a shared model must not retarget an old visible button to another player.
            m.SelectedKey = "browser1";
            clickedKey = "";
            play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            TestRunner.Eq("spotify", clickedKey, "an old card keeps its displayed command target");
            view.Update(m);
            TestRunner.Check(Find(view, "Play") == null && Find(view, "Pause").IsEnabled, "playing updates the action to Pause");
            TestRunner.Check(Find(view, "Next track").IsEnabled, "browser skip support comes from Windows");
            TestRunner.Check(ReferenceEquals(next, Find(view, "Next track")), "updates keep buttons in place");
            m.Busy = true;
            view.Update(m);
            TestRunner.Check(!Find(view, "Pause").IsEnabled && !next.IsEnabled, "commands disable controls until completion");
            view.Update(new MusicSnapshot { Note = "Play a song" });
            TestRunner.Check(view.DesiredSize.Height > 0, "empty state can be laid out");
            TestRunner.Eq("Music", new MusicSnapshot().Cell().Text, "empty cell invites music playback");
        }

        sealed class Source : IMusicSource
        {
            public MusicSnapshot Snapshot = new MusicSnapshot { Tracks = Tracks(), SelectedKey = "spotify" };
            public int Commands;
            public string Key;
            public TaskCompletionSource<bool> Pending;
            public bool FailRead;
            public Task<MusicSnapshot> ReadAsync()
            {
                if (FailRead) throw new InvalidOperationException("Synthetic failure");
                return Task.FromResult(new MusicSnapshot { Tracks = Snapshot.Tracks, SelectedKey = Snapshot.SelectedKey });
            }
            public Task<bool> CommandAsync(string key, MusicCommand command)
            {
                Key = key;
                Interlocked.Increment(ref Commands);
                return Pending == null ? Task.FromResult(true) : Pending.Task;
            }
        }

        static void CommandsStayWithTheirPlayer()
        {
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try
            {
                var source = new Source();
                using (var module = new MusicModule(source, TaskScheduler.FromCurrentSynchronizationContext()))
                {
                    module.Refresh();
                    Pump(() => module.Current.Tracks.Count > 0);
                    module.Select("spotify");
                    module.Command("browser1", MusicCommand.TogglePlayPause);
                    module.Command("spotify", MusicCommand.Next);
                    TestRunner.Eq(0, source.Commands, "stale keys and unsupported actions never reach a player");
                    source.Pending = new TaskCompletionSource<bool>();
                    module.Command("spotify", MusicCommand.TogglePlayPause);
                    Pump(() => source.Commands == 1);
                    module.Command("spotify", MusicCommand.TogglePlayPause);
                    module.Select("browser1");
                    TestRunner.Eq(1, source.Commands, "repeated clicks do not duplicate a pending command");
                    TestRunner.Eq("spotify", module.Current.SelectedKey, "a pending command keeps the chosen player");
                    TestRunner.Eq("spotify", source.Key, "command targets exactly the selected session");
                    source.Pending.SetResult(false);
                    Pump(() => !module.Current.Busy);
                    TestRunner.Check(module.Current.Note.Contains("couldn't"), "a rejected command tells the user");
                    module.Select("browser1");
                    module.Command("spotify", MusicCommand.TogglePlayPause);
                    TestRunner.Eq(1, source.Commands, "an old Spotify control cannot act after switching to a browser");
                    source.Snapshot.Tracks = new List<MusicTrack>();
                    module.Refresh();
                    Pump(() => module.Current.Tracks.Count == 0);
                    TestRunner.Eq(null, module.Current.Selected, "closed sessions clear the displayed track");
                    source.FailRead = true;
                    module.Refresh();
                    Pump(() => module.Current.Note.Contains("Couldn't read"));
                    source.FailRead = false;
                    source.Snapshot.Tracks = Tracks();
                    module.Refresh();
                    Pump(() => module.Current.Tracks.Count > 0);
                    TestRunner.Check(module.Current.Selected != null, "a failed poll can recover");
                }
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        }

        static void Pump(Func<bool> done)
        {
            var watch = Stopwatch.StartNew();
            while (!done() && watch.ElapsedMilliseconds < 3000)
            {
                var frame = new DispatcherFrame();
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
                timer.Tick += delegate { timer.Stop(); frame.Continue = false; };
                timer.Start();
                Dispatcher.PushFrame(frame);
            }
            TestRunner.Check(done(), "asynchronous music operation finishes");
        }

        static Button Find(DependencyObject root, string name)
        {
            var button = root as Button;
            if (button != null && AutomationProperties.GetName(button) == name) return button;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var found = Find(VisualTreeHelper.GetChild(root, i), name);
                if (found != null) return found;
            }
            return null;
        }
    }
}
