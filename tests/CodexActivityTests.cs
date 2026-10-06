using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Capsule
{
    public static class CodexActivityTests
    {
        static readonly DateTime Noon = new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Local);
        static readonly long Now = Clock.ToMs(Noon);

        public static void Run()
        {
            StartedMeansWorking();
            EndedOrAbortedMeansIdle();
            EachAliasCounts();
            OneWorkingSessionOfTwo();
            SilentForHalfAnHourMeansIdle();
            TurnsThatCrossMidnightCount();
            OnlyTheTailIsRead();
            TheTailsPartialFirstLineIsSkipped();
            WordsInAMessageNeverCount();
            AnUnreadableFileIsSkipped();
            UnchangedFilesAreNotReadAgain();
            NoCodexFolderMeansNoActivity();
            OpenTurnInAnOldFolderSpinsUntilItEnds();
            DayFoldersAreLocalNotUtc();
            IllegalCodexHomeMeansNoActivity();
            AFileUnreadableOnceIsReadLater();
            ALengthChangeWithTheSameWriteTimeIsRead();
            ACutInsideAMultiByteCharacterIsHarmless();
            TheFullScanStopsAtNinetyDays();
        }

        // The scan of every day folder only looks back 90 days (by the folder's date), so its cost stops growing with the
        // sessions folder. Today's and yesterday's folders are always listed, whatever the scan does.
        static void TheFullScanStopsAtNinetyDays()
        {
            string old = TestRunner.NewTempDir();
            Log(old, Noon.AddDays(-100), "a", 1, Event("task_started"));
            TestRunner.Check(!Working(old), "an open turn in a folder 100 days old isn't scanned");
            string recent = TestRunner.NewTempDir();
            Log(recent, Noon.AddDays(-5), "a", 1, Event("task_started"));
            TestRunner.Check(Working(recent), "one 5 days old still is");
            string edge = TestRunner.NewTempDir();
            Log(edge, Noon.AddDays(-90), "a", 1, Event("task_started"));
            TestRunner.Check(Working(edge), "and one exactly 90 days old");
            string past = TestRunner.NewTempDir();
            Log(past, Noon.AddDays(-91), "a", 1, Event("task_started"));
            TestRunner.Check(!Working(past), "but not one 91 days old");
            string odd = TestRunner.NewTempDir();
            Log(odd, Noon.AddDays(-2), "a", 1, Event("task_started"));
            Directory.CreateDirectory(Path.Combine(odd, "sessions", "notes", "x", "y"));
            TestRunner.Check(Working(odd), "a folder that isn't named like a date is passed over without harm");
        }

        // A turn event as Codex writes it into a rollout file.
        static string Event(string type)
        {
            return "{\"timestamp\":\"2026-10-03T03:58:00.000Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"" + type + "\",\"turn_id\":\"t1\"}}";
        }

        // Lines that are no turn event: a session's first line, and a message.
        static string Meta() { return "{\"timestamp\":\"2026-10-03T03:57:00.000Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"s1\"}}"; }

        static string Message(string role, string text)
        {
            return "{\"timestamp\":\"2026-10-03T03:58:30.000Z\",\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"" + role + "\",\"content\":[{\"type\":\"output_text\",\"text\":\"" + text + "\"}]}}";
        }

        // A rollout file in a CODEX_HOME's day folder for `day` (a local date, as Codex names them), last written
        // `minutesAgo` before Now.
        static string Log(string home, DateTime day, string name, int minutesAgo, params string[] lines)
        {
            string dir = Path.Combine(home, "sessions", day.ToString("yyyy", CultureInfo.InvariantCulture), day.ToString("MM", CultureInfo.InvariantCulture), day.ToString("dd", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "rollout-" + name + ".jsonl");
            File.WriteAllText(path, string.Join("\n", lines) + "\n");
            File.SetLastWriteTimeUtc(path, Clock.FromMs(Now - minutesAgo * 60000L));
            return path;
        }

        static bool Working(string home) { return new CodexActivity(home).Check(Now); }

        // About `bytes` of replies, a line each, none of them a turn event.
        static string Filler(int bytes)
        {
            var text = new StringBuilder();
            string line = Message("assistant", new string('x', 200)) + "\n";
            while (text.Length + line.Length <= bytes) text.Append(line);
            return text.ToString();
        }

        static void StartedMeansWorking()
        {
            string home = TestRunner.NewTempDir();
            Log(home, Noon, "a", 1, Meta(), Event("task_started"), Message("assistant", "Reading the code"));
            TestRunner.Check(Working(home), "a turn that has started is Codex working");
        }

        static void EndedOrAbortedMeansIdle()
        {
            string done = TestRunner.NewTempDir();
            Log(done, Noon, "a", 1, Meta(), Event("task_started"), Message("assistant", "Done"), Event("task_complete"));
            TestRunner.Check(!Working(done), "a turn that has completed is not");
            string aborted = TestRunner.NewTempDir();
            Log(aborted, Noon, "a", 1, Event("task_started"), Event("turn_aborted"));
            TestRunner.Check(!Working(aborted), "nor one that was cut short");
            string again = TestRunner.NewTempDir();
            Log(again, Noon, "a", 1, Event("task_started"), Event("task_complete"), Event("task_started"));
            TestRunner.Check(Working(again), "the latest turn event counts: a new turn after a finished one is working");
        }

        static void EachAliasCounts()
        {
            string started = TestRunner.NewTempDir();
            Log(started, Noon, "a", 1, Event("turn_started"));
            TestRunner.Check(Working(started), "turn_started is a start too");
            string completed = TestRunner.NewTempDir();
            Log(completed, Noon, "a", 1, Event("turn_started"), Event("turn_complete"));
            TestRunner.Check(!Working(completed), "and turn_complete an end");
        }

        static void OneWorkingSessionOfTwo()
        {
            string home = TestRunner.NewTempDir();
            Log(home, Noon, "a", 2, Event("task_started"), Event("task_complete"));
            Log(home, Noon, "b", 1, Event("task_started"));
            TestRunner.Check(Working(home), "one session working of two is Codex working");
        }

        static void SilentForHalfAnHourMeansIdle()
        {
            string crashed = TestRunner.NewTempDir();
            Log(crashed, Noon, "a", 31, Event("task_started"));
            TestRunner.Check(!Working(crashed), "a turn whose log has been silent for 31 minutes has stopped (Codex crashed)");
            string slow = TestRunner.NewTempDir();
            Log(slow, Noon, "a", 29, Event("task_started"));
            TestRunner.Check(Working(slow), "after 29 minutes it is still working (a long command)");
        }

        static void TurnsThatCrossMidnightCount()
        {
            string home = TestRunner.NewTempDir();
            Log(home, Noon.AddDays(-1), "a", 5, Event("task_started"));
            TestRunner.Check(Working(home), "a session begun yesterday, still writing, counts");
            string older = TestRunner.NewTempDir();
            Log(older, Noon.AddDays(-2), "a", 5, Event("task_started"));
            TestRunner.Check(Working(older), "so does one begun two days ago: the full scan finds every day folder");
            string stale = TestRunner.NewTempDir();
            Log(stale, Noon.AddDays(-2), "a", 31, Event("task_started"));
            Log(stale, Noon.AddDays(-9), "b", 125, Event("task_started"));
            TestRunner.Check(!Working(stale), "but an old folder's file that has been silent for 31 minutes, or two hours, doesn't");
        }

        // Codex keeps appending to the file it made when the session began, so a session left open for days, or an old
        // thread resumed, writes into an old day folder. The full scan (the first one at once, then every 15 s by the
        // clock it is given) finds it, and the 2 s checks after that stat it again.
        static void OpenTurnInAnOldFolderSpinsUntilItEnds()
        {
            string home = TestRunner.NewTempDir();
            string path = Log(home, Noon.AddDays(-5), "a", 1, Event("task_started"));
            var reader = new CodexActivity(home);
            TestRunner.Check(reader.Check(Now), "an open turn in a five-day-old folder spins after the first scan");
            File.AppendAllText(path, Event("task_complete") + "\n");
            File.SetLastWriteTimeUtc(path, Clock.FromMs(Now + 1000));
            TestRunner.Check(!reader.Check(Now + 2000), "and stops when the turn completes: the remembered file is looked at every 2 s");
            File.AppendAllText(path, Event("task_started") + "\n");
            File.SetLastWriteTimeUtc(path, Clock.FromMs(Now + 3000));
            TestRunner.Check(reader.Check(Now + 4000), "a new turn in it spins again, without waiting for another scan");

            // A file that appears in an old folder after a scan waits for the next scan, 15 s after the last one.
            Log(home, Noon.AddDays(-7), "b", 0, Event("task_started"));
            File.AppendAllText(path, Event("task_complete") + "\n");
            File.SetLastWriteTimeUtc(path, Clock.FromMs(Now + 5000));
            TestRunner.Check(!reader.Check(Now + 14000), "a new old-folder file isn't seen before the next scan");
            TestRunner.Check(reader.Check(Now + 15000), "but it is when the scan is due");

            // A remembered file is dropped once it has been silent past the 30-minute rule, and again when it is gone.
            string quiet = TestRunner.NewTempDir();
            Log(quiet, Noon.AddDays(-4), "a", 29, Event("task_started"));
            var quietReader = new CodexActivity(quiet);
            TestRunner.Check(quietReader.Check(Now), "29 minutes after its last write it still counts");
            TestRunner.Check(!quietReader.Check(Now + 2 * 60000), "31 minutes after, it doesn't (Codex crashed)");
            TestRunner.Check(!quietReader.Check(Now + 40 * 60000), "and nothing brings it back");

            string gone = TestRunner.NewTempDir();
            string goneFile = Log(gone, Noon.AddDays(-3), "a", 1, Event("task_started"));
            var goneReader = new CodexActivity(gone);
            TestRunner.Check(goneReader.Check(Now), "found by the scan");
            File.Delete(goneFile);
            bool still = true;
            Exception thrown = null;
            try { still = goneReader.Check(Now + 2000); }
            catch (Exception e) { thrown = e; }
            TestRunner.Check(thrown == null && !still, "a remembered file that vanishes before it is stat'd is dropped, without an error");
        }

        // The day folders are named by the local date, as Codex names them: a check on a machine whose clock is ahead of or
        // behind UTC must look in the local day's folder, not the UTC date's. (Checked after a scan that found nothing, so
        // only the 2 s listing of today's and yesterday's folders can see the file.)
        static void DayFoldersAreLocalNotUtc()
        {
            TimeSpan offset = TimeZoneInfo.Local.GetUtcOffset(Noon);
            if (offset == TimeSpan.Zero)
            {
                TestRunner.Check(true, "(this machine is on UTC: local and UTC dates can't be told apart)");
                return;
            }
            DateTime local = offset > TimeSpan.Zero ? new DateTime(2026, 10, 3, 1, 0, 0, DateTimeKind.Local) : new DateTime(2026, 10, 3, 23, 0, 0, DateTimeKind.Local);
            TestRunner.Check(local.ToUniversalTime().Date != local.Date, "the test hour has a UTC date other than the local one");
            long now = Clock.ToMs(local);
            string home = TestRunner.NewTempDir();
            var reader = new CodexActivity(home);
            TestRunner.Check(!reader.Check(now), "nothing yet");
            string dir = Path.Combine(home, "sessions", local.ToString("yyyy", CultureInfo.InvariantCulture), local.ToString("MM", CultureInfo.InvariantCulture), local.ToString("dd", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "rollout-a.jsonl");
            File.WriteAllText(path, Event("task_started") + "\n");
            File.SetLastWriteTimeUtc(path, Clock.FromMs(now - 60000));
            TestRunner.Check(reader.Check(now + 2000), "a session in the local date's folder is seen by the 2 s listing, not only by the scan");
            DateTime yesterday = local.AddDays(-1);
            string yDir = Path.Combine(home, "sessions", yesterday.ToString("yyyy", CultureInfo.InvariantCulture), yesterday.ToString("MM", CultureInfo.InvariantCulture), yesterday.ToString("dd", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(yDir);
            string yPath = Path.Combine(yDir, "rollout-b.jsonl");
            File.WriteAllText(path, Event("task_complete") + "\n");   // the first one ends
            File.SetLastWriteTimeUtc(path, Clock.FromMs(now + 3000));
            File.WriteAllText(yPath, Event("task_started") + "\n");
            File.SetLastWriteTimeUtc(yPath, Clock.FromMs(now + 3000));
            TestRunner.Check(reader.Check(now + 4000), "and one in the local yesterday's folder too");
        }

        // Check never throws, whatever CODEX_HOME holds: a quote or a control character in it makes Path.Combine throw.
        static void IllegalCodexHomeMeansNoActivity()
        {
            foreach (string bad in new[] { "C:\\codex\"home", "C:\\codex\thome", "C:\\codex<home>", "\0" })
            {
                bool working = true;
                Exception thrown = null;
                try { working = Working(bad); }
                catch (Exception e) { thrown = e; }
                TestRunner.Check(thrown == null && !working, "a CODEX_HOME that isn't a usable path is no activity, and no error" + (thrown != null ? " (threw " + thrown.GetType().Name + ")" : ""));
            }
        }

        // The file was locked at the first look and is free at the next: it is read then, though its length and write time
        // haven't changed since (they were never recorded, because nothing was read).
        static void AFileUnreadableOnceIsReadLater()
        {
            string home = TestRunner.NewTempDir();
            string path = Log(home, Noon, "a", 1, Event("task_started"));
            var reader = new CodexActivity(home);
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                TestRunner.Check(!reader.Check(Now), "locked: nothing is known of it");
            TestRunner.Check(reader.Check(Now + 2000), "free again, unchanged on disk: it is read now");
        }

        // The length alone can change (a clock that wasn't updated, a copy that kept the time): that is a change.
        static void ALengthChangeWithTheSameWriteTimeIsRead()
        {
            string home = TestRunner.NewTempDir();
            string path = Log(home, Noon, "a", 1, Event("task_started"));
            var reader = new CodexActivity(home);
            TestRunner.Check(reader.Check(Now), "started");
            DateTime written = File.GetLastWriteTimeUtc(path);
            File.AppendAllText(path, Event("task_complete") + "\n");
            File.SetLastWriteTimeUtc(path, written);
            TestRunner.Check(!reader.Check(Now + 2000), "a longer file with the write time it had is read again");
        }

        // The 64 KB cut can fall inside a character of more than one byte: whatever it makes of that character is in the
        // partial first line, which is dropped, so the whole lines after it are read as usual (all three alignments).
        static void ACutInsideAMultiByteCharacterIsHarmless()
        {
            int inside = 0;
            for (int pad = 0; pad < 3; pad++)
            {
                string big = "{\"note\":\"" + new string('\u4E2D', 30000) + new string('a', pad) + "\"}";   // 90 KB of three-byte characters; the pad moves the cut
                string home = TestRunner.NewTempDir();
                string path = Log(home, Noon, "a", 1, Event("task_complete"), big, Event("task_started"));
                var bytes = File.ReadAllBytes(path);
                int bigStart = Event("task_complete").Length + 1;
                long cut = bytes.Length - CodexActivity.TailBytes;
                TestRunner.Check(cut > bigStart, "the cut falls inside the long line (pad " + pad + ")");
                if ((bytes[cut] & 0xC0) == 0x80) inside++;   // the tail begins with a continuation byte
                TestRunner.Check(Working(home), "the start after the cut is still read (pad " + pad + ")");
            }
            TestRunner.Eq(2, inside, "and in two of the three alignments the cut is inside a character");
        }

        static void OnlyTheTailIsRead()
        {
            string far = TestRunner.NewTempDir();
            Log(far, Noon, "a", 1, Event("task_started"), Filler(70 * 1024).TrimEnd('\n'));
            TestRunner.Check(!Working(far), "a start more than 64 KB from the end isn't seen: only the last 64 KB is read");

            // A reader that saw the start keeps it while the turn writes on, past those 64 KB.
            string home = TestRunner.NewTempDir();
            string path = Log(home, Noon, "a", 2, Event("task_started"));
            var reader = new CodexActivity(home);
            reader.Check(Now);
            File.AppendAllText(path, Filler(70 * 1024));
            File.SetLastWriteTimeUtc(path, Clock.FromMs(Now - 60000));
            TestRunner.Check(reader.Check(Now), "a turn whose start has scrolled out of the last 64 KB is still working: each file's latest turn event is kept");
        }

        // The last 64 KB begins inside a line. That line is skipped, not half read: what is left of a start cut in two still
        // names task_started, but isn't taken for one; and the whole lines after the cut are read as usual.
        static void TheTailsPartialFirstLineIsSkipped()
        {
            string longStart = "{\"timestamp\":\"2026-10-03T03:58:00.000Z\",\"type\":\"event_msg\",\"payload\":{\"note\":\"" + new string('y', 4000) + "\",\"type\":\"task_started\"}}";
            string cut = TestRunner.NewTempDir();
            Log(cut, Noon, "a", 1, Event("task_complete"), longStart, Filler(63 * 1024).TrimEnd('\n'));
            TestRunner.Check(!Working(cut), "a start the 64 KB cut falls inside isn't counted");

            string longEnd = "{\"timestamp\":\"2026-10-03T03:58:00.000Z\",\"type\":\"event_msg\",\"payload\":{\"last_agent_message\":\"" + new string('z', 4000) + "\",\"type\":\"task_complete\"}}";
            string after = TestRunner.NewTempDir();
            Log(after, Noon, "a", 1, longEnd, Event("task_started"), Filler(63 * 1024 - Event("task_started").Length).TrimEnd('\n'));
            TestRunner.Check(Working(after), "and a start in a whole line after the cut is");
        }

        static void WordsInAMessageNeverCount()
        {
            string home = TestRunner.NewTempDir();
            Log(home, Noon, "a", 1, Event("task_started"), Event("task_complete"),
                Message("user", "private 4e1f: why does task_started never fire, and turn_started?"),
                "{\"type\":\"response_item\",\"payload\":{\"type\":\"function_call_output\",\"output\":\"{\\\"payload\\\":{\\\"type\\\":\\\"task_started\\\"}}\"}}",
                "task_started, in a line that isn't JSON");
            TestRunner.Check(!Working(home), "a prompt, a reply or a tool's output that mentions task_started never counts");
            string log = Files.ReadText(Paths.LogFile) ?? "";
            TestRunner.Check(!log.Contains("4e1f"), "and nothing from the file reaches the log");
        }

        static void AnUnreadableFileIsSkipped()
        {
            string home = TestRunner.NewTempDir();
            string locked = Log(home, Noon, "b", 1, Event("task_started"));
            using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))   // another program has it, shared with nobody
            {
                bool working = true;
                Exception thrown = null;
                try { working = Working(home); }
                catch (Exception e) { thrown = e; }
                TestRunner.Check(thrown == null && !working, "a file that can't be read is skipped, without an error");
                Log(home, Noon, "a", 1, Event("task_started"));
                TestRunner.Check(Working(home), "and the others are still read");
            }
        }

        // Each check reads only files whose length or write time changed since the last one.
        static void UnchangedFilesAreNotReadAgain()
        {
            string home = TestRunner.NewTempDir();
            string path = Log(home, Noon, "a", 1, Event("task_started"));
            var reader = new CodexActivity(home);
            reader.Check(Now);
            DateTime written = File.GetLastWriteTimeUtc(path);
            File.WriteAllText(path, Event("turn_aborted") + "\n");   // the same length as the start it replaces
            File.SetLastWriteTimeUtc(path, written);
            TestRunner.Check(reader.Check(Now), "a file whose length and write time are unchanged isn't read again");
            File.SetLastWriteTimeUtc(path, written.AddSeconds(1));
            TestRunner.Check(!reader.Check(Now), "once one of them changes, it is");
        }

        static void NoCodexFolderMeansNoActivity()
        {
            TestRunner.Check(!Working(Path.Combine(TestRunner.NewTempDir(), "missing")), "no Codex folder: not working");
            TestRunner.Check(!Working("") && !Working(null), "nor without one named");
        }
    }
}
