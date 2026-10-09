using System;
using System.Collections.Generic;
using System.Text;

namespace Capsule
{
    // Codex and local Work use command hooks, not Claude's updatedInput/updatedPermissions protocol.
    public static class CodexHook
    {
        public static readonly string[][] Wiring = {
            new[] { "PermissionRequest", "^Bash$" },
            new[] { "PreToolUse", "^(request_user_input|request_user_input_async)$" },
        };

        // Both Windows hook shells can launch cmd; stdin and stdout stay attached to the hook executable.
        public static string Command(string exe)
        {
            // shortcut: cmd expands percent variables in paths, use a normal installation folder until a native hook launcher exists.
            if (exe.IndexOf('%') >= 0 || exe.IndexOf('"') >= 0 || !PromptRequest.VisibleText(exe, false))
                throw new ArgumentException("Install Capsule in a folder without percent signs or control characters before connecting Codex.");
            return "cmd.exe /d /s /c \"\"" + exe + "\" --codex\"";
        }

        public static PromptRequest Request(Dictionary<string, object> root, long now)
        {
            if (root == null) return null;
            string evt = Json.Str(Json.Get(root, "hook_event_name"));
            string tool = Json.Str(Json.Get(root, "tool_name"));
            var input = Json.Obj(Json.Get(root, "tool_input"));
            if (input == null) return null;
            bool question = evt == "PreToolUse" && (tool == "request_user_input" || tool == "request_user_input_async");
            if (!question && (evt != "PermissionRequest" || tool != "Bash")) return null;
            var copy = new Dictionary<string, object>(root);
            copy.Remove("suggestions");
            copy.Remove("permission_suggestions");
            if (question)
            {
                input = Questions(input, tool == "request_user_input_async");
                if (input == null) return null;
                copy["hook_event_name"] = "PermissionRequest";
                copy["tool_name"] = ToolNames.AskUserQuestion;
                copy["tool_input"] = input;
            }
            else
            {
                // shortcut: only the documented command and reason are previewed, pass wider grants to the host.
                foreach (string key in input.Keys) if (key != "command" && key != "description") return null;
                object description = Json.Get(input, "description");
                if (description != null && (!(description is string) || !Fits((string)description, PromptRequest.MaxText, true))) return null;
            }
            string cwd = Json.Str(Json.Get(root, "cwd"));
            if (string.IsNullOrWhiteSpace(cwd) || !Fits(cwd, PromptRequest.MaxText, false)) return null;
            PromptRequest request = PromptRequest.FromHookInput(copy, now);
            if (request == null) return null;
            request.Provider = "codex";
            if (!question) request.Description = Json.Str(Json.Get(input, "description")) ?? "";
            return request;
        }

        static Dictionary<string, object> Questions(Dictionary<string, object> input, bool async)
        {
            object[] raw = Json.Arr(Json.Get(input, "questions"));
            if (raw == null || raw.Length == 0 || raw.Length > HookReply.MaxQuestions) return null;
            var questions = new List<object>();
            var texts = new HashSet<string>();
            var ids = new HashSet<string>();
            foreach (object item in raw)
            {
                string text = Json.Str(Json.Get(item, async ? "title" : "question"));
                string header = async ? "" : Json.Str(Json.Get(item, "header"));
                string id = async ? text : Json.Str(Json.Get(item, "id"));
                if (string.IsNullOrWhiteSpace(text) || !Fits(text, 300, true) || !texts.Add(text)) return null;
                if (header == null || !Fits(header, 40, false) || string.IsNullOrWhiteSpace(id) || !ids.Add(id)) return null;
                object secret = Json.Get(item, "isSecret") ?? Json.Get(item, "is_secret");
                if (secret != null && (!(secret is bool) || (bool)secret)) return null;
                object[] options = Json.Arr(Json.Get(item, "options"));
                if ((!async && options == null) || (options != null && (options.Length == 0 || options.Length > 4))) return null;
                if (Json.Get(item, "options") != null && options == null) return null;
                var converted = new List<object>();
                var labels = new HashSet<string>();
                foreach (object option in options ?? new object[0])
                {
                    string label = async ? Json.Str(option) : Json.Str(Json.Get(option, "label"));
                    string description = async ? "" : Json.Str(Json.Get(option, "description"));
                    if (string.IsNullOrWhiteSpace(label) || !Fits(label, 80, false) || !labels.Add(label)
                        || description == null || !Fits(description, 160, true)) return null;
                    converted.Add(new Dictionary<string, object> { { "label", label }, { "description", description } });
                }
                questions.Add(new Dictionary<string, object> {
                    { "question", text }, { "header", header }, { "options", converted.ToArray() }, { "freeText", async && converted.Count == 0 }
                });
            }
            return new Dictionary<string, object> { { "questions", questions.ToArray() } };
        }

        static bool Fits(string text, int max, bool lines) { return text.Length <= max && PromptRequest.VisibleText(text, lines); }

        public static string Handle(string input, long now, string pipe, int connectMs, int replyMs)
        {
            var root = Json.Obj(Json.TryParse(input));
            PromptRequest request = Request(root, now);
            if (request == null) return "";
            return Output(PromptReply.Parse(PromptClient.Ask(pipe, request.ToJson(), connectMs, replyMs)), root);
        }

        public static string Output(PromptReply reply, Dictionary<string, object> root)
        {
            PromptRequest request = Request(root, 0);
            if (reply == null || request == null || reply.Kind == PromptReply.Pass || reply.Always) return "";
            if (request.ToolName != ToolNames.AskUserQuestion)
            {
                if (reply.Answers != null) return "";
                return HookReply.Output(reply, root);
            }
            object[] questions = Json.Arr(Json.Get(request.ToolInput, "questions"));
            if (reply.Kind != PromptReply.Allow || !HookReply.ValidAnswers(reply.Answers, questions)) return "";
            foreach (object value in reply.Answers.Values)
                if (!(value is string) || !Fits((string)value, HookReply.MaxAnswer, true)) return "";
            // PreToolUse cannot return a tool result. Its documented deny reason delivers the user's answers to the model.
            string feedback = "The user answered in Capsule. Use these answers and continue without asking these questions again.\n" + Json.Write(reply.Answers);
            return Json.Write(new Dictionary<string, object> { { "hookSpecificOutput", new Dictionary<string, object> {
                { "hookEventName", "PreToolUse" }, { "permissionDecision", "deny" }, { "permissionDecisionReason", feedback }
            } } });
        }
    }
}
