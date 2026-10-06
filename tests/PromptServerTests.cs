using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace Capsule
{
    public static class PromptServerTests
    {
        public static void Run()
        {
            ARequestIsAnsweredThroughItsLink();
            SeveralHooksAreServedAtOnce();
            AHookThatGoesAwayIsNoticed();
            AMalformedRequestIsClosed();
            AHookStopsWaitingAtItsTimeLimit();
            WithNoCapsuleTheHookGoesOn();
            OnlyTheCurrentUserCanOpenThePipe();
            ASecondServerCantTakeTheName();
            APipeSomeoneElseMadeIsNeverServed();
            TheNameStaysTakenBetweenConnections();
            ALaterInstanceIsAFirstInstanceWhenNoneIsAlive();
            ASlowReaderGetsTheWholeReply();
            AStrayByteIsNotTheHookGoingAway();
            ANewsOfAHookGoingComesOnce();
            AStoppedServerAnswersPass();
            AFailingHandlerLeavesTheServerServing();
        }

        static string NewName() { return "capsule-tests-" + Guid.NewGuid().ToString("N"); }

        // What the log gained since `before`.
        static string LogSince(int before)
        {
            string log = Files.ReadText(Paths.LogFile) ?? "";
            return log.Length > before ? log.Substring(before) : "";
        }

        static int LogLength() { return (Files.ReadText(Paths.LogFile) ?? "").Length; }

        // The pipe is made with the first-instance flag, so a pipe of that name that someone else made first is never
        // served (they could read the requests), and a second Capsule can't take over a running one's name.
        static void ASecondServerCantTakeTheName()
        {
            string name = NewName();
            using (var first = new PromptServer(name, delegate(PromptRequest request, PromptLink link) { link.Send(PromptReply.Denying("first")); }))
            {
                TestRunner.Check(first.Start(), "the first server makes its pipe");
                int before = LogLength();
                using (var second = new PromptServer(name, delegate(PromptRequest request, PromptLink link) { link.Send(PromptReply.Denying("second")); }))
                    TestRunner.Check(!second.Start(), "a second server on the same name doesn't start");
                string said = LogSince(before);
                TestRunner.Check(said.Contains("prompts: pipe couldn't be made") && (said.Contains("5") || said.Contains("231")), "and says so, with the Win32 error number: " + said.Trim());
                PromptReply reply = PromptReply.Parse(PromptClient.Ask(name, Sample("s-1"), 1000, 5000));
                TestRunner.Check(reply != null && reply.Message == "first", "the first one keeps serving");
            }
        }

        static void APipeSomeoneElseMadeIsNeverServed()
        {
            string name = NewName();
            using (var squatter = new NamedPipeServerStream(name, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
            {
                int before = LogLength();
                using (var server = new PromptServer(name, delegate(PromptRequest request, PromptLink link) { link.Send(PromptReply.Allowing()); }))
                    TestRunner.Check(!server.Start(), "a pipe made first by someone else: Capsule doesn't serve");
                TestRunner.Check(LogSince(before).Contains("prompts: pipe couldn't be made"), "and logs it");
            }
        }

        // However many connections have come and gone, no one else can make the first instance of the name.
        static void TheNameStaysTakenBetweenConnections()
        {
            string name = NewName();
            using (var server = new PromptServer(name, delegate(PromptRequest request, PromptLink link) { link.Send(PromptReply.Allowing()); }))
            {
                server.Start();
                for (int i = 0; i < 3; i++)
                {
                    TestRunner.Check(PromptReply.Parse(PromptClient.Ask(name, Sample("s-" + i), 1000, 5000)) != null, "connection " + i + " is answered");
                    int error;
                    NamedPipeServerStream squat = PromptServer.CreatePipe(name, true, out error);
                    TestRunner.Check(squat == null && (error == 5 || error == 231), "after it, the name still isn't free (error " + error + ")");
                    if (squat != null) squat.Dispose();
                }
            }
        }

        // When no instance of ours is alive the name is free: the next one is then made as a first instance, so a pipe that
        // someone else took in that gap is never joined; while one is alive, a plain instance joins ours.
        static void ALaterInstanceIsAFirstInstanceWhenNoneIsAlive()
        {
            string name = NewName();
            var none = new Action<PromptRequest, PromptLink>(delegate(PromptRequest request, PromptLink link) { link.Send(PromptReply.Allowing()); });
            using (var taken = new PromptServer(name, none))
            using (var squatter = new NamedPipeServerStream(name, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
                TestRunner.Check(taken.Next() == null, "no instance alive and the name taken by someone else: nothing is made, so nothing is joined");
            using (var server = new PromptServer(name, none))
            {
                NamedPipeServerStream one = server.Next();
                TestRunner.Check(one != null, "no instance alive and the name free: a first instance is made");
                NamedPipeServerStream two = server.Next();
                TestRunner.Check(two != null, "with one alive, the next joins it");
                if (two != null) two.Dispose();
                if (one != null) one.Dispose();
                NamedPipeServerStream again = server.Next();
                TestRunner.Check(again != null, "and once they are gone, the name is made again as the first");
                if (again != null) again.Dispose();
            }
        }

        // The pipe is drained before it closes: a hook that reads late still gets all of a reply bigger than the buffer.
        static void ASlowReaderGetsTheWholeReply()
        {
            string name = NewName();
            var big = new Dictionary<string, object>();
            big["q"] = new string('x', 300000);
            using (var server = new PromptServer(name, delegate(PromptRequest request, PromptLink link)
            {
                link.Send(request.SessionId == "big" ? PromptReply.Answering(big) : PromptReply.Denying("small"));
            }))
            {
                server.Start();
                foreach (string session in new[] { "big", "small" })
                    using (var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous))
                    {
                        client.Connect(2000);
                        PromptPipe.Write(client, Sample(session), 2000);
                        Thread.Sleep(1200);   // the reply is waiting, unread
                        PromptReply reply = PromptReply.Parse(PromptPipe.Read(client, 5000));
                        bool whole = session == "big"
                            ? reply != null && reply.Answers != null && Json.Str(Json.Get(reply.Answers, "q")) == big["q"] as string
                            : reply != null && reply.Message == "small";
                        TestRunner.Check(whole, "a hook that reads late still gets the whole " + session + " reply");
                    }
            }
        }

        // A byte from the hook before the reply isn't the hook going away; only its end closing is.
        static void AStrayByteIsNotTheHookGoingAway()
        {
            string name = NewName();
            var arrived = new ManualResetEvent(false);
            PromptLink kept = null;
            using (var server = new PromptServer(name, delegate(PromptRequest request, PromptLink link) { kept = link; arrived.Set(); }))
            {
                server.Start();
                using (var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous))
                {
                    client.Connect(2000);
                    PromptPipe.Write(client, Sample("s-1"), 2000);
                    arrived.WaitOne(3000);
                    client.WriteByte(7);
                    client.Flush();
                    Thread.Sleep(400);
                    TestRunner.Check(kept != null && !kept.IsGone, "a stray byte doesn't mark the link gone");
                    TestRunner.Check(kept != null && kept.Send(PromptReply.Denying("still here")), "and it still takes its reply");
                    PromptReply reply = PromptReply.Parse(PromptPipe.Read(client, 3000));
                    TestRunner.Check(reply != null && reply.Message == "still here", "which the hook reads");
                }
            }
        }

        static void ANewsOfAHookGoingComesOnce()
        {
            string name = NewName();
            int told = 0;
            var arrived = new ManualResetEvent(false);
            PromptLink kept = null;
            using (var server = new PromptServer(name, delegate(PromptRequest request, PromptLink link)
            {
                kept = link;
                link.WhenGone(delegate { Interlocked.Increment(ref told); });
                arrived.Set();
            }))
            {
                server.Start();
                using (var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous))
                {
                    client.Connect(2000);
                    PromptPipe.Write(client, Sample("s-1"), 2000);
                    arrived.WaitOne(3000);
                }
                Thread.Sleep(600);
                TestRunner.Eq(1, told, "the hook going away is reported once");
                // Asking again after the fact tells the new asker, and not the old one a second time.
                kept.WhenGone(delegate { Interlocked.Increment(ref told); });
                Thread.Sleep(200);
                TestRunner.Eq(2, told, "a late asker is told once, and the first isn't told again");
            }
        }

        // A request that is read after Capsule stopped goes back as a pass, not into silence.
        static void AStoppedServerAnswersPass()
        {
            string name = NewName();
            int calls = 0;
            var server = new PromptServer(name, delegate { Interlocked.Increment(ref calls); });
            server.Start();
            using (var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                client.Connect(2000);
                Thread.Sleep(300);   // accepted, and being read
                server.Dispose();
                PromptPipe.Write(client, Sample("s-1"), 2000);
                PromptReply reply = PromptReply.Parse(PromptPipe.Read(client, 3000));
                TestRunner.Check(reply != null && reply.Kind == PromptReply.Pass, "stopping while a hook is connected answers it pass");
                TestRunner.Eq(0, calls, "and nothing is held for it");
            }
        }

        static void AFailingHandlerLeavesTheServerServing()
        {
            string name = NewName();
            int calls = 0;
            using (var server = new PromptServer(name, delegate(PromptRequest request, PromptLink link)
            {
                if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("sample");
                link.Send(PromptReply.Denying("second"));
            }))
            {
                server.Start();
                PromptReply first = PromptReply.Parse(PromptClient.Ask(name, Sample("s-1"), 1000, 5000));
                TestRunner.Check(first != null && first.Kind == PromptReply.Pass, "a handler that throws: the hook is passed");
                PromptReply next = PromptReply.Parse(PromptClient.Ask(name, Sample("s-2"), 1000, 5000));
                TestRunner.Check(next != null && next.Message == "second", "and the server serves the next one");
            }
        }

        // A sample request: never a real one.
        static string Sample(string session)
        {
            var r = new PromptRequest { SessionId = session, Project = "confetti", ToolName = "Bash", At = 1000 };
            r.ToolInput["command"] = "npm test";
            return r.ToJson();
        }

        static void ARequestIsAnsweredThroughItsLink()
        {
            string name = NewName();
            PromptRequest seen = null;
            PromptLink kept = null;
            using (var server = new PromptServer(name, delegate(PromptRequest request, PromptLink link)
            {
                seen = request;
                kept = link;
                link.Send(PromptReply.Denying("Denied from the capsule"));
            }))
            {
                TestRunner.Check(server.Start(), "the server makes its pipe");
                PromptReply reply = PromptReply.Parse(PromptClient.Ask(name, Sample("s-1"), 1000, 5000));
                TestRunner.Check(seen != null && seen.SessionId == "s-1" && seen.ToolName == "Bash" && Json.Str(Json.Get(seen.ToolInput, "command")) == "npm test", "the request arrives whole");
                TestRunner.Check(reply != null && reply.Kind == PromptReply.Deny && reply.Message == "Denied from the capsule", "and its reply goes back");
                TestRunner.Check(kept != null && !kept.Send(PromptReply.Allowing()), "a link answers once only");
            }
        }

        static void SeveralHooksAreServedAtOnce()
        {
            string name = NewName();
            // Each waits until all three have arrived: served one after another, none would ever get its reply.
            var all = new CountdownEvent(3);
            using (var server = new PromptServer(name, delegate(PromptRequest request, PromptLink link)
            {
                all.Signal();
                if (all.Wait(5000)) link.Send(PromptReply.Denying(request.SessionId));
            }))
            {
                server.Start();
                var asks = new List<Task<string>>();
                foreach (string session in new[] { "a", "b", "c" })
                {
                    string s = session;
                    asks.Add(Task.Run(() => PromptClient.Ask(name, Sample(s), 2000, 5000)));
                }
                Task.WaitAll(asks.ToArray(), 10000);
                string got = "";
                foreach (Task<string> ask in asks)
                {
                    PromptReply reply = PromptReply.Parse(ask.Result);
                    got += reply == null ? "-" : reply.Message;
                }
                TestRunner.Eq("abc", got, "three hooks at once each get their own reply");
            }
        }

        static void AHookThatGoesAwayIsNoticed()
        {
            string name = NewName();
            var arrived = new ManualResetEvent(false);
            var goneSeen = new ManualResetEvent(false);
            PromptLink kept = null;
            using (var server = new PromptServer(name, delegate(PromptRequest request, PromptLink link)
            {
                kept = link;
                link.WhenGone(delegate { goneSeen.Set(); });
                arrived.Set();
            }))
            {
                server.Start();
                using (var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous))
                {
                    client.Connect(2000);
                    PromptPipe.Write(client, Sample("s-1"), 2000);
                    arrived.WaitOne(3000);
                }   // the hook is killed, or gives up
                TestRunner.Check(goneSeen.WaitOne(3000), "a hook that goes away before its reply is noticed");
                TestRunner.Check(kept != null && kept.IsGone && !kept.Send(PromptReply.Allowing()), "and its link takes no reply");
                bool late = false;
                if (kept != null) kept.WhenGone(delegate { late = true; });
                TestRunner.Check(late, "told after the fact, the news comes at once");
            }
        }

        static void AMalformedRequestIsClosed()
        {
            string name = NewName();
            int calls = 0;
            using (var server = new PromptServer(name, delegate { Interlocked.Increment(ref calls); }))
            {
                server.Start();
                using (var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous))
                {
                    client.Connect(2000);
                    PromptPipe.Write(client, "not json", 2000);
                    var clock = Stopwatch.StartNew();
                    string reply = PromptPipe.Read(client, 5000);
                    TestRunner.Check(reply == null && clock.ElapsedMilliseconds < 2000, "a request that isn't one is closed at once, unanswered (" + clock.ElapsedMilliseconds + " ms)");
                }
                TestRunner.Eq(0, calls, "and never handed on");
            }
        }

        static void AHookStopsWaitingAtItsTimeLimit()
        {
            string name = NewName();
            var goneSeen = new ManualResetEvent(false);
            using (var server = new PromptServer(name, delegate(PromptRequest request, PromptLink link) { link.WhenGone(delegate { goneSeen.Set(); }); }))
            {
                server.Start();
                var clock = Stopwatch.StartNew();
                string reply = PromptClient.Ask(name, Sample("s-1"), 1000, 300);
                long took = clock.ElapsedMilliseconds;
                TestRunner.Check(reply == null && took >= 250 && took < 3000, "no reply in time: the hook goes on without one (" + took + " ms)");
                TestRunner.Check(goneSeen.WaitOne(3000), "and Capsule notices it has gone");
            }
        }

        static void WithNoCapsuleTheHookGoesOn()
        {
            var clock = Stopwatch.StartNew();
            string reply = PromptClient.Ask(NewName(), Sample("s-1"), 300, 5000);
            TestRunner.Check(reply == null && clock.ElapsedMilliseconds < 3000, "no pipe: no reply, soon (" + clock.ElapsedMilliseconds + " ms)");
            string name = NewName();
            var server = new PromptServer(name, delegate(PromptRequest request, PromptLink link) { link.Send(PromptReply.Allowing()); });
            server.Start();
            server.Dispose();
            TestRunner.Check(PromptClient.Ask(name, Sample("s-1"), 300, 5000) == null, "nor once Capsule has stopped");
        }

        static void OnlyTheCurrentUserCanOpenThePipe()
        {
            AuthorizationRuleCollection rules = PromptServer.Security().GetAccessRules(true, true, typeof(SecurityIdentifier));
            TestRunner.Eq(1, rules.Count, "the pipe has one access rule");
            var rule = rules.Count == 1 ? rules[0] as PipeAccessRule : null;
            SecurityIdentifier me;
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent()) me = identity.User;
            TestRunner.Check(rule != null && rule.AccessControlType == AccessControlType.Allow && me.Equals(rule.IdentityReference), "it lets in the current user");
            TestRunner.Check(rule != null && (rule.PipeAccessRights & PipeAccessRights.ReadWrite) == PipeAccessRights.ReadWrite, "to read and write");
        }
    }
}
