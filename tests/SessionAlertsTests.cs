using System.Collections.Generic;

namespace Capsule
{
    // The heads-ups for a session's context (once past 90%, again after it drops under 80%) and a waiting session's
    // hour-long cache (once, 5 minutes before it goes cold).
    public static class SessionAlertsTests
    {
        const long Minute = 60 * 1000, Now = 1000000000L;

        public static void Run()
        {
            Context();
            Cache();
            AfterFocus();
        }

        static List<SessionStatus> One(string state) { return new List<SessionStatus> { new SessionStatus { SessionId = "s1", Project = "capsule", State = state } }; }

        static void Context()
        {
            var alerts = new SessionAlerts();
            var use = new SessionUse { Model = "claude-opus-4-8", Context = 170000 };   // 85% of 200K
            var list = One(States.Working);
            TestRunner.Eq(0, alerts.Check(list, id => use, Now).Count, "85% says nothing");
            use.Context = 182000;   // 91%
            List<string> texts = alerts.Check(list, id => use, Now);
            TestRunner.Eq(1, texts.Count, "past 90%: one alert");
            TestRunner.Check(texts.Count == 1 && texts[0].StartsWith("capsule is at 91% of its context"), "it names the project and the percentage");
            use.Context = 196000;
            TestRunner.Eq(0, alerts.Check(list, id => use, Now).Count, "still past 90%: not again");
            use.Context = 40000;   // compacted
            TestRunner.Eq(0, alerts.Check(list, id => use, Now).Count, "after a compaction: quiet");
            use.Context = 184000;
            TestRunner.Eq(1, alerts.Check(list, id => use, Now).Count, "past 90% again after dropping under 80%: alerted again");
            TestRunner.Eq(0, alerts.Check(list, id => null, Now).Count, "no transcript read yet: nothing");
            TestRunner.Eq(0, alerts.Check(new List<SessionStatus>(), id => use, Now).Count, "the session ended");
            TestRunner.Eq(1, alerts.Check(list, id => use, Now).Count, "a session seen again starts afresh");
        }

        static void AfterFocus()
        {
            var waiting = One(States.Waiting);
            var use = new SessionUse { Model = "claude-opus-4-8", Context = 184000, LastReplyMs = Now - 56 * Minute, CacheTtlMs = SessionContext.HourMs };
            var alerts = new SessionAlerts();
            TestRunner.Eq(0, alerts.Check(waiting, id => use, Now, defer: true).Count, "Focus suppresses both session alerts");
            use.Context = 190000;
            var current = alerts.Check(waiting, id => use, Now + 2 * Minute);
            TestRunner.Eq(2, current.Count, "both still relevant alerts appear on the break");
            TestRunner.Check(current.Exists(text => text.Contains("95%")) && current.Exists(text => text.Contains("about 2 min")), "the break uses current context and cache time");
            TestRunner.Eq(0, alerts.Check(waiting, id => use, Now + 2 * Minute).Count, "released alerts are not repeated");

            alerts = new SessionAlerts();
            alerts.Check(waiting, id => use, Now, defer: true);
            use.Context = 40000;
            TestRunner.Eq(0, alerts.Check(waiting, id => use, Now + 5 * Minute).Count, "compacted context and expired cache produce no break alert");

            alerts = new SessionAlerts();
            alerts.Check(waiting, id => use, Now, defer: true);
            TestRunner.Eq(0, alerts.Check(One(States.Working), id => use, Now + Minute).Count, "a session no longer waiting has no cache warning");
        }

        static void Cache()
        {
            var alerts = new SessionAlerts();
            var use = new SessionUse { Model = "claude-opus-4-8", Context = 50000, LastReplyMs = Now - 50 * Minute, CacheTtlMs = SessionContext.HourMs };
            var waiting = One(States.Waiting);
            TestRunner.Eq(0, alerts.Check(waiting, id => use, Now).Count, "10 minutes left: not yet");
            List<string> texts = alerts.Check(waiting, id => use, Now + 6 * Minute);
            TestRunner.Eq(1, texts.Count, "4 minutes left on a waiting session: one alert");
            TestRunner.Check(texts.Count == 1 && texts[0].Contains("goes cold in about 4 min"), "it says how long is left");
            TestRunner.Eq(0, alerts.Check(waiting, id => use, Now + 7 * Minute).Count, "the same reply: not again");
            TestRunner.Eq(0, alerts.Check(waiting, id => use, Now + 11 * Minute).Count, "already cold: nothing");
            use.LastReplyMs = Now + 20 * Minute;
            TestRunner.Eq(1, alerts.Check(waiting, id => use, Now + 76 * Minute).Count, "a newer reply nearing cold: alerted again");

            var done = new SessionAlerts();
            var finished = new SessionUse { Model = "claude-opus-4-8", LastReplyMs = Now - 57 * Minute, CacheTtlMs = SessionContext.HourMs };
            TestRunner.Eq(0, done.Check(One(States.Idle), id => finished, Now).Count, "a finished session's cache running out says nothing");
            TestRunner.Eq(0, done.Check(One(States.Working), id => finished, Now).Count, "nor a working one's");
            var api = new SessionUse { Model = "claude-opus-4-8", LastReplyMs = Now - 2 * Minute, FallbackTtlMs = SessionContext.FiveMinutesMs };
            TestRunner.Eq(0, done.Check(waiting, id => api, Now).Count, "a 5-minute cache would alert all the time: not alerted");
            var plan = new SessionUse { Model = "claude-opus-4-8", LastReplyMs = Now - 57 * Minute };   // nothing in the transcript: a plan's hour
            TestRunner.Eq(1, new SessionAlerts().Check(waiting, id => plan, Now).Count, "without a word from the transcript, a plan's hour counts");
        }
    }
}
