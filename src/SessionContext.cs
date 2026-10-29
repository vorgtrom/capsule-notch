using System;
using System.Text.RegularExpressions;

namespace Capsule
{
    // How full a session's context window is, and how long its prompt cache is likely to stay warm. Both come from the
    // session's latest reply in its transcript; the cache time is an estimate, since Claude doesn't say when it expires.
    public static class SessionContext
    {
        public const long Window = 200000, LongWindow = 1000000;
        public const long HourMs = 60 * 60 * 1000, FiveMinutesMs = 5 * 60 * 1000;
        public const int WarnPercent = 80;

        public const string Tip = "ctx: how full the context window was at the latest reply. cache: an estimate of how long the "
            + "prompt cache stays warm after the latest reply (an hour on Claude plans, 5 minutes with an API key). "
            + "Claude doesn't publish the exact expiry.";

        // Claude 5 Opus, Sonnet and Fable come with a 1M window; transcripts name the bare model, without "[1m]".
        static readonly Regex LongByDefault = new Regex(@"^claude-(opus|sonnet|fable)-([5-9]|\d{2})", RegexOptions.IgnoreCase);

        // The window the context is measured against: 1M for a 1M-context model (named with [1m], configured as one, or
        // 1M by default), or once a reply has used more than 200K, otherwise 200K; then no more than an auto-compact
        // window, when one is set.
        public static long WindowFor(string model, long context, bool longContext, long limit)
        {
            model = model ?? "";
            bool longModel = longContext || model.IndexOf("[1m]", StringComparison.OrdinalIgnoreCase) >= 0 || LongByDefault.IsMatch(model) || context > Window;
            long window = longModel ? LongWindow : Window;
            return limit > 0 && limit < window ? limit : window;
        }

        // The share of the window used, rounded, or -1 when there is nothing to measure.
        public static int Percent(long context, long window)
        {
            if (context <= 0 || window <= 0) return -1;
            return (int)Math.Min(999, Math.Round(100.0 * context / window, MidpointRounding.AwayFromZero));
        }

        // "cache 41m", "cache <1m", "cache cold", or "" when there is no reply to count from.
        public static string CacheText(long lastReplyMs, long ttlMs, long now)
        {
            if (lastReplyMs <= 0 || ttlMs <= 0) return "";
            long left = lastReplyMs + ttlMs - now;
            if (left <= 0) return "cache cold";
            long minutes = left / 60000;
            return minutes < 1 ? "cache <1m" : "cache " + minutes + "m";
        }

        // From ~/.claude/settings.json and the environment: whether the configured model is a 1M-context one, and the
        // auto-compact window when one is set (CLAUDE_CODE_AUTO_COMPACT_WINDOW, then autoCompactWindow). Only read.
        public static void ReadSettings(string settingsJson, string environmentValue, out bool longContext, out long limit)
        {
            object root = Json.TryParse(settingsJson);
            string model = Json.Str(Json.Get(root, "model")) ?? "";
            longContext = model.IndexOf("[1m]", StringComparison.OrdinalIgnoreCase) >= 0;
            limit = Positive(environmentValue);
            if (limit == 0) limit = Positive(Json.Get(Json.Get(root, "env"), "CLAUDE_CODE_AUTO_COMPACT_WINDOW"));
            if (limit == 0) limit = Positive(Json.Get(root, "autoCompactWindow"));
        }

        static long Positive(object value)
        {
            double? n = Json.Num(value);
            long parsed;
            if (!n.HasValue && value is string && long.TryParse(((string)value).Trim(), out parsed)) n = parsed;
            return n.HasValue && n.Value > 0 && n.Value < 1e9 ? (long)n.Value : 0;
        }
    }
}
