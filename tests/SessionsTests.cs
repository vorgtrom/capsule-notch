using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Capsule
{
    public static class SessionsTests
    {
        const long Now = 1790690940000;
        const long Min = 60 * 1000;
        static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

        public static void Run()
        {
            LoadsAndAppliesStaleness();
            AggregatesWaitingOverWorking();
            PicksRowsForTheCard();
            BuildsTheClaudeCard();
            BuildsTheCodexCardFromLogs();
            HintsWhenHooksAreNotConnected();
            WaitingGoesIdleWhenSilent();
            CleansUpLeftovers();
            MissingStartTimeCountsFromTheLastEvent();
            NamesTheDayOfAnOlderLogSnapshot();
            ColoursAndCountsSessionRows();
        }

        static SessionStatus S(string id, string state, long at, long since, string project, string reason)
        {
            return new SessionStatus { SessionId = id, State = state, At = at, Since = since, Project = project, Reason = reason ?? "" };
        }

        static void LoadsAndAppliesStaleness()
        {
            string dir = TestRunner.NewTempDir();
            File.WriteAllText(Path.Combine(dir, "a.json"), S("a", States.Working, Now - 5 * Min, Now - 6 * Min, "p", "").ToJson());
            File.WriteAllText(Path.Combine(dir, "b.json"), S("b", States.Working, Now - 31 * Min, Now - 40 * Min, "p", "").ToJson());
            File.WriteAllText(Path.Combine(dir, "c.json"), S("c", States.Done, Now - 25 * 60 * Min, Now - 25 * 60 * Min, "p", "").ToJson());
            File.WriteAllText(Path.Combine(dir, "d.json.123.tmp"), "half-written");
            List<SessionStatus> list = Sessions.Load(dir, Now);
            TestRunner.Eq(2, list.Count, "day-old file dropped, temp file ignored");
            TestRunner.Eq(States.Working, list.Find(s => s.SessionId == "a").State, "recent working stays working");
            TestRunner.Eq(States.Idle, list.Find(s => s.SessionId == "b").State, "silent for 30 min: idle");
            TestRunner.Check(!File.Exists(Path.Combine(dir, "c.json")), "day-old file deleted");
        }

        static void AggregatesWaitingOverWorking()
        {
            TestRunner.Eq(null, Sessions.Aggregate(new List<SessionStatus> { S("a", States.Done, Now, Now, "", "") }), "nothing active");
            TestRunner.Eq(States.Working, Sessions.Aggregate(new List<SessionStatus> { S("a", States.Working, Now, Now, "", ""), S("b", States.Idle, Now, Now, "", "") }), "working");
            TestRunner.Eq(States.Waiting, Sessions.Aggregate(new List<SessionStatus> { S("a", States.Working, Now, Now, "", ""), S("b", States.Waiting, Now, Now, "", "") }), "waiting wins");
        }

        static void PicksRowsForTheCard()
        {
            var list = new List<SessionStatus>
            {
                S("old-done", States.Done, Now - 11 * Min, Now - 11 * Min, "old", ""),
                S("done", States.Done, Now - 5 * Min, Now - 5 * Min, "fresh", ""),
                S("w1", States.Working, Now - 1 * Min, Now - 2 * Min, "one", ""),
                S("wait", States.Waiting, Now - 3 * Min, Now - 3 * Min, "two", "needs permission: Bash"),
                S("w2", States.Working, Now - 2 * Min, Now - 9 * Min, "three", ""),
            };
            int more;
            List<SessionStatus> rows = Sessions.ForCard(list, Now, out more);
            TestRunner.Eq("wait,w1,w2,done", string.Join(",", rows.ConvertAll(s => s.SessionId)), "waiting, then working (newest first), then recently done");
            TestRunner.Eq(0, more, "nothing hidden");
            TestRunner.Eq("waiting · needs permission: Bash", Sessions.RowText(rows[0], Now), "waiting text");
            TestRunner.Eq("working · 2m", Sessions.RowText(rows[1], Now), "working text");
            TestRunner.Eq("done · 5 min ago", Sessions.RowText(rows[3], Now), "done text");
            for (int i = 0; i < 4; i++) list.Add(S("x" + i, States.Working, Now - i * 1000, Now, "p", ""));
            Sessions.ForCard(list, Now, out more);
            TestRunner.Eq(3, more, "at most five rows");
        }

        static Reading ClaudeReading()
        {
            var r = new Reading { Provider = "claude", Status = "ok", HeadlineId = "five_hour", Plan = "Max", DataAtMs = Now - Min };
            r.Windows.Add(new LimitWindow { Id = "five_hour", Label = "Current session", Used = 73, ResetsAtMs = Now + 51 * Min });
            r.Windows.Add(new LimitWindow { Id = "seven_day", Label = "Weekly · all models", Used = 7, ResetsAtMs = Now + 300 * Min });
            return r;
        }

        static void BuildsTheClaudeCard()
        {
            var sessions = new List<SessionStatus> { S("w", States.Working, Now, Now - 2 * Min, "confetti", "") };
            CardModel m = CardModel.From(ClaudeReading(), sessions, true, Now, En);
            TestRunner.Eq("Claude", m.Title, "title");
            TestRunner.Eq("Max", m.Plan, "plan");
            TestRunner.Eq("Resets in 51 min", m.Rows[0].ResetText, "reset text");
            TestRunner.Eq("73% used", m.Rows[0].UsedText, "used text");
            TestRunner.Eq(Palette.Red, m.Rows[0].Color, "row colour");
            TestRunner.Eq("confetti", m.SessionRows[0].Project, "session row");
            TestRunner.Eq("working · 2m", m.SessionRows[0].Status, "session status");
            TestRunner.Eq(Palette.Text, m.SessionRows[0].Color, "working rows are white");
            TestRunner.Eq("Updated 1 min ago", m.Footer, "footer");
        }

        static void BuildsTheCodexCardFromLogs()
        {
            var r = new Reading { Provider = "codex", Status = "ok", HeadlineId = "primary", FromLogs = true, DataAtMs = Now - 90 * Min, Note = "Rate limited, retrying in 2 min" };
            r.Windows.Add(new LimitWindow { Id = "primary", Label = "5-hour limit", Used = 0, Note = "reset since last use" });
            CardModel m = CardModel.From(r, new List<SessionStatus>(), true, Now, En);
            TestRunner.Eq("Codex", m.Title, "title");
            TestRunner.Check(m.SessionRows == null, "no sessions for Codex");
            TestRunner.Eq("reset since last use", m.Rows[0].ResetText, "the window note replaces the reset text");
            DateTime at = Clock.FromMs(Now - 90 * Min).ToLocalTime();
            // 90 minutes back crosses local midnight in a few time zones (UTC+10 to +11); the day is named then.
            string day = at.Date == Clock.FromMs(Now).ToLocalTime().Date ? "" : at.ToString("ddd ", En);
            TestRunner.Eq("As of " + day + at.ToString("h:mm tt", En) + " · Rate limited, retrying in 2 min", m.Footer, "footer");
        }

        static void HintsWhenHooksAreNotConnected()
        {
            CardModel m = CardModel.From(ClaudeReading(), new List<SessionStatus>(), false, Now, En);
            TestRunner.Eq(0, m.SessionRows.Count, "no rows");
            TestRunner.Check(m.SessionsHint.StartsWith("Connect to Claude Code", StringComparison.Ordinal), "hint shown");
            var signin = new Reading { Provider = "claude", Status = "signin", Note = "Sign in to the Claude CLI to see your usage" };
            TestRunner.Eq("Sign in to the Claude CLI to see your usage", CardModel.From(signin, null, true, Now, En).Footer, "sign-in footer");
        }

        static void WaitingGoesIdleWhenSilent()
        {
            string dir = TestRunner.NewTempDir();
            File.WriteAllText(Path.Combine(dir, "a.json"), S("a", States.Waiting, Now - 29 * Min, Now - 29 * Min, "p", "needs permission: Bash").ToJson());
            File.WriteAllText(Path.Combine(dir, "b.json"), S("b", States.Waiting, Now - 31 * Min, Now - 40 * Min, "p", "needs permission: Bash").ToJson());
            File.WriteAllText(Path.Combine(dir, "c.json"), S("c", States.Working, Now - 30 * Min, Now - 30 * Min, "p", "").ToJson());
            List<SessionStatus> list = Sessions.Load(dir, Now);
            TestRunner.Eq(States.Waiting, list.Find(s => s.SessionId == "a").State, "waiting 29 min: still waiting");
            TestRunner.Eq(States.Working, list.Find(s => s.SessionId == "c").State, "silent for exactly 30 min: not stale yet");
            SessionStatus b = list.Find(s => s.SessionId == "b");
            TestRunner.Eq(States.Idle, b.State, "silent for 30 min: waiting goes idle too");
            TestRunner.Eq(Now - 31 * Min, b.Since, "idle since the last event");
            TestRunner.Eq(null, Sessions.Aggregate(new List<SessionStatus> { b }), "a silent waiting session stops pulsing");
        }

        static void CleansUpLeftovers()
        {
            string dir = TestRunner.NewTempDir();
            string oldBroken = Path.Combine(dir, "old.json");
            string newBroken = Path.Combine(dir, "new.json");
            string oldTemp = Path.Combine(dir, "e.json.0123abcd.tmp");
            string oldReplaceTemp = Path.Combine(dir, "e.json~RF1a2b3c.TMP");
            string newTemp = Path.Combine(dir, "f.json.4567cdef.tmp");
            foreach (string f in new[] { oldBroken, newBroken, oldTemp, oldReplaceTemp, newTemp }) File.WriteAllText(f, "{half-written");
            foreach (string f in new[] { oldBroken, oldTemp, oldReplaceTemp }) File.SetLastWriteTimeUtc(f, Clock.FromMs(Now - 25 * 60 * Min));
            foreach (string f in new[] { newBroken, newTemp }) File.SetLastWriteTimeUtc(f, Clock.FromMs(Now - Min));
            TestRunner.Eq(0, Sessions.Load(dir, Now).Count, "unreadable files are skipped");
            TestRunner.Check(!File.Exists(oldBroken), "a day-old unreadable status file is deleted");
            TestRunner.Check(File.Exists(newBroken), "a recent unreadable status file is kept (it may be mid-write)");
            TestRunner.Check(!File.Exists(oldTemp) && !File.Exists(oldReplaceTemp), "day-old temp files are deleted");
            TestRunner.Check(File.Exists(newTemp), "a recent temp file is kept");
        }

        static void MissingStartTimeCountsFromTheLastEvent()
        {
            string dir = TestRunner.NewTempDir();
            File.WriteAllText(Path.Combine(dir, "a.json"), "{\"session_id\":\"a\",\"state\":\"working\",\"at\":" + (Now - 2 * Min) + ",\"project\":\"p\"}");
            List<SessionStatus> list = Sessions.Load(dir, Now);
            TestRunner.Eq("working · 2m", Sessions.RowText(list[0], Now), "no start time: counted from the last event");
        }

        static void NamesTheDayOfAnOlderLogSnapshot()
        {
            // Built from local midnight so it holds in every time zone.
            DateTime today = Clock.FromMs(Now).ToLocalTime().Date;
            DateTime earlierToday = today.AddMinutes(1);
            DateTime twoDaysAgo = today.AddDays(-2).AddHours(22);
            var r = new Reading { Provider = "codex", Status = "ok", HeadlineId = "primary", FromLogs = true, DataAtMs = Clock.ToMs(earlierToday) };
            TestRunner.Eq("As of " + earlierToday.ToString("h:mm tt", En), CardModel.From(r, null, true, Now, En).Footer, "a snapshot from today shows the time only");
            r.DataAtMs = Clock.ToMs(twoDaysAgo);
            TestRunner.Eq("As of " + twoDaysAgo.ToString("ddd h:mm tt", En), CardModel.From(r, null, true, Now, En).Footer, "an older snapshot names its day");
            DateTime weekAgo = today.AddDays(-7).AddHours(22);
            r.DataAtMs = Clock.ToMs(weekAgo);
            TestRunner.Eq("As of " + weekAgo.ToString("d", En) + " " + weekAgo.ToString("h:mm tt", En), CardModel.From(r, null, true, Now, En).Footer, "a snapshot a week or more old shows its date");
            r.DataAtMs = 0;
            TestRunner.Eq("Checking…", CardModel.From(r, null, true, Now, En).Footer, "a log reading with no time shows no 'As of'");
        }

        static void ColoursAndCountsSessionRows()
        {
            var sessions = new List<SessionStatus> { S("wait", States.Waiting, Now, Now, "", ""), S("done", States.Done, Now - Min, Now - Min, "p", "") };
            for (int i = 0; i < 5; i++) sessions.Add(S("w" + i, States.Working, Now - i * 1000, Now, "p", ""));
            CardModel m = CardModel.From(ClaudeReading(), sessions, true, Now, En);
            TestRunner.Eq(5, m.SessionRows.Count, "five rows at most");
            TestRunner.Eq(2, m.MoreSessions, "the rest are counted");
            TestRunner.Eq("Claude", m.SessionRows[0].Project, "a session without a project is called Claude");
            TestRunner.Eq("waiting · needs you", m.SessionRows[0].Status, "waiting without a reason");
            TestRunner.Eq(Palette.Amber, m.SessionRows[0].Color, "waiting rows are amber");
        }
    }
}
