using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Capsule
{
    // Every order of events, up to a bound, run against the real PromptBroker. After each step it checks the promises in
    // PromptBroker's header: every request gets exactly one reply (none once its hook went away), the card shows the oldest,
    // a click answers only the request it names and only as it says, nothing is held past its cap or for a tick after the
    // Claude app comes to the front, and a request that comes to the front gets ReadMs to be read. A failure names the steps
    // that led to it, shortest first.
    // Bounds: up to MaxRequests requests, each in its own session and a command (Bash); every sequence of up to Depth steps,
    // then seeded random walks of WalkSteps. The clock ticks every 250 ms while anything is held, as Controller's promptTick
    // does. Questions and plans are left out: they are answered through the same Finish as Allow.
    public static class PromptBrokerOrderTests
    {
        const long Start = 1790690940000, TickMs = 250, Sec = 1000;
        const int MaxRequests = 3, Depth = 6, Walks = 500, WalkSteps = 30;

        enum Step
        {
            Arrive, ArriveLate,                       // ArriveLate: the hook sent it 50 s before it got here
            Allow, InClaude, Touch, AllowStale,       // on the card; AllowStale names a request the card no longer shows
            Wait200ms, Wait10s, Wait30s, Wait50s,
            ClaudeToFront,                            // the Claude app's window comes to the front, or leaves it
            MovedOnFront, MovedOnBack,                // the request's session went on: it was answered in the app
            GoneFront, GoneBack,                      // the request's hook went away
        }

        static readonly Step[] Steps = (Step[])Enum.GetValues(typeof(Step));

        sealed class Req
        {
            public HeldPrompt Held;   // null when it went straight back
            public int Replies;
            public string Kind = "";
            public long RepliedAt;
            public bool Gone;
            public int Id { get { return Held != null ? Held.Id : 0; } }
        }

        sealed class World
        {
            public readonly PromptBroker Broker;
            public readonly List<Req> Reqs = new List<Req>();
            public long Now = Start;
            public bool Front;
            long frontSince;
            long timerStart = -1;   // the 250 ms timer: started when something is first held, stopped when nothing is
            bool allowExpected;     // an Allow on the card is being acted on: the only time a reply may allow
            HeldPrompt shown;
            public string Code, Detail;   // the first broken promise

            public World()
            {
                Broker = new PromptBroker(() => Front);
                Broker.Changed += OnChanged;
            }

            void OnChanged()
            {
                if (Broker.Count == 0) timerStart = -1;
                else if (timerStart < 0) timerStart = Now;
                HeldPrompt c = Broker.Current;
                long shortBy = c == null ? 0 : Math.Min(Now + PromptBroker.ReadMs, c.Cap) - c.Deadline;
                if (c != null && c != shown && shortBy > 0)
                    Fail(shortBy <= TickMs ? "read-time-tick" : "read-time", "#" + c.Id + " came to the front with " + (c.Deadline - Now) + " ms left, " + shortBy + " ms under ReadMs (or its cap)");
                shown = c;
            }

            // Everything that decides what happens next, with times relative to now: two worlds with the same key behave
            // the same from here on, so only one of them is explored further. lastCall mirrors the broker's lastNow.
            public string Key()
            {
                var k = new System.Text.StringBuilder();
                k.Append(Front ? "F" + Math.Min(Now - frontSince, TickMs) : "-");
                k.Append(" t").Append(timerStart < 0 ? -1 : (Now - timerStart) % TickMs);
                k.Append(" l").Append(Broker.Count == 0 ? 0 : Now - lastCall);
                foreach (Req r in Reqs)
                {
                    if (r.Gone) k.Append(" G");
                    else if (r.Held == null) k.Append(" P");
                    else if (r.Replies > 0) k.Append(" R");
                    else k.Append(" H").Append(r.Held.Deadline - Now).Append(',').Append(r.Held.Cap - Now);
                }
                return k.ToString();
            }

            long lastCall = Start;

            public void Fail(string code, string detail)
            {
                if (Code != null) return;
                Code = code;
                Detail = detail;
            }

            List<Req> Held() { return Reqs.Where(r => r.Held != null && r.Replies == 0 && !r.Gone).ToList(); }

            Req FrontReq()
            {
                HeldPrompt c = Broker.Current;
                return c == null ? null : Reqs.FirstOrDefault(r => r.Held == c);
            }

            Action<PromptReply> ReplyFor(Req r)
            {
                return delegate(PromptReply p)
                {
                    r.Replies++;
                    r.Kind = p.Kind;
                    r.RepliedAt = Now;
                    if (p.Kind != PromptReply.Pass && !allowExpected) Fail("wrong-kind", "#" + r.Id + " got " + p.Kind + " without an Allow on its card");
                };
            }

            // False when the step can't happen here (nothing on the card to click, no request left to arrive...).
            public bool Do(Step s)
            {
                Req front = FrontReq();
                Req back = Held().LastOrDefault(r => r != front);
                Req stale = Reqs.LastOrDefault(r => r != front && r.Id != 0);
                switch (s)
                {
                    case Step.Arrive:
                    case Step.ArriveLate:
                        if (Reqs.Count >= MaxRequests) return false;
                        var r = new Req();
                        Reqs.Add(r);
                        var request = new PromptRequest { SessionId = "s" + Reqs.Count, ToolName = "Bash", Project = "p", At = s == Step.ArriveLate ? Now - 50 * Sec : Now };
                        request.ToolInput["command"] = "npm test";
                        lastCall = Now;
                        r.Held = Broker.Arrive(request, ReplyFor(r), Now);
                        if (r.Held == null && (r.Replies != 1 || r.Kind != PromptReply.Pass)) Fail("step-aside", "a request that wasn't held wasn't passed back once");
                        if (r.Held == null && !Front) Fail("step-aside", "a request went straight back with the Claude app not in front");
                        if (r.Held != null && Front) Fail("step-aside", "#" + r.Id + " was held with the Claude app in front");
                        return true;
                    case Step.Allow: return front != null && Act(PromptAction.Allow, front.Id, front);
                    case Step.InClaude: return front != null && Act(PromptAction.InClaude, front.Id, front);
                    case Step.Touch: return front != null && Act(PromptAction.Touch, front.Id, front);
                    case Step.AllowStale: return stale != null && Act(PromptAction.Allow, stale.Id, null);
                    case Step.Wait200ms: Wait(200); return true;
                    case Step.Wait10s: Wait(10 * Sec); return true;
                    case Step.Wait30s: Wait(30 * Sec); return true;
                    case Step.Wait50s: Wait(50 * Sec); return true;
                    case Step.ClaudeToFront:
                        Front = !Front;
                        frontSince = Now;
                        return true;
                    case Step.MovedOnFront: return front != null && MovedOn(front);
                    case Step.MovedOnBack: return back != null && MovedOn(back);
                    case Step.GoneFront: return front != null && Gone(front);
                    case Step.GoneBack: return back != null && Gone(back);
                }
                return false;
            }

            bool Act(string kind, int target, Req expected)
            {
                int[] before = Reqs.Select(r => r.Replies).ToArray();
                allowExpected = kind == PromptAction.Allow && expected != null;
                lastCall = Now;
                Broker.Act(PromptAction.Of(kind, target), Now);
                allowExpected = false;
                for (int i = 0; i < before.Length; i++)
                    if (Reqs[i].Replies != before[i] && Reqs[i] != expected) Fail("wrong-target", "a click naming #" + target + " answered #" + Reqs[i].Id);
                if (expected == null) return true;
                if (kind == PromptAction.Touch && expected.Replies != 0) Fail("touch-answers", "a touch answered #" + expected.Id);
                if (kind == PromptAction.Allow && (expected.Replies != 1 || expected.Kind != PromptReply.Allow)) Fail("click-lost", "Allow on #" + expected.Id + " didn't allow it");
                if (kind == PromptAction.InClaude && (expected.Replies != 1 || expected.Kind != PromptReply.Pass)) Fail("click-lost", "Answer in Claude on #" + expected.Id + " didn't pass it back");
                return true;
            }

            bool MovedOn(Req r)
            {
                int[] before = Reqs.Select(x => x.Replies).ToArray();
                var status = new SessionStatus { SessionId = r.Held.Request.SessionId, Event = "PostToolUse", State = States.Working, At = Now + 1 };
                Broker.SessionsChanged(new[] { status });
                for (int i = 0; i < before.Length; i++)
                    if (Reqs[i].Replies != before[i] && Reqs[i] != r) Fail("wrong-target", "#" + r.Id + "'s session moving on answered #" + Reqs[i].Id);
                if (r.Replies != 1 || r.Kind != PromptReply.Pass) Fail("moved-on", "#" + r.Id + "'s session moved on but it wasn't passed back");
                return true;
            }

            bool Gone(Req r)
            {
                int[] before = Reqs.Select(x => x.Replies).ToArray();
                Broker.Gone(r.Id);
                r.Gone = true;
                for (int i = 0; i < before.Length; i++)
                    if (Reqs[i].Replies != before[i]) Fail("gone-replies", "#" + r.Id + "'s hook going away sent a reply to #" + Reqs[i].Id);
                return true;
            }

            // Time passes; the timer ticks every 250 ms from when it started, while anything is held.
            void Wait(long ms)
            {
                long until = Now + ms;
                while (timerStart >= 0)
                {
                    long next = timerStart + ((Now - timerStart) / TickMs + 1) * TickMs;
                    if (next > until) break;
                    Now = next;
                    lastCall = Now;
                    Broker.Tick(Now);
                }
                Now = until;
            }

            public void Check()
            {
                List<Req> held = Held();
                foreach (Req r in Reqs)
                {
                    if (r.Replies > 1) Fail("many-replies", "#" + r.Id + " got " + r.Replies + " replies");
                    if (r.Gone && r.Replies > 0) Fail("gone-replies", "#" + r.Id + " got a reply after its hook went away");
                    if (r.Held != null && r.Replies == 1 && r.RepliedAt > r.Held.Cap + TickMs) Fail("past-cap", "#" + r.Id + " was answered " + (r.RepliedAt - r.Held.Cap) + " ms after its cap");
                }
                if (Broker.Count != held.Count) Fail("lost", "the broker holds " + Broker.Count + " but " + held.Count + " are unanswered");
                else if (held.Count > 0 && Broker.Current != held[0].Held) Fail("not-oldest", "the card shows #" + Broker.Current.Id + ", not the oldest, #" + held[0].Id);
                foreach (Req h in held)
                {
                    if (h.Held.Deadline > h.Held.Cap) Fail("past-cap", "#" + h.Id + "'s countdown runs " + (h.Held.Deadline - h.Held.Cap) + " ms past its cap");
                    if (Now > h.Held.Cap + TickMs) Fail("past-cap", "#" + h.Id + " is still held " + (Now - h.Held.Cap) + " ms after its cap");
                }
                if (Front && held.Count > 0 && Now - frontSince >= TickMs) Fail("step-aside", "the Claude app has been in front for " + (Now - frontSince) + " ms and #" + held[0].Id + " is still held");
            }

            // Capsule quits: everything left goes to the app, and every request has had exactly one reply, or none once its
            // hook went away.
            public void Quit()
            {
                Broker.ReleaseAll();
                foreach (Req r in Reqs)
                {
                    if (!r.Gone && r.Replies != 1) Fail("lost", "after quitting, #" + r.Id + " has " + r.Replies + " replies");
                    if (r.Gone && r.Replies != 0) Fail("gone-replies", "after quitting, #" + r.Id + " got a reply after its hook went away");
                }
                if (Broker.Count != 0) Fail("lost", "after quitting, the broker still holds " + Broker.Count);
            }
        }

        // The first failure of each kind, with the shortest steps found to it.
        static readonly Dictionary<string, string> Found = new Dictionary<string, string>();
        static readonly Dictionary<string, int> FoundLength = new Dictionary<string, int>();

        static void Record(World w, List<Step> path)
        {
            if (w.Code == null) return;
            int length;
            if (FoundLength.TryGetValue(w.Code, out length) && length <= path.Count) return;
            FoundLength[w.Code] = path.Count;
            Found[w.Code] = w.Detail + "\n      after: " + (path.Count == 0 ? "(nothing)" : string.Join(", ", path.Select(p => p.ToString()).ToArray())) + ", then quit";
        }

        // Plays the steps in a new world, checking after each. Null when the last step can't happen.
        static World Play(List<Step> path)
        {
            var w = new World();
            for (int i = 0; i < path.Count; i++)
            {
                if (!w.Do(path[i])) return null;
                w.Check();
                if (w.Code != null) return w;
            }
            return w;
        }

        // Breadth first, so each failure is found by its shortest steps; a world already seen (by Key) isn't explored again.
        // Each world is also checked as if Capsule quit there. Returns how many distinct worlds it reached.
        static int Explore()
        {
            var seen = new HashSet<string> { new World().Key() };
            var frontier = new List<List<Step>> { new List<Step>() };
            for (int depth = 1; depth <= Depth && frontier.Count > 0; depth++)
            {
                var next = new List<List<Step>>();
                foreach (List<Step> path in frontier)
                    foreach (Step s in Steps)
                    {
                        var p = new List<Step>(path) { s };
                        World w = Play(p);
                        if (w == null) continue;
                        bool fresh = w.Code == null && seen.Add(w.Key());
                        if (w.Code == null) w.Quit();
                        Record(w, p);
                        if (fresh && w.Code == null) next.Add(p);
                    }
                frontier = next;
            }
            return seen.Count;
        }

        static void Walk(int seed)
        {
            var rnd = new Random(seed);
            var w = new World();
            var path = new List<Step>();
            for (int i = 0; i < WalkSteps && w.Code == null; i++)
            {
                Step s = Steps[rnd.Next(Steps.Length)];
                if (!w.Do(s)) continue;
                path.Add(s);
                w.Check();
            }
            if (w.Code == null) w.Quit();
            Record(w, path);
        }

        // Findings in the current broker, reported but not yet fixed. A new failure fails the tests; so does a known one that
        // stops happening, so it comes off this list with its fix.
        // read-time-tick: SessionsChanged and Gone take no time, so a request they bring to the front gets ReadMs from the
        // last tick (the broker's lastNow), up to 250 ms ago, instead of from now: as little as 9.75 s to read it.
        // Shortest: Arrive, Arrive, Wait200ms, Wait50s, MovedOnFront.
        static readonly HashSet<string> Known = new HashSet<string> { "read-time-tick" };

        public static void Run()
        {
            Found.Clear();
            FoundLength.Clear();
            var watch = Stopwatch.StartNew();
            int worlds = Explore();
            for (int seed = 1; seed <= Walks; seed++) Walk(seed);
            Console.WriteLine("  PromptBroker event orders: " + worlds + " distinct states within " + Depth + " steps, and " + Walks + " walks of " + WalkSteps + ", " + watch.ElapsedMilliseconds + " ms");
            foreach (KeyValuePair<string, string> f in Found.OrderBy(f => f.Key))
                TestRunner.Check(Known.Contains(f.Key), "PromptBroker broke a promise (" + f.Key + "): " + f.Value);
            foreach (string k in Known)
                TestRunner.Check(Found.ContainsKey(k), "PromptBroker no longer breaks " + k + ": take it off Known");
        }
    }
}
