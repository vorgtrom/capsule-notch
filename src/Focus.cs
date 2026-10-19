using System;
using System.Globalization;

namespace Capsule
{
    public enum FocusPhase { Focus, ShortBreak, LongBreak }

    // A Pomodoro timer: focus, a short break, and a long break after every RoundsPerSet focus sessions. Each part starts
    // the next by itself. The end is a time on the clock, so sleep and a busy UI thread can't make it drift. In memory
    // only: quitting Capsule stops it.
    public sealed class FocusTimer
    {
        public const int RoundsPerSet = 4;
        public const string Glyph = "";   // U+E916 Stopwatch in the icon fonts, kept as an escape (invisible otherwise)

        int focusMinutes = 25, shortMinutes = 5, longMinutes = 15;
        long endsAtMs, leftMs;

        public bool Running { get; private set; }
        public bool Paused { get; private set; }
        public bool Active { get { return Running || Paused; } }
        public FocusPhase Phase { get; private set; }
        public int Round { get; private set; }   // which focus session of the set: 1 to RoundsPerSet

        public FocusTimer() { Round = 1; }

        // New lengths count from the next part on; the one running keeps its end.
        public void SetLengths(int focus, int shortBreak, int longBreak)
        {
            focusMinutes = Math.Max(1, focus);
            shortMinutes = Math.Max(1, shortBreak);
            longMinutes = Math.Max(1, longBreak);
        }

        public long LengthMs(FocusPhase phase)
        {
            int minutes = phase == FocusPhase.Focus ? focusMinutes : phase == FocusPhase.ShortBreak ? shortMinutes : longMinutes;
            return minutes * 60L * 1000;
        }

        public long EndsAtMs { get { return endsAtMs; } }

        public long LeftMs(long now)
        {
            if (Running) return Math.Max(0, endsAtMs - now);
            return Paused ? leftMs : 0;
        }

        public void Start(long now)
        {
            Phase = FocusPhase.Focus;
            Round = 1;
            Running = true;
            Paused = false;
            endsAtMs = now + LengthMs(Phase);
        }

        public void Pause(long now)
        {
            if (!Running) return;
            leftMs = Math.Max(0, endsAtMs - now);
            Running = false;
            Paused = true;
        }

        public void Resume(long now)
        {
            if (!Paused) return;
            endsAtMs = now + leftMs;
            Running = true;
            Paused = false;
        }

        public void Stop()
        {
            Running = Paused = false;
            Phase = FocusPhase.Focus;
            Round = 1;
        }

        // On to the next part at once, without a sound. A paused timer stays paused, at the next part's full length.
        public void Skip(long now)
        {
            if (Active) Advance(now);
        }

        // The part that just ended, or null. The next part has started by then.
        public FocusPhase? Tick(long now)
        {
            if (!Running || now < endsAtMs) return null;
            FocusPhase ended = Phase;
            Advance(now);
            return ended;
        }

        void Advance(long now)
        {
            if (Phase == FocusPhase.Focus) Phase = Round >= RoundsPerSet ? FocusPhase.LongBreak : FocusPhase.ShortBreak;
            else
            {
                Round = Phase == FocusPhase.LongBreak ? 1 : Round + 1;
                Phase = FocusPhase.Focus;
            }
            if (Running) endsAtMs = now + LengthMs(Phase);
            else leftMs = LengthMs(Phase);
        }

        // What to say when a part ends, now that the next one has started.
        public string EndedText(FocusPhase ended)
        {
            if (ended == FocusPhase.Focus)
                return Phase == FocusPhase.LongBreak ? "Focus done, " + RoundsPerSet + " in a row. Take a " + longMinutes + " min break."
                    : "Focus done. Take a " + shortMinutes + " min break.";
            return "Break's over. Focus " + Round + " of " + RoundsPerSet + " has started.";
        }

        public static string PhaseName(FocusPhase p)
        {
            return p == FocusPhase.Focus ? "Focus" : p == FocusPhase.ShortBreak ? "Short break" : "Long break";
        }

        // "18:42"; "65m" from an hour up, where the seconds would no longer fit the ring.
        public static string Clock(long ms)
        {
            long seconds = (ms + 999) / 1000;
            if (seconds >= 3600) return (seconds / 60) + "m";
            return (seconds / 60) + ":" + (seconds % 60).ToString("00", CultureInfo.InvariantCulture);
        }

        // The Focus ring: the time left as text and as the arc, red while you focus and green on a break, dimmed while paused.
        public CellModel Cell(long now)
        {
            long left = LeftMs(now), length = LengthMs(Phase);
            return new CellModel
            {
                Provider = "focus",
                Glyph = Glyph,
                Text = Clock(left),
                ShowArc = true,
                Used = length > 0 ? Math.Round(100.0 * left / length, 1) : 0,
                ArcColor = Phase == FocusPhase.Focus ? Palette.Red : Palette.Green,
                Dimmed = Paused,
            };
        }
    }

    // What the panel's Focus tile shows.
    public sealed class FocusTile
    {
        public bool Active, Running, Paused;
        public string Heading = "";   // "2 of 4", "Short break · 2 of 4", or "Ready"
        public string Time = "";      // "18:42"
        public string Note = "";      // "until 4:10 PM", or the lengths while stopped
        public string Color = Palette.Text;

        public static FocusTile From(FocusTimer t, long now, CultureInfo culture)
        {
            var tile = new FocusTile { Active = t.Active, Running = t.Running, Paused = t.Paused };
            if (!t.Active)
            {
                tile.Heading = "Ready";
                tile.Time = FocusTimer.Clock(t.LengthMs(FocusPhase.Focus));
                tile.Note = (t.LengthMs(FocusPhase.Focus) / 60000) + " min focus, " + (t.LengthMs(FocusPhase.ShortBreak) / 60000) + " min breaks, "
                    + (t.LengthMs(FocusPhase.LongBreak) / 60000) + " min after " + FocusTimer.RoundsPerSet;
                return tile;
            }
            tile.Heading = (t.Phase == FocusPhase.Focus ? "" : FocusTimer.PhaseName(t.Phase) + " · ") + t.Round + " of " + FocusTimer.RoundsPerSet;   // the tile is called Focus already
            tile.Time = FocusTimer.Clock(t.LeftMs(now));
            tile.Note = t.Paused ? "Paused" : "until " + Clock.FromMs(t.EndsAtMs).ToLocalTime().ToString("t", culture);
            tile.Color = t.Phase == FocusPhase.Focus ? Palette.Red : Palette.Green;
            return tile;
        }
    }
}
