using System;
using System.Threading.Tasks;

namespace Capsule
{
    public interface IMusicSource
    {
        Task<MusicSnapshot> ReadAsync();
        Task<bool> CommandAsync(string key, MusicCommand command);
    }

    // All mutable state and Changed notifications belong to the UI thread. Only the Windows reader runs off it.
    public sealed class MusicModule : IModule, IDisposable
    {
        readonly IMusicSource source;
        readonly TaskScheduler ui;
        string pinned = "", commandNote = "";
        string lastFault = "";
        long due;
        bool polling, commanding, disposed;
        public MusicSnapshot Current { get; private set; }
        public event Action Changed;

        public MusicModule(IMusicSource source, TaskScheduler ui)
        {
            this.source = source;
            this.ui = ui;
            Current = new MusicSnapshot { Note = "Looking for music\u2026" };
        }

        public void Tick(long now) { if (!disposed && !polling && !commanding && now >= due) Poll(); }
        public void Refresh() { due = 0; Tick(Clock.NowMs()); }
        public void NetworkBack() { }
        public void Resumed() { due = 0; }

        void Poll()
        {
            polling = true;
            due = long.MaxValue;
            Task.Run(() => source.ReadAsync()).ContinueWith(t =>
            {
                polling = false;
                if (disposed) return;
                // Every second while there is a player to show; every 3 s while there is none.
                if (due != 0) due = Clock.NowMs() + (t.IsFaulted || t.IsCanceled ? 10000 : t.Result.Tracks.Count == 0 ? 3000 : 1000);
                MusicSnapshot next = t.IsFaulted || t.IsCanceled
                    ? new MusicSnapshot { Note = "Couldn't read Windows media. Try playing music again." } : t.Result;
                string fault = t.IsFaulted ? t.Exception.GetBaseException().GetType().Name : "";
                if (fault != "" && fault != lastFault) Log.Error("music read (" + fault + ")", null);
                lastFault = fault;
                if (!next.Tracks.Exists(track => track.Key == pinned)) pinned = "";
                next.SelectedKey = MusicSelection.Choose(next.Tracks, pinned, Current.SelectedKey, next.SelectedKey);
                next.Busy = commanding;
                if (commandNote != "") next.Note = commandNote;
                Current = next;
                RaiseChanged();
            }, ui);
        }

        public void Select(string key)
        {
            if (disposed || commanding || !Current.Tracks.Exists(t => t.Key == key)) return;
            pinned = key;
            Current.SelectedKey = key;
            commandNote = Current.Note = "";
            RaiseChanged();
        }

        public void Command(string key, MusicCommand command)
        {
            MusicTrack track = Current.Selected;
            // A click from an older card must never control a newly selected player.
            if (disposed || commanding || track == null || track.Key != key || !track.Allows(command)) return;
            commanding = Current.Busy = true;
            commandNote = Current.Note = "";
            RaiseChanged();
            Task.Run(() => source.CommandAsync(key, command)).ContinueWith(t =>
            {
                commanding = false;
                if (disposed) return;
                Current.Busy = false;
                if (t.IsFaulted) Log.Error("music control (" + t.Exception.GetBaseException().GetType().Name + ")", null);
                commandNote = t.IsFaulted || t.IsCanceled || !t.Result ? "The player couldn't perform that action. Try it in the music app." : "";
                Current.Note = commandNote;
                RaiseChanged();
                Refresh();
            }, ui);
        }

        void RaiseChanged() { if (Changed != null) Changed(); }
        public void Dispose() { disposed = true; }
    }
}
