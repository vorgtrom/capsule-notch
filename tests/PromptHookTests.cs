using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace Capsule
{
    public static class PromptHookTests
    {
        public static void Run()
        {
            APermissionRequestIsAskedAndAnswered();
            OtherEventsAskNothing();
            PassPrintsNothing();
            TheHookExePrintsTheDecision();
            TheHookExePrintsAnswersAsUtf8();
            WithNoCapsuleTheHookExePrintsNothing();
            AProbingHookAsksNothing();
            ASlowCapsuleIsNotWaitedForForever();
            ASkippedHookAsksNothing();
            ThePermissionRequestEventHasOneName();
            TheHookOnlyTalksToTheRealCapsule();
            ThePeerHelpersKnowThisProcess();
            AnotherProcessOnTheNameIsNeverTold();
            AWritesContentNeverReachesCapsule();
            IncompleteApprovalsNeverReachCapsule();
            CompleteCommandsStillReachCapsule();
        }

        // A filename alone cannot describe a Write safely. Its content stays with Claude, which asks as usual.
        static void AWritesContentNeverReachesCapsule()
        {
            string write = "{\"session_id\":\"ph-3\",\"hook_event_name\":\"PermissionRequest\",\"cwd\":\"C:\\\\work\\\\confetti\",\"tool_name\":\"Write\"," +
                "\"tool_input\":{\"file_path\":\"C:\\\\work\\\\confetti\\\\a.txt\",\"content\":\"SECRET-CONTENT\"},\"tool_use_id\":\"toolu_3\"}";
            using (var capsule = new Answerer(PromptReply.Allowing()))
            {
                string output = PromptClient.Handle(write, 1000, capsule.Name, 1000, 5000);
                TestRunner.Eq("", output, "a Write without a full preview passes to Claude");
                TestRunner.Eq(0, capsule.Requests, "a Write sends neither its filename nor its content to Capsule");
                TestRunner.Check(capsule.Last == null, "Capsule never receives the Write request");
            }
        }

        static string ToolRequest(string tool, Dictionary<string, object> input)
        {
            Dictionary<string, object> root = Json.Obj(Json.Parse(Bash));
            root["tool_name"] = tool;
            root["tool_input"] = input;
            return Json.Write(root);
        }

        static void IncompleteApprovalsNeverReachCapsule()
        {
            using (var capsule = new Answerer(PromptReply.AlwaysAllowing()))
            {
                foreach (string tool in new[] { "Bash", "PowerShell" })
                {
                    string hook = ToolRequest(tool, new Dictionary<string, object> { { "command", new string('x', 4001) } });
                    var clock = Stopwatch.StartNew();
                    TestRunner.Eq("", PromptClient.Handle(hook, 1000, capsule.Name, 1000, 5000), tool + ": an oversized original command passes to Claude");
                    TestRunner.Check(clock.ElapsedMilliseconds < 1000, tool + ": fallback does not wait for Capsule");
                }
                foreach (string tool in new[] { "Write", "Edit", "MultiEdit", "NotebookEdit", "ExitPlanMode", "mcp__sample__change" })
                {
                    string hook = ToolRequest(tool, new Dictionary<string, object> {
                        { "file_path", "C:\\work\\synthetic.txt" }, { "notebook_path", "C:\\work\\synthetic.ipynb" },
                        { "nested", new Dictionary<string, object> { { "secret", "SYNTHETIC-CONTENT" } } }
                    });
                    TestRunner.Eq("", PromptClient.Handle(hook, 1000, capsule.Name, 1000, 5000), tool + ": a request without a complete card preview passes to Claude");
                }
                TestRunner.Eq(0, capsule.Requests, "incomplete approvals never contact Capsule, even when it would always allow");
            }
        }

        static void CompleteCommandsStillReachCapsule()
        {
            using (var capsule = new Answerer(PromptReply.Allowing()))
            {
                foreach (string tool in new[] { "Bash", "PowerShell" })
                    foreach (string command in new[] { "echo synthetic", new string('x', 4000) })
                    {
                        string output = PromptClient.Handle(ToolRequest(tool, new Dictionary<string, object> { { "command", command } }), 1000, capsule.Name, 1000, 5000);
                        TestRunner.Eq("allow", Json.Str(Json.Get(Decision(output), "behavior")), tool + ": a complete command can still be allowed");
                        TestRunner.Eq(command, capsule.Last == null ? null : Json.Str(Json.Get(capsule.Last.ToolInput, "command")), tool + ": Capsule receives the whole command");
                    }
                TestRunner.Eq(4, capsule.Requests, "both short commands and the 4000-character boundary reach Capsule");
            }
        }

        // The check of who is on the other end (F1): the server here runs in Tests.exe, so with the expected image pointed
        // anywhere else nothing is sent and the client sees no Capsule; pointed at this process's own image it works.
        static void TheHookOnlyTalksToTheRealCapsule()
        {
            string own = PipePeer.ExpectedImageForTests;
            try
            {
                using (var capsule = new Answerer(PromptReply.Allowing()))
                {
                    PipePeer.ExpectedImageForTests = Path.Combine(Paths.ExeDir, "Capsule.exe");   // what the hook expects: not this process
                    TestRunner.Check(PromptClient.Ask(capsule.Name, Bash, 1000, 2000) == null, "a server that isn't Capsule.exe: the client gets no reply");
                    TestRunner.Eq("", PromptClient.Handle(Bash, 1000, capsule.Name, 1000, 2000), "so the hook prints nothing");
                    Thread.Sleep(300);
                    TestRunner.Eq(0, capsule.Requests, "and the server never saw a request: nothing was sent");
                    PipePeer.ExpectedImageForTests = own;
                    TestRunner.Check(PromptClient.Ask(capsule.Name, Bash, 1000, 2000) != null, "the real image is talked to");
                    TestRunner.Eq(1, capsule.Requests, "and sees the request");
                    PipePeer.ExpectedImageForTests = "";
                    TestRunner.Check(PromptClient.Ask(capsule.Name, Bash, 1000, 2000) == null, "an empty expected image never matches");
                }
            }
            finally { PipePeer.ExpectedImageForTests = own; }
        }

        static void ThePeerHelpersKnowThisProcess()
        {
            uint me = (uint)Process.GetCurrentProcess().Id;
            using (System.Security.Principal.WindowsIdentity id = System.Security.Principal.WindowsIdentity.GetCurrent())
                TestRunner.Eq(id.User, PipePeer.UserOf(me), "the user of this process is the current user");
            TestRunner.Check(PipePeer.IntegrityOf(me) >= 0x2000, "its integrity level is medium or more: " + PipePeer.IntegrityOf(me).ToString("X"));
            string image = PipePeer.ImageOf(me);
            TestRunner.Check(image != null && string.Equals(Path.GetFullPath(image), Path.GetFullPath(PipePeer.ExpectedImageForTests), StringComparison.OrdinalIgnoreCase), "and its image is this exe");
            TestRunner.Check(PipePeer.IsCapsuleProcess(me, image), "so it passes as the process it is");
            TestRunner.Check(!PipePeer.IsCapsuleProcess(me, Path.Combine(Paths.ExeDir, "Capsule.exe")), "but not as another exe");
            TestRunner.Check(PipePeer.IsCapsuleProcess(me, image.ToUpperInvariant()), "the path is compared without regard to case");
            TestRunner.Check(!PipePeer.IsCapsuleProcess(uint.MaxValue - 1, image), "a process that isn't there is nothing");
            TestRunner.Check(!PipePeer.IsCapsule(null), "and no handle is no Capsule");
        }

        // CAPSULE_PIPE can be set by a project's settings, so the image check holds for any pipe name: the hook exe, run
        // with CAPSULE_PIPE naming a server in this (Tests.exe) process, sends nothing.
        static void AnotherProcessOnTheNameIsNeverTold()
        {
            using (var capsule = new Answerer(PromptReply.Allowing()))
            {
                int code;
                long ms;
                string output = RunHook(Bash, capsule.Name, "", out code, out ms);
                Thread.Sleep(300);
                TestRunner.Check(code == 0 && output == "", "CAPSULE_PIPE naming another process's pipe: the hook prints nothing");
                TestRunner.Eq(0, capsule.Requests, "and sends it nothing");
            }
        }

        // The stand-in Capsule: Tests.exe copied to a folder as Capsule.exe, with capsule-hook.exe beside it, serving one
        // pipe. "always" replies with an always-allow; "answer" with a non-ASCII answer to the question "Which name?".
        public static int FakeCapsule(string pipe, string kind)
        {
            PromptReply reply;
            if (kind == "always") reply = PromptReply.AlwaysAllowing();
            else
            {
                var answers = new Dictionary<string, object>();
                answers["Which name?"] = "Caf" + (char)0xE9 + " " + (char)0x2026;
                reply = PromptReply.Answering(answers);
            }
            using (var server = new PromptServer(pipe, delegate(PromptRequest request, PromptLink link) { link.Send(reply); }))
            {
                if (!server.Start()) return 2;
                Console.WriteLine("ready");
                Console.Out.Flush();
                Console.In.ReadToEnd();   // the test closes stdin when it is done
            }
            return 0;
        }

        sealed class FakeCapsuleProcess : IDisposable
        {
            public readonly string Name = NewName();
            public readonly string HookExe;
            readonly Process process;
            readonly string dir;   // the folder this stand-in made (its copies of Capsule.exe and capsule-hook.exe), and nothing else
            public string Folder { get { return dir; } }

            public FakeCapsuleProcess(string kind)
            {
                dir = TestRunner.NewTempDir();
                File.Copy(Process.GetCurrentProcess().MainModule.FileName, Path.Combine(dir, "Capsule.exe"));
                File.Copy(Path.Combine(Paths.ExeDir, "capsule-hook.exe"), Path.Combine(dir, "capsule-hook.exe"));
                HookExe = Path.Combine(dir, "capsule-hook.exe");
                var psi = new ProcessStartInfo(Path.Combine(dir, "Capsule.exe"), "--fake-capsule " + Name + " " + kind);
                psi.UseShellExecute = false;
                psi.RedirectStandardInput = true;
                psi.RedirectStandardOutput = true;
                psi.CreateNoWindow = true;
                process = Process.Start(psi);
                var line = System.Threading.Tasks.Task.Run(() => process.StandardOutput.ReadLine());
                TestRunner.Check(line.Wait(10000) && line.Result == "ready", "the stand-in Capsule is serving");
            }

            public void Dispose()
            {
                try { process.StandardInput.Close(); } catch (Exception) { }
                if (!process.WaitForExit(5000)) process.Kill();
                process.Dispose();
                // The process has gone, so the copies it ran are free to delete; a moment's retry for a lock that lets go late.
                for (int attempt = 0; attempt < 10; attempt++)
                {
                    try
                    {
                        if (Directory.Exists(dir)) Directory.Delete(dir, true);
                        return;
                    }
                    catch (Exception) { Thread.Sleep(100); }
                }
            }
        }

        static void ThePermissionRequestEventHasOneName()
        {
            TestRunner.Eq("PermissionRequest", HookEvents.PermissionRequest, "the event's name, in one place");
            bool wired = false;
            foreach (string[] wire in HookEvents.Wiring) if (wire[0] == HookEvents.PermissionRequest) wired = true;
            TestRunner.Check(wired, "and Connect wires it");
        }

        // Sample events only: never a real request.
        const string Bash = "{\"session_id\":\"ph-1\",\"hook_event_name\":\"PermissionRequest\",\"cwd\":\"C:\\\\work\\\\confetti\",\"tool_name\":\"Bash\"," +
            "\"tool_input\":{\"command\":\"npm test\"},\"tool_use_id\":\"toolu_1\",\"suggestions\":[{\"rule\":\"Bash(npm test:*)\",\"description\":\"npm test\",\"applies\":\"project\"}]}";
        const string Question = "{\"session_id\":\"ph-2\",\"hook_event_name\":\"PermissionRequest\",\"cwd\":\"C:\\\\work\\\\confetti\",\"tool_name\":\"AskUserQuestion\"," +
            "\"tool_input\":{\"questions\":[{\"question\":\"Which name?\",\"header\":\"Name\",\"multiSelect\":false,\"options\":[{\"label\":\"Ada\",\"description\":\"\"},{\"label\":\"Grace\",\"description\":\"\"}]}]}}";

        static string NewName() { return "capsule-tests-" + Guid.NewGuid().ToString("N"); }

        // A server that answers every request with `reply`, and counts them.
        sealed class Answerer : IDisposable
        {
            public readonly string Name = NewName();
            public int Requests;
            public PromptRequest Last;
            readonly PromptServer server;
            readonly List<PromptLink> held = new List<PromptLink>();

            // reply = null: a Capsule that takes the request and never answers it.
            public Answerer(PromptReply reply)
            {
                server = new PromptServer(Name, delegate(PromptRequest request, PromptLink link)
                {
                    Last = request;
                    Interlocked.Increment(ref Requests);
                    if (reply == null) lock (held) held.Add(link);
                    else link.Send(reply);
                });
                server.Start();
            }

            public void Dispose() { server.Dispose(); }
        }

        static Dictionary<string, object> Decision(string output)
        {
            return Json.Obj(Json.Get(Json.TryParse(output), "hookSpecificOutput", "decision"));
        }

        static void APermissionRequestIsAskedAndAnswered()
        {
            var answers = new Dictionary<string, object>();
            answers["Which name?"] = "Grace";
            using (var capsule = new Answerer(PromptReply.Answering(answers)))
            {
                string output = PromptClient.Handle(Question, 4242, capsule.Name, 1000, 5000);
                TestRunner.Eq(1, capsule.Requests, "a PermissionRequest is put to Capsule");
                TestRunner.Check(capsule.Last != null && capsule.Last.At == 4242 && capsule.Last.ToolName == "AskUserQuestion", "stamped with the time the hook wrote the session's status");
                TestRunner.Eq("Grace", Json.Str(Json.Get(Decision(output), "updatedInput", "answers", "Which name?")), "and Capsule's answer is what the hook prints");
            }
        }

        static void OtherEventsAskNothing()
        {
            using (var capsule = new Answerer(PromptReply.Allowing()))
            {
                TestRunner.Eq("", PromptClient.Handle(Bash.Replace("PermissionRequest", "PostToolUse"), 1000, capsule.Name, 1000, 5000), "any other event prints nothing");
                TestRunner.Eq("", PromptClient.Handle("not json", 1000, capsule.Name, 1000, 5000), "nor does garbage");
                TestRunner.Eq(0, capsule.Requests, "and asks Capsule nothing");
            }
        }

        static void PassPrintsNothing()
        {
            using (var capsule = new Answerer(PromptReply.Passing()))
            {
                TestRunner.Eq("", PromptClient.Handle(Bash, 1000, capsule.Name, 1000, 5000), "Capsule passes: the hook prints nothing");
                TestRunner.Eq(1, capsule.Requests, "having been asked");
            }
        }

        // Capsule takes the request and never answers: the hook waits only as long as its reply limit (a short one here;
        // PromptClient.Handle's own parameter, so no seam was added), then prints nothing and Claude asks as usual.
        static void ASlowCapsuleIsNotWaitedForForever()
        {
            using (var capsule = new Answerer(null))
            {
                var clock = Stopwatch.StartNew();
                string output = PromptClient.Handle(Bash, 1000, capsule.Name, 1000, 600);
                long took = clock.ElapsedMilliseconds;
                TestRunner.Eq("", output, "a Capsule that never replies: the hook prints nothing");
                TestRunner.Check(took >= 500 && took < 4000, "once its wait is over (" + took + " ms)");
                TestRunner.Eq(1, capsule.Requests, "though Capsule did get the request");
            }
        }

        // A claude -p that Capsule started itself: not a session, and nothing to ask, even for a PermissionRequest.
        static void ASkippedHookAsksNothing()
        {
            using (var capsule = new Answerer(PromptReply.Allowing()))
            {
                int code;
                long ms;
                string output = RunHook(Bash.Replace("ph-1", "ph-skip"), capsule.Name, "", out code, out ms, true, null);
                TestRunner.Check(code == 0 && output == "", "CAPSULE_SKIP_HOOK: the hook prints nothing and exits 0");
                TestRunner.Eq(0, capsule.Requests, "asks Capsule nothing");
                TestRunner.Check(!File.Exists(Path.Combine(Paths.SessionsDir, "ph-skip.json")), "and writes no session");
            }
        }

        static string RunHook(string input, string pipe, string args, out int exitCode, out long ms)
        {
            return RunHook(input, pipe, args, out exitCode, out ms, false, null);
        }

        // capsule-hook.exe itself, built with this Tests.exe and next to it, with CAPSULE_PIPE set to the pipe given (and,
        // for skip, CAPSULE_SKIP_HOOK=1).
        static string RunHook(string input, string pipe, string args, out int exitCode, out long ms, bool skip, string hookExe)
        {
            string exe = hookExe ?? Path.Combine(Paths.ExeDir, "capsule-hook.exe");
            string before = Environment.GetEnvironmentVariable("CAPSULE_PIPE");
            string beforeSkip = Environment.GetEnvironmentVariable("CAPSULE_SKIP_HOOK");
            Environment.SetEnvironmentVariable("CAPSULE_PIPE", pipe);
            Environment.SetEnvironmentVariable("CAPSULE_SKIP_HOOK", skip ? "1" : null);
            try
            {
                var psi = new ProcessStartInfo(exe, args);
                psi.UseShellExecute = false;
                psi.RedirectStandardInput = true;
                psi.RedirectStandardOutput = true;
                psi.StandardOutputEncoding = new UTF8Encoding(false);
                psi.CreateNoWindow = true;
                var clock = Stopwatch.StartNew();
                using (Process p = Process.Start(psi))   // inherits CAPSULE_DATA and CAPSULE_PIPE
                {
                    p.StandardInput.Write(input);
                    p.StandardInput.Close();
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(10000);
                    exitCode = p.ExitCode;
                    ms = clock.ElapsedMilliseconds;
                    return output;
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("CAPSULE_PIPE", before);
                Environment.SetEnvironmentVariable("CAPSULE_SKIP_HOOK", beforeSkip);
            }
        }

        static void TheHookExePrintsTheDecision()
        {
            using (var capsule = new FakeCapsuleProcess("always"))
            {
                int code;
                long ms;
                string output = RunHook(Bash, capsule.Name, "", out code, out ms, false, capsule.HookExe);
                TestRunner.Eq(0, code, "the hook exits 0");
                Dictionary<string, object> d = Decision(output);
                object[] rules = d == null ? null : Json.Arr(Json.Get(d, "updatedPermissions"));
                TestRunner.Check(d != null && Json.Str(Json.Get(d, "behavior")) == "allow" && rules != null && rules.Length == 1 && Json.Str(rules[0]) == "Bash(npm test:*)",
                    "and prints Capsule's decision, with the rule Claude Code suggested");
                SessionStatus s = SessionStatus.Read(Path.Combine(Paths.SessionsDir, "ph-1.json"));
                TestRunner.Check(s != null && s.State == States.Waiting, "having first marked the session as waiting, as before");
            }
        }

        static void TheHookExePrintsAnswersAsUtf8()
        {
            string folder;
            using (var capsule = new FakeCapsuleProcess("answer"))
            {
                folder = capsule.Folder;
                int code;
                long ms;
                string output = RunHook(Question, capsule.Name, "", out code, out ms, false, capsule.HookExe);
                TestRunner.Eq("Café …", Json.Str(Json.Get(Decision(output), "updatedInput", "answers", "Which name?")), "an answer that isn't ASCII reaches Claude Code as UTF-8");
                TestRunner.Check(Directory.Exists(folder), "the stand-in's folder is there while it runs");
            }
            TestRunner.Check(!Directory.Exists(folder), "and gone once it is disposed: the copied exes aren't left behind");
        }

        static void WithNoCapsuleTheHookExePrintsNothing()
        {
            int code;
            long ms;
            string output = RunHook(Bash, NewName(), "", out code, out ms);
            TestRunner.Check(code == 0 && output == "", "no Capsule: the hook prints nothing and exits 0");
            TestRunner.Check(ms < 2000, "soon (" + ms + " ms)");
        }

        static void AProbingHookAsksNothing()
        {
            using (var capsule = new Answerer(PromptReply.Allowing()))
            {
                int code;
                long ms;
                string output = RunHook(Bash, capsule.Name, "--probe", out code, out ms);
                TestRunner.Check(output == "" && capsule.Requests == 0, "a hook connected for probing only watches: it asks Capsule nothing");
            }
        }
    }
}
