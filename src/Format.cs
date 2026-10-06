using System;
using System.Globalization;

namespace Capsule
{
    public static class Palette
    {
        // Theme tokens: Theme.Brush gives each one in the current light or dark colours.
        public const string Text = "text";
        public const string Secondary = "secondary";
        public const string Track = "track";
        public const string Amber = "amber";   // waiting on you; deeper in light mode, where it is also text
        // Accents: the same in both modes.
        public const string Green = "#30D158";
        public const string Yellow = "#FFD60A";
        public const string Red = "#FF453A";

        // The colour for a percentage used, judged on the unrounded value.
        public static string ForUsed(double used)
        {
            if (used >= 70) return Red;
            if (used >= 50) return Yellow;
            return Green;
        }
    }

    public static class Format
    {
        public static string Percent(LimitWindow w)
        {
            if (w == null) return "–";
            return Math.Round(w.Used, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + "%";
        }

        // "Resets in 51 min", "Resets in 3 hr 5 min", or "Resets Thu 12:00 AM" once it is a day or more away.
        public static string ResetText(long resetsAtMs, long nowMs, CultureInfo culture)
        {
            if (resetsAtMs <= 0) return "";
            long ms = resetsAtMs - nowMs;
            if (ms < 60 * 1000) return "Resets in <1 min";
            long minutes = ms / (60 * 1000);
            if (minutes < 60) return "Resets in " + minutes + " min";
            if (ms < 24L * 60 * 60 * 1000)
            {
                long hours = minutes / 60, rest = minutes % 60;
                return "Resets in " + hours + " hr" + (rest > 0 ? " " + rest + " min" : "");
            }
            DateTime local = Clock.FromMs(resetsAtMs).ToLocalTime();
            return "Resets " + local.ToString("ddd", culture) + " " + local.ToString(culture.DateTimeFormat.ShortTimePattern, culture);
        }

        public static string Ago(long thenMs, long nowMs)
        {
            long minutes = Math.Max(0, nowMs - thenMs) / (60 * 1000);
            if (minutes < 1) return "just now";
            if (minutes < 60) return minutes + " min ago";
            long hours = minutes / 60;
            if (hours < 24) return hours + " hr ago";
            return (hours / 24) + " d ago";
        }

        // Short elapsed time for session rows: "<1m", "2m", "1h 5m".
        public static string Elapsed(long ms)
        {
            long minutes = Math.Max(0, ms) / (60 * 1000);
            if (minutes < 1) return "<1m";
            if (minutes < 60) return minutes + "m";
            return (minutes / 60) + "h " + (minutes % 60) + "m";
        }

        public static string PlanName(string plan)
        {
            if (string.IsNullOrEmpty(plan)) return "";
            string p = plan.Replace('_', ' ').Trim();
            return p.Length == 0 ? "" : char.ToUpperInvariant(p[0]) + p.Substring(1);
        }
    }
}
