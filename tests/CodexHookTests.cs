using System;
using System.Collections.Generic;
using System.IO;

namespace Capsule
{
    public static class CodexHookTests
    {
        public static Dictionary<string, object> Sample(string evt, string tool, string input)
        {
            return new Dictionary<string, object> {
                { "session_id", "codex-test" }, { "cwd", @"C:\work\sample" }, { "hook_event_name", evt },
                { "tool_name", tool }, { "tool_input", Json.Parse(input) }, { "turn_id", "turn-1" }, { "permission_mode", "default" }
            };
        }

        public const string Question = "{\"questions\":[{\"id\":\"name\",\"header\":\"Name\",\"question\":\"Which name?\",\"options\":[{\"label\":\"Ada\",\"description\":\"First name\"},{\"label\":\"Grace\",\"description\":\"Second name\"}]}]}";

        public static void Run()
        {
            var bash = Sample("PermissionRequest", "Bash", "{\"command\":\"echo 'a  b'\",\"description\":\"Read outside the workspace\"}");
            PromptRequest request = CodexHook.Request(bash, 1000);
            TestRunner.Check(request != null, "Codex's documented command approval is accepted");
            TestRunner.Eq("codex", request.Provider, "Codex's card receives its provider");
            TestRunner.Eq("echo 'a  b'", Json.Str(Json.Get(request.ToolInput, "command")), "the complete command is preserved");
            request = PromptRequest.Parse(request.ToJson());
            TestRunner.Eq("codex", request.Provider, "provider survives the pipe");
            TestRunner.Eq("Read outside the workspace", request.Description, "the reason survives the pipe");
            TestRunner.Check(PromptRequest.Parse(request.ToJson().Replace("\"codex\"", "\"other\"")) == null, "unknown providers are rejected");
            var older = Json.Obj(Json.Parse(request.ToJson()));
            older.Remove("provider");
            TestRunner.Eq("claude", PromptRequest.Parse(Json.Write(older)).Provider, "older hooks still default to Claude");
            TestRunner.Eq("allow", Json.Str(Json.Get(Json.Parse(CodexHook.Output(PromptReply.Allowing(), bash)), "hookSpecificOutput", "decision", "behavior")), "approval uses Codex's own decision protocol");
            TestRunner.Eq("deny", Json.Str(Json.Get(Json.Parse(CodexHook.Output(PromptReply.Denying("No"), bash)), "hookSpecificOutput", "decision", "behavior")), "denial uses the decision protocol");
            TestRunner.Eq("", CodexHook.Output(PromptReply.AlwaysAllowing(), bash), "Codex never receives unsupported persistent permissions");
            TestRunner.Eq("", CodexHook.Output(PromptReply.Answering(new Dictionary<string, object>()), bash), "an answer cannot approve a command");

            foreach (string tool in new[] { "apply_patch", "PowerShell", "mcp__sample__write", "request_permissions" })
                TestRunner.Check(CodexHook.Request(Sample("PermissionRequest", tool, "{\"command\":\"echo test\"}"), 0) == null, tool + " stays with its native preview");
            foreach (string input in new[] { "{\"command\":\"\"}", "{\"command\":\"echo\\u202etest\"}", "{\"command\":\"echo test\",\"permissions\":{\"network\":true}}" })
                TestRunner.Check(CodexHook.Request(Sample("PermissionRequest", "Bash", input), 0) == null, "incomplete or wider grants pass");
            bash["tool_input"] = new Dictionary<string, object> { { "command", new string('x', 4001) } };
            TestRunner.Eq("", CodexHook.Output(PromptReply.Allowing(), bash), "even a forged allow cannot approve a truncated command");

            var ask = Sample("PreToolUse", "request_user_input", Question);
            request = CodexHook.Request(ask, 1000);
            TestRunner.Check(request != null && QuestionFlow.Parse(request.ToolInput) != null, "Codex choices use the existing question flow");
            var answers = new Dictionary<string, object> { { "Which name?", "Café …" } };
            string output = CodexHook.Output(PromptReply.Answering(answers), ask);
            TestRunner.Eq("PreToolUse", Json.Str(Json.Get(Json.Parse(output), "hookSpecificOutput", "hookEventName")), "answers use PreToolUse feedback");
            TestRunner.Eq("deny", Json.Str(Json.Get(Json.Parse(output), "hookSpecificOutput", "permissionDecision")), "native question is skipped after the answer");
            TestRunner.Check(Json.Str(Json.Get(Json.Parse(output), "hookSpecificOutput", "permissionDecisionReason")).Contains("Café …"), "the user's exact answer reaches the agent");
            TestRunner.Eq("", CodexHook.Output(PromptReply.Allowing(), ask), "Allow without answers cannot skip a question");
            TestRunner.Eq("", CodexHook.Output(PromptReply.Passing(), ask), "fallback lets the normal question run");
            answers["Which name?"] = new[] { "Ada", "Grace" };
            TestRunner.Eq("", CodexHook.Output(PromptReply.Answering(answers), ask), "Codex's single-choice questions reject multiple answers");
            foreach (string bad in new[] {
                Question.Replace("\"id\":\"name\"", "\"isSecret\":true,\"id\":\"name\""),
                Question.Replace("Which name?", new string('x', 301)),
                Question.Replace("Ada", "Ada\\u200b"),
                Question.Replace("\"id\":\"name\"", "\"id\":\"\""),
                Question.Replace("\"id\":\"name\"", "\"multiSelect\":true,\"id\":\"name\""),
                Question.Replace("\"id\":\"name\"", "\"isOther\":false,\"id\":\"name\""),
                Question.Replace("First name", "First\\u200bname"),
                Question.Replace("\"label\":\"Ada\"", "\"hidden\":true,\"label\":\"Ada\"")
            }) TestRunner.Check(CodexHook.Request(Sample("PreToolUse", "request_user_input", bad), 0) == null, "secret, hidden or incomplete questions stay in the host");
            var async = Sample("PreToolUse", "request_user_input_async", "{\"questions\":[{\"title\":\"Which name?\",\"options\":[\"Ada\",\"Grace\"]}]}");
            TestRunner.Check(QuestionFlow.Parse(CodexHook.Request(async, 0).ToolInput) != null, "Work's async choices use the same flow");
            async["tool_input"] = Json.Parse("{\"questions\":[{\"title\":\"Which name?\"}]}");
            var flow = new QuestionFlow(QuestionFlow.Parse(CodexHook.Request(async, 0).ToolInput));
            TestRunner.Check(flow.OtherOpen && !flow.CanAdvance, "a free-text question starts with an empty answer box");
            flow.SetOther("Ada");
            TestRunner.Check(CodexHook.Output(PromptReply.Answering(flow.Answers()), async) != "", "typed Work answers can be delivered");

            CardsAndQueuesStaySeparate();
            ConnectIsReversible();
        }

        static void CardsAndQueuesStaySeparate()
        {
            var a = new PromptBroker(() => false);
            var b = new PromptBroker(() => false);
            var request = CodexHook.Request(Sample("PermissionRequest", "Bash", "{\"command\":\"echo test\"}"), 1000);
            HeldPrompt one = a.Arrive(new PromptRequest { SessionId = "a", ToolName = "Bash" }, delegate { }, 1000);
            HeldPrompt two = b.Arrive(request, delegate { }, 1000);
            TestRunner.Check(one.Id != two.Id, "providers never share an action target");
            b.Act(PromptAction.Of(PromptAction.Allow, one.Id), 1200);
            TestRunner.Eq(1, b.Count, "a stale Claude action cannot approve a Codex request");
            PromptCardModel card = PromptCardModel.From(two, 1, 45000);
            TestRunner.Eq("Codex / Work wants to run a command", card.Title, "the card names its host");
            TestRunner.Check(card.Countdown.Contains("Codex / Work") && !card.CanAlwaysAllow, "Codex fallback and approval scope are clear");
            TestRunner.Check(CodexApp.IsAppPath(@"C:\Program Files\WindowsApps\OpenAI.Codex_26.1002.7124.0_x64__test\app\ChatGPT.exe"), "the installed local Work app is recognised");
            TestRunner.Check(!CodexApp.IsAppPath(@"C:\Downloads\ChatGPT.exe") && !CodexApp.IsAppPath(@"C:\OpenAI\Codex\bin\abc\codex.exe"), "downloaded executables and CLI processes do not count as the desktop window");
            a.ReleaseAll(); b.ReleaseAll();
        }

        static void ConnectIsReversible()
        {
            string path = Path.Combine(TestRunner.NewTempDir(), "hooks.json");
            File.WriteAllText(path, "{\"description\":\"keep me\",\"hooks\":{\"Stop\":[{\"hooks\":[{\"type\":\"command\",\"command\":\"other.exe\"}]}]}}");
            HookSetup.ConnectCodex(path, @"C:\My Tools\capsule-hook.exe", DateTime.Now);
            HookSetup.ConnectCodex(path, @"C:\My Tools\capsule-hook.exe", DateTime.Now);
            object root = Json.Parse(File.ReadAllText(path));
            object[] entries = Json.Arr(Json.Get(root, "hooks", "PreToolUse"));
            TestRunner.Eq(1, entries.Length, "reconnecting does not duplicate the question hook");
            object handler = Json.Arr(Json.Get(entries[0], "hooks"))[0];
            TestRunner.Eq(120.0, Json.Num(Json.Get(handler, "timeout")).Value, "questions have time to be answered");
            string command = Json.Str(Json.Get(handler, "command"));
            TestRunner.Eq("cmd.exe /d /v:off /c \"C:\\My Tools\\capsule-hook.exe\" --codex", command, "the launcher preserves spaces and the provider argument");
            TestRunner.Check(Json.Get(handler, "args") == null, "Codex does not receive Claude's unsupported exec arguments");
            HookSetup.Disconnect(path, DateTime.Now);
            root = Json.Parse(File.ReadAllText(path));
            TestRunner.Eq("keep me", Json.Str(Json.Get(root, "description")), "disconnect preserves unrelated settings");
            TestRunner.Check(Json.Get(root, "hooks", "Stop") != null && Json.Get(root, "hooks", "PreToolUse") == null, "disconnect removes only Capsule's hooks");
            TestRunner.Check(CodexHook.Command(@"C:\O'Brien\capsule-hook.exe").Contains("O'Brien"), "the launcher preserves apostrophes");
            foreach (char special in new[] { '%', '$', '`', '\n', '&', '(', ')', '^', '|', '<', '>', '@' })
            {
                bool rejected = false;
                try { HookSetup.ConnectCodex(path, "C:\\" + special + "literal\\capsule-hook.exe", DateTime.Now); }
                catch (ArgumentException) { rejected = true; }
                TestRunner.Check(rejected && File.ReadAllText(path) == Json.Write(root) + "\n", "unsafe installation paths leave settings unchanged");
            }
        }
    }
}
