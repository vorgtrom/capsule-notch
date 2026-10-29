using System;
using System.Collections.Generic;
using System.IO;

namespace Capsule
{
    // A session row's context fill ("ctx 64%") and prompt-cache countdown ("cache 41m").
    public static class SessionContextTests
    {
        public static void Run()
        {
            Windows();
            Percents();
            Countdown();
            Transcript();
            Settings();
        }

        const long Minute = 60 * 1000;

        static void Windows()
        {
            TestRunner.Eq(200000L, SessionContext.WindowFor("claude-opus-4-8", 50000, false, 0), "a 200K window by default");
            TestRunner.Eq(1000000L, SessionContext.WindowFor("claude-sonnet-4-5[1m]", 50000, false, 0), "1M for a [1m] model");
            TestRunner.Eq(1000000L, SessionContext.WindowFor("claude-opus-4-8", 50000, true, 0), "1M when the configured model is a 1M one");
            TestRunner.Eq(1000000L, SessionContext.WindowFor("claude-opus-4-8", 250000, false, 0), "1M once a reply has used more than 200K");
            TestRunner.Eq(150000L, SessionContext.WindowFor("claude-opus-4-8", 50000, false, 150000), "an auto-compact window below the model's is used");
            TestRunner.Eq(200000L, SessionContext.WindowFor("claude-opus-4-8", 50000, false, 500000), "an auto-compact window can't make the model's bigger");
            TestRunner.Eq(400000L, SessionContext.WindowFor("claude-opus-4-8", 50000, true, 400000), "a 1M model's auto-compact window");
            TestRunner.Eq(1000000L, SessionContext.WindowFor("claude-opus-5-5", 150000, false, 0), "Opus 5.5 has 1M by default");
            TestRunner.Eq(1000000L, SessionContext.WindowFor("claude-sonnet-5-5", 150000, false, 0), "so does Sonnet 5.5");
            TestRunner.Eq(1000000L, SessionContext.WindowFor("claude-fable-5-1", 150000, false, 0), "and Fable 5.1");
            TestRunner.Eq(200000L, SessionContext.WindowFor("claude-haiku-5-5", 150000, false, 0), "Haiku stays at 200K");
            TestRunner.Eq(15, SessionContext.Percent(152000, SessionContext.WindowFor("claude-opus-5-5", 152000, false, 0)), "152K on Opus 5.5 is 15%, not 76%");
        }

        static void Percents()
        {
            TestRunner.Eq(64, SessionContext.Percent(128000, 200000), "128K of 200K is 64%");
            TestRunner.Eq(-1, SessionContext.Percent(0, 200000), "no reply yet: nothing to show");
            var use = new SessionUse { Model = "claude-opus-4-8", Context = 128000 };
            TestRunner.Eq("ctx 64%", use.ContextText, "the row's context text");
            TestRunner.Check(!use.ContextHigh, "64% isn't amber");
            use.Context = 160000;
            TestRunner.Check(use.ContextHigh, "80% is amber");
            use.Context = 158000;
            TestRunner.Check(!use.ContextHigh, "79% isn't");
            TestRunner.Eq("", new SessionUse().ContextText, "a session without a reply shows no context");
        }

        static void Countdown()
        {
            long now = 1000000000L;
            TestRunner.Eq("cache 41m", SessionContext.CacheText(now - 19 * Minute, SessionContext.HourMs, now), "an hour's cache, 19 minutes in");
            TestRunner.Eq("cache <1m", SessionContext.CacheText(now - 59 * Minute - 30000, SessionContext.HourMs, now), "under a minute left");
            TestRunner.Eq("cache cold", SessionContext.CacheText(now - 61 * Minute, SessionContext.HourMs, now), "past the hour: cold");
            TestRunner.Eq("cache cold", SessionContext.CacheText(now - SessionContext.HourMs, SessionContext.HourMs, now), "exactly at the hour: cold");
            TestRunner.Eq("", SessionContext.CacheText(0, SessionContext.HourMs, now), "no reply yet: no countdown");
            var api = new SessionUse { LastReplyMs = now - 2 * Minute, FallbackTtlMs = SessionContext.FiveMinutesMs };
            TestRunner.Eq("cache 3m", api.CacheText(now), "with an API key the cache lasts 5 minutes");
            api.CacheTtlMs = SessionContext.HourMs;
            TestRunner.Eq("cache 58m", api.CacheText(now), "what the transcript says about the cache wins over the plan");
        }

        static string Reply(string id, int input, int created, int read, string cacheCreation, string at)
        {
            return "{\"type\":\"assistant\",\"timestamp\":\"" + at + "\",\"requestId\":\"req_" + id + "\",\"message\":{\"id\":\"" + id
                + "\",\"model\":\"claude-opus-4-8\",\"usage\":{\"input_tokens\":" + input + ",\"cache_creation_input_tokens\":" + created
                + ",\"cache_read_input_tokens\":" + read + ",\"output_tokens\":500" + (cacheCreation != null ? ",\"cache_creation\":" + cacheCreation : "") + "}}}";
        }

        static void Transcript()
        {
            UsageEntry hour = UsageLogs.ParseClaude(Reply("m1", 10, 2000, 126000, "{\"ephemeral_5m_input_tokens\":0,\"ephemeral_1h_input_tokens\":2000}", "2026-10-29T10:00:00.000Z"));
            TestRunner.Eq(128010L, hour.Context, "context is the reply's input, cache writes and cache reads");
            TestRunner.Eq(SessionContext.HourMs, hour.CacheTtlMs, "1h cache writes: an hour");
            UsageEntry five = UsageLogs.ParseClaude(Reply("m2", 10, 300, 0, "{\"ephemeral_5m_input_tokens\":300,\"ephemeral_1h_input_tokens\":0}", "2026-10-29T10:00:00.000Z"));
            TestRunner.Eq(SessionContext.FiveMinutesMs, five.CacheTtlMs, "5m cache writes: 5 minutes");
            TestRunner.Eq(0L, UsageLogs.ParseClaude(Reply("m3", 10, 0, 500, null, "2026-10-29T10:00:00.000Z")).CacheTtlMs, "no cache writes: it doesn't say");

            // Through the tally: the latest reply sets the context, and the last one that wrote to the cache its lifetime.
            string claudeDir = TestRunner.NewTempDir();
            string project = Path.Combine(claudeDir, "projects", "C--work-capsule");
            Directory.CreateDirectory(project);
            const string id = "7c1d2e3f-2222-4b2c-9d3e-abcdefabcdef";
            File.WriteAllText(Path.Combine(project, id + ".jsonl"),
                Reply("m1", 10, 2000, 20000, "{\"ephemeral_5m_input_tokens\":0,\"ephemeral_1h_input_tokens\":2000}", "2026-10-29T10:00:00.000Z") + "\n" +
                Reply("m2", 10, 0, 127990, null, "2026-10-29T10:05:00.000Z") + "\n");
            var tally = new SessionTally(claudeDir);
            var list = new List<SessionStatus> { new SessionStatus { SessionId = id, State = States.Idle, At = 1 } };
            tally.Update(list, 1);
            SessionUse use = tally.For(id);
            TestRunner.Eq("ctx 64%", use.ContextText, "the tally's row: context from the latest reply");
            long lastReply = UsageLogs.ParseClaude(Reply("m2", 10, 0, 127990, null, "2026-10-29T10:05:00.000Z")).AtMs;
            TestRunner.Eq("cache 41m", use.CacheText(lastReply + 19 * Minute), "the countdown runs from the latest reply, with the hour the session's writes used");
            tally.Subscription = false;
            TestRunner.Eq("cache 41m", tally.For(id).CacheText(lastReply + 19 * Minute), "the transcript's hour holds even without a plan");

            File.WriteAllText(Path.Combine(claudeDir, "settings.json"), "{\"env\":{\"CLAUDE_CODE_AUTO_COMPACT_WINDOW\":\"160000\"}}");
            var limited = new SessionTally(claudeDir);
            limited.Update(list, 1);
            TestRunner.Eq("ctx 80%", limited.For(id).ContextText, "an auto-compact window in Claude's settings is what the context fills");
            TestRunner.Check(limited.For(id).ContextHigh, "and 80% of it is amber");
        }

        static void Settings()
        {
            bool longContext; long limit;
            SessionContext.ReadSettings("{\"model\":\"opus[1m]\"}", null, out longContext, out limit);
            TestRunner.Check(longContext && limit == 0, "a [1m] model in settings: 1M, no auto-compact window");
            SessionContext.ReadSettings("{\"model\":\"opus\",\"env\":{\"CLAUDE_CODE_AUTO_COMPACT_WINDOW\":\"150000\"}}", null, out longContext, out limit);
            TestRunner.Check(!longContext && limit == 150000, "the auto-compact window from settings' env, written as text");
            SessionContext.ReadSettings("{\"autoCompactWindow\":120000}", null, out longContext, out limit);
            TestRunner.Eq(120000L, limit, "autoCompactWindow as a number");
            SessionContext.ReadSettings("{\"autoCompactWindow\":120000}", "90000", out longContext, out limit);
            TestRunner.Eq(90000L, limit, "the environment variable wins");
            SessionContext.ReadSettings("not json", "abc", out longContext, out limit);
            TestRunner.Check(!longContext && limit == 0, "unreadable settings and a junk value change nothing");
            SessionContext.ReadSettings(null, "-5", out longContext, out limit);
            TestRunner.Eq(0L, limit, "a negative window is ignored");
        }
    }
}
