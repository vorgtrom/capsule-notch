using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace Capsule
{
    public static class ModulesTests
    {
        const long Now = 1790690940000;
        static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

        public static void Run()
        {
            RetriesSoonAfterNoReply();
            ChecksCanBeStartedFromAnyThread();
            UsageTilesDescribeReadings();
            SessionsTileFollowsTheCardsRules();
        }

        // A usage source that answers at once.
        sealed class QuickSource : IUsageSource
        {
            public int Polls;
            readonly Reading reading = new Reading { Provider = "claude", Status = "ok" };
            public Reading Current { get { return reading; } }
            public Reading Poll(long now)
            {
                Interlocked.Increment(ref Polls);
                return reading;
            }
            public bool LastCheckUnreachable { get { return false; } }
        }

        // A check's result is handed back on the UI thread, whose scheduler the module takes when it is built (on that
        // thread). A refresh asked for from another thread (the network coming back, a timer) must still work: asking
        // the thread it happens on for its scheduler at that moment threw, after the module had marked itself busy, and
        // left it busy for good.
        static void ChecksCanBeStartedFromAnyThread()
        {
            SynchronizationContext before = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());   // stands in for the UI thread's
            var source = new QuickSource();
            UsageModule module;
            try { module = new UsageModule(source, () => 60 * 1000); }
            finally { SynchronizationContext.SetSynchronizationContext(before); }
            var finished = new ManualResetEvent(false);
            module.Changed += delegate { finished.Set(); };
            Exception thrown = null;
            var other = new Thread(delegate()
            {
                try { module.Refresh(); }
                catch (Exception e) { thrown = e; }
            });
            other.Start();
            other.Join();
            TestRunner.Check(thrown == null, "a check can be started from a thread with no synchronization context" + (thrown != null ? " (it threw " + thrown.GetType().Name + ")" : ""));
            TestRunner.Check(finished.WaitOne(3000), "and it finishes and tells the module's listeners");
            finished.Reset();
            module.Refresh();
            TestRunner.Check(finished.WaitOne(3000) && source.Polls == 2, "and the module isn't left busy: the next refresh checks again (checks: " + source.Polls + ")");
        }

        static void RetriesSoonAfterNoReply()
        {
            const long Usual = 300 * 1000;
            int retries = 0;
            for (int i = 1; i <= 6; i++) TestRunner.Eq(20000L, UsageModule.RetryDelay(true, ref retries, Usual), "no reply: retry in 20 s, try " + i);
            TestRunner.Eq(Usual, UsageModule.RetryDelay(true, ref retries, Usual), "after six quick retries, the usual interval");
            TestRunner.Eq(Usual, UsageModule.RetryDelay(false, ref retries, Usual), "a reply: the usual interval");
            TestRunner.Eq(20000L, UsageModule.RetryDelay(true, ref retries, Usual), "and the next failure retries quickly again");
        }

        static void UsageTilesDescribeReadings()
        {
            var r = new Reading { Provider = "claude", Status = "ok", HeadlineId = "five_hour", Plan = "Max", DataAtMs = Now };
            r.Windows.Add(new LimitWindow { Id = "five_hour", Label = "Current session", Used = 73, ResetsAtMs = Now + 51 * 60 * 1000L });
            UsageTile t = UsageTile.From(r, Now, En);
            TestRunner.Eq("Claude", t.Title, "title");
            TestRunner.Eq("Max", t.Plan, "plan");
            TestRunner.Eq("73%", t.Percent, "percent");
            TestRunner.Check(t.ShowBar && t.Used == 73, "a bar at 73%");
            TestRunner.Eq(Palette.Red, t.Color, "red from 70%");
            TestRunner.Eq("Resets in 51 min", t.ResetText, "reset time");
            TestRunner.Check(!t.SignIn && !t.Missing && !t.Dimmed && t.Note == "", "nothing wrong");

            Reading stale = r.Clone();
            stale.LastCheckFailed = true;
            stale.DataAtMs = Now - 11 * 60 * 1000;
            stale.Note = "Network error (Timeout)";
            UsageTile old = UsageTile.From(stale, Now, En);
            TestRunner.Check(old.Dimmed && old.Note == "Network error (Timeout)", "old numbers dim, with the reason");

            UsageTile signIn = UsageTile.From(new Reading { Provider = "claude", Status = "signin" }, Now, En);
            TestRunner.Check(signIn.SignIn && signIn.Percent == "–" && !signIn.ShowBar && signIn.Note != "", "Claude needing sign-in gets the Sign in button");
            UsageTile codexMissing = UsageTile.From(new Reading { Provider = "codex", Status = "none" }, Now, En);
            TestRunner.Check(codexMissing.Missing && codexMissing.Title == "Codex" && !codexMissing.SignIn, "no Codex on this PC");
            UsageTile checking = UsageTile.From(new Reading { Provider = "claude", Status = "none" }, Now, En);
            TestRunner.Check(!checking.Missing && checking.Note == "Checking…", "Claude before its first check");
        }

        static void SessionsTileFollowsTheCardsRules()
        {
            var list = new List<SessionStatus>();
            for (int i = 0; i < 7; i++) list.Add(new SessionStatus { SessionId = "s" + i, State = States.Working, Project = "p" + i, At = Now - i * 1000, Since = Now - 60000 });
            list.Add(new SessionStatus { SessionId = "w", State = States.Waiting, Project = "confetti", Reason = "asked you a question", At = Now - 9000, Since = Now - 9000 });
            SessionsTile t = SessionsTile.From(list, true, Now);
            TestRunner.Eq(5, t.Rows.Count, "five rows");
            TestRunner.Eq(3, t.More, "then +3 more");
            TestRunner.Eq("confetti", t.Rows[0].Project, "waiting first");
            TestRunner.Eq(Palette.Amber, t.Rows[0].Color, "in amber");
            TestRunner.Eq("", t.Hint, "no hint when there are rows");
            TestRunner.Check(SessionsTile.From(list, false, Now).Hint.Contains("Connect to Claude Code"), "hooks off: how to connect");
            TestRunner.Eq("No active sessions", SessionsTile.From(new List<SessionStatus>(), true, Now).Hint, "nothing going on");
        }
    }
}
