using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Capsule
{
    public static class IdeasTests
    {
        const string Secret = "secret_for_tests_only_0123456789";

        public static void Run()
        {
            SecretsAreEncrypted();
            QueueKeepsOrderAcrossRestarts();
            UnreadableQueueIsSetAside();
            CleansIdeas();
            SyncSendsInOrderAndStopsAtAFailure();
            RefusedIdeaStopsTheQueue();
            ModuleSendsQueuedIdeas();
            FailureKeepsTheIdea();
            RefusedIdeaCanBeDiscarded();
            WaitsForSetup();
            ConfigKeepsTheDatabase();
            TickWaitsForARunningPass();
            BrieflyLockedQueueFileIsWaitedFor();
            StillLockedQueueFileIsNotAnEmptyQueue();
            SecretThatCantBeSavedIsReported();
            HeldBackQueueIsReadBackOnceItIsFree();
            HeldBackQueueIsReadBackByATick();
            HeldBackFileThatIsStillNoQueueIsSetAside();
            HeldBackQueueIsSavedOnceTheFileIsGone();
            HeldBackQueueReadableOnceSetAsideComesBack();
            ABriefLockOnTheSetAsideFileIsWaitedFor();
            AnIdeaTypedWhileHeldBackIsMergedAfterTheSecondRead();
            AFailedSecondReadLeavesTheCopyAsideAndMergesNothing();
            AFileThatCantBeMovedIsLoggedByTypeOnly();
            UnsavedIdeaIsWrittenByALaterTick();
            QueueItemsAreNeverSilentlyDropped();
            FailingListenerDoesNotStallSyncing();
            MergeKeepsTheFilesIdeasAsTheyAre();
            RefusedIdeaStaysOnScreen();
            DiscardKeepsACopyBeforeDropping();
            DiscardIsRefusedWhenItsCopyCantBeKept();
            RepeatedRefusalsGetAHint();
            ConfirmedIdeaKeepsItsTickWhenNotionListsIt();
            HugeRetryAfterDoesNotParkTheRetries();
            AnIdeaAddedDuringAPassIsSentRightAfter();
            DiskNotesSayWhichIdeasArentSafeYet();
            SwitchingDatabasesClearsTheTicks();
            AFaultedPassIsLoggedByItsTypeOnly();
        }

        static void SecretsAreEncrypted()
        {
            string dir = TestRunner.NewTempDir();
            string path = Path.Combine(dir, "notion-secret.bin");
            TestRunner.Check(SecretStore.Save(path, Secret), "saved");
            string onDisk = File.ReadAllText(path);
            TestRunner.Check(!onDisk.Contains(Secret) && !onDisk.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes(Secret))), "the file doesn't hold the secret in plain text");
            TestRunner.Eq(Secret, SecretStore.Load(path), "DPAPI gives it back");
            TestRunner.Eq(null, SecretStore.Load(Path.Combine(dir, "missing.bin")), "no file, no secret");
            File.WriteAllText(path, "not base64!");
            TestRunner.Eq(null, SecretStore.Load(path), "a damaged file reads as no secret");
        }

        static void QueueKeepsOrderAcrossRestarts()
        {
            string path = Path.Combine(TestRunner.NewTempDir(), "ideas-queue.json");
            var ideas = new List<QueuedIdea>
            {
                new QueuedIdea { Id = "a", Text = "first", CreatedAtMs = 1 },
                new QueuedIdea { Id = "b", Text = "second · with “quotes”", CreatedAtMs = 2 },
            };
            TestRunner.Check(IdeasQueue.Save(path, ideas), "saved");
            List<QueuedIdea> back = IdeasQueue.Load(path);
            TestRunner.Check(back.Count == 2 && back[0].Id == "a" && back[1].Id == "b", "read back in order");
            TestRunner.Check(back[1].Text == "second · with “quotes”" && back[1].CreatedAtMs == 2, "text and time intact");
            TestRunner.Eq(0, IdeasQueue.Load(Path.Combine(TestRunner.NewTempDir(), "none.json")).Count, "no file: an empty queue");
        }

        static void UnreadableQueueIsSetAside()
        {
            string dir = TestRunner.NewTempDir();
            string path = Path.Combine(dir, "ideas-queue.json");
            File.WriteAllText(path, "{ this is not a queue");
            var module = new IdeasModule(path, Path.Combine(dir, "ideas-discarded.json"), Path.Combine(dir, "notion-secret.bin"), () => "", TaskScheduler.Default);
            TestRunner.Eq(0, module.Waiting, "starts with an empty queue");
            TestRunner.Check(!File.Exists(path) && Directory.GetFiles(dir, "ideas-queue.json.unreadable-*").Length == 1, "the unreadable file is kept under another name, not overwritten");
        }

        static void CleansIdeas()
        {
            TestRunner.Eq("one line", IdeasModule.Clean("  one\r\nline \n"), "one line, trimmed");
            TestRunner.Eq("", IdeasModule.Clean("   "), "blank is nothing");
            TestRunner.Eq(IdeasModule.MaxChars, IdeasModule.Clean(new string('x', 2500)).Length, "cut to Notion's 2,000 characters");
            string emoji = new string('x', IdeasModule.MaxChars - 1) + "\U0001F600tail";
            TestRunner.Eq(IdeasModule.MaxChars - 1, IdeasModule.Clean(emoji).Length, "never cutting a character in half");
        }

        static List<QueuedIdea> TwoIdeas()
        {
            return new List<QueuedIdea> { new QueuedIdea { Id = "a", Text = "first" }, new QueuedIdea { Id = "b", Text = "second" } };
        }

        static string ContentOf(NotionRequest r)
        {
            object[] title = Json.Arr(Json.Get(Json.Parse(r.Body), "properties", "title", "title"));
            return title == null ? null : Json.Str(Json.Get(title[0], "text", "content"));
        }

        static FakeNotion Described()
        {
            return new FakeNotion()
                .Reply("GET", "databases/" + NotionTests.Db, 200, TestRunner.Fixture("notion-database.json"))
                .Reply("GET", "data_sources/" + NotionTests.Source, 200, TestRunner.Fixture("notion-data-source.json"));
        }

        static void SyncSendsInOrderAndStopsAtAFailure()
        {
            FakeNotion notion = FakeNotion.Working();
            SyncResult r = IdeasSync.Run(new NotionClient(Secret) { Transport = notion.Answer }, NotionTests.Db, null, TwoIdeas(), false);
            TestRunner.Check(r.Failure == null, "no failure");
            TestRunner.Eq("a,b", string.Join(",", r.Sent), "both confirmed, in order");
            TestRunner.Eq("first", ContentOf(notion.Requests[2]), "the older idea goes first");
            TestRunner.Eq("second", ContentOf(notion.Requests[3]), "then the newer one");
            TestRunner.Check(r.Recent != null && r.Recent.Count == 2, "and the recent list is read after sending");

            FakeNotion flaky = Described()
                .Reply("POST", "pages", 200, TestRunner.Fixture("notion-page.json"))
                .Reply("POST", "pages", 503, "{\"code\":\"service_unavailable\"}");
            SyncResult partial = IdeasSync.Run(new NotionClient(Secret) { Transport = flaky.Answer }, NotionTests.Db, null, TwoIdeas(), true);
            TestRunner.Eq("a", string.Join(",", partial.Sent), "only the first was confirmed");
            TestRunner.Check(partial.Failure != null && partial.Failure.Status == 503 && partial.RejectedId == null, "the second waits for a retry");
            TestRunner.Check(partial.Database != null && partial.Recent == null, "the database stays known; nothing more is asked");

            FakeNotion known = FakeNotion.Working();
            IdeasSync.Run(new NotionClient(Secret) { Transport = known.Answer }, NotionTests.Db, partial.Database, TwoIdeas().GetRange(1, 1), false);
            TestRunner.Eq("POST pages", known.Requests[0].Method + " " + known.Requests[0].Path, "a known database isn't read again");

            FakeNotion refusing = new FakeNotion().Reply("GET", "databases/" + NotionTests.Db, 401, "{\"code\":\"unauthorized\"}");
            SyncResult refused = IdeasSync.Run(new NotionClient("wrong") { Transport = refusing.Answer }, NotionTests.Db, null, TwoIdeas(), false);
            TestRunner.Check(refused.Sent.Count == 0 && refused.Failure.Status == 401 && refused.Database == null, "a refused secret sends nothing");
        }

        static void RefusedIdeaStopsTheQueue()
        {
            FakeNotion picky = Described().Reply("POST", "pages", 400, TestRunner.Fixture("notion-error-validation.json"));
            SyncResult r = IdeasSync.Run(new NotionClient(Secret) { Transport = picky.Answer }, NotionTests.Db, null, TwoIdeas(), false);
            TestRunner.Eq("a", r.RejectedId, "the idea Notion refused");
            TestRunner.Check(r.Sent.Count == 0 && r.FailedCreating, "nothing after it is sent, so the order holds");
            TestRunner.Check(r.Database == null, "and the database is read again next time");
        }

        static IdeasModule NewModule(string dir, string databaseId, FakeNotion notion)
        {
            var module = new IdeasModule(Path.Combine(dir, "ideas-queue.json"), Path.Combine(dir, "ideas-discarded.json"), Path.Combine(dir, "notion-secret.bin"), () => databaseId, TaskScheduler.Default);
            module.NewClient = s => new NotionClient(s) { Transport = notion.Answer };
            return module;
        }

        static void ModuleSendsQueuedIdeas()
        {
            string dir = TestRunner.NewTempDir();
            SecretStore.Save(Path.Combine(dir, "notion-secret.bin"), Secret);
            IdeasModule module = NewModule(dir, NotionTests.Db, FakeNotion.Working());
            TestRunner.Check(module.Add("Recipe app: add a shopping list"), "queued");
            module.LastSync.Wait();
            TestRunner.Eq(0, module.Waiting, "confirmed, so out of the queue");
            TestRunner.Eq(0, IdeasQueue.Load(Path.Combine(dir, "ideas-queue.json")).Count, "and out of the file");
            IdeasTile tile = module.Tile();
            TestRunner.Check(tile.Rows.Count == 3 && tile.Rows[0].State == IdeaRow.Saved, "at the top with a tick, above Notion's recent ideas");
            TestRunner.Check(tile.Rows[0].Url.StartsWith("https://app.notion.com/p/"), "with its Notion link");
            TestRunner.Eq("", tile.Note, "nothing wrong");
            TestRunner.Check(!module.Add("   "), "a blank idea isn't queued");
        }

        static void FailureKeepsTheIdea()
        {
            string dir = TestRunner.NewTempDir();
            SecretStore.Save(Path.Combine(dir, "notion-secret.bin"), Secret);
            const string Idea = "private idea text 7f3a";
            IdeasModule module = NewModule(dir, NotionTests.Db, new FakeNotion());   // nothing answers
            module.Add(Idea);
            module.LastSync.Wait();
            IdeasTile tile = module.Tile();
            TestRunner.Eq(1, tile.Waiting, "no reply: still queued");
            TestRunner.Eq(IdeaRow.Waiting, tile.Rows[0].State, "shown as waiting");
            TestRunner.Check(tile.Note.Contains("Can't reach Notion"), "with the reason: " + tile.Note);
            IdeasModule restarted = NewModule(dir, NotionTests.Db, new FakeNotion());
            TestRunner.Eq(1, restarted.Waiting, "it survives a restart");
            string log = Files.ReadText(Paths.LogFile) ?? "";
            TestRunner.Check(log.Contains("ideas: 1 waiting"), "the log has counts and statuses");
            TestRunner.Check(!log.Contains("7f3a"), "but never the idea's text");
        }

        static void RefusedIdeaCanBeDiscarded()
        {
            string dir = TestRunner.NewTempDir();
            SecretStore.Save(Path.Combine(dir, "notion-secret.bin"), Secret);
            IdeasModule module = NewModule(dir, NotionTests.Db, Described().Reply("POST", "pages", 400, TestRunner.Fixture("notion-error-validation.json")));
            module.Add("an idea Notion refuses");
            module.LastSync.Wait();
            IdeasTile tile = module.Tile();
            TestRunner.Check(tile.CanDiscard && tile.Rows[0].State == IdeaRow.Rejected, "offered for discarding");
            TestRunner.Eq("Notion rejected the idea (validation_error)", tile.Note, "with Notion's code");
            module.Discard();
            TestRunner.Eq(0, module.Waiting, "discarded");
            TestRunner.Check(!module.Tile().CanDiscard, "and nothing left to discard");
        }

        static void WaitsForSetup()
        {
            string dir = TestRunner.NewTempDir();
            IdeasModule module = NewModule(dir, "", FakeNotion.Working());
            module.Add("before setting up");
            IdeasTile tile = module.Tile();
            TestRunner.Check(!tile.Configured && tile.Waiting == 1, "not set up: the idea waits");
            TestRunner.Check(tile.Note.Contains("Set up Notion"), "and the tile says how");
            TestRunner.Eq(null, module.LastSync, "nothing was sent");
        }

        static void ConfigKeepsTheDatabase()
        {
            string path = Path.Combine(TestRunner.NewTempDir(), "config.json");
            TestRunner.Check(new Config { NotionDatabase = NotionTests.Db }.Save(path), "saving config.json says it worked");
            TestRunner.Eq(NotionTests.Db, Config.Load(path).NotionDatabase, "the database id is kept in config.json");
            TestRunner.Eq("", Config.Load(Path.Combine(TestRunner.NewTempDir(), "none.json")).NotionDatabase, "none set up yet");
            string blocked = Path.Combine(TestRunner.NewTempDir(), "config.json");
            Directory.CreateDirectory(blocked);   // a folder where the file should go: it can't be written
            TestRunner.Check(!new Config { NotionDatabase = NotionTests.Db }.Save(blocked), "a config.json that can't be written says so, so nobody reports it saved");
        }

        // Waits for the module's current pass, and for any that its result chained behind it (none, if no pass started).
        static void WaitForPasses(IdeasModule module)
        {
            Task last;
            do
            {
                last = module.LastSync;
                if (last != null) last.Wait();
            } while (!ReferenceEquals(last, module.LastSync));
        }

        static void TickWaitsForARunningPass()
        {
            string dir = TestRunner.NewTempDir();
            SecretStore.Save(Path.Combine(dir, "notion-secret.bin"), Secret);
            FakeNotion notion = Described();
            var inFlight = new ManualResetEvent(false);
            var release = new ManualResetEvent(false);
            int posts = 0;
            var module = new IdeasModule(Path.Combine(dir, "ideas-queue.json"), Path.Combine(dir, "ideas-discarded.json"), Path.Combine(dir, "notion-secret.bin"), () => NotionTests.Db, TaskScheduler.Default);
            module.NewClient = s => new NotionClient(s)
            {
                Transport = r =>
                {
                    if (r.Method != "POST" || r.Path != "pages") return notion.Answer(r);
                    Interlocked.Increment(ref posts);
                    inFlight.Set();
                    release.WaitOne(10000);   // Notion is slow to answer, until the test lets it
                    return new HttpResult { Status = 429, RetryAfterSeconds = 60, Body = "{\"code\":\"rate_limited\"}" };
                }
            };
            module.Add("an idea sent while Notion is slow");
            TestRunner.Check(inFlight.WaitOne(10000), "the pass is under way, waiting for Notion's reply");
            module.Tick(long.MaxValue);   // the 5-second tick lands while it runs
            release.Set();
            WaitForPasses(module);
            TestRunner.Eq(1, posts, "a tick during a pass doesn't start another one right behind it");
            TestRunner.Eq(1, module.Waiting, "the idea is still queued");

            int sent = posts;
            module.Tick(Clock.NowMs() + 55 * 1000);   // inside the Retry-After of 60 s
            WaitForPasses(module);
            TestRunner.Eq(sent, posts, "Retry-After is waited out");
            module.Tick(Clock.NowMs() + 65 * 1000);   // after it
            WaitForPasses(module);
            TestRunner.Eq(sent + 1, posts, "and then a tick sends again");
        }

        // Another program (a virus scan, say) has the file open and shared with nobody: from when this returns until
        // letGo is set or holdMs have passed.
        static Thread HoldOpen(string path, int holdMs, ManualResetEvent letGo)
        {
            var held = new ManualResetEvent(false);
            bool acquired = false;
            var thread = new Thread(() =>
            {
                try
                {
                    using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                    {
                        acquired = true;
                        held.Set();
                        letGo.WaitOne(holdMs);
                    }
                }
                catch (Exception) { }
                finally { held.Set(); }   // never leave the test waiting
            });
            thread.IsBackground = true;
            thread.Start();
            held.WaitOne(5000);
            TestRunner.Check(acquired, "the file is held open by another program");
            return thread;
        }

        static void BrieflyLockedQueueFileIsWaitedFor()
        {
            string dir = TestRunner.NewTempDir();
            string path = Path.Combine(dir, "ideas-queue.json");
            var ideas = new List<QueuedIdea>
            {
                new QueuedIdea { Id = "a", Text = "first", CreatedAtMs = 1 },
                new QueuedIdea { Id = "b", Text = "second", CreatedAtMs = 2 },
            };
            TestRunner.Check(IdeasQueue.Save(path, ideas), "saved");
            Thread holder = HoldOpen(path, 80, new ManualResetEvent(false));   // another program has it for a moment
            var module = new IdeasModule(path, Path.Combine(dir, "ideas-discarded.json"), Path.Combine(dir, "notion-secret.bin"), () => "", TaskScheduler.Default);
            holder.Join();
            TestRunner.Eq(2, module.Waiting, "a queue file that is only briefly locked is waited for, not read as an empty queue");
            TestRunner.Check(Directory.GetFiles(dir, "ideas-queue.json.unreadable-*").Length == 0, "and it isn't set aside");
            TestRunner.Eq(2, IdeasQueue.Load(path).Count, "its ideas are still in the file");
        }

        static void StillLockedQueueFileIsNotAnEmptyQueue()
        {
            string dir = TestRunner.NewTempDir();
            string path = Path.Combine(dir, "ideas-queue.json");
            TestRunner.Check(IdeasQueue.Save(path, new List<QueuedIdea> { new QueuedIdea { Id = "a", Text = "first", CreatedAtMs = 1 } }), "saved");
            var letGo = new ManualResetEvent(false);
            Thread holder = HoldOpen(path, 10000, letGo);
            List<QueuedIdea> whileLocked = IdeasQueue.Load(path);
            letGo.Set();
            holder.Join();
            TestRunner.Check(whileLocked == null, "a file that stays unreadable is not taken for an empty queue (the module sets it aside)");
            TestRunner.Eq(1, IdeasQueue.Load(path).Count, "once it is free again, its idea is still there");
        }

        static void SecretThatCantBeSavedIsReported()
        {
            string dir = TestRunner.NewTempDir();
            TestRunner.Check(SecretStore.Save(Path.Combine(dir, "notion-secret.bin"), Secret), "a good secret is saved");
            string path = Path.Combine(dir, "never-written.bin");
            bool? saved = null;
            Exception thrown = null;
            try { saved = SecretStore.Save(path, null); }
            catch (Exception e) { thrown = e; }
            TestRunner.Check(thrown == null, "a secret that can't be encrypted doesn't throw" + (thrown != null ? " (it threw " + thrown.GetType().Name + ")" : ""));
            TestRunner.Check(saved == false, "it is reported as not saved");
            TestRunner.Check(!File.Exists(path), "and nothing is written");
            // The failure that matters for privacy is a real one: the real secret is encrypted, then the write fails (a
            // folder stands where the file should go), and nothing about that may put the secret in the log.
            string blocked = Path.Combine(dir, "blocked.bin");
            Directory.CreateDirectory(blocked);
            bool? realSaved = null;
            Exception realThrown = null;
            try { realSaved = SecretStore.Save(blocked, Secret); }
            catch (Exception e) { realThrown = e; }
            TestRunner.Check(realThrown == null, "a real secret that can't be written doesn't throw" + (realThrown != null ? " (it threw " + realThrown.GetType().Name + ")" : ""));
            TestRunner.Check(realSaved == false, "it is reported as not saved");
            TestRunner.Eq(0, Directory.GetFiles(dir, "blocked.bin.*").Length, "and leaves no half-written file behind");
            string log = Files.ReadText(Paths.LogFile) ?? "";
            TestRunner.Check(log.Contains("notion: couldn't save the secret"), "the failure is logged");
            TestRunner.Check(!log.Contains(Secret), "but never with the secret in it");
        }

        // An idea that never reached the disk is invisible and, without a retry, lost for good when Capsule quits: so a
        // queue file that couldn't be read or moved when Capsule started (another program had it open) is read again
        // once it is free, and its ideas come back into the queue, ahead of newer ones, rather than staying behind in a
        // set-aside file.
        static void HeldBackQueueIsReadBackOnceItIsFree()
        {
            string dir = TestRunner.NewTempDir();
            string path = Path.Combine(dir, "ideas-queue.json");
            var ideas = new List<QueuedIdea>
            {
                new QueuedIdea { Id = "a", Text = "first", CreatedAtMs = 1 },
                new QueuedIdea { Id = "b", Text = "second", CreatedAtMs = 2 },
            };
            TestRunner.Check(IdeasQueue.Save(path, ideas), "saved");
            var letGo = new ManualResetEvent(false);
            Thread holder = HoldOpen(path, 10000, letGo);   // locked well past the retry window
            var module = new IdeasModule(path, Path.Combine(dir, "ideas-discarded.json"), Path.Combine(dir, "notion-secret.bin"), () => "", TaskScheduler.Default);
            TestRunner.Eq(0, module.Waiting, "the queue can't be read, so the module starts empty");
            TestRunner.Eq("Saved ideas can't be read yet. Retrying.", module.Tile().DiskNote, "and the tile says the saved ideas can't be read yet");
            letGo.Set();
            holder.Join();   // the file is free again
            TestRunner.Check(module.Add("a new idea"), "queued");
            TestRunner.Eq(3, module.Waiting, "the ideas that were in the file are back in the queue, ahead of the new one");
            TestRunner.Eq(0, Directory.GetFiles(dir, "ideas-queue.json.unreadable-*").Length, "and nothing is set aside: the file could be read after all");
            List<QueuedIdea> onDisk = IdeasQueue.Load(path);
            TestRunner.Check(onDisk != null && onDisk.Count == 3 && onDisk[0].Id == "a" && onDisk[1].Id == "b" && onDisk[2].Text == "a new idea", "the file holds all three, in order");
            TestRunner.Eq("", module.Tile().DiskNote, "and the tile's note about the disk is gone");
        }

        // Nothing has to be typed for that: the tick that comes every few seconds does it.
        static void HeldBackQueueIsReadBackByATick()
        {
            string dir = TestRunner.NewTempDir();
            string path = Path.Combine(dir, "ideas-queue.json");
            var ideas = new List<QueuedIdea>
            {
                new QueuedIdea { Id = "a", Text = "first", CreatedAtMs = 1 },
                new QueuedIdea { Id = "b", Text = "second", CreatedAtMs = 2 },
            };
            TestRunner.Check(IdeasQueue.Save(path, ideas), "saved");
            var letGo = new ManualResetEvent(false);
            Thread holder = HoldOpen(path, 10000, letGo);
            var module = new IdeasModule(path, Path.Combine(dir, "ideas-discarded.json"), Path.Combine(dir, "notion-secret.bin"), () => "", TaskScheduler.Default);
            int raised = 0;
            module.Changed += delegate { raised++; };
            module.Tick(Clock.NowMs());   // still locked: nothing changes, and nothing is written over it
            TestRunner.Eq(0, module.Waiting, "while the file is locked the queue stays empty");
            letGo.Set();
            holder.Join();
            raised = 0;
            module.Tick(Clock.NowMs());
            TestRunner.Eq(2, module.Waiting, "a tick reads the file's ideas back");
            TestRunner.Check(raised > 0, "and tells the tile");
            TestRunner.Eq("", module.Tile().DiskNote, "whose note about the disk goes away");
            TestRunner.Eq(0, Directory.GetFiles(dir, "ideas-queue.json.unreadable-*").Length, "with nothing set aside");
            TestRunner.Eq(2, IdeasQueue.Load(path).Count, "and the file still holds both");
        }

        // A file that is no queue at all, once it can be moved, is kept as it was under another name: only then is a
        // fresh queue written.
        static void HeldBackFileThatIsStillNoQueueIsSetAside()
        {
            string dir = TestRunner.NewTempDir();
            string path = Path.Combine(dir, "ideas-queue.json");
            File.WriteAllText(path, "{ this is not a queue");
            var letGo = new ManualResetEvent(false);
            Thread holder = HoldOpen(path, 10000, letGo);
            var module = new IdeasModule(path, Path.Combine(dir, "ideas-discarded.json"), Path.Combine(dir, "notion-secret.bin"), () => "", TaskScheduler.Default);
            letGo.Set();
            holder.Join();
            TestRunner.Check(module.Add("a new idea"), "queued");
            string[] aside = Directory.GetFiles(dir, "ideas-queue.json.unreadable-*");
            TestRunner.Check(aside.Length == 1 && File.ReadAllText(aside[0]) == "{ this is not a queue", "the file that is no queue is kept as it was");
            List<QueuedIdea> fresh = IdeasQueue.Load(path);
            TestRunner.Check(fresh != null && fresh.Count == 1 && fresh[0].Text == "a new idea", "and the new idea is saved in a fresh queue file");
        }

        // Another program has the queue file open when an idea is added: the idea can't be written, but it is queued and
        // shown, the tile says it isn't saved yet, and the next tick after the file is free writes it.
        static void UnsavedIdeaIsWrittenByALaterTick()
        {
            string dir = TestRunner.NewTempDir();
            string path = Path.Combine(dir, "ideas-queue.json");
            TestRunner.Check(IdeasQueue.Save(path, new List<QueuedIdea> { new QueuedIdea { Id = "a", Text = "from before", CreatedAtMs = 1 } }), "saved");
            var module = new IdeasModule(path, Path.Combine(dir, "ideas-discarded.json"), Path.Combine(dir, "notion-secret.bin"), () => "", TaskScheduler.Default);
            TestRunner.Eq(1, module.Waiting, "the queue was read");
            var letGo = new ManualResetEvent(false);
            Thread holder = HoldOpen(path, 10000, letGo);
            TestRunner.Check(module.Add("typed while the file is locked"), "an idea is still queued when it can't be written");
            TestRunner.Eq(2, module.Waiting, "and counted");
            TestRunner.Check(module.Tile().Rows[0].Text == "typed while the file is locked", "and shown");
            TestRunner.Eq("Couldn't save to disk yet. Retrying.", module.Tile().DiskNote, "with a note that it isn't saved yet");
            module.Tick(Clock.NowMs());
            TestRunner.Eq("Couldn't save to disk yet. Retrying.", module.Tile().DiskNote, "a tick while the file is still locked changes nothing");
            letGo.Set();
            holder.Join();
            module.Tick(Clock.NowMs());
            List<QueuedIdea> onDisk = IdeasQueue.Load(path);
            TestRunner.Check(onDisk != null && onDisk.Count == 2 && onDisk[0].Id == "a" && onDisk[1].Text == "typed while the file is locked", "the next tick writes the idea to disk");
            TestRunner.Eq("", module.Tile().DiskNote, "and the note goes away");
        }

        static void HeldBackQueueIsSavedOnceTheFileIsGone()
        {
            string dir = TestRunner.NewTempDir();
            string path = Path.Combine(dir, "ideas-queue.json");
            TestRunner.Check(IdeasQueue.Save(path, new List<QueuedIdea> { new QueuedIdea { Id = "a", Text = "first", CreatedAtMs = 1 } }), "saved");
            var letGo = new ManualResetEvent(false);
            Thread holder = HoldOpen(path, 10000, letGo);
            var module = new IdeasModule(path, Path.Combine(dir, "ideas-discarded.json"), Path.Combine(dir, "notion-secret.bin"), () => "", TaskScheduler.Default);
            letGo.Set();
            holder.Join();
            File.Delete(path);   // someone removed it meanwhile: nothing is left to protect
            TestRunner.Check(module.Add("a new idea"), "queued");
            List<QueuedIdea> fresh = IdeasQueue.Load(path);
            TestRunner.Check(fresh != null && fresh.Count == 1 && fresh[0].Text == "a new idea", "the queue is saved again, not held back for good");
            TestRunner.Eq(0, Directory.GetFiles(dir, "ideas-queue.json.unreadable-*").Length, "and nothing is set aside");
        }

        // The file couldn't be read or moved when Capsule started. Later another program still keeps it from being read but
        // lets it be moved, and lets go of it just after Capsule has moved it aside: one more try, at the moved file, finds a
        // queue after all, and its ideas come back. The set-aside copy stays.
        static void HeldBackQueueReadableOnceSetAsideComesBack()
        {
            string dir = TestRunner.NewTempDir();
            string path = Path.Combine(dir, "ideas-queue.json");
            var ideas = new List<QueuedIdea>
            {
                new QueuedIdea { Id = "a", Text = "first", CreatedAtMs = 1 },
                new QueuedIdea { Id = "b", Text = "second", CreatedAtMs = 2 },
            };
            TestRunner.Check(IdeasQueue.Save(path, ideas), "saved");
            var letGo = new ManualResetEvent(false);
            Thread holder = HoldOpen(path, 10000, letGo);   // can be neither read nor moved
            var module = new IdeasModule(path, Path.Combine(dir, "ideas-discarded.json"), Path.Combine(dir, "notion-secret.bin"), () => "", TaskScheduler.Default);
            letGo.Set();
            holder.Join();
            var unreadable = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Delete);   // can be moved, still not read
            string movedTo = null;
            module.QueueSetAside = delegate(string aside)
            {
                movedTo = aside;
                unreadable.Dispose();   // the other program lets go just after the move
            };
            try { module.Tick(Clock.NowMs()); }
            finally { unreadable.Dispose(); }
            TestRunner.Check(movedTo != null && File.Exists(movedTo), "the file that couldn't be read was moved aside, and that copy is kept");
            TestRunner.Eq(2, module.Waiting, "read once more where it was moved to, it was a queue after all: its ideas are back");
            List<QueuedIdea> onDisk = IdeasQueue.Load(path);
            TestRunner.Check(onDisk != null && onDisk.Count == 2 && onDisk[0].Id == "a" && onDisk[1].Id == "b", "and saved in ideas-queue.json again, in order");
            TestRunner.Eq("", module.Tile().DiskNote, "nothing is held back any more");
            List<QueuedIdea> kept = IdeasQueue.Load(movedTo);
            TestRunner.Check(kept != null && kept.Count == 2 && kept[0].Id == "a" && kept[1].Id == "b", "and the set-aside copy still holds both ideas, as it was");
        }

        // A held-back queue file (a, b), made movable but not readable: another program has it open for reading and sharing
        // only deletes, as a virus scan might. The returned module has it held back; the stream is the lock, to be disposed.
        static IdeasModule HeldBackWithAMovableLock(string dir, out string path, out FileStream lockStream)
        {
            path = Path.Combine(dir, "ideas-queue.json");
            var ideas = new List<QueuedIdea>
            {
                new QueuedIdea { Id = "a", Text = "first", CreatedAtMs = 1 },
                new QueuedIdea { Id = "b", Text = "second", CreatedAtMs = 2 },
            };
            TestRunner.Check(IdeasQueue.Save(path, ideas), "saved");
            var letGo = new ManualResetEvent(false);
            Thread holder = HoldOpen(path, 10000, letGo);   // can be neither read nor moved
            var module = new IdeasModule(path, Path.Combine(dir, "ideas-discarded.json"), Path.Combine(dir, "notion-secret.bin"), () => "", TaskScheduler.Default);
            letGo.Set();
            holder.Join();
            lockStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Delete);   // can be moved, still not read
            return module;
        }

        // The lock on the moved file is brief but still there at the first look: Load's few attempts wait it out, so the
        // copy isn't left set aside for good with its ideas out of the queue.
        static void ABriefLockOnTheSetAsideFileIsWaitedFor()
        {
            string dir = TestRunner.NewTempDir();
            string path;
            FileStream lockStream;
            IdeasModule module = HeldBackWithAMovableLock(dir, out path, out lockStream);
            FileStream held = lockStream;
            module.QueueSetAside = delegate(string aside)
            {
                var release = new Thread(delegate()
                {
                    Thread.Sleep(80);   // gone within Load's attempts (5 x 50 ms), not at the first look
                    held.Dispose();
                });
                release.IsBackground = true;
                release.Start();
            };
            try { module.Tick(Clock.NowMs()); }
            finally { held.Dispose(); }
            TestRunner.Eq(2, module.Waiting, "a lock that clears a moment after the move is waited out: the ideas come back");
            List<QueuedIdea> onDisk = IdeasQueue.Load(path);
            TestRunner.Check(onDisk != null && onDisk.Count == 2 && onDisk[0].Id == "a" && onDisk[1].Id == "b", "and ideas-queue.json holds them");
        }

        // An idea typed while the file was held back is merged with the ones the second read finds: theirs first, and the
        // copy of ours that the file has too (the same id) dropped, as Merge does.
        static void AnIdeaTypedWhileHeldBackIsMergedAfterTheSecondRead()
        {
            string dir = TestRunner.NewTempDir();
            string path;
            FileStream lockStream;
            IdeasModule module = HeldBackWithAMovableLock(dir, out path, out lockStream);
            FileStream held = lockStream;
            module.QueueSetAside = delegate(string aside)
            {
                held.Dispose();   // the other program lets go just after the move
                var inMemory = (List<QueuedIdea>)typeof(IdeasModule).GetField("queue", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(module);
                var theirs = new List<QueuedIdea>
                {
                    new QueuedIdea { Id = "a", Text = "first", CreatedAtMs = 1 },
                    new QueuedIdea { Id = "b", Text = "second", CreatedAtMs = 2 },
                    new QueuedIdea { Id = inMemory[0].Id, Text = "the file's copy of the typed idea", CreatedAtMs = 3 },
                };
                TestRunner.Check(IdeasQueue.Save(aside, theirs), "the set-aside file is rewritten with a copy of the typed idea in it");
            };
            try { TestRunner.Check(module.Add("typed while held back"), "queued"); }
            finally { held.Dispose(); }
            TestRunner.Eq(3, module.Waiting, "the file's three ideas, and the typed one counted once");
            List<QueuedIdea> onDisk = IdeasQueue.Load(path);
            TestRunner.Check(onDisk != null && onDisk.Count == 3 && onDisk[0].Id == "a" && onDisk[1].Id == "b" && onDisk[2].Text == "the file's copy of the typed idea", "saved in order, theirs first, with the file's copy of the typed idea kept");
            TestRunner.Eq("", module.Tile().DiskNote, "and nothing is held back or unsaved");
        }

        // The second read fails too (the lock stays): the file stays where it was moved to, and nothing of it is merged in.
        static void AFailedSecondReadLeavesTheCopyAsideAndMergesNothing()
        {
            string dir = TestRunner.NewTempDir();
            string path;
            FileStream lockStream;
            IdeasModule module = HeldBackWithAMovableLock(dir, out path, out lockStream);
            string movedTo = null;
            module.QueueSetAside = delegate(string aside) { movedTo = aside; };
            try
            {
                TestRunner.Check(module.Add("typed while held back"), "queued");
                TestRunner.Check(movedTo != null && File.Exists(movedTo), "the file that couldn't be read was moved aside, and stays there");
                TestRunner.Eq(1, module.Waiting, "none of its ideas are merged in: only the typed one waits");
            }
            finally { lockStream.Dispose(); }
            List<QueuedIdea> copy = IdeasQueue.Load(movedTo);
            TestRunner.Check(copy != null && copy.Count == 2 && copy[0].Id == "a" && copy[1].Id == "b", "the set-aside copy still holds its two ideas");
            List<QueuedIdea> onDisk = IdeasQueue.Load(path);
            TestRunner.Check(onDisk != null && onDisk.Count == 1 && onDisk[0].Text == "typed while held back", "and a fresh ideas-queue.json holds the typed one");
            TestRunner.Eq("", module.Tile().DiskNote, "nothing is held back any more");
        }

        // A queue file that can't be moved is logged by the exception's type only: its message names the file and could say more.
        static void AFileThatCantBeMovedIsLoggedByTypeOnly()
        {
            string dir = TestRunner.NewTempDir();
            string path = Path.Combine(dir, "ideas-queue.json");
            TestRunner.Check(IdeasQueue.Save(path, new List<QueuedIdea> { new QueuedIdea { Id = "a", Text = "first", CreatedAtMs = 1 } }), "saved");
            var letGo = new ManualResetEvent(false);
            Thread holder = HoldOpen(path, 10000, letGo);
            try { new IdeasModule(path, Path.Combine(dir, "ideas-discarded.json"), Path.Combine(dir, "notion-secret.bin"), () => "", TaskScheduler.Default); }
            finally
            {
                letGo.Set();
                holder.Join();
            }
            string line = null;
            foreach (string l in (Files.ReadText(Paths.LogFile) ?? "").Split('\n')) if (l.Contains("set aside an unreadable file")) line = l.TrimEnd('\r');
            TestRunner.Check(line != null && line.EndsWith("set aside an unreadable file (IOException)"), "the failure is logged by its type" + (line != null ? "" : " (no line)"));
        }

        static void QueueItemsAreNeverSilentlyDropped()
        {
            string dir = TestRunner.NewTempDir();
            string path = Path.Combine(dir, "ideas-queue.json");

            File.WriteAllText(path, "[{\"text\":\"no id\",\"created_at\":5},{\"id\":\"b\",\"text\":\"has an id\",\"created_at\":6}]");
            List<QueuedIdea> kept = IdeasQueue.Load(path);
            TestRunner.Check(kept != null && kept.Count == 2, "an item without an id keeps its idea");
            TestRunner.Check(kept != null && kept.Count == 2 && kept[0].Text == "no id" && kept[0].Id != "" && kept[0].Id != kept[1].Id && kept[0].CreatedAtMs == 5, "under a fresh id of its own");

            File.WriteAllText(path, "[{\"id\":\"a\",\"text\":\"fine\"},{\"id\":\"b\"}]");
            TestRunner.Eq(null, IdeasQueue.Load(path), "an item without text can't be kept, so the file isn't taken for a queue");
            var module = new IdeasModule(path, Path.Combine(dir, "ideas-discarded.json"), Path.Combine(dir, "notion-secret.bin"), () => "", TaskScheduler.Default);
            string[] aside = Directory.GetFiles(dir, "ideas-queue.json.unreadable-*");
            TestRunner.Check(!File.Exists(path) && aside.Length == 1 && File.ReadAllText(aside[0]).Contains("fine"), "such a file is set aside as it was, not trimmed to what could be read");

            File.WriteAllText(path, "[{\"x\":1}]");
            TestRunner.Eq(null, IdeasQueue.Load(path), "nor is a file where every item is unusable");

            File.WriteAllText(path, "[{\"id\":\"a\",\"text\":\"one\"},{\"id\":\"a\",\"text\":\"two, copied by hand\"}]");
            List<QueuedIdea> twice = IdeasQueue.Load(path);
            TestRunner.Check(twice != null && twice.Count == 2 && twice[0].Id == "a" && twice[1].Text == "two, copied by hand" && twice[1].Id != "a", "an id the file has twice keeps both ideas, the second under a fresh id: confirming one can't take the other out of the queue");
        }

        static void FailingListenerDoesNotStallSyncing()
        {
            string dir = TestRunner.NewTempDir();
            SecretStore.Save(Path.Combine(dir, "notion-secret.bin"), Secret);
            TestRunner.Check(IdeasQueue.Save(Path.Combine(dir, "ideas-queue.json"), new List<QueuedIdea> { new QueuedIdea { Id = "a", Text = "waiting from before", CreatedAtMs = 1 } }), "saved");
            IdeasModule module = NewModule(dir, NotionTests.Db, FakeNotion.Working());
            int raised = 0;
            module.Changed += delegate { if (Interlocked.Increment(ref raised) == 1) throw new InvalidOperationException("listener broke 9c1d"); };
            try { module.Tick(Clock.NowMs()); }   // the first Changed is raised as this pass starts, and its listener throws
            catch (Exception) { }
            WaitForPasses(module);
            TestRunner.Eq(0, module.Waiting, "a listener that fails once doesn't stop the pass");
            TestRunner.Check(module.Add("a new idea"), "queued");
            WaitForPasses(module);
            TestRunner.Eq(0, module.Waiting, "and ideas added afterwards are still sent");
            string log = Files.ReadText(Paths.LogFile) ?? "";
            TestRunner.Check(log.Contains("a Changed handler threw InvalidOperationException"), "the failure is logged, by its type");
            TestRunner.Check(!log.Contains("9c1d"), "but never with its message");
        }

        // The file's ideas first (they are older), all of them as they are, then the ones in memory that the file hasn't got.
        static void MergeKeepsTheFilesIdeasAsTheyAre()
        {
            var theirs = new List<QueuedIdea>
            {
                new QueuedIdea { Id = "a", Text = "from the file", CreatedAtMs = 1 },
                new QueuedIdea { Id = "b", Text = "also from the file", CreatedAtMs = 2 },
                new QueuedIdea { Id = "b", Text = "the file's second b", CreatedAtMs = 3 },
            };
            var ours = new List<QueuedIdea>
            {
                new QueuedIdea { Id = "b", Text = "b in memory as well", CreatedAtMs = 2 },
                new QueuedIdea { Id = "c", Text = "typed since", CreatedAtMs = 4 },
            };
            List<QueuedIdea> merged = IdeasQueue.Merge(theirs, ours);
            TestRunner.Eq("a,b,b,c", string.Join(",", merged.Select(i => i.Id)), "the file's ideas first, all of them, even an id it has twice; then ours that it hasn't got");
            TestRunner.Eq("also from the file", merged.Count > 1 ? merged[1].Text : null, "where an id is in both, the file's copy is the one kept");
            TestRunner.Eq("the file's second b", merged.Count > 2 ? merged[2].Text : null, "and the file's second idea under that id isn't dropped: it may be one edited in by hand");
            TestRunner.Eq(0, IdeasQueue.Merge(new List<QueuedIdea>(), new List<QueuedIdea>()).Count, "nothing and nothing is nothing");
        }

        static string DiscardedFile(string dir) { return Path.Combine(dir, "ideas-discarded.json"); }

        // A module with Notion set up and these ideas already waiting in its queue file, oldest first (ids q0, q1, ...).
        static IdeasModule WithQueued(string dir, FakeNotion notion, params string[] texts)
        {
            SecretStore.Save(Path.Combine(dir, "notion-secret.bin"), Secret);
            var list = new List<QueuedIdea>();
            for (int i = 0; i < texts.Length; i++) list.Add(new QueuedIdea { Id = "q" + i, Text = texts[i], CreatedAtMs = i + 1 });
            TestRunner.Check(IdeasQueue.Save(Path.Combine(dir, "ideas-queue.json"), list), "the waiting ideas are saved");
            return NewModule(dir, NotionTests.Db, notion);
        }

        // A Notion that refuses every idea (HTTP 400).
        static FakeNotion Picky()
        {
            return Described().Reply("POST", "pages", 400, TestRunner.Fixture("notion-error-validation.json"));
        }

        // Rows are newest first and cut to five, and here the refused idea is the oldest one waiting: it must still be the
        // first row, or "Discard it" would be about an idea nobody can see.
        static void RefusedIdeaStaysOnScreen()
        {
            string dir = TestRunner.NewTempDir();
            IdeasModule module = WithQueued(dir, Picky(), "oldest", "second", "third", "fourth", "fifth", "sixth", "newest");
            module.Tick(Clock.NowMs());   // a pass: Notion refuses the oldest idea
            WaitForPasses(module);
            IdeasTile tile = module.Tile();
            TestRunner.Eq(7, tile.Waiting, "seven wait");
            TestRunner.Eq(IdeasModule.RecentCount, tile.Rows.Count, "five rows show");
            TestRunner.Check(tile.CanDiscard && tile.Rows[0].State == IdeaRow.Rejected && tile.Rows[0].Text == "oldest", "the refused idea is the first row, though it is the oldest of seven (first row: " + tile.Rows[0].Text + ", " + tile.Rows[0].State + ")");
            TestRunner.Check(tile.Rows[1].Text == "newest" && tile.Rows[4].Text == "fourth", "the others follow, newest first");
            int refused = 0;
            foreach (IdeaRow row in tile.Rows) if (row.State == IdeaRow.Rejected) refused++;
            TestRunner.Eq(1, refused, "and it is shown once");
        }

        // Notion can refuse good ideas for a reason that has nothing to do with them (the database was archived, say), and
        // a discard is for good: so the idea is first kept in ideas-discarded.json, and only then dropped.
        static void DiscardKeepsACopyBeforeDropping()
        {
            string dir = TestRunner.NewTempDir();
            IdeasModule module = WithQueued(dir, Picky(), "private refused text 5b2e", "the idea after it");
            module.Tick(Clock.NowMs());
            WaitForPasses(module);
            TestRunner.Check(!File.Exists(DiscardedFile(dir)), "nothing is kept before anything is discarded");
            module.Discard();
            WaitForPasses(module);
            object[] kept = Json.Arr(Json.TryParse(Files.ReadText(DiscardedFile(dir))));
            TestRunner.Check(kept != null && kept.Length == 1, "the discarded idea is in ideas-discarded.json");
            if (kept != null && kept.Length == 1)
            {
                TestRunner.Eq("q0", Json.Str(Json.Get(kept[0], "id")), "with its id");
                TestRunner.Eq("private refused text 5b2e", Json.Str(Json.Get(kept[0], "text")), "its text");
                TestRunner.Check((Json.Num(Json.Get(kept[0], "discarded_at")) ?? 0) > 1000000000000.0, "and when it was discarded");
            }
            TestRunner.Eq(1, module.Waiting, "and it is out of the queue");
            List<QueuedIdea> queueFile = IdeasQueue.Load(Path.Combine(dir, "ideas-queue.json"));
            TestRunner.Check(queueFile != null && queueFile.Count == 1 && queueFile[0].Text == "the idea after it", "and out of the queue file");
            string log = Files.ReadText(Paths.LogFile) ?? "";
            TestRunner.Check(log.Contains("discarded one idea Notion rejected"), "the log says one was discarded");
            TestRunner.Check(!log.Contains("5b2e"), "but never what it said");

            // Discarding again appends: the first copy stays.
            module.Tick(Clock.NowMs() + 2 * IdeasModule.RetryMs);
            WaitForPasses(module);
            module.Discard();
            WaitForPasses(module);
            object[] both = Json.Arr(Json.TryParse(Files.ReadText(DiscardedFile(dir))));
            TestRunner.Check(both != null && both.Length == 2 && Json.Str(Json.Get(both[0], "id")) == "q0" && Json.Str(Json.Get(both[1], "text")) == "the idea after it", "a second discard is added after the first");
        }

        // The idea leaves the queue only once its copy is safe: with no copy, nothing is dropped.
        static void DiscardIsRefusedWhenItsCopyCantBeKept()
        {
            string dir = TestRunner.NewTempDir();
            Directory.CreateDirectory(DiscardedFile(dir));   // a folder stands where the file should go, so it can't be written
            IdeasModule module = WithQueued(dir, Picky(), "refused", "after it");
            module.Tick(Clock.NowMs());
            WaitForPasses(module);
            module.Discard();
            WaitForPasses(module);
            IdeasTile tile = module.Tile();
            TestRunner.Eq(2, module.Waiting, "when no copy can be kept the idea isn't dropped");
            TestRunner.Check(tile.CanDiscard && tile.Rows[0].State == IdeaRow.Rejected && tile.Rows[0].Text == "refused", "it is still the refused row, still offered for discarding");
            TestRunner.Check(tile.Note.Contains("Couldn't keep a copy"), "and the tile says why it wasn't discarded: " + tile.Note);
            TestRunner.Eq(2, IdeasQueue.Load(Path.Combine(dir, "ideas-queue.json")).Count, "the queue file still has both");

            // A damaged archive is no reason to refuse for good: it is set aside, as the queue file is, and a fresh one is begun.
            string dir2 = TestRunner.NewTempDir();
            File.WriteAllText(DiscardedFile(dir2), "{ not a list");
            IdeasModule second = WithQueued(dir2, Picky(), "refused too");
            second.Tick(Clock.NowMs());
            WaitForPasses(second);
            second.Discard();
            WaitForPasses(second);
            string[] aside = Directory.GetFiles(dir2, "ideas-discarded.json.unreadable-*");
            TestRunner.Check(aside.Length == 1 && File.ReadAllText(aside[0]) == "{ not a list", "a damaged archive is kept as it was under another name");
            object[] fresh = Json.Arr(Json.TryParse(Files.ReadText(DiscardedFile(dir2))));
            TestRunner.Check(fresh != null && fresh.Length == 1 && Json.Str(Json.Get(fresh[0], "text")) == "refused too", "and the discarded idea goes into a new one");
            TestRunner.Eq(0, second.Waiting, "so the idea is discarded");
        }

        // Ideas refused one after another, with none accepted in between, point at the destination rather than at the ideas.
        static void RepeatedRefusalsGetAHint()
        {
            string dir = TestRunner.NewTempDir();
            IdeasModule module = WithQueued(dir, Picky(), "first refused", "second refused");
            module.Tick(Clock.NowMs());
            WaitForPasses(module);
            TestRunner.Eq("Notion rejected the idea (validation_error)", module.Tile().Note, "the first refusal says only what Notion said");
            module.Discard();
            WaitForPasses(module);   // the next idea is refused as well
            IdeasTile tile = module.Tile();
            TestRunner.Check(tile.CanDiscard && tile.Rows[0].Text == "second refused", "the second idea is the refused one now");
            TestRunner.Check(tile.Note.StartsWith("Notion rejected the idea (validation_error)") && tile.Note.Contains("still exists"), "a second idea refused in a row adds a hint about the database: " + tile.Note);

            // An idea that is accepted in between starts the count again.
            string dir2 = TestRunner.NewTempDir();
            FakeNotion mixed = Described()
                .Reply("POST", "pages", 400, TestRunner.Fixture("notion-error-validation.json"))
                .Reply("POST", "pages", 200, TestRunner.Fixture("notion-page.json"))
                .Reply("POST", "pages", 400, TestRunner.Fixture("notion-error-validation.json"));
            IdeasModule steady = WithQueued(dir2, mixed, "refused once", "accepted", "refused later");
            steady.Tick(Clock.NowMs());
            WaitForPasses(steady);
            steady.Discard();
            WaitForPasses(steady);
            IdeasTile after = steady.Tile();
            TestRunner.Check(after.CanDiscard && after.Rows[0].Text == "refused later", "the third idea is refused after the second was accepted");
            TestRunner.Eq("Notion rejected the idea (validation_error)", after.Note, "which isn't a repeat: no hint");

            // Retrying an idea that is refused again isn't a second idea either.
            steady.Tick(Clock.NowMs() + 2 * IdeasModule.RetryMs);
            WaitForPasses(steady);
            TestRunner.Eq("Notion rejected the idea (validation_error)", steady.Tile().Note, "the same idea refused again is no repeat");
        }

        // A query reply with one page in it, for a pass that reads the recent list.
        static string QueryReply(string pageId, string title)
        {
            return "{\"object\":\"list\",\"results\":[{\"object\":\"page\",\"id\":\"" + pageId + "\",\"created_time\":\"2026-10-01T10:15:00.000Z\",\"url\":\"https://app.notion.com/p/" + pageId.Replace("-", "") + "\","
                + "\"properties\":{\"Idea\":{\"id\":\"title\",\"type\":\"title\",\"title\":[{\"type\":\"text\",\"text\":{\"content\":\"" + title + "\"},\"plain_text\":\"" + title + "\"}]}}}],\"next_cursor\":null,\"has_more\":false}";
        }

        // Every pass that sends ideas also reads Notion's recent list, and the page just created is normally in it: its tick
        // (Notion confirmed it) has to survive that, not turn into a plain row.
        static void ConfirmedIdeaKeepsItsTickWhenNotionListsIt()
        {
            string dir = TestRunner.NewTempDir();
            SecretStore.Save(Path.Combine(dir, "notion-secret.bin"), Secret);
            const string Created = "59833787-2cf9-4fdf-8782-e53db20768a5";   // the page notion-page.json says Notion created
            FakeNotion notion = Described()
                .Reply("POST", "pages", 200, TestRunner.Fixture("notion-page.json"))
                .Reply("POST", "data_sources/" + NotionTests.Source + "/query", 200, QueryReply(Created, "Recipe app: add a shopping list"));
            IdeasModule module = NewModule(dir, NotionTests.Db, notion);
            module.Add("Recipe app: add a shopping list");
            WaitForPasses(module);
            IdeasTile tile = module.Tile();
            TestRunner.Eq(1, tile.Rows.Count, "the new page is one row: Notion's list has it, so it isn't shown twice");
            TestRunner.Check(tile.Rows[0].State == IdeaRow.Saved && tile.Rows[0].Text == "Recipe app: add a shopping list", "and that row has its tick (state: " + tile.Rows[0].State + ")");
            TestRunner.Check(tile.Rows[0].Url.StartsWith("https://app.notion.com/p/"), "and its Notion link");
            module.Refresh();   // the list is read again
            WaitForPasses(module);
            TestRunner.Eq(IdeaRow.Saved, module.Tile().Rows[0].State, "and the tick stays when Notion's list is read again");
        }

        // A Retry-After of days must not park the retries: it is cut to five minutes.
        static void HugeRetryAfterDoesNotParkTheRetries()
        {
            string dir = TestRunner.NewTempDir();
            SecretStore.Save(Path.Combine(dir, "notion-secret.bin"), Secret);
            FakeNotion notion = Described();
            int posts = 0;
            IdeasModule module = NewModule(dir, NotionTests.Db, notion);
            module.NewClient = s => new NotionClient(s)
            {
                Transport = r =>
                {
                    if (r.Method != "POST" || r.Path != "pages") return notion.Answer(r);
                    Interlocked.Increment(ref posts);
                    return new HttpResult { Status = 429, RetryAfterSeconds = 30L * 24 * 3600, Body = "{\"code\":\"rate_limited\"}" };   // "come back in 30 days"
                }
            };
            module.Add("an idea held up by a huge Retry-After");
            WaitForPasses(module);
            int sent = posts;
            TestRunner.Eq(1, sent, "the first try");
            module.Tick(Clock.NowMs() + 4 * 60 * 1000);
            WaitForPasses(module);
            TestRunner.Eq(sent, posts, "four minutes later it is still waiting");
            module.Tick(Clock.NowMs() + 5 * 60 * 1000 + 5000);
            WaitForPasses(module);
            TestRunner.Eq(sent + 1, posts, "and after five minutes it tries again, not after 30 days");
            TestRunner.Eq(IdeasModule.MaxRetryAfterMs, IdeasModule.WaitMs(new HttpResult { Status = 429, RetryAfterSeconds = long.MaxValue }), "even a silly Retry-After isn't multiplied into the past");
            TestRunner.Eq(30000L, IdeasModule.WaitMs(new HttpResult { Status = 429, RetryAfterSeconds = 30 }), "a short one is waited out as given");
            TestRunner.Eq(1000L, IdeasModule.WaitMs(new HttpResult { Status = 429 }), "a 429 that names no time waits a second");
            TestRunner.Eq(IdeasModule.RetryMs, IdeasModule.WaitMs(new HttpResult { Status = 503 }), "any other failure waits the usual minute");
            TestRunner.Eq(IdeasModule.RetryMs, IdeasModule.WaitMs(null), "and so does a pass that didn't fail");
        }

        // An idea added while a pass is running isn't left for the next minute: it is sent right after that pass.
        static void AnIdeaAddedDuringAPassIsSentRightAfter()
        {
            string dir = TestRunner.NewTempDir();
            SecretStore.Save(Path.Combine(dir, "notion-secret.bin"), Secret);
            FakeNotion notion = FakeNotion.Working();
            var inFlight = new ManualResetEvent(false);
            var release = new ManualResetEvent(false);
            var sentTexts = new List<string>();
            IdeasModule module = NewModule(dir, NotionTests.Db, notion);
            module.NewClient = s => new NotionClient(s)
            {
                Transport = r =>
                {
                    if (r.Method != "POST" || r.Path != "pages") return notion.Answer(r);
                    bool first;
                    lock (sentTexts)
                    {
                        sentTexts.Add(ContentOf(r));
                        first = sentTexts.Count == 1;
                    }
                    if (first)
                    {
                        inFlight.Set();
                        release.WaitOne(10000);   // Notion is slow to answer the first one, until the test lets it
                    }
                    return notion.Answer(r);
                }
            };
            module.Add("first idea");
            TestRunner.Check(inFlight.WaitOne(10000), "the first idea's pass is under way, waiting for Notion's reply");
            TestRunner.Check(module.Add("second idea"), "an idea added meanwhile is queued");
            release.Set();
            WaitForPasses(module);
            lock (sentTexts) TestRunner.Eq("first idea,second idea", string.Join(",", sentTexts), "both are sent, in order, without waiting for a tick");
            TestRunner.Eq(0, module.Waiting, "and nothing is left waiting");
        }

        // Polish spec §4.3: the tile tells saved ideas that can't be read yet (the queue file is held back) from an idea
        // that couldn't be written, a line each, apart from what it says about Notion.
        static void DiskNotesSayWhichIdeasArentSafeYet()
        {
            string dir = TestRunner.NewTempDir();
            string path = Path.Combine(dir, "ideas-queue.json");
            TestRunner.Check(IdeasQueue.Save(path, new List<QueuedIdea> { new QueuedIdea { Id = "a", Text = "saved before", CreatedAtMs = 1 } }), "saved");
            var letGo = new ManualResetEvent(false);
            Thread holder = HoldOpen(path, 10000, letGo);
            var module = new IdeasModule(path, Path.Combine(dir, "ideas-discarded.json"), Path.Combine(dir, "notion-secret.bin"), () => "", TaskScheduler.Default);   // Notion isn't set up
            IdeasTile tile = module.Tile();
            TestRunner.Eq("Saved ideas can't be read yet. Retrying.", tile.DiskNote, "held back: the saved ideas can't be read yet");
            TestRunner.Check(tile.Note.Contains("Set up Notion") && !tile.Note.Contains("Retrying"), "the setup hint is a note of its own");
            module.Tick(Clock.NowMs());   // still locked, nothing typed: the retry fails but there is no idea to lose
            TestRunner.Eq("Saved ideas can't be read yet. Retrying.", module.Tile().DiskNote, "a tick while the file is still held, with nothing typed, adds no line about an idea that couldn't be written");
            module.Tick(Clock.NowMs());
            TestRunner.Eq("Saved ideas can't be read yet. Retrying.", module.Tile().DiskNote, "nor does a second one");
            module.Add("typed while the file is held");
            TestRunner.Eq("Saved ideas can't be read yet. Retrying.\nCouldn't save to disk yet. Retrying.", module.Tile().DiskNote, "and an idea typed meanwhile can't be written either: both are said");
            letGo.Set();
            holder.Join();
            module.Tick(Clock.NowMs());
            TestRunner.Eq("", module.Tile().DiskNote, "once the file is free, the ideas are read and written, and both notes go");
            TestRunner.Eq(2, module.Waiting, "the saved idea and the new one");
        }

        // Polish spec §4.6: saving another database clears the ✓ marks of the old one's pages, also when a pass for the old
        // one ends after the switch.
        static void SwitchingDatabasesClearsTheTicks()
        {
            const string Other = "fedcba9876543210fedcba9876543210";
            string dir = TestRunner.NewTempDir();
            SecretStore.Save(Path.Combine(dir, "notion-secret.bin"), Secret);
            string db = NotionTests.Db;
            var module = new IdeasModule(Path.Combine(dir, "ideas-queue.json"), Path.Combine(dir, "ideas-discarded.json"), Path.Combine(dir, "notion-secret.bin"), () => db, TaskScheduler.Default);
            module.NewClient = s => new NotionClient(s) { Transport = FakeNotion.Working().Answer };
            module.Add("an idea for the first database");
            WaitForPasses(module);
            TestRunner.Check(module.Tile().Rows.Any(r => r.State == IdeaRow.Saved), "confirmed by the first database: a ✓");
            db = Other;   // another database saved in the settings; this fake Notion doesn't know it
            module.SettingsChanged();
            WaitForPasses(module);
            TestRunner.Check(!module.Tile().Rows.Any(r => r.State == IdeaRow.Saved), "after switching databases, no ✓ of the first one's is left");

            // A pass for the first database that ends after the switch brings back neither a ✓ nor that database's pages.
            string dir2 = TestRunner.NewTempDir();
            SecretStore.Save(Path.Combine(dir2, "notion-secret.bin"), Secret);
            string db2 = NotionTests.Db;
            FakeNotion notion = FakeNotion.Working();
            var inFlight = new ManualResetEvent(false);
            var release = new ManualResetEvent(false);
            var late = new IdeasModule(Path.Combine(dir2, "ideas-queue.json"), Path.Combine(dir2, "ideas-discarded.json"), Path.Combine(dir2, "notion-secret.bin"), () => db2, TaskScheduler.Default);
            late.NewClient = s => new NotionClient(s)
            {
                Transport = r =>
                {
                    if (r.Method == "POST" && r.Path == "pages")
                    {
                        inFlight.Set();
                        release.WaitOne(10000);   // Notion is slow to answer, until the test lets it
                    }
                    return notion.Answer(r);
                }
            };
            late.Add("sent as the database changes");
            TestRunner.Check(inFlight.WaitOne(10000), "the pass for the first database is under way");
            db2 = Other;
            late.SettingsChanged();
            release.Set();
            WaitForPasses(late);
            TestRunner.Eq(0, late.Waiting, "Notion took the idea, so it is out of the queue");
            TestRunner.Eq(0, late.Tile().Rows.Count, "but no ✓ and no page of the old database is shown");
        }

        // Polish spec §4.5: a pass that faulted is logged by the exception's type, never by its message, which could say
        // anything (a reply quoting an idea, say).
        static void AFaultedPassIsLoggedByItsTypeOnly()
        {
            string dir = TestRunner.NewTempDir();
            SecretStore.Save(Path.Combine(dir, "notion-secret.bin"), Secret);
            IdeasModule module = NewModule(dir, NotionTests.Db, new FakeNotion());
            module.NewClient = s => new NotionClient(s) { Transport = r => { throw new InvalidOperationException("private 77c1: a reply that quotes an idea"); } };
            module.Add("an idea whose pass faults");
            WaitForPasses(module);
            string log = Files.ReadText(Paths.LogFile) ?? "";
            TestRunner.Check(log.Contains("ideas: a sync pass failed (InvalidOperationException)"), "a pass that faulted is logged, by the exception's type");
            TestRunner.Check(!log.Contains("77c1"), "never with its message");
            TestRunner.Eq(1, module.Waiting, "and the idea stays queued");
        }
    }
}
