using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Capsule
{
    // Usage history (usage history spec): the log readers, the history store and scanner, pace, alerts, the page's model
    // and the page. Every log here is a hand-written fixture; no real transcript is ever read.
    public static class UsageLogsTests
    {
        public static void Run()
        {
            ReadsClaudeReplies();
            ReadsCodexTurns();
            KeepsNoText();
        }

        static List<string> Lines(string fixture) { return TestRunner.Fixture(fixture).Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l != "").ToList(); }

        static void ReadsClaudeReplies()
        {
            List<UsageEntry> e = Lines("claude-transcript.jsonl").Select(UsageLogs.ParseClaude).Where(x => x != null).ToList();
            TestRunner.Eq(5, e.Count, "assistant lines with usage count; the prompt, <synthetic> and a broken line don't");
            TestRunner.Eq("msg_a:req_a", e[0].Key, "a reply's key is its message id and request id");
            TestRunner.Eq(1210L, e[0].Tokens, "Claude tokens: input + cache writes + output");
            TestRunner.Eq(50000L, e[0].CacheRead, "cache reads kept apart");
            TestRunner.Eq("claude-opus-4-1-20250805", e[0].Model, "the model");
            TestRunner.Eq(UsageLogs.Time("2026-10-12T09:00:05.000Z"), e[0].AtMs, "the reply's time");
            TestRunner.Eq("msg_c", e[4].Key, "without a request id the key is the message id");
            TestRunner.Eq(305L, e[2].Tokens, "a reply written again with more output reads its larger numbers");
            TestRunner.Check(UsageLogs.ParseClaude("{\"type\":\"assistant\",\"timestamp\":\"2026-10-12T09:00:05Z\",\"message\":{\"model\":\"claude-opus-4\",\"usage\":{\"output_tokens\":5}}}") == null, "no message id: skipped");
        }

        static void ReadsCodexTurns()
        {
            var state = new CodexFileState();
            List<UsageEntry> e = Lines("codex-usage-rollout.jsonl").Select(l => UsageLogs.ParseCodex(l, state)).Where(x => x != null).ToList();
            TestRunner.Eq(3, e.Count, "token counts with a new running total; a repeat and info:null don't count");
            TestRunner.Eq("codex", e[0].Model, "before any turn_context the model is codex");
            TestRunner.Eq(1050L, e[0].Tokens, "Codex tokens: input − cached + output");
            TestRunner.Eq("gpt-5-codex", e[1].Model, "the latest turn_context's model");
            TestRunner.Eq(700L, e[1].Tokens, "3000 − 2500 + 200");
            TestRunner.Eq(2500L, e[1].CacheRead, "cached input kept apart");
            TestRunner.Eq("gpt-5", e[2].Model, "a model switch mid-file");
            TestRunner.Eq(550L, e[2].Tokens, "600 − 100 + 50");
            TestRunner.Check(e.All(x => x.Key.StartsWith(UsageLogs.CodexKeyPrefix)), "a Codex entry is keyed by its running total");
            TestRunner.Check(e[1].Key != e[2].Key, "different turns, different keys");
            TestRunner.Eq(4600L, state.Totals[0], "the running total is remembered for the next scan");
        }

        static void KeepsNoText()
        {
            foreach (string line in Lines("claude-transcript.jsonl"))
            {
                UsageEntry e = UsageLogs.ParseClaude(line);
                if (e == null) continue;
                string kept = e.Key + e.Model;
                TestRunner.Check(!kept.Contains("secret") && !kept.Contains("reply") && !kept.Contains("prompt"), "only the key and the model are kept from a line");
            }
        }
    }

    public static class UsageHistoryTests
    {
        static readonly long Now = UsageLogs.Time("2026-10-12T12:00:00Z");

        public static void Run()
        {
            CountsEverythingOnce();
            ReadsOnlyWhatIsNew();
            WaitsForAHalfWrittenLine();
            ReReadsAShrunkLogWithoutDoubleCounting();
            IgnoresOldCopiesAfterTheBackfill();
            ReadsWithinItsBudget();
            ForgetsDeletedLogs();
            SavesAndReloads();
            RenamesADamagedFile();
            StoresNoNamesOrText();
            PrunesSeenReplies();
            CountsAForkedCodexChatOnce();
            CountsSeparateCodexChatsOfTheSameSize();
            WaitsForALockedLog();
            CountsArchivedCodexChats();
            KeepsCountsFromVersion150();
        }

        sealed class Setup
        {
            public string Claude, Codex, ClaudeLog, CodexLog;
            public List<LogFile> Logs() { return UsageScanner.Discover(Claude, Codex); }
        }

        static Setup Make()
        {
            string root = TestRunner.NewTempDir();
            var s = new Setup { Claude = Path.Combine(root, ".claude"), Codex = Path.Combine(root, ".codex") };
            string project = Path.Combine(s.Claude, "projects", "C--work-website");
            Directory.CreateDirectory(project);
            s.ClaudeLog = Path.Combine(project, "session-one.jsonl");
            File.WriteAllText(s.ClaudeLog, TestRunner.Fixture("claude-transcript.jsonl").Replace("\r", ""));
            string day = Path.Combine(s.Codex, "sessions", "2026", "10", "12");
            Directory.CreateDirectory(day);
            s.CodexLog = Path.Combine(day, "rollout-2026-10-12T10-00-00-test.jsonl");
            File.WriteAllText(s.CodexLog, TestRunner.Fixture("codex-usage-rollout.jsonl").Replace("\r", ""));
            return s;
        }

        static string Day { get { return UsageScanner.Day(UsageLogs.Time("2026-10-12T09:00:05Z")); } }
        static string CodexDay { get { return UsageScanner.Day(UsageLogs.Time("2026-10-12T10:00:01Z")); } }

        static void CountsEverythingOnce()
        {
            Setup s = Make();
            var store = new UsageStore();
            TestRunner.Check(UsageScanner.Scan(store, s.Logs(), Now, UsageScanner.BudgetBytes), "the first scan changes the store");
            TestRunner.Eq(1515L, store.Get(Day, "claude-opus-4-1-20250805")[0], "Opus: 1210 + 305 (a reply written three times counts once, at its largest)");
            TestRunner.Eq(110000L, store.Get(Day, "claude-opus-4-1-20250805")[1], "Opus cache reads");
            TestRunner.Eq(7L, store.Get(Day, "claude-sonnet-4-5-20250929")[0], "Sonnet");
            TestRunner.Eq(1050L, store.Get(CodexDay, "codex")[0], "Codex before its first turn_context");
            TestRunner.Eq(700L, store.Get(CodexDay, "gpt-5-codex")[0], "GPT-5 Codex");
            TestRunner.Eq(550L, store.Get(CodexDay, "gpt-5")[0], "GPT-5");
            TestRunner.Check(store.BackfillDone, "the backfill is done once every log was read to its end");
            TestRunner.Check(!UsageScanner.Scan(store, s.Logs(), Now + 1000, UsageScanner.BudgetBytes) || store.Get(Day, "claude-opus-4-1-20250805")[0] == 1515, "scanning again counts nothing twice");
            TestRunner.Eq(1515L, store.Get(Day, "claude-opus-4-1-20250805")[0], "still 1515 after a second scan");
        }

        static string Reply(string id, string at, int output)
        {
            return "{\"type\":\"assistant\",\"timestamp\":\"" + at + "\",\"requestId\":\"r" + id + "\",\"message\":{\"id\":\"" + id + "\",\"model\":\"claude-haiku-4-5\",\"usage\":{\"input_tokens\":0,\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":0,\"output_tokens\":" + output + "}}}";
        }

        static void ReadsOnlyWhatIsNew()
        {
            Setup s = Make();
            var store = new UsageStore();
            UsageScanner.Scan(store, s.Logs(), Now, UsageScanner.BudgetBytes);
            File.AppendAllText(s.ClaudeLog, Reply("m1", "2026-10-12T11:00:00Z", 40) + "\n");
            UsageScanner.Scan(store, s.Logs(), Now + 60000, UsageScanner.BudgetBytes);
            UsageScanner.Scan(store, s.Logs(), Now + 120000, UsageScanner.BudgetBytes);
            TestRunner.Eq(40L, store.Get(UsageScanner.Day(UsageLogs.Time("2026-10-12T11:00:00Z")), "claude-haiku-4-5")[0], "an appended reply is counted once");
            TestRunner.Eq(1515L, store.Get(Day, "claude-opus-4-1-20250805")[0], "the rest isn't counted again");
        }

        static void WaitsForAHalfWrittenLine()
        {
            Setup s = Make();
            var store = new UsageStore();
            UsageScanner.Scan(store, s.Logs(), Now, UsageScanner.BudgetBytes);
            string line = Reply("m2", "2026-10-12T11:30:00Z", 25);
            File.AppendAllText(s.ClaudeLog, line.Substring(0, 40));
            UsageScanner.Scan(store, s.Logs(), Now + 60000, UsageScanner.BudgetBytes);
            string day = UsageScanner.Day(UsageLogs.Time("2026-10-12T11:30:00Z"));
            TestRunner.Eq(0L, store.Get(day, "claude-haiku-4-5")[0], "half a line waits");
            File.AppendAllText(s.ClaudeLog, line.Substring(40) + "\n");
            UsageScanner.Scan(store, s.Logs(), Now + 120000, UsageScanner.BudgetBytes);
            TestRunner.Eq(25L, store.Get(day, "claude-haiku-4-5")[0], "once finished, the line counts");
        }

        static void ReReadsAShrunkLogWithoutDoubleCounting()
        {
            Setup s = Make();
            var store = new UsageStore();
            UsageScanner.Scan(store, s.Logs(), Now, UsageScanner.BudgetBytes);
            string[] lines = File.ReadAllText(s.ClaudeLog).Split('\n');
            File.WriteAllText(s.ClaudeLog, lines[1] + "\n");
            UsageScanner.Scan(store, s.Logs(), Now + 60000, UsageScanner.BudgetBytes);
            TestRunner.Eq(1515L, store.Get(Day, "claude-opus-4-1-20250805")[0], "a log rewritten shorter is read again, but its replies aren't counted twice");
        }

        static void IgnoresOldCopiesAfterTheBackfill()
        {
            Setup s = Make();
            var store = new UsageStore();
            UsageScanner.Scan(store, s.Logs(), Now, UsageScanner.BudgetBytes);
            string resumed = Path.Combine(Path.GetDirectoryName(s.ClaudeLog), "session-two.jsonl");
            File.WriteAllText(resumed, Reply("old", "2026-10-07T09:00:00Z", 999) + "\n" + Reply("new", "2026-10-12T11:59:00Z", 11) + "\n");
            UsageScanner.Scan(store, s.Logs(), Now + 60000, UsageScanner.BudgetBytes);
            TestRunner.Eq(0L, store.Get(UsageScanner.Day(UsageLogs.Time("2026-10-07T09:00:00Z")), "claude-haiku-4-5")[0], "a reply from days before the last scan is a resumed copy: ignored");
            TestRunner.Eq(11L, store.Get(UsageScanner.Day(UsageLogs.Time("2026-10-12T11:59:00Z")), "claude-haiku-4-5")[0], "a new reply in the same file counts");

            var fresh = new UsageStore();
            UsageScanner.Scan(fresh, s.Logs(), Now, UsageScanner.BudgetBytes);
            TestRunner.Eq(999L, fresh.Get(UsageScanner.Day(UsageLogs.Time("2026-10-07T09:00:00Z")), "claude-haiku-4-5")[0], "during the backfill old replies count");
        }

        static void ReadsWithinItsBudget()
        {
            Setup s = Make();
            var store = new UsageStore();
            UsageScanner.Scan(store, s.Logs(), Now, 300);
            TestRunner.Check(!store.BackfillDone, "a scan that ran out of budget hasn't finished the backfill");
            TestRunner.Check(store.Progress > 0 && store.Progress < 1, "progress is part way: " + store.Progress);
            for (int i = 0; i < 40 && !store.BackfillDone; i++) UsageScanner.Scan(store, s.Logs(), Now + i, 300);
            TestRunner.Check(store.BackfillDone, "later scans finish it");
            TestRunner.Eq(1515L, store.Get(Day, "claude-opus-4-1-20250805")[0], "in pieces, the totals come out the same");
            TestRunner.Eq(550L, store.Get(CodexDay, "gpt-5")[0], "including Codex's model tracking across pieces");

            Setup one = Make();
            File.Delete(one.CodexLog);
            var big = new System.Text.StringBuilder();
            for (int i = 0; i < 12000; i++) big.Append(Reply("big" + i, "2026-10-12T08:00:00Z", 1)).Append('\n');   // over 1 MB: more than one read block
            File.WriteAllText(one.ClaudeLog, big.ToString());
            var single = new UsageStore();
            UsageScanner.Scan(single, one.Logs(), Now, 300);
            TestRunner.Check(!single.BackfillDone, "the budget running out inside the last log doesn't finish the backfill");
        }

        static string CodexTurn(string at, int input, int output)
        {
            return "{\"timestamp\":\"" + at + "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":" + input + ",\"cached_input_tokens\":0,\"output_tokens\":" + output + ",\"reasoning_output_tokens\":0,\"total_tokens\":" + (input + output) + "},\"last_token_usage\":{\"input_tokens\":" + input + ",\"cached_input_tokens\":0,\"output_tokens\":" + output + ",\"reasoning_output_tokens\":0,\"total_tokens\":" + (input + output) + "}}}}";
        }

        static string CodexDir(Setup s, string name)
        {
            string dir = Path.Combine(s.Codex, name);
            Directory.CreateDirectory(dir);
            return dir;
        }

        static string Meta(string at, string id, string root, string forkedFrom)
        {
            return "{\"timestamp\":\"" + at + "\",\"type\":\"session_meta\",\"payload\":{\"id\":\"" + id + "\"" + (root != null ? ",\"session_id\":\"" + root + "\"" : "") + (forkedFrom != null ? ",\"forked_from_id\":\"" + forkedFrom + "\"" : "") + ",\"cwd\":\"/work\",\"originator\":\"codex_cli_rs\",\"cli_version\":\"0.50.0\"}}";
        }

        static string Context(string at, string model)
        {
            return "{\"timestamp\":\"" + at + "\",\"type\":\"turn_context\",\"payload\":{\"model\":\"" + model + "\"}}";
        }

        static void CountsAForkedCodexChatOnce()
        {
            Setup s = Make();
            File.Delete(s.CodexLog);
            string day = Path.GetDirectoryName(s.CodexLog);
            string parent = Path.Combine(day, "rollout-2026-10-12T10-00-00-parent.jsonl");
            File.WriteAllText(parent, Meta("2026-10-12T10:00:00Z", "p1", "p1", null) + "\n" + Context("2026-10-12T10:00:01Z", "gpt-5-codex") + "\n" + CodexTurn("2026-10-12T10:00:05Z", 100, 50) + "\n");
            // The fork: its own session_meta (same root, forked from p1), a copy of the parent's history written again with
            // new times (the parent's session_meta included), then a turn of its own.
            string child = Path.Combine(day, "rollout-2026-10-12T11-00-00-child.jsonl");
            File.WriteAllText(child, Meta("2026-10-12T11:00:00Z", "c1", "p1", "p1") + "\n" + Meta("2026-10-12T11:00:00Z", "p1", "p1", null) + "\n" + Context("2026-10-12T11:00:00Z", "gpt-5-codex") + "\n"
                + CodexTurn("2026-10-12T11:00:00Z", 100, 50) + "\n" + CodexTurn("2026-10-12T11:00:09Z", 300, 70) + "\n");
            var store = new UsageStore();
            UsageScanner.Scan(store, s.Logs(), Now, UsageScanner.BudgetBytes);
            TestRunner.Eq(150L + 370, store.Get(UsageScanner.Day(UsageLogs.Time("2026-10-12T10:00:05Z")), "gpt-5-codex")[0], "a forked chat's copied turn counts once; its own turn counts (150 + 370, not 670)");
            var later = new UsageStore();
            UsageScanner.Scan(later, s.Logs().Where(l => !l.Path.Contains("child")).ToList(), Now, UsageScanner.BudgetBytes);
            UsageScanner.Scan(later, s.Logs(), Now + 60000, UsageScanner.BudgetBytes);
            TestRunner.Eq(520L, later.Get(UsageScanner.Day(UsageLogs.Time("2026-10-12T10:00:05Z")), "gpt-5-codex")[0], "also when the fork appears after the parent was counted");

            // An older fork without session_id still finds its family through forked_from_id.
            File.WriteAllText(child, Meta("2026-10-12T11:00:00Z", "c1", null, "p1") + "\n" + Context("2026-10-12T11:00:00Z", "gpt-5-codex") + "\n"
                + CodexTurn("2026-10-12T11:00:00Z", 100, 50) + "\n" + CodexTurn("2026-10-12T11:00:09Z", 300, 70) + "\n");
            File.WriteAllText(parent, Meta("2026-10-12T10:00:00Z", "p1", null, null) + "\n" + Context("2026-10-12T10:00:01Z", "gpt-5-codex") + "\n" + CodexTurn("2026-10-12T10:00:05Z", 100, 50) + "\n");
            var legacy = new UsageStore();
            UsageScanner.Scan(legacy, s.Logs(), Now, UsageScanner.BudgetBytes);
            TestRunner.Eq(520L, legacy.Get(UsageScanner.Day(UsageLogs.Time("2026-10-12T10:00:05Z")), "gpt-5-codex")[0], "a fork named only by forked_from_id counts its copy once too");
        }

        static void CountsSeparateCodexChatsOfTheSameSize()
        {
            Setup s = Make();
            File.Delete(s.CodexLog);
            string day = Path.GetDirectoryName(s.CodexLog);
            File.WriteAllText(Path.Combine(day, "rollout-2026-10-12T10-00-00-a.jsonl"), Meta("2026-10-12T10:00:00Z", "a1", "a1", null) + "\n" + Context("2026-10-12T10:00:01Z", "gpt-5-codex") + "\n" + CodexTurn("2026-10-12T10:00:05Z", 100, 50) + "\n");
            File.WriteAllText(Path.Combine(day, "rollout-2026-10-12T12-00-00-b.jsonl"), Meta("2026-10-12T12:00:00Z", "b1", "b1", null) + "\n" + Context("2026-10-12T12:00:01Z", "gpt-5-codex") + "\n" + CodexTurn("2026-10-12T12:00:05Z", 100, 50) + "\n");
            // A third on another day, with another model, again 150 tokens.
            string other = Path.Combine(s.Codex, "sessions", "2026", "10", "10");
            Directory.CreateDirectory(other);
            File.WriteAllText(Path.Combine(other, "rollout-2026-10-10T09-00-00-c.jsonl"), Meta("2026-10-10T09:00:00Z", "d1", "d1", null) + "\n" + Context("2026-10-10T09:00:01Z", "gpt-5") + "\n" + CodexTurn("2026-10-10T09:00:05Z", 100, 50) + "\n");
            // And one with no session_meta at all: known by its own log.
            File.WriteAllText(Path.Combine(day, "rollout-2026-10-12T13-00-00-e.jsonl"), Context("2026-10-12T13:00:01Z", "gpt-5-codex") + "\n" + CodexTurn("2026-10-12T13:00:05Z", 100, 50) + "\n");
            var store = new UsageStore();
            UsageScanner.Scan(store, s.Logs(), Now, UsageScanner.BudgetBytes);
            TestRunner.Eq(450L, store.Get(UsageScanner.Day(UsageLogs.Time("2026-10-12T10:00:05Z")), "gpt-5-codex")[0], "separate chats with equal-sized turns each count (3 × 150, not 150)");
            TestRunner.Eq(150L, store.Get(UsageScanner.Day(UsageLogs.Time("2026-10-10T09:00:05Z")), "gpt-5")[0], "and on another day, with another model");
        }

        static void WaitsForALockedLog()
        {
            Setup s = Make();
            var store = new UsageStore();
            using (new FileStream(s.ClaudeLog, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                UsageScanner.Scan(store, s.Logs(), Now, UsageScanner.BudgetBytes);
            UsageScanner.Scan(store, s.Logs(), Now + 3L * 24 * 3600 * 1000, UsageScanner.BudgetBytes);
            TestRunner.Eq(1515L, store.Get(Day, "claude-opus-4-1-20250805")[0], "a log locked during the first count still counts its older replies once it frees up");

            // Locked for a long time: the rest of the backfill finishes, and the log's history still counts when it frees up.
            Setup t = Make();
            var stuck = new UsageStore();
            using (new FileStream(t.ClaudeLog, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                for (int i = 0; i < 25; i++) UsageScanner.Scan(stuck, t.Logs(), Now + i * 5000L, UsageScanner.BudgetBytes);
            TestRunner.Check(stuck.BackfillDone, "the rest of the backfill doesn't wait for a locked log");
            TestRunner.Eq(700L, stuck.Get(CodexDay, "gpt-5-codex")[0], "the other logs are counted meanwhile");
            UsageScanner.Scan(stuck, t.Logs(), Now + 14L * 24 * 3600 * 1000, UsageScanner.BudgetBytes);
            TestRunner.Eq(1515L, stuck.Get(Day, "claude-opus-4-1-20250805")[0], "unlocked two weeks later, its older replies still count");
            UsageScanner.Scan(stuck, t.Logs(), Now + 15L * 24 * 3600 * 1000, UsageScanner.BudgetBytes);
            TestRunner.Eq(1515L, stuck.Get(Day, "claude-opus-4-1-20250805")[0], "and only once");
        }

        static void CountsArchivedCodexChats()
        {
            Setup s = Make();
            string archived = CodexDir(s, "archived_sessions");
            File.Move(s.CodexLog, Path.Combine(archived, Path.GetFileName(s.CodexLog)));
            var store = new UsageStore();
            UsageScanner.Scan(store, s.Logs(), Now, UsageScanner.BudgetBytes);
            TestRunner.Eq(700L, store.Get(CodexDay, "gpt-5-codex")[0], "a chat archived before Capsule counted it still counts");

            Setup t = Make();
            var moved = new UsageStore();
            UsageScanner.Scan(moved, t.Logs(), Now, UsageScanner.BudgetBytes);
            File.Move(t.CodexLog, Path.Combine(CodexDir(t, "archived_sessions"), Path.GetFileName(t.CodexLog)));
            UsageScanner.Scan(moved, t.Logs(), Now + 60000, UsageScanner.BudgetBytes);
            TestRunner.Eq(700L, moved.Get(CodexDay, "gpt-5-codex")[0], "archiving a counted chat doesn't count it again");
            TestRunner.Eq(2, moved.Files.Count, "and it stays one log in the index");
        }

        static void KeepsCountsFromVersion150()
        {
            Setup s = Make();
            var store = new UsageStore();
            UsageScanner.Scan(store, s.Logs(), Now, UsageScanner.BudgetBytes);
            // As Capsule 1.5.0 left it: Codex logs known by their path, and no Codex turn keys.
            var log = s.Logs().First(l => l.Codex);
            FileMark mark = store.Files[UsageScanner.Key(log)];
            store.Files.Remove(UsageScanner.Key(log));
            store.Files[UsageScanner.Hash(log.Path)] = mark;
            foreach (string k in store.Seen.Keys.Where(k => k.StartsWith(UsageLogs.CodexKeyPrefix)).ToList()) store.Seen.Remove(k);
            UsageScanner.Scan(store, s.Logs(), Now + 60000, UsageScanner.BudgetBytes);
            TestRunner.Eq(700L, store.Get(CodexDay, "gpt-5-codex")[0], "a Codex log counted by 1.5.0 isn't counted again after the update");
        }

        static void ForgetsDeletedLogs()
        {
            Setup s = Make();
            var store = new UsageStore();
            UsageScanner.Scan(store, s.Logs(), Now, UsageScanner.BudgetBytes);
            File.Delete(s.CodexLog);
            UsageScanner.Scan(store, s.Logs(), Now + 1000, UsageScanner.BudgetBytes);
            TestRunner.Eq(1, store.Files.Count, "a deleted log leaves the index");
            TestRunner.Eq(700L, store.Get(CodexDay, "gpt-5-codex")[0], "its counted days stay");
        }

        static void SavesAndReloads()
        {
            Setup s = Make();
            var store = new UsageStore();
            UsageScanner.Scan(store, s.Logs(), Now, UsageScanner.BudgetBytes);
            UsageStore back = UsageStore.FromJson(Json.Write(store.ToJson()));
            TestRunner.Check(back != null, "the saved store reads back");
            TestRunner.Eq(1515L, back.Get(Day, "claude-opus-4-1-20250805")[0], "day totals survive");
            TestRunner.Eq(store.Files.Count, back.Files.Count, "the file index survives");
            TestRunner.Eq("gpt-5", back.Files[UsageScanner.Key(new LogFile { Path = s.CodexLog, Codex = true })].Codex.Model, "a Codex log's model survives");
            TestRunner.Check(back.Seen.ContainsKey("msg_b:req_b") && back.BackfillDone, "seen replies and the backfill flag survive");
            File.AppendAllText(s.CodexLog, "{\"timestamp\":\"2026-10-12T11:00:00.000Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":4600,\"cached_input_tokens\":2600,\"output_tokens\":300},\"last_token_usage\":{\"input_tokens\":600,\"cached_input_tokens\":100,\"output_tokens\":50}}}}\n");
            UsageScanner.Scan(back, s.Logs(), Now + 1000, UsageScanner.BudgetBytes);
            TestRunner.Eq(550L, back.Get(CodexDay, "gpt-5")[0], "after reloading, a repeated Codex total still isn't counted");
        }

        static void RenamesADamagedFile()
        {
            string path = Path.Combine(TestRunner.NewTempDir(), "usage-history.json");
            File.WriteAllText(path, "{ not json");
            UsageStore s = UsageStore.Load(path);
            TestRunner.Check(s.Days.Count == 0 && !s.BackfillDone, "a damaged store starts over");
            TestRunner.Check(File.Exists(path + ".bad") && !File.Exists(path), "and is kept aside as .bad");
            TestRunner.Check(UsageStore.Load(Path.Combine(TestRunner.NewTempDir(), "none.json")).Days.Count == 0, "no file: a fresh store");
        }

        static void StoresNoNamesOrText()
        {
            Setup s = Make();
            var store = new UsageStore();
            UsageScanner.Scan(store, s.Logs(), Now, UsageScanner.BudgetBytes);
            string saved = Json.Write(store.ToJson());
            TestRunner.Check(!saved.Contains("website") && !saved.Contains("projects") && !saved.Contains("session-one") && !saved.Contains("work"), "no folder or project names");
            TestRunner.Check(!saved.Contains("never be stored") && !saved.Contains("secret") && !saved.Contains("part one"), "no prompt, reply or tool text");
        }

        static void PrunesSeenReplies()
        {
            Setup s = Make();
            var store = new UsageStore();
            UsageScanner.Scan(store, s.Logs(), Now, UsageScanner.BudgetBytes);
            Func<int> claude = () => store.Seen.Keys.Count(k => !k.StartsWith(UsageLogs.CodexKeyPrefix));
            Func<int> codex = () => store.Seen.Keys.Count(k => k.StartsWith(UsageLogs.CodexKeyPrefix));
            TestRunner.Check(claude() == 3 && codex() == 3, "seen replies and Codex turns are kept while recent");
            UsageScanner.Scan(store, s.Logs(), Now + 4L * 24 * 3600 * 1000, UsageScanner.BudgetBytes);
            TestRunner.Check(claude() == 0 && codex() == 3, "Claude replies are dropped after 3 days; Codex turns stay, a fork can come later");
            UsageScanner.Scan(store, s.Logs(), Now + 36L * 24 * 3600 * 1000, UsageScanner.BudgetBytes);
            TestRunner.Eq(0, codex(), "and go after 35 days");
        }
    }

    public static class PaceTests
    {
        const long Min = 60 * 1000, Hour = 60 * Min, DayMs = 24 * Hour;
        static readonly long Now = UsageLogs.Time("2026-10-12T12:00:00Z");
        static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

        public static void Run()
        {
            FullBeforeTheReset();
            AboutAtTheReset();
            NeedsEnoughToGoOn();
            ReachedAndNewWindows();
            WeeklyUsesADay();
            PicksTheReference();
            KnowsWindowLengths();
            WritesTheCardLine();
            ComparesWithAnEvenPace();
        }

        static List<UsageSample> S(long reset, params double[] pairs)   // minutes before Now, used; ...
        {
            var list = new List<UsageSample>();
            for (int i = 0; i < pairs.Length; i += 2) list.Add(new UsageSample { Provider = "claude", Window = "five_hour", AtMs = Now - (long)(pairs[i] * Min), Used = pairs[i + 1], ResetMs = reset });
            return list;
        }

        static LimitWindow W(double used, long reset, string id = "five_hour") { return new LimitWindow { Id = id, Used = used, ResetsAtMs = reset }; }

        static void FullBeforeTheReset()
        {
            long reset = Now + 2 * Hour;
            Forecast f = Pace.For(S(reset, 60, 30, 0, 60), W(60, reset), 5 * Hour, Now);
            TestRunner.Eq(ForecastKind.Full, f.Kind, "30 points an hour from 60%: full before a reset 2 hours away");
            TestRunner.Eq(Now + 80 * Min, f.FullAtMs, "full in 80 minutes");
        }

        static void AboutAtTheReset()
        {
            long reset = Now + 2 * Hour;
            Forecast f = Pace.For(S(reset, 60, 50, 0, 55), W(55, reset), 5 * Hour, Now);
            TestRunner.Eq(ForecastKind.AtReset, f.Kind, "5 points an hour: not full before the reset");
            TestRunner.Eq(65, f.AtResetPercent, "about 65% at the reset");
        }

        static void NeedsEnoughToGoOn()
        {
            long reset = Now + 2 * Hour;
            TestRunner.Eq(ForecastKind.None, Pace.For(S(reset, 10, 30, 0, 60), W(60, reset), 5 * Hour, Now).Kind, "under 15 minutes of samples: no forecast");
            TestRunner.Eq(ForecastKind.None, Pace.For(S(reset, 60, 59, 0, 60), W(60, reset), 5 * Hour, Now).Kind, "a rise under 2 points: no forecast");
            TestRunner.Eq(ForecastKind.None, Pace.For(S(reset, 0, 60), W(60, reset), 5 * Hour, Now).Kind, "one sample: no forecast");
        }

        static void ReachedAndNewWindows()
        {
            long reset = Now + 2 * Hour;
            TestRunner.Eq(ForecastKind.Reached, Pace.For(S(reset, 60, 50, 0, 100), W(100, reset), 5 * Hour, Now).Kind, "at 100%: reached");
            var store = new SampleStore();
            store.Samples.AddRange(S(Now - Hour, 120, 20));            // an earlier window instance
            store.Samples.AddRange(S(reset, 30, 5, 0, 30));
            TestRunner.Eq(2, store.For("claude", "five_hour", reset).Count, "only this window instance's samples count");
            TestRunner.Eq(ForecastKind.Full, Pace.For(store, "claude", W(30, reset), Now).Kind, "25 points in 30 minutes: full before the reset");
        }

        static void WeeklyUsesADay()
        {
            long reset = Now + 5 * DayMs;
            var list = new List<UsageSample>
            {
                new UsageSample { Provider = "claude", Window = "seven_day", AtMs = Now - 30 * Hour, Used = 5, ResetMs = reset },
                new UsageSample { Provider = "claude", Window = "seven_day", AtMs = Now - DayMs, Used = 10, ResetMs = reset },
                new UsageSample { Provider = "claude", Window = "seven_day", AtMs = Now, Used = 20, ResetMs = reset },
            };
            Forecast f = Pace.For(list, W(20, reset, "seven_day"), 7 * DayMs, Now);
            TestRunner.Eq(ForecastKind.AtReset, f.Kind, "10 points a day, 5 days to go");
            TestRunner.Eq(70, f.AtResetPercent, "about 70% at the reset");
            var shortList = list.Skip(2).ToList();
            shortList.Insert(0, new UsageSample { Provider = "claude", Window = "seven_day", AtMs = Now - Hour, Used = 18, ResetMs = reset });
            TestRunner.Eq(ForecastKind.None, Pace.For(shortList, W(20, reset, "seven_day"), 7 * DayMs, Now).Kind, "a weekly limit needs 2 hours of samples");
        }

        static void PicksTheReference()
        {
            long reset = Now + 3 * Hour;
            Forecast f = Pace.For(S(reset, 90, 20, 60, 30, 30, 40, 0, 50), W(50, reset), 5 * Hour, Now);
            TestRunner.Eq(Now + (long)(2.5 * Hour), f.FullAtMs, "the rate over the last hour (20 points), not since 90 minutes ago");
        }

        static void KnowsWindowLengths()
        {
            TestRunner.Eq(5 * Hour, Pace.LengthMs("claude", W(0, 0)), "Claude five_hour");
            TestRunner.Eq(7 * DayMs, Pace.LengthMs("claude", W(0, 0, "seven_day_opus")), "Claude seven_day_opus");
            TestRunner.Eq(5 * Hour, Pace.LengthMs("codex", new LimitWindow { Id = "primary", Label = "5-hour limit" }), "Codex 5-hour");
            TestRunner.Eq(7 * DayMs, Pace.LengthMs("codex", new LimitWindow { Id = "secondary", Label = "Weekly limit" }), "Codex weekly");
            TestRunner.Eq(3 * DayMs, Pace.LengthMs("codex", new LimitWindow { Id = "secondary", Label = "3-day limit" }), "Codex n-day");
        }

        static void WritesTheCardLine()
        {
            long at = new DateTime(2026, 10, 12, 16, 10, 0, DateTimeKind.Local).ToUniversalTime().Subtract(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).Ticks / 10000;
            TestRunner.Eq("At this pace: full at 4:10 PM", Pace.CardLine(new Forecast { Kind = ForecastKind.Full, FullAtMs = at, LengthMs = 5 * Hour }, En), "full, amber");
            TestRunner.Eq("At this pace: full Mon 4:10 PM", Pace.CardLine(new Forecast { Kind = ForecastKind.Full, FullAtMs = at, LengthMs = 7 * DayMs }, En), "a weekly limit names the day");
            TestRunner.Eq("At this pace: about 64% at reset", Pace.CardLine(new Forecast { Kind = ForecastKind.AtReset, AtResetPercent = 64 }, En), "about, at reset");
            TestRunner.Eq(null, Pace.CardLine(new Forecast(), En), "no forecast, no line");
            TestRunner.Eq("At this pace: full at 4:10 PM · locked 41 min", Pace.CardLine(new Forecast { Kind = ForecastKind.Full, FullAtMs = at, ResetMs = at + 41 * Min, LengthMs = 5 * Hour }, En), "full early: how long it stays locked");
            TestRunner.Eq("At this pace: full Mon 4:10 PM · locked 2 days", Pace.CardLine(new Forecast { Kind = ForecastKind.Full, FullAtMs = at, ResetMs = at + 2 * DayMs, LengthMs = 7 * DayMs }, En), "a weekly lockout in days");
            TestRunner.Eq("81% of the window gone · 1.1× even pace", Pace.CardLine(new Forecast { Kind = ForecastKind.Even, GonePercent = 81, EvenRatio = 90 / 81.0 }, En), "even pace, one decimal");
            TestRunner.Eq("81% of the window gone · 1.0× even pace", Pace.CardLine(new Forecast { Kind = ForecastKind.Even, GonePercent = 81, EvenRatio = 85 / 81.0 }, En), "just over even rounds to 1.0");

            var r = new Reading { Provider = "claude", Status = "ok", DataAtMs = Now };
            r.Windows.Add(new LimitWindow { Id = "five_hour", Label = "Current session", Used = 60, ResetsAtMs = Now + 2 * Hour });
            CardModel m = CardModel.From(r, null, true, Now, En, w => new Forecast { Kind = ForecastKind.Full, FullAtMs = at, LengthMs = 5 * Hour });
            TestRunner.Check(m.Rows[0].Pace == "At this pace: full at 4:10 PM" && m.Rows[0].PaceWarn, "the hover card's row carries the pace line");
            TestRunner.Eq("", CardModel.From(r, null, true, Now, En).Rows[0].Pace, "without a forecast the row has none");

            var store = new SampleStore();
            store.Samples.AddRange(S(Now + 2 * Hour, 60, 30, 0, 60));
            var fresh = new Reading { Provider = "claude", Status = "ok", DataAtMs = Now };
            fresh.Windows.Add(W(60, Now + 2 * Hour));
            TestRunner.Eq(ForecastKind.Full, Pace.Shown(store, fresh, fresh.Windows[0], Now).Kind, "fresh numbers: the forecast shows");
            Reading failed = fresh.Clone();
            failed.LastCheckFailed = true;
            TestRunner.Eq(ForecastKind.None, Pace.Shown(store, failed, failed.Windows[0], Now + 11 * Min).Kind, "after a failed check, no forecast from the old numbers");
        }

        static void ComparesWithAnEvenPace()
        {
            // A 5-hour window resetting in 1 hour is 80% gone; 40% used is half the even pace.
            var r = new Reading { Provider = "claude", Status = "ok", DataAtMs = Now };
            r.Windows.Add(W(40, Now + Hour));
            Forecast f = Pace.Shown(new SampleStore(), r, r.Windows[0], Now);
            TestRunner.Eq(ForecastKind.Even, f.Kind, "no samples yet: the even-pace comparison shows");
            TestRunner.Eq(80, f.GonePercent, "80% of the window gone");
            TestRunner.Check(Math.Abs(f.EvenRatio - 0.5) < 1e-9, "40% used in 80% of the window is 0.5x");

            var store = new SampleStore();
            store.Samples.AddRange(S(Now + 2 * Hour, 60, 30, 0, 60));
            var paced = new Reading { Provider = "claude", Status = "ok", DataAtMs = Now };
            paced.Windows.Add(W(60, Now + 2 * Hour));
            TestRunner.Eq(ForecastKind.Full, Pace.Shown(store, paced, paced.Windows[0], Now).Kind, "a real pace wins over the even comparison");

            Func<double, long, ForecastKind> kind = (used, resetIn) =>
            {
                var x = new Reading { Provider = "claude", Status = "ok", DataAtMs = Now };
                x.Windows.Add(W(used, Now + resetIn));
                return Pace.Shown(new SampleStore(), x, x.Windows[0], Now).Kind;
            };
            TestRunner.Eq(ForecastKind.None, kind(0, Hour), "an unused limit: nothing");
            TestRunner.Eq(ForecastKind.None, kind(10, 5 * Hour - 10 * Min), "in the first 5% of the window: nothing");
            var odd = new Reading { Provider = "codex", Status = "ok", DataAtMs = Now };
            odd.Windows.Add(new LimitWindow { Id = "primary", Label = "Odd limit", Used = 40, ResetsAtMs = Now + Hour });
            TestRunner.Eq(ForecastKind.None, Pace.Shown(new SampleStore(), odd, odd.Windows[0], Now).Kind, "an unknown window length: nothing");
        }
    }

    public static class LimitAlertsTests
    {
        const long Min = 60 * 1000, Hour = 60 * Min;
        static readonly long Now = UsageLogs.Time("2026-10-12T12:00:00Z");
        static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");
        static readonly long Reset = Now + 3 * Hour;

        public static void Run()
        {
            ThresholdsFireOnce();
            StartingHighOnlySays95();
            PaceNeedsTwoInARow();
            QuietWhenOffOrStale();
            RemembersWhatWasSent();
            NamesAndGaps();
        }

        static Reading R(double used, long at, string provider = "claude")
        {
            var r = new Reading { Provider = provider, Status = "ok", DataAtMs = at };
            r.Windows.Add(provider == "claude"
                ? new LimitWindow { Id = "five_hour", Label = "Current session", Used = used, ResetsAtMs = Reset }
                : new LimitWindow { Id = "primary", Label = "5-hour limit", Used = used, ResetsAtMs = Reset });
            return r;
        }

        static List<string> Feed(SampleStore s, Reading r, bool enabled = true)
        {
            s.Record(r, r.DataAtMs);
            return LimitAlerts.Check(s, r, enabled, r.DataAtMs, En);
        }

        static void ThresholdsFireOnce()
        {
            var s = new SampleStore();
            List<string> first = Feed(s, R(82, Now));
            TestRunner.Check(first.Count == 1 && first[0].StartsWith("Claude's 5-hour limit is at 82%. It resets at ", StringComparison.Ordinal), "80%: " + string.Join(" | ", first));
            TestRunner.Eq(0, Feed(s, R(84, Now + Min)).Count, "not again in the same window");
            List<string> high = Feed(s, R(96, Now + 2 * Min));
            TestRunner.Check(high.Any(m => m.StartsWith("Claude's 5-hour limit is at 96%", StringComparison.Ordinal)), "95%");
            TestRunner.Eq(0, Feed(s, R(97, Now + 3 * Min)).Count, "and 95% only once");
        }

        static void StartingHighOnlySays95()
        {
            var s = new SampleStore();
            List<string> m = Feed(s, R(96, Now));
            TestRunner.Check(m.Count == 1 && m[0].Contains("96%"), "first reading at 96%: only the 95% alert");
            TestRunner.Eq(0, Feed(s, R(96, Now + Min)).Count, "and no 80% after it");
        }

        static void PaceNeedsTwoInARow()
        {
            var s = new SampleStore();
            TestRunner.Eq(0, Feed(s, R(30, Now - 60 * Min)).Count, "one sample: nothing");
            TestRunner.Eq(0, Feed(s, R(45, Now - 30 * Min)).Count, "the first full-before-reset forecast: not yet");
            List<string> m = Feed(s, R(60, Now));
            TestRunner.Check(m.Count == 1 && m[0].StartsWith("At this pace you'll hit Claude's 5-hour limit at ", StringComparison.Ordinal) && m[0].EndsWith(" before it resets.", StringComparison.Ordinal), "the second in a row: the pace warning: " + string.Join(" | ", m));
            TestRunner.Eq(0, Feed(s, R(75, Now + 30 * Min)).Count, "only once per window");

            var low = new SampleStore();
            Feed(low, R(5, Now - 60 * Min));
            Feed(low, R(15, Now - 30 * Min));
            TestRunner.Eq(0, Feed(low, R(25, Now)).Count, "under 30%: no pace warning");
        }

        static void QuietWhenOffOrStale()
        {
            var s = new SampleStore();
            TestRunner.Eq(0, Feed(s, R(90, Now), false).Count, "alerts off: nothing");
            Reading stale = R(90, Now + Min);
            stale.LastCheckFailed = true;
            var t = new SampleStore();
            TestRunner.Eq(0, Feed(t, stale).Count, "a failed check's numbers: nothing");
            TestRunner.Eq(0, t.Samples.Count, "and no sample");
        }

        static void RemembersWhatWasSent()
        {
            string path = Path.Combine(TestRunner.NewTempDir(), "usage-samples.json");
            var s = new SampleStore();
            Feed(s, R(85, Now));
            s.Save(path);
            SampleStore back = SampleStore.Load(path);
            TestRunner.Eq(1, back.Samples.Count, "samples survive a restart");
            TestRunner.Eq(0, LimitAlerts.Check(back, R(86, Now + Min), true, Now + Min, En).Count, "an alert already shown isn't shown again after a restart");
            File.WriteAllText(path, "nope");
            TestRunner.Check(SampleStore.Load(path).Samples.Count == 0 && File.Exists(path + ".bad"), "a damaged file starts over, kept as .bad");
        }

        static void NamesAndGaps()
        {
            TestRunner.Eq("weekly Opus limit", LimitAlerts.Name("claude", new LimitWindow { Id = "seven_day_opus" }), "Claude weekly Opus");
            TestRunner.Eq("weekly limit", LimitAlerts.Name("claude", new LimitWindow { Id = "seven_day" }), "Claude weekly");
            TestRunner.Eq("5-hour limit", LimitAlerts.Name("codex", new LimitWindow { Id = "primary", Label = "5-hour limit" }), "Codex");
            List<string> codex = Feed(new SampleStore(), R(81, Now, "codex"));
            TestRunner.Check(codex.Count == 1 && codex[0].StartsWith("Codex's 5-hour limit is at 81%", StringComparison.Ordinal), "Codex's name in its alert");
            TestRunner.Eq("41 min", LimitAlerts.Gap(41 * Min), "minutes");
            TestRunner.Eq("3 hr", LimitAlerts.Gap(3 * Hour), "hours");
            TestRunner.Eq("a day", LimitAlerts.Gap(26 * Hour), "a day");
            TestRunner.Eq("2 days", LimitAlerts.Gap(50 * Hour), "days");
        }
    }

    public static class UsageModelTests
    {
        static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US"), Gb = CultureInfo.GetCultureInfo("en-GB");
        static readonly DateTime Today = new DateTime(2026, 10, 14);   // a Wednesday

        public static void Run()
        {
            FormatsTokens();
            NamesModels();
            ColoursByFamily();
            WeeksStartByCulture();
            BuildsAWeek();
            KeepsSixInTheLegend();
            BuildsMonthsAndYears();
        }

        static void FormatsTokens()
        {
            TestRunner.Eq("950", Format.Tokens(950), "under a thousand");
            TestRunner.Eq("9.9K", Format.Tokens(9900), "one decimal under 10");
            TestRunner.Eq("10K", Format.Tokens(9960), "rounding up to 10 drops the decimal");
            TestRunner.Eq("12K", Format.Tokens(12000), "none from 10");
            TestRunner.Eq("640K", Format.Tokens(640000), "hundreds of thousands");
            TestRunner.Eq("1.7M", Format.Tokens(1700000), "millions");
            TestRunner.Eq("1M", Format.Tokens(999999), "999,999 rounds to 1M, not 1000K");
            TestRunner.Eq("2.3B", Format.Tokens(2300000000), "billions");
        }

        static void NamesModels()
        {
            TestRunner.Eq("Opus 4.1", UsageModel.ModelName("claude-opus-4-1-20250805"), "family, major.minor, date dropped");
            TestRunner.Eq("Sonnet 4", UsageModel.ModelName("claude-sonnet-4-20250514"), "family, major");
            TestRunner.Eq("Sonnet 4.5", UsageModel.ModelName("claude-sonnet-4-5"), "no date");
            TestRunner.Eq("Sonnet 3.5", UsageModel.ModelName("claude-3-5-sonnet-20241022"), "the older order");
            TestRunner.Eq("GPT-5 Codex", UsageModel.ModelName("gpt-5-codex"), "GPT with a suffix");
            TestRunner.Eq("GPT-5", UsageModel.ModelName("gpt-5"), "GPT");
            TestRunner.Eq("Codex", UsageModel.ModelName("codex"), "Codex before a model was known");
            TestRunner.Eq("mystery-model", UsageModel.ModelName("mystery-model"), "anything else as is");
        }

        static void ColoursByFamily()
        {
            var c = UsageModel.Colors(new List<string> { "claude-opus-4-1-20250805", "claude-opus-4-20250514", "claude-sonnet-4-5", "gpt-5-codex", "gpt-5", "mystery" }, true);
            TestRunner.Eq("#0A84FF", c["claude-opus-4-1-20250805"], "the newest Opus in Opus blue");
            TestRunner.Eq(UsageModel.Shade("#0A84FF", 0.18, true), c["claude-opus-4-20250514"], "an older Opus a step lighter");
            TestRunner.Eq("#BF5AF2", c["claude-sonnet-4-5"], "Sonnet purple");
            TestRunner.Eq("#FF9F0A", c["gpt-5-codex"], "GPT-5 Codex orange");
            TestRunner.Eq("#30D158", c["gpt-5"], "GPT-5 green");
            TestRunner.Eq("#FF375F", c["mystery"], "others from the fixed list");
            TestRunner.Check(UsageModel.Shade("#0A84FF", 0.18, false) != c["claude-opus-4-20250514"], "darker, not lighter, in the light theme");
        }

        static void WeeksStartByCulture()
        {
            DateTime from, to;
            UsageModel.Range(UsagePeriod.Week, 0, Today, En, out from, out to);
            TestRunner.Check(from == new DateTime(2026, 10, 11) && to == new DateTime(2026, 10, 17), "en-US weeks start on Sunday");
            UsageModel.Range(UsagePeriod.Week, 0, Today, Gb, out from, out to);
            TestRunner.Eq(new DateTime(2026, 10, 12), from, "en-GB weeks start on Monday");
            UsageModel.Range(UsagePeriod.Week, -1, Today, Gb, out from, out to);
            TestRunner.Eq(new DateTime(2026, 10, 5), from, "‹ steps back a week");
        }

        static UsageStore Store()
        {
            var s = new UsageStore { BackfillDone = true };
            s.Add("2026-10-12", "claude-opus-4-1-20250805", 600000, 9000000);
            s.Add("2026-10-12", "gpt-5-codex", 100000, 50000);
            s.Add("2026-10-14", "claude-opus-4-1-20250805", 400000, 1000000);
            s.Add("2026-10-14", "claude-sonnet-4-5", 300000, 0);
            s.Add("2026-09-14", "claude-sonnet-4-5", 10, 0);
            return s;
        }

        static void BuildsAWeek()
        {
            UsageModel m = UsageModel.Build(Store(), UsagePeriod.Week, 0, Today, Gb, true);
            TestRunner.Eq("Oct 12 – 18", m.Title, "the week's title");
            TestRunner.Eq(7, m.Bars.Count, "seven days");
            TestRunner.Eq("Mon", m.Bars[0].Label, "from Monday");
            TestRunner.Eq(1400000L, m.Total, "the week's total, without cache reads");
            TestRunner.Check(m.Bars[2].IsToday && !m.Bars[2].Future && m.Bars[3].Future, "today, and the days still to come");
            TestRunner.Eq(700000L, m.Bars[0].Total, "Monday's bar");
            TestRunner.Eq("Opus 4.1", m.Bars[0].Segments[0].Model, "the biggest model at the bottom");
            TestRunner.Check(m.Legend.Select(l => l.Name).SequenceEqual(new[] { "Opus 4.1", "Sonnet 4.5", "GPT-5 Codex" }), "the legend, biggest first");
            TestRunner.Eq(1000000L, m.Legend[0].Tokens, "a model's period total");
            TestRunner.Check(m.Details.Contains("Claude 1.3M · 10M cache reads") && m.Details.Contains("Codex 100K · 50K cached"), "each tool's tokens and cache: " + string.Join(" | ", m.Details));
            TestRunner.Check(m.Details.Contains("Busiest day: Mon, Oct 12 · 700K") && m.Details.Contains("Daily average: 467K"), "busiest day, and the average over the days so far");
            TestRunner.Check(m.Details.Last().StartsWith("Counted from Claude Code and Codex logs on this PC since Sep 14", StringComparison.Ordinal), "where the numbers come from");
            TestRunner.Check(m.Bars[0].Tip.Contains("Opus 4.1 · 600K"), "a bar's tooltip lists its models");
            TestRunner.Check(!m.CanNext && UsageModel.Build(Store(), UsagePeriod.Week, -1, Today, Gb, true).CanNext, "› only back from the past");
            TestRunner.Eq("", m.Progress, "no progress line after the backfill");
            var reading = new UsageStore();
            TestRunner.Check(UsageModel.Build(reading, UsagePeriod.Week, 0, Today, Gb, true).Progress.StartsWith("Reading your logs…", StringComparison.Ordinal), "during the backfill: Reading your logs…");
            TestRunner.Check(UsageModel.Build(new UsageStore { BackfillDone = true }, UsagePeriod.Week, 0, Today, Gb, true).Empty, "nothing counted at all: empty");
        }

        static void KeepsSixInTheLegend()
        {
            var s = new UsageStore { BackfillDone = true };
            for (int i = 0; i < 8; i++) s.Add("2026-10-14", "model-" + i, 1000 * (10 - i), 0);
            UsageModel m = UsageModel.Build(s, UsagePeriod.Week, 0, Today, Gb, true);
            TestRunner.Eq(6, m.Legend.Count, "six rows at most");
            TestRunner.Eq(UsageModel.OtherName, m.Legend[5].Name, "the rest as Other");
            TestRunner.Eq(5000L + 4000 + 3000, m.Legend[5].Tokens, "Other sums them");
            TestRunner.Eq(6, m.Bars[2].Segments.Count, "the bar has the same six parts");
        }

        static void BuildsMonthsAndYears()
        {
            UsageModel month = UsageModel.Build(Store(), UsagePeriod.Month, 0, Today, En, true);
            TestRunner.Eq("October 2026", month.Title, "the month's title");
            TestRunner.Eq(31, month.Bars.Count, "a bar a day");
            TestRunner.Check(month.Bars[0].Label == "1" && month.Bars[7].Label == "8" && month.Bars[1].Label == "", "labels every 7 days");
            UsageModel previous = UsageModel.Build(Store(), UsagePeriod.Month, -1, Today, En, true);
            TestRunner.Check(previous.Title == "September 2026" && previous.Total == 10, "‹ to September");
            UsageModel year = UsageModel.Build(Store(), UsagePeriod.Year, 0, Today, En, true);
            TestRunner.Eq("2026", year.Title, "the year's title");
            TestRunner.Eq(12, year.Bars.Count, "a bar a month");
            TestRunner.Check(year.Bars[9].Total == 1400000 && year.Bars[9].IsToday && year.Bars[10].Future && year.Bars[8].Total == 10, "October's bar sums its days; November is still to come");
        }
    }

    public static class UsageViewTests
    {
        public static void Run()
        {
            OpensFromATileAndGoesBack();
            DrawsTheModel();
        }

        static void Lay(FrameworkElement e)
        {
            e.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            e.Arrange(new Rect(e.DesiredSize));
            e.UpdateLayout();
        }

        static TextBlock FindText(DependencyObject root, string text)
        {
            var e = root as UIElement;
            if (e != null && e.Visibility != Visibility.Visible) return null;
            var t = root as TextBlock;
            if (t != null && t.Text == text) return t;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                TextBlock found = FindText(VisualTreeHelper.GetChild(root, i), text);
                if (found != null) return found;
            }
            return null;
        }

        static FrameworkElement Named(DependencyObject root, string name)
        {
            var e = root as FrameworkElement;
            if (e != null && e.Visibility != Visibility.Visible) return null;
            if (e != null && System.Windows.Automation.AutomationProperties.GetName(e) == name) return e;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                FrameworkElement found = Named(VisualTreeHelper.GetChild(root, i), name);
                if (found != null) return found;
            }
            return null;
        }

        static void Click(UIElement target)
        {
            if (target == null) return;
            target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent, Source = target });
        }

        static UsageModel Sample()
        {
            var s = new UsageStore { BackfillDone = true };
            s.Add("2026-10-12", "claude-opus-4-1-20250805", 600000, 9000000);
            s.Add("2026-10-14", "gpt-5-codex", 100000, 50000);
            UsageModel m = UsageModel.Build(s, UsagePeriod.Week, 0, new DateTime(2026, 10, 14), CultureInfo.GetCultureInfo("en-GB"), true);
            m.Pace.Add(new PaceLine { Text = "Claude · 5-hour · 73% · full at 4:10 PM", Warn = true });
            return m;
        }

        static void OpensFromATileAndGoesBack()
        {
            const long now = 1790690940000;
            var en = CultureInfo.GetCultureInfo("en-US");
            var view = new PanelView();
            view.Update(new PanelModel
            {
                Claude = UsageTile.From(new Reading { Provider = "claude", Status = "none" }, now, en),
                Codex = UsageTile.From(new Reading { Provider = "codex", Status = "none" }, now, en),
                Sessions = SessionsTile.From(new List<SessionStatus>(), true, now),
                Ideas = new IdeasTile { Configured = true },
            });
            Lay(view);
            string clicked = null;
            int closed = 0;
            view.UsageClicked += delegate(string p) { clicked = p; };
            view.UsageClosed += delegate { closed++; };
            Border codexTile = null;
            for (DependencyObject t = FindText(view, "Codex"); t != null; t = VisualTreeHelper.GetParent(t))
                if (t is Border && ((Border)t).ToolTip as string == "Usage history") { codexTile = (Border)t; break; }
            Click(codexTile);
            TestRunner.Eq("codex", clicked, "clicking the Codex tile asks for the Usage page");
            view.ShowUsage(Sample());
            Lay(view);
            TestRunner.Check(view.ShowingUsage && FindText(view, "Usage") != null && FindText(view, "Oct 12 – 18") != null, "the Usage page in the tiles' place");
            Click(Named(view, "Back"));
            TestRunner.Check(!view.ShowingUsage && closed == 1, "← goes back to the tiles");
        }

        static void DrawsTheModel()
        {
            var view = new UsageView();
            UsagePeriod? chosen = null;
            int previous = 0, next = 0;
            view.PeriodChosen += delegate(UsagePeriod p) { chosen = p; };
            view.PreviousClicked += delegate { previous++; };
            view.NextClicked += delegate { next++; };
            view.Update(Sample());
            Lay(view);
            TestRunner.Check(FindText(view, "700K") != null && FindText(view, "tokens") != null, "the total");
            TestRunner.Check(FindText(view, "Opus 4.1") != null && FindText(view, "600K") != null && FindText(view, "GPT-5 Codex") != null, "the legend");
            TestRunner.Check(FindText(view, "Claude · 5-hour · 73% · full at 4:10 PM") != null, "the pace line");
            TestRunner.Check(FindText(view, "Mon") != null && FindText(view, "Sun") != null, "the days under the bars");
            TestRunner.Check(FindText(view, "Daily average: 233K") == null, "Details start closed");
            Click(Named(view, "Details"));
            Lay(view);
            TestRunner.Check(view.DetailsOpen && FindText(view, "Daily average: 233K") != null, "Details open: " + string.Join(" | ", view.Model.Details));
            Click(Named(view, "Month"));
            Click(Named(view, "Previous"));
            Click(Named(view, "Next"));
            TestRunner.Check(chosen == UsagePeriod.Month && previous == 1 && next == 0, "Month and ‹ are handed over; › does nothing in the current period");

            var empty = new UsageView();
            empty.Update(UsageModel.Build(new UsageStore { BackfillDone = true }, UsagePeriod.Week, 0, new DateTime(2026, 10, 14), CultureInfo.GetCultureInfo("en-GB"), true));
            Lay(empty);
            TestRunner.Check(FindText(empty, "No Claude Code or Codex activity found on this PC yet.") != null, "the empty state");
        }
    }
}
