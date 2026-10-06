using System;
using System.Diagnostics;
using System.IO;

namespace Capsule
{
    public static class HookEventsTests
    {
        public static void Run()
        {
            MapsEachEvent();
            SessionStartOnlyCreates();
            SinceIsKeptWhileTheStateHolds();
            ValidatesSessionIds();
            TakesTheProjectFolderName();
            HandleWritesAndDeletesStatusFiles();
            ProbeLogsNamesOnly();
            HookExeEndToEnd();
        }

        static void Expect(string evt, string notification, string tool, string state, string reasonPrefix)
        {
            HookOutcome o = HookEvents.Map(evt, notification, tool, "Claude needs your permission to use Bash");
            string label = evt + "/" + (notification ?? tool ?? "");
            if (state == null) { TestRunner.Check(o.Ignore, label + " ignored"); return; }
            TestRunner.Eq(state, o.State, label + " state");
            TestRunner.Check(o.Reason.StartsWith(reasonPrefix, StringComparison.Ordinal), label + " reason '" + o.Reason + "'");
        }

        static void MapsEachEvent()
        {
            Expect("UserPromptSubmit", null, null, States.Working, "");
            Expect("PreToolUse", null, "AskUserQuestion", States.Waiting, "asked you a question");
            Expect("PreToolUse", null, "ExitPlanMode", States.Waiting, "wants plan approval");
            Expect("PreToolUse", null, "Bash", null, null);
            Expect("PermissionRequest", null, "Bash", States.Waiting, "needs permission: Bash");
            Expect("PermissionRequest", null, "AskUserQuestion", States.Waiting, "asked you a question");
            Expect("Notification", "permission_prompt", null, States.Waiting, "Claude needs your permission");
            Expect("Notification", "elicitation_dialog", null, States.Waiting, "Claude needs");
            Expect("Notification", "agent_needs_input", null, States.Waiting, "Claude needs");
            Expect("Notification", "idle_prompt", null, States.Done, "");
            Expect("Notification", "auth_success", null, null, null);
            Expect("PostToolUse", null, "Bash", States.Working, "");
            Expect("PostToolUseFailure", null, "Bash", States.Working, "");
            Expect("PermissionDenied", null, "Bash", States.Working, "");
            Expect("Stop", null, null, States.Done, "");
            Expect("StopFailure", null, null, States.Done, "");
            Expect("SomethingNew", null, null, null, null);
            TestRunner.Check(HookEvents.Map("SessionEnd", null, null, null).Delete, "SessionEnd deletes");
        }

        static void SessionStartOnlyCreates()
        {
            HookOutcome start = HookEvents.Map("SessionStart", null, null, null);
            SessionStatus created = HookEvents.Next(null, start, "s1", "SessionStart", "proj", 1000);
            TestRunner.Eq(States.Idle, created.State, "a new session starts idle");
            var working = new SessionStatus { SessionId = "s1", State = States.Working, Since = 500, At = 900 };
            TestRunner.Check(HookEvents.Next(working, start, "s1", "SessionStart", "proj", 1000) == null, "a compact or resume start leaves a working session alone");
        }

        static void SinceIsKeptWhileTheStateHolds()
        {
            var prev = new SessionStatus { SessionId = "s1", State = States.Working, Since = 500, At = 900, Project = "proj" };
            SessionStatus same = HookEvents.Next(prev, HookEvents.Map("PostToolUse", null, "Read", null), "s1", "PostToolUse", "", 2000);
            TestRunner.Eq(500L, same.Since, "since kept");
            TestRunner.Eq(2000L, same.At, "at moves");
            TestRunner.Eq("proj", same.Project, "project kept when the event has no cwd");
            SessionStatus changed = HookEvents.Next(prev, HookEvents.Map("Stop", null, null, null), "s1", "Stop", "proj", 3000);
            TestRunner.Eq(3000L, changed.Since, "since resets on a new state");
        }

        static void ValidatesSessionIds()
        {
            TestRunner.Check(HookEvents.IsValidSessionId("a3316a2f-febe-41f0-816f-d430f12024f7"), "uuid ok");
            TestRunner.Check(!HookEvents.IsValidSessionId("../evil"), "path characters rejected");
            TestRunner.Check(!HookEvents.IsValidSessionId(""), "empty rejected");
            TestRunner.Check(!HookEvents.IsValidSessionId("é"), "non-ASCII rejected");
        }

        static void TakesTheProjectFolderName()
        {
            TestRunner.Eq("My Projects", HookEvents.ProjectName("C:\\Users\\User\\Desktop\\My Projects"), "windows path");
            TestRunner.Eq("confetti", HookEvents.ProjectName("C:/work/confetti/"), "trailing slash");
            TestRunner.Eq("", HookEvents.ProjectName(null), "no cwd");
        }

        static string Event(string name, string session, string extra)
        {
            return "{\"session_id\":\"" + session + "\",\"hook_event_name\":\"" + name + "\",\"cwd\":\"C:\\\\work\\\\confetti\"" + extra + "}";
        }

        static void HandleWritesAndDeletesStatusFiles()
        {
            string file = Path.Combine(Paths.SessionsDir, "h1.json");
            TestRunner.Eq(States.Working, HookEvents.Handle(Event("UserPromptSubmit", "h1", ",\"prompt\":\"secret prompt\""), false, 1000), "handled");
            SessionStatus s = SessionStatus.Read(file);
            TestRunner.Eq(States.Working, s.State, "state written");
            TestRunner.Eq("confetti", s.Project, "project written");
            TestRunner.Check(!File.ReadAllText(file).Contains("secret"), "prompt text never stored");
            TestRunner.Eq("deleted", HookEvents.Handle(Event("SessionEnd", "h1", ""), false, 2000), "session end");
            TestRunner.Check(!File.Exists(file), "file removed");
            TestRunner.Eq("unparsed", HookEvents.Handle("not json", false, 3000), "garbage ignored");
            TestRunner.Eq("bad-session", HookEvents.Handle(Event("Stop", "../x", ""), false, 3000), "bad id ignored");
        }

        static void ProbeLogsNamesOnly()
        {
            HookEvents.Handle(Event("PreToolUse", "p1", ",\"tool_name\":\"Bash\",\"tool_input\":{\"command\":\"echo top-secret\"},\"permission_mode\":\"default\""), true, 1000);
            string log = File.ReadAllText(Paths.ProbeLogFile);
            TestRunner.Check(log.Contains("hook_event_name=PreToolUse") && log.Contains("tool_name=Bash") && log.Contains("permission_mode=default"), "probe line has names");
            TestRunner.Check(!log.Contains("top-secret"), "probe line has no tool input");
            HookEvents.Handle(Event("PermissionDenied", "p2", ",\"tool_name\":\"Bash\",\"reason\":\"blocked: rm -rf top-secret-dir\""), true, 2000);
            HookEvents.Handle(Event("SessionEnd", "p2", ",\"reason\":\"prompt_input_exit\""), true, 3000);
            string later = File.ReadAllText(Paths.ProbeLogFile);
            TestRunner.Check(!later.Contains("top-secret-dir"), "a free-text reason is never logged");
            TestRunner.Check(later.Contains("reason=prompt_input_exit"), "a session-end reason is");
        }

        static void HookExeEndToEnd()
        {
            // The hook built along with this Tests.exe, next to it: in bin\ for build.cmd, in bin-dev\ for build.cmd dev.
            string exe = Path.Combine(Paths.ExeDir, "capsule-hook.exe");
            if (!File.Exists(exe))
            {
                TestRunner.Check(false, "capsule-hook.exe is next to Tests.exe (" + Paths.ExeDir + ")");
                return;
            }
            var psi = new ProcessStartInfo(exe);
            psi.UseShellExecute = false;
            psi.RedirectStandardInput = true;
            psi.RedirectStandardOutput = true;
            psi.CreateNoWindow = true;
            var watch = Stopwatch.StartNew();
            using (Process p = Process.Start(psi))   // inherits CAPSULE_DATA, so it writes into the test folder
            {
                p.StandardInput.Write(Event("Stop", "e2e", ""));
                p.StandardInput.Close();
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(10000);
                TestRunner.Eq(0, p.ExitCode, "exit code");
                TestRunner.Eq("", output, "prints nothing");
            }
            SessionStatus s = SessionStatus.Read(Path.Combine(Paths.SessionsDir, "e2e.json"));
            TestRunner.Check(s != null && s.State == States.Done, "exe wrote the status file");
            Console.WriteLine("  capsule-hook.exe round trip: " + watch.ElapsedMilliseconds + " ms");
        }
    }
}
