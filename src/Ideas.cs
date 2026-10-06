using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Capsule
{
    // An idea typed but not yet confirmed by Notion.
    public sealed class QueuedIdea
    {
        public string Id = "";   // Capsule's own id for it
        public string Text = "";
        public long CreatedAtMs;
    }

    // ideas-queue.json: the ideas Notion hasn't confirmed yet, oldest first (spec §4).
    // A file Capsule can't make sense of is never written over: IdeasModule moves it aside first, and while it can't
    // even be moved it leaves the file alone, keeps the queue in memory, and reads the file again later.
    public static class IdeasQueue
    {
        // A file that exists but can't be read is usually another program's brief lock (a virus scan, say): it is tried
        // again a few times before it is given up on.
        const int ReadAttempts = 5, ReadRetryMs = 50;

        // The queue: empty when there is no file. Null when the file can't be trusted to be one: it can't be read, isn't
        // a JSON array, or has an item without text (it is then set aside, not overwritten: it may hold ideas). An item
        // with text but no id, or with an id an earlier item has (a file edited by hand), is kept under a fresh id: ideas
        // leave the queue by id, so two under one id would both go when Notion confirmed one. attempts: how often a file
        // that can't be read is tried (a caller that comes back every few seconds anyway can try once).
        public static List<QueuedIdea> Load(string path, int attempts = ReadAttempts)
        {
            string text = Files.ReadText(path);
            for (int attempt = 1; text == null && attempt < attempts && File.Exists(path); attempt++)
            {
                Thread.Sleep(ReadRetryMs);
                text = Files.ReadText(path);
            }
            if (text == null && File.Exists(path)) return null;
            var list = new List<QueuedIdea>();
            if (string.IsNullOrWhiteSpace(text)) return list;
            object[] items = Json.Arr(Json.TryParse(text));
            if (items == null) return null;
            var ids = new HashSet<string>();
            foreach (object item in items)
            {
                string id = Json.Str(Json.Get(item, "id"));
                string idea = Json.Str(Json.Get(item, "text"));
                if (string.IsNullOrEmpty(idea)) return null;   // not an idea: skipping it would lose it at the next save
                if (string.IsNullOrEmpty(id) || ids.Contains(id)) id = Guid.NewGuid().ToString("N");   // but it is still an idea
                ids.Add(id);
                list.Add(new QueuedIdea { Id = id, Text = idea, CreatedAtMs = (long)(Json.Num(Json.Get(item, "created_at")) ?? 0) });
            }
            return list;
        }

        public static bool Save(string path, IEnumerable<QueuedIdea> ideas)
        {
            var list = new List<object>();
            foreach (QueuedIdea i in ideas) list.Add(new Dictionary<string, object> { { "id", i.Id }, { "text", i.Text }, { "created_at", i.CreatedAtMs } });
            return Files.WriteAtomic(path, Json.Write(list));
        }

        // The ideas of a file that was read late (theirs, the older ones) and the ones queued in memory since (ours), in that
        // order. The file's ideas are all kept, as they are; only ours that the file has too (by id) are dropped, so where
        // an id is in both, theirs is the copy kept.
        public static List<QueuedIdea> Merge(IEnumerable<QueuedIdea> theirs, IEnumerable<QueuedIdea> ours)
        {
            var merged = new List<QueuedIdea>(theirs);
            var inFile = new HashSet<string>(merged.Select(i => i.Id));
            foreach (QueuedIdea idea in ours) if (!inFile.Contains(idea.Id)) merged.Add(idea);
            return merged;
        }
    }

    // What one pass of sending the queue to Notion did.
    public sealed class SyncResult
    {
        public NotionDatabase Database;                            // known or just described; null when that failed
        public List<string> Sent = new List<string>();             // ids Notion confirmed, in order
        public List<NotionPage> Created = new List<NotionPage>();  // their pages, in the same order
        public HttpResult Failure;                                 // what stopped the pass, or null
        public bool FailedCreating;                                // the failure came while adding an idea
        public string RejectedId;                                  // the idea Notion refused (HTTP 400), or null
        public List<NotionPage> Recent;                            // newest first, when read
    }

    public static class IdeasSync
    {
        // Sends the pending ideas in order, stopping at the first failure so the queue keeps its order, then reads
        // the recent ideas if asked to or if any were sent. Runs on a worker thread; touches nothing but its arguments.
        public static SyncResult Run(NotionClient client, string databaseId, NotionDatabase known, IList<QueuedIdea> pending, bool readRecent)
        {
            var result = new SyncResult();
            NotionDatabase db = known ?? client.Describe(databaseId);
            if (db == null)
            {
                result.Failure = client.LastFailure;
                return result;
            }
            result.Database = db;
            foreach (QueuedIdea idea in pending)
            {
                NotionPage page = client.Create(db, idea.Text);
                if (page == null)
                {
                    result.Failure = client.LastFailure;
                    result.FailedCreating = true;
                    if (result.Failure != null && result.Failure.Status == 400)
                    {
                        result.RejectedId = idea.Id;
                        result.Database = null;   // a renamed or removed title property also gives 400s: read it again
                    }
                    return result;
                }
                result.Sent.Add(idea.Id);
                result.Created.Add(page);
            }
            if (readRecent || result.Sent.Count > 0)
            {
                List<NotionPage> recent = client.Recent(db, IdeasModule.RecentCount);
                if (recent != null) result.Recent = recent;
                else result.Failure = client.LastFailure;
            }
            return result;
        }
    }

    // Ideas → Notion (spec §4). Every idea goes into the queue on disk first, and leaves it only when Notion confirms
    // its page (or when the user discards one Notion refused, after a copy of it is kept in ideas-discarded.json). The
    // queue is sent in order: every minute while it isn't empty, as soon as the network is back, and when the panel
    // opens. An idea that couldn't be written to disk waits in memory, and every tick tries to write it again. Idea text
    // never reaches the log: only counts and statuses do.
    public sealed class IdeasModule : IModule
    {
        public const int MaxChars = 2000;   // Notion's limit for one piece of text
        public const int RecentCount = 5;
        public const long RetryMs = 60 * 1000;
        public const long MaxRetryAfterMs = 5 * 60 * 1000;   // a longer Retry-After is cut to this, so a huge one can't park the retries for days
        public const string RefusalHint = "More than one idea was refused. Check that the database still exists and is shared with Capsule's connection.";

        readonly string queuePath, discardedPath, secretPath;
        readonly Func<string> databaseId;   // config.json's notion_database
        readonly TaskScheduler ui;
        readonly List<QueuedIdea> queue;
        readonly List<NotionPage> saved = new List<NotionPage>();   // confirmed since Capsule started, newest first
        readonly HashSet<string> confirmed = new HashSet<string>();   // their page ids: Notion's own list shows those with the tick too
        string marksFor;   // the database the ✓ marks above belong to
        List<NotionPage> recent = new List<NotionPage>();
        NotionDatabase database;
        string describedFor = "";
        HttpResult failure;
        string problem = "";   // the line the tile shows about it
        string rejectedId;
        string lastRefused;    // the idea Notion refused in the latest pass that refused one
        int refusedInARow;     // different ideas it refused one after another, with none accepted in between
        bool discardFailed;    // Discard couldn't keep a copy of the refused idea, so it kept the idea too
        string lastLogged = "";
        long nextTryMs;
        bool busy, again, againRecent;
        bool heldBack;   // the unreadable queue file couldn't be moved aside, so it mustn't be written over yet
        bool unsaved;    // ideas-queue.json couldn't be written: until it can, the queue exists only in memory

        public Func<string, NotionClient> NewClient = secret => new NotionClient(secret);   // tests swap in a fake
        public Action<string> QueueSetAside;         // tests: told the new name right after the held-back queue file is moved aside
        public event Action Changed;                 // raised on the UI thread
        public Task LastSync { get; private set; }   // the last pass, for tests to wait on

        // discardedPath: where Discard keeps a copy of the idea it drops. ui: where results are applied (the UI thread's
        // scheduler; tests pass TaskScheduler.Default).
        public IdeasModule(string queuePath, string discardedPath, string secretPath, Func<string> databaseId, TaskScheduler ui)
        {
            this.queuePath = queuePath;
            this.discardedPath = discardedPath;
            this.secretPath = secretPath;
            this.databaseId = databaseId;
            this.ui = ui;
            marksFor = databaseId();
            queue = IdeasQueue.Load(queuePath);
            if (queue == null)
            {
                heldBack = !SetAside(queuePath, true);
                if (heldBack) Log.Info("ideas: ideas-queue.json can't be read or moved aside; it is left alone and the queue is kept in memory until it can be moved");
                queue = new List<QueuedIdea>();
            }
            if (queue.Count > 0) Log.Info("ideas: " + queue.Count + " waiting to sync");
        }

        // A queue or archive file Capsule can't read is kept under another name, never overwritten: it may hold ideas.
        // False when it can't be moved either (it is locked, say): the file is then left alone, and not written until a
        // later try gets it out of the way (for the queue file, SaveQueue makes that try, on every tick).
        static bool SetAside(string path, bool logFailure)
        {
            string aside;
            return SetAside(path, logFailure, out aside);
        }

        // aside: the file's new name, or null when it was gone and nothing was moved.
        static bool SetAside(string path, bool logFailure, out string aside)
        {
            aside = null;
            if (!File.Exists(path)) return true;   // gone meanwhile: nothing is left to protect
            string name = path + ".unreadable-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            try
            {
                File.Move(path, name);
                aside = name;
                Log.Info("ideas: " + Path.GetFileName(path) + " couldn't be read; kept as " + Path.GetFileName(name));
                return true;
            }
            catch (Exception e)
            {
                if (logFailure) Log.Error("ideas: set aside an unreadable file (" + e.GetType().Name + ")", null);   // the type only: a message could say anything
                return false;
            }
        }

        public bool Configured { get { return databaseId() != "" && SecretStore.Exists(secretPath); } }
        public int Waiting { get { return queue.Count; } }

        // Queues an idea and starts sending it. False for an empty one. If the queue file can't be written the idea is
        // still queued and shown (the tile says it isn't saved yet), and Tick keeps trying to write it.
        public bool Add(string text)
        {
            string idea = Clean(text);
            if (idea == "") return false;
            queue.Add(new QueuedIdea { Id = Guid.NewGuid().ToString("N"), Text = idea, CreatedAtMs = Clock.NowMs() });
            SaveQueue();
            Raise();
            Sync(false);
            return true;
        }

        // One line of at most MaxChars, never cutting a character in half.
        public static string Clean(string text)
        {
            string t = (text ?? "").Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (t.Length > MaxChars) t = t.Substring(0, char.IsHighSurrogate(t[MaxChars - 1]) ? MaxChars - 1 : MaxChars).TrimEnd();
            return t;
        }

        // Drops the idea Notion refused, so the ones after it can go. A discard is for good, and Notion can refuse good
        // ideas for a reason that has nothing to do with them (an archived database, say), so the idea is first kept in
        // ideas-discarded.json and leaves the queue only once that copy is written; if it can't be, the idea stays.
        public void Discard()
        {
            if (rejectedId == null) return;
            QueuedIdea idea = queue.FirstOrDefault(i => i.Id == rejectedId);
            if (idea != null && !Archive(discardedPath, idea))
            {
                discardFailed = true;
                Log.Info("ideas: couldn't keep a copy of the refused idea in ideas-discarded.json; it stays queued");
                Raise();
                return;
            }
            discardFailed = false;
            queue.RemoveAll(i => i.Id == rejectedId);
            SaveQueue();
            Log.Info("ideas: discarded one idea Notion rejected");
            rejectedId = null;
            failure = null;
            problem = "";
            Raise();
            Sync(false);
        }

        // Adds the idea to ideas-discarded.json, a list of {id, text, discarded_at}. False when that can't be done: the
        // file can't be read right now or written, or it is no list and can't be set aside. As with the queue file, one
        // Capsule can't understand is set aside before a new one is begun, so nothing in it is ever written over.
        static bool Archive(string path, QueuedIdea idea)
        {
            var items = new List<object>();
            string text = Files.ReadText(path);
            if (text == null && File.Exists(path)) return false;   // locked, say: the discard can be tried again
            if (!string.IsNullOrWhiteSpace(text))
            {
                object[] earlier = Json.Arr(Json.TryParse(text));
                if (earlier != null) items.AddRange(earlier);
                else if (!SetAside(path, true)) return false;
            }
            if (!items.Any(i => Json.Str(Json.Get(i, "id")) == idea.Id))
                items.Add(new Dictionary<string, object> { { "id", idea.Id }, { "text", idea.Text }, { "discarded_at", Clock.NowMs() } });
            return Files.WriteAtomic(path, Json.Write(items));
        }

        public void PanelOpened() { Sync(true); }

        // The secret or the database link changed: start afresh with them.
        public void SettingsChanged()
        {
            if (databaseId() != marksFor)   // another database: the ✓ marks were for the old one's pages (polish spec §4.6)
            {
                saved.Clear();
                confirmed.Clear();
                marksFor = databaseId();
            }
            database = null;
            describedFor = "";
            recent = new List<NotionPage>();
            failure = null;
            problem = "";
            rejectedId = null;
            lastRefused = null;
            refusedInARow = 0;
            discardFailed = false;
            Sync(true);
        }

        // Ideas that exist only in memory aren't safe, so first another try at writing the queue. Sending is not done
        // while a pass is running: a tick then would only queue another pass behind it, which would start the moment this
        // one ends and skip the wait it just set (a minute, or the Retry-After after a 429).
        public void Tick(long now)
        {
            if (unsaved || heldBack) RetrySave();
            if (!busy && queue.Count > 0 && now >= nextTryMs) Sync(false);
        }
        public void Refresh() { Sync(true); }
        public void NetworkBack() { if (queue.Count > 0) Sync(false); }
        public void Resumed() { if (queue.Count > 0) Sync(false); }

        void Sync(bool readRecent)
        {
            if (busy)
            {
                again = true;
                againRecent |= readRecent;
                return;
            }
            string id = databaseId();
            if (id == "" || !SecretStore.Exists(secretPath)) return;   // the tile says how to set up; ideas wait
            string secret = SecretStore.Load(secretPath);
            if (secret == null)
            {
                problem = "Can't read the saved Notion secret — paste it again in ⚙";
                Raise();
                return;
            }
            if (describedFor != id)
            {
                database = null;
                describedFor = id;
                recent = new List<NotionPage>();
            }
            busy = true;
            Raise();   // queued rows show "saving…" while this runs
            var pending = new List<QueuedIdea>(queue);
            NotionDatabase known = database;
            NotionClient client = NewClient(secret);
            LastSync = Task.Run(() => IdeasSync.Run(client, id, known, pending, readRecent)).ContinueWith(t => Apply(t, id), ui);
        }

        // Runs as a continuation nobody observes, so nothing may escape it: an exception there would vanish and skip the
        // rest, including the pass an idea added meanwhile is waiting for. It is logged (its type only: its message
        // could say anything) and the bookkeeping below still completes.
        void Apply(Task<SyncResult> t, string id)
        {
            busy = false;
            try
            {
                if (t.IsFaulted)
                {
                    Exception e = t.Exception.GetBaseException();
                    Log.Error("ideas: a sync pass failed (" + e.GetType().Name + ")", null);   // never its message: it could say anything
                    failure = new HttpResult { Status = 0, Error = e.GetType().Name };
                    problem = NotionClient.Problem(failure, true);
                }
                else
                {
                    SyncResult r = t.Result;
                    if (describedFor == id) database = r.Database;
                    bool shown = id == marksFor;   // false when another database was saved while this pass ran
                    if (r.Sent.Count > 0)
                    {
                        queue.RemoveAll(i => r.Sent.Contains(i.Id));
                        SaveQueue();
                        if (shown)
                        {
                            foreach (NotionPage page in r.Created)
                            {
                                saved.Insert(0, page);
                                confirmed.Add(page.Id);
                            }
                        }
                        lastRefused = null;   // Notion takes ideas, so the refusals before were about those ideas
                        refusedInARow = 0;
                    }
                    if (r.Recent != null && shown)
                    {
                        recent = r.Recent;
                        saved.RemoveAll(p => recent.Any(x => x.Id == p.Id));
                    }
                    failure = r.Failure;
                    problem = NotionClient.Problem(failure, r.FailedCreating);
                    if (r.RejectedId != rejectedId) discardFailed = false;
                    rejectedId = r.RejectedId;
                    if (r.RejectedId != null && r.RejectedId != lastRefused)
                    {
                        lastRefused = r.RejectedId;
                        refusedInARow++;
                    }
                    string status = (r.Sent.Count > 0 ? "sent " + r.Sent.Count + ", " : "") + queue.Count + " waiting" + (failure != null ? " (" + Describe(failure) + ")" : "");
                    if (r.Sent.Count > 0 || status != lastLogged) Log.Info("ideas: " + status);
                    lastLogged = status;
                }
            }
            catch (Exception e)
            {
                Log.Error("ideas: applying a pass's result threw " + e.GetType().Name, null);
            }
            nextTryMs = Clock.NowMs() + WaitMs(failure);
            Raise();
            if (again)
            {
                bool readRecent = againRecent;
                again = false;
                againRecent = false;
                Sync(readRecent);
            }
        }

        // The wait before the next try after a pass that ended with this failure (null: it didn't): the Retry-After after
        // a 429, cut to MaxRetryAfterMs, otherwise a minute.
        public static long WaitMs(HttpResult failure)
        {
            if (failure == null || failure.Status != 429) return RetryMs;
            return Math.Min(MaxRetryAfterMs / 1000, Math.Max(1, failure.RetryAfterSeconds)) * 1000;
        }

        // A failure for the log: its status and Notion's error code, never a body.
        static string Describe(HttpResult r)
        {
            if (r.Status == 0) return "no reply: " + r.Error;
            string code = NotionClient.Code(r);
            return "HTTP " + r.Status + (code != "" ? " " + code : "");
        }

        // Writes the queue to ideas-queue.json. False when it can't be: the unreadable file is still in the way, or the
        // write failed. The ideas then exist only in memory, so `unsaved` stays set (the tile says so) and Tick tries again.
        bool SaveQueue()
        {
            if (heldBack && !TakeBackHeldFile())
            {
                SetUnsaved(true);
                return false;
            }
            bool written = IdeasQueue.Save(queuePath, queue);
            SetUnsaved(!written);
            return written;
        }

        // The log hears of a change, not of every try.
        void SetUnsaved(bool now)
        {
            if (now == unsaved) return;
            unsaved = now;
            Log.Info(now ? "ideas: couldn't write ideas-queue.json; the queue is kept in memory and tried again" : "ideas: ideas-queue.json written again");
        }

        // The queue file that couldn't be read or moved aside when Capsule started was left alone. Another look at it: if it
        // can be read now, its ideas (the older ones) come back into the queue ahead of the newer ones, rather than stay
        // behind in a set-aside file; if it still isn't a queue it is set aside, as it would have been then. False while
        // it is still in the way.
        bool TakeBackHeldFile()
        {
            List<QueuedIdea> theirs = IdeasQueue.Load(queuePath, 1);   // one quick try: a tick comes back every few seconds
            if (theirs != null)
            {
                TakeBack(theirs);
                if (theirs.Count > 0) Log.Info("ideas: ideas-queue.json can be read again; " + theirs.Count + " waiting to sync from it");
                heldBack = false;
                return true;
            }
            string aside;
            heldBack = !SetAside(queuePath, false, out aside);
            if (heldBack || aside == null) return !heldBack;
            // The lock can go between that try and the move: one more try, at the file where it is now, with Load's usual
            // few attempts (a brief lock that hasn't cleared yet mustn't leave the copy set aside for good). A queue after
            // all, its ideas come back too; the set-aside copy is kept either way.
            if (QueueSetAside != null) QueueSetAside(aside);
            List<QueuedIdea> moved = IdeasQueue.Load(aside);
            if (moved != null && moved.Count > 0)
            {
                TakeBack(moved);
                Log.Info("ideas: the set-aside queue file could be read after all; " + moved.Count + " waiting to sync from it");
            }
            return true;
        }

        // The ideas of a queue file read late (the older ones) back into the queue, ahead of the ones queued since.
        void TakeBack(List<QueuedIdea> theirs)
        {
            List<QueuedIdea> merged = IdeasQueue.Merge(theirs, queue);
            queue.Clear();
            queue.AddRange(merged);
        }

        // Another try at getting the queue onto the disk, while a write has failed or the queue file is held back. The
        // tile is told if that changed what it shows.
        void RetrySave()
        {
            int before = queue.Count;
            bool wasStuck = unsaved || heldBack;
            SaveQueue();
            if (queue.Count != before || wasStuck != (unsaved || heldBack)) Raise();
        }

        // A listener that throws is logged (its type only: its message could say anything) and ignored, so it can never
        // stop a pass from starting, or from being applied, or an idea from being sent.
        void Raise()
        {
            try { if (Changed != null) Changed(); }
            catch (Exception e) { Log.Error("ideas: a Changed handler threw " + e.GetType().Name, null); }
        }

        // The tile: the refused idea first, if there is one, then the waiting ideas (newest first), then ones confirmed
        // since Capsule started, then Notion's own, five rows in all. The refused idea is the first one Notion turned down,
        // usually the oldest waiting, though not always (ideas from a queue file read late go ahead of it): wherever it is
        // in the queue it leads, or the cut to five could hide it while "Discard it" still showed.
        public IdeasTile Tile()
        {
            var t = new IdeasTile();
            t.Configured = Configured;
            t.Waiting = queue.Count;
            QueuedIdea refused = rejectedId == null ? null : queue.FirstOrDefault(i => i.Id == rejectedId);
            t.CanDiscard = refused != null;
            if (refused != null) t.Rows.Add(new IdeaRow { Text = refused.Text, State = IdeaRow.Rejected });
            for (int i = queue.Count - 1; i >= 0; i--)
            {
                QueuedIdea idea = queue[i];
                if (idea == refused) continue;
                string state = busy ? IdeaRow.Saving
                    : failure != null || !t.Configured || problem != "" ? IdeaRow.Waiting : IdeaRow.Saving;
                t.Rows.Add(new IdeaRow { Text = idea.Text, State = state });
            }
            foreach (NotionPage p in saved) t.Rows.Add(new IdeaRow { Text = p.Title, State = IdeaRow.Saved, Url = p.Url });
            foreach (NotionPage p in recent) t.Rows.Add(new IdeaRow { Text = p.Title, State = confirmed.Contains(p.Id) ? IdeaRow.Saved : "", Url = p.Url });
            if (t.Rows.Count > RecentCount) t.Rows.RemoveRange(RecentCount, t.Rows.Count - RecentCount);
            t.Note = NoteFor(t.Configured, refused != null);
            t.DiskNote = DiskNote();
            return t;
        }

        // What the tile says besides its rows, a line each: how to set up or what went wrong with Notion, a hint when
        // refusals repeat, and a discard that wasn't done.
        string NoteFor(bool configured, bool refusalShowing)
        {
            var lines = new List<string>();
            if (!configured) lines.Add("Set up Notion in ⚙ to send ideas there. They wait here until then.");
            else if (problem != "") lines.Add(problem);
            if (refusalShowing && refusedInARow >= 2) lines.Add(RefusalHint);
            if (refusalShowing && discardFailed) lines.Add("Couldn't keep a copy of that idea, so it wasn't discarded.");
            return string.Join("\n", lines);
        }

        // Ideas that aren't safe on disk yet, a line per reason (polish spec §4.3): the saved ones can't be read yet (the
        // queue file is held back), or one couldn't be written. Tick tries again every few seconds meanwhile.
        string DiskNote()
        {
            var lines = new List<string>();
            if (heldBack) lines.Add("Saved ideas can't be read yet. Retrying.");
            if (unsaved && (!heldBack || queue.Count > 0)) lines.Add("Couldn't save to disk yet. Retrying.");   // held back with nothing typed: there is no idea that couldn't be written
            return string.Join("\n", lines);
        }
    }
}
