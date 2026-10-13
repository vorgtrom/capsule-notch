using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;

namespace Capsule
{
    public enum MusicCommand { Previous, TogglePlayPause, Next }

    // Session keys identify a particular player, including separate tabs of the same browser.
    // Titles and artwork stay in memory and are never logged or saved.
    public sealed class MusicTrack
    {
        public string Key = "", Source = "", Title = "", Artist = "", Album = "";
        public bool Playing, CanToggle, CanPrevious, CanNext;
        public double PositionSeconds, DurationSeconds;
        public ImageSource Artwork;

        public bool Allows(MusicCommand command)
        {
            return command == MusicCommand.Previous ? CanPrevious : command == MusicCommand.Next ? CanNext : CanToggle;
        }
    }

    public sealed class MusicSnapshot
    {
        public List<MusicTrack> Tracks = new List<MusicTrack>();
        public string SelectedKey = "", Note = "";
        public bool Busy;
        public MusicTrack Selected { get { return Tracks.FirstOrDefault(t => t.Key == SelectedKey); } }

        public CellModel Cell()
        {
            MusicTrack t = Selected;
            return new CellModel
            {
                Provider = "music", Glyph = "\uE8D6", Artwork = t == null ? null : t.Artwork,
                Text = t == null ? "Music" : t.Playing ? "Playing" : "Paused",
                ShowArc = t != null && t.DurationSeconds > 0,
                Used = t == null || t.DurationSeconds <= 0 ? 0 : 100 * Math.Max(0, Math.Min(1, t.PositionSeconds / t.DurationSeconds)),
                ArcColor = t != null && t.Source == "Spotify" ? "#1DB954" : "#FF5C67",
                Dimmed = t == null,
            };
        }
    }

    public static class MusicSelection
    {
        public const long LingerMs = 2 * 60 * 1000;

        // The Music cell shows while something plays, and for 2 minutes after the last player stopped, so Play stays in
        // reach after a pause. Otherwise the capsule keeps to its other cells.
        public static bool ShowCell(MusicSnapshot s, long now, long lastPlayingMs)
        {
            if (s == null || s.Selected == null) return false;
            if (s.Tracks.Any(t => t.Playing)) return true;
            return lastPlayingMs > 0 && now - lastPlayingMs < LingerMs;
        }

        // Windows gives browsers' app IDs, not their tab URLs. Do not claim every Chrome session is YouTube Music.
        public static string SourceName(string id)
        {
            string s = (id ?? "").ToLowerInvariant();
            if (s.Contains("spotify")) return "Spotify";
            if (s.Contains("youtube-music") || s.Contains("youtubemusic") || s.Contains("youtube music") || s.Contains("youtube_music")
                || s.Contains("ytmdesktop") || s.Contains("pear-desktop") || s == "pear.exe") return "YouTube Music";
            if (s.Contains("msedge")) return "Edge";
            if (s.Contains("chrome")) return "Chrome";
            if (s.Contains("firefox")) return "Firefox";
            if (s.Contains("brave")) return "Brave";
            if (s.Contains("vivaldi")) return "Vivaldi";
            if (s.Contains("opera")) return "Opera";
            return null;
        }

        public static string Choose(IList<MusicTrack> tracks, string pinned, string previous, string systemCurrent)
        {
            MusicTrack choice = tracks.FirstOrDefault(t => t.Key == pinned);
            if (choice == null) choice = tracks.FirstOrDefault(t => t.Key == previous && t.Playing);
            if (choice == null) choice = tracks.FirstOrDefault(t => t.Key == systemCurrent && t.Playing);
            if (choice == null) choice = tracks.FirstOrDefault(t => t.Playing);
            if (choice == null) choice = tracks.FirstOrDefault(t => t.Key == previous);
            if (choice == null) choice = tracks.FirstOrDefault(t => t.Key == systemCurrent);
            if (choice == null) choice = tracks.FirstOrDefault();
            return choice == null ? "" : choice.Key;
        }

        public static string Time(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) seconds = 0;
            TimeSpan t = TimeSpan.FromSeconds(Math.Min(seconds, 86400 * 365));
            return t.TotalHours >= 1 ? ((int)t.TotalHours) + ":" + t.Minutes.ToString("00") + ":" + t.Seconds.ToString("00")
                : ((int)t.TotalMinutes) + ":" + t.Seconds.ToString("00");
        }
    }
}
