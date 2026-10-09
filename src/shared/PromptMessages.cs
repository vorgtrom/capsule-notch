using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Capsule
{
    // What capsule-hook.exe sends Capsule for one PermissionRequest (act-from-notch spec §4): what the card needs, held in
    // memory only. The tool input is shown on screen and never logged or written anywhere.
    public sealed class PromptRequest
    {
        public string SessionId = "";
        public string Project = "";     // the project folder's name
        public string Cwd = "";         // the folder itself, to shorten file paths on the card
        public string ToolName = "";
        public string ToolUseId = "";
        public Dictionary<string, object> ToolInput = new Dictionary<string, object>();
        public List<string> Rules = new List<string>();   // what "Always allow" would apply, as Claude Code names it
        // Where each of Rules would be kept, one entry for each (the list may be shorter: then the rest are unknown):
        // DestSession, DestProject, DestUser, or "".
        public List<string> Destinations = new List<string>();
        public long At;   // when the hook wrote the session's status for this request (ms)

        public const string AccessPrefix = "Access to ";   // an addDirectories suggestion's rule names: "Access to <directory>"
        public const string AcceptEditsName = "Accept all edits";   // a setMode acceptEdits suggestion's name: the card's pill then says so
        public const string DestSession = "session", DestProject = "project", DestUser = "user";
        public const int MaxCommand = 4000, MaxText = 2000;   // the most of a command, or of any other text, the hook sends

        // From the hook's input: null unless it is a PermissionRequest with a valid session id and a tool name.
        public static PromptRequest FromHookInput(Dictionary<string, object> root, long now)
        {
            if (root == null || Json.Str(Json.Get(root, "hook_event_name")) != HookEvents.PermissionRequest) return null;
            var r = new PromptRequest();
            r.SessionId = Json.Str(Json.Get(root, "session_id")) ?? "";
            r.ToolName = Json.Str(Json.Get(root, "tool_name")) ?? "";
            if (!HookEvents.IsValidSessionId(r.SessionId) || r.ToolName == "") return null;
            if (!CanReview(r.ToolName, Json.Obj(Json.Get(root, "tool_input")))) return null;
            r.Cwd = Json.Str(Json.Get(root, "cwd")) ?? "";
            r.Project = HookEvents.ProjectName(r.Cwd);
            r.ToolUseId = Json.Str(Json.Get(root, "tool_use_id")) ?? "";
            r.ToolInput = ForTheCard(r.ToolName, Json.Obj(Json.Get(root, "tool_input")));
            // What "Always allow" would do, named. If any suggestion can't be named, none is: Always allow isn't offered.
            var names = new List<string>();
            var destinations = new List<string>();
            foreach (object suggestion in HookReply.Suggestions(root))
            {
                List<string> named = NamesOf(suggestion);
                if (named == null)
                {
                    names.Clear();
                    destinations.Clear();
                    break;
                }
                string destination = DestinationOf(suggestion);
                foreach (string name in named)
                {
                    names.Add(name);
                    destinations.Add(destination);
                }
            }
            r.Rules.AddRange(names);
            r.Destinations.AddRange(destinations);
            if (!HookReply.AllNameable(root)) { r.Rules.Clear(); r.Destinations.Clear(); }
            r.At = now;
            return r;
        }

        // shortcut: only complete commands and questions have a full preview, add other tools when their full input is shown.
        internal static bool CanReview(string tool, Dictionary<string, object> input)
        {
            if (input == null) return false;
            if (tool == ToolNames.AskUserQuestion) return true;
            if (tool != ToolNames.Bash && tool != ToolNames.PowerShell) return false;
            string command = Json.Str(Json.Get(input, "command"));
            return !string.IsNullOrWhiteSpace(command) && command.Length <= MaxCommand && VisibleText(command, true);
        }

        internal static bool VisibleText(string text, bool lineBreaks)
        {
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (lineBreaks && (c == '\r' || c == '\n' || c == '\t')) continue;
                if (char.IsControl(c) || CharUnicodeInfo.GetUnicodeCategory(text, i) == UnicodeCategory.Format) return false;
                if (char.IsHighSurrogate(c))
                {
                    if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1])) return false;
                    i++;
                }
                else if (char.IsLowSurrogate(c)) return false;
            }
            return true;
        }

        // Only what the card shows is sent to Capsule (the hook's own reply never takes tool input from Capsule's reply,
        // except an AskUserQuestion's answers, so nothing is lost): a command, a file, a URL, a query; a question's own
        // input, which the card needs whole; for another tool, its text, each part cut to MaxText. Never what a file is
        // to hold (Write's content, Edit's old and new text, MultiEdit's edits, a notebook's new source) or a plan.
        internal static Dictionary<string, object> ForTheCard(string tool, Dictionary<string, object> input)
        {
            var sent = new Dictionary<string, object>();
            if (input == null) return sent;
            switch (tool)
            {
                case ToolNames.AskUserQuestion:
                    return input;
                case ToolNames.ExitPlanMode:
                    return sent;
                case ToolNames.Bash:
                case ToolNames.PowerShell:
                    Keep(input, sent, "command", MaxCommand);
                    return sent;
                case ToolNames.Edit:
                case ToolNames.MultiEdit:
                case ToolNames.Write:
                case ToolNames.Read:
                    Keep(input, sent, "file_path", MaxText);
                    return sent;
                case ToolNames.NotebookEdit:
                    Keep(input, sent, "notebook_path", MaxText);
                    return sent;
                case ToolNames.WebFetch:
                    Keep(input, sent, "url", MaxText);
                    return sent;
                case ToolNames.WebSearch:
                    Keep(input, sent, "query", MaxText);
                    return sent;
                default:
                    foreach (KeyValuePair<string, object> pair in input)
                        if (pair.Value is string) sent[pair.Key] = CapText((string)pair.Value, MaxText);
                    return sent;
            }
        }

        static void Keep(Dictionary<string, object> source, Dictionary<string, object> target, string key, int max)
        {
            string text = Json.Str(Json.Get(source, key));
            if (text != null) target[key] = CapText(text, max);
        }

        // At most max characters, ending in … when it was cut, never between the halves of a surrogate pair.
        internal static string CapText(string text, int max)
        {
            if (text == null || text.Length <= max) return text;
            int cut = max - 1;
            if (cut > 0 && char.IsHighSurrogate(text[cut - 1])) cut--;
            return text.Substring(0, cut) + "…";
        }

        // What one of Claude Code's suggestions would do, named for the card: a rule (a plain {rule}, or addRules, with its
        // rules written as Claude Code writes them), a mode, or a directory (one name each). Null when it can't be named:
        // any other type, a missing field, an unknown mode, a rule that doesn't allow. Always allow is then not offered.
        internal static List<string> NamesOf(object suggestion)
        {
            if (Json.Obj(suggestion) == null) return null;
            var names = new List<string>();
            object typeValue = Json.Get(suggestion, "type");
            if (typeValue == null)
            {
                string name = Json.Str(Json.Get(suggestion, "rule"));
                if (string.IsNullOrEmpty(name)) name = Json.Str(Json.Get(suggestion, "description"));
                if (string.IsNullOrEmpty(name)) name = RuleNames(suggestion);
                if (string.IsNullOrEmpty(name)) return null;
                names.Add(name);
                return names;
            }
            switch (Json.Str(typeValue))
            {
                case "addRules":
                    object behavior = Json.Get(suggestion, "behavior");
                    if (behavior != null && Json.Str(behavior) != "allow") return null;
                    string rules = RuleNames(suggestion);
                    if (string.IsNullOrEmpty(rules)) return null;
                    names.Add(rules);
                    return names;
                case "setMode":
                    string mode = ModeName(Json.Str(Json.Get(suggestion, "mode")));
                    if (mode == null) return null;
                    names.Add(mode);
                    return names;
                case "addDirectories":
                    object[] directories = Json.Arr(Json.Get(suggestion, "directories"));
                    if (directories == null || directories.Length == 0) return null;
                    foreach (object directory in directories)
                    {
                        string path = Json.Str(directory);
                        if (string.IsNullOrWhiteSpace(path)) return null;
                        names.Add(AccessPrefix + path);
                    }
                    return names;
                default:
                    return null;
            }
        }

        // Only accepting edits can be named on the card. Every other mode (bypassPermissions above all, but plan and default
        // too) changes more than the card can say in a pill, so Always allow isn't offered with it.
        static string ModeName(string mode)
        {
            return mode == "acceptEdits" ? AcceptEditsName : null;
        }

        // Where Claude Code would keep what a suggestion does, as the card words it: for this session, saved for this
        // project, or saved for the user. "" when it names none, or one that isn't known.
        internal static string DestinationOf(object suggestion)
        {
            switch (Json.Str(Json.Get(suggestion, "destination")))
            {
                case "session": return DestSession;
                case "projectSettings":
                case "localSettings": return DestProject;
                case "userSettings": return DestUser;
                default: return "";
            }
        }

        // rules[].toolName(ruleContent), as Claude Code writes a rule, joined by ", ". "" when there are none, or one has no
        // tool.
        static string RuleNames(object suggestion)
        {
            var names = new List<string>();
            object[] rules = Json.Arr(Json.Get(suggestion, "rules"));
            if (rules == null) return "";
            foreach (object rule in rules)
            {
                string tool = Json.Str(Json.Get(rule, "toolName"));
                if (string.IsNullOrEmpty(tool)) return "";
                string content = Json.Str(Json.Get(rule, "ruleContent"));
                names.Add(string.IsNullOrEmpty(content) ? tool : tool + "(" + content + ")");
            }
            return string.Join(", ", names);
        }

        public string ToJson()
        {
            var d = new Dictionary<string, object>();
            d["session_id"] = SessionId;
            d["project"] = Project;
            d["cwd"] = Cwd;
            d["tool_name"] = ToolName;
            d["tool_use_id"] = ToolUseId;
            d["tool_input"] = ToolInput;
            d["rules"] = Rules;
            d["destinations"] = Destinations;
            d["at"] = At;
            return Json.Write(d);
        }

        // Null when it isn't a request: not JSON, a bad session id, no tool.
        public static PromptRequest Parse(string json)
        {
            var root = Json.Obj(Json.TryParse(json));
            if (root == null) return null;
            var r = new PromptRequest();
            r.SessionId = Json.Str(Json.Get(root, "session_id")) ?? "";
            r.ToolName = Json.Str(Json.Get(root, "tool_name")) ?? "";
            if (!HookEvents.IsValidSessionId(r.SessionId) || r.ToolName == "") return null;
            r.Project = Json.Str(Json.Get(root, "project")) ?? "";
            r.Cwd = Json.Str(Json.Get(root, "cwd")) ?? "";
            r.ToolUseId = Json.Str(Json.Get(root, "tool_use_id")) ?? "";
            r.ToolInput = Json.Obj(Json.Get(root, "tool_input")) ?? new Dictionary<string, object>();
            object[] rules = Json.Arr(Json.Get(root, "rules"));
            if (rules != null) foreach (object rule in rules) if (rule is string) r.Rules.Add((string)rule);
            object[] destinations = Json.Arr(Json.Get(root, "destinations"));
            if (destinations != null)
                foreach (object destination in destinations)
                {
                    string d = destination as string;
                    r.Destinations.Add(d == DestSession || d == DestProject || d == DestUser ? d : "");   // only the words the card knows
                }
            r.At = (long)(Json.Num(Json.Get(root, "at")) ?? 0);
            return r;
        }
    }

    // Capsule's reply to one request: pass (Claude Code asks as usual), allow (perhaps always, perhaps with answers), or
    // deny. It carries no tool input and no rules: the hook takes those from Claude Code's own input (HookReply).
    public sealed class PromptReply
    {
        public const string Pass = "pass", Allow = "allow", Deny = "deny";

        public string Kind = Pass;
        public bool Always;                          // apply the rules Claude Code suggested
        public Dictionary<string, object> Answers;   // AskUserQuestion: question text -> label, labels or typed text
        public string Message = "";                  // deny's message to Claude

        public static PromptReply Passing() { return new PromptReply(); }
        public static PromptReply Allowing() { return new PromptReply { Kind = Allow }; }
        public static PromptReply AlwaysAllowing() { return new PromptReply { Kind = Allow, Always = true }; }
        public static PromptReply Denying(string message) { return new PromptReply { Kind = Deny, Message = message ?? "" }; }
        // answers: question text -> a label or typed text, or, for a multi-select, an array of them (object[], string[] or
        // a list). The hook joins an array with ", " when it prints the decision.
        public static PromptReply Answering(Dictionary<string, object> answers) { return new PromptReply { Kind = Allow, Answers = answers }; }

        public string ToJson()
        {
            var d = new Dictionary<string, object>();
            d["reply"] = Kind;
            if (Always) d["always"] = true;
            if (Answers != null) d["answers"] = Answers;
            if (Message != "") d["message"] = Message;
            return Json.Write(d);
        }

        // Null unless it is a well-formed reply.
        public static PromptReply Parse(string json)
        {
            var root = Json.Obj(Json.TryParse(json));
            if (root == null) return null;
            string kind = Json.Str(Json.Get(root, "reply"));
            if (kind != Pass && kind != Allow && kind != Deny) return null;
            var r = new PromptReply { Kind = kind };
            object always = Json.Get(root, "always");
            if (always != null && !(always is bool)) return null;
            r.Always = always != null && (bool)always;
            object answers = Json.Get(root, "answers");
            if (answers != null && Json.Obj(answers) == null) return null;
            r.Answers = Json.Obj(answers);
            object message = Json.Get(root, "message");
            if (message != null && !(message is string)) return null;
            r.Message = (string)message ?? "";
            return r;
        }
    }

    // What capsule-hook.exe prints for Capsule's reply (spec §4): nothing, so Claude Code asks as usual, or the decision.
    // An allow echoes only Claude Code's own input and suggestions: the reply can't invent rules or input.
    public static class HookReply
    {
        public const string DeniedMessage = "Denied from the capsule";
        public const string KeepPlanningMessage = "Not approved from the capsule: keep planning";
        public const int MaxMessage = 200, MaxAnswer = 2000, MaxQuestions = 4;

        // "" for pass, for no reply, and for any reply that doesn't fit the request.
        public static string Output(PromptReply reply, Dictionary<string, object> hookInput)
        {
            if (reply == null || hookInput == null) return "";
            string tool = Json.Str(Json.Get(hookInput, "tool_name")) ?? "";
            var decision = new Dictionary<string, object>();
            if (reply.Kind == PromptReply.Deny)
            {
                decision["behavior"] = "deny";
                decision["message"] = Message(reply.Message);
            }
            else if (reply.Kind == PromptReply.Allow)
            {
                if (!PromptRequest.CanReview(tool, Json.Obj(Json.Get(hookInput, "tool_input")))) return "";
                decision["behavior"] = "allow";
                if (tool == ToolNames.AskUserQuestion)
                {
                    Dictionary<string, object> input = Json.Obj(Json.Get(hookInput, "tool_input"));
                    object[] questions = input == null ? null : Json.Arr(Json.Get(input, "questions"));
                    if (questions == null || !ValidAnswers(reply.Answers, questions)) return "";
                    var updated = new Dictionary<string, object>(input);   // Claude Code's own input, as it came
                    updated["answers"] = Printed(reply.Answers);
                    decision["updatedInput"] = updated;
                }
                else if (reply.Answers != null) return "";
                else if (reply.Always)
                {
                    // Always allow is only for suggestions the card could name: nothing else is ever applied.
                    if (!AllNameable(hookInput)) return "";
                    List<object> rules = Permissions(hookInput);
                    if (rules.Count > 0) decision["updatedPermissions"] = rules;
                }
            }
            else return "";
            var specific = new Dictionary<string, object>();
            specific["hookEventName"] = HookEvents.PermissionRequest;
            specific["decision"] = decision;
            var output = new Dictionary<string, object>();
            output["hookSpecificOutput"] = specific;
            return Json.Write(output);
        }

        // Claude Code's suggested rules, from `suggestions` (or `permission_suggestions`): the objects as they came.
        public static List<object> Suggestions(Dictionary<string, object> hookInput)
        {
            var list = new List<object>();
            object[] raw = Json.Arr(Json.Get(hookInput, "suggestions")) ?? Json.Arr(Json.Get(hookInput, "permission_suggestions"));
            if (raw != null) foreach (object s in raw) if (Json.Obj(s) != null) list.Add(s);
            return list;
        }

        // Every suggestion can be named on the card (so the user saw what Always allow does).
        internal static bool AllNameable(Dictionary<string, object> hookInput)
        {
            int length = 0;
            foreach (object s in Suggestions(hookInput))
            {
                List<string> names = PromptRequest.NamesOf(s);
                if (names == null) return false;
                foreach (string name in names)
                {
                    length += name.Length;
                    if (length > PromptRequest.MaxCommand || !PromptRequest.VisibleText(name, false)) return false;
                }
            }
            return true;
        }

        // What "Always allow" applies: each suggestion's rule, or the suggestion itself when it has no rule string, exactly
        // as Claude Code's own button would.
        public static List<object> Permissions(Dictionary<string, object> hookInput)
        {
            var list = new List<object>();
            foreach (object s in Suggestions(hookInput))
            {
                string rule = Json.Str(Json.Get(s, "rule"));
                list.Add(rule != null ? (object)rule : s);
            }
            return list;
        }

        // Every question answered, by its own text, with non-blank text or a list of it: at most one part for each of the
        // question's options, and one more for typed text. Each part is checked, not the joined text, which may be longer
        // than one answer can be.
        public static bool ValidAnswers(Dictionary<string, object> answers, object[] questions)
        {
            if (answers == null || questions == null || questions.Length == 0 || questions.Length > MaxQuestions || answers.Count != questions.Length) return false;
            foreach (object q in questions)
            {
                string text = Json.Str(Json.Get(q, "question"));
                object answer;
                if (text == null || !answers.TryGetValue(text, out answer)) return false;
                List<object> several = Several(answer);
                if (several != null)
                {
                    object[] options = Json.Arr(Json.Get(q, "options"));
                    int most = (options == null ? 0 : options.Length) + 1;
                    if (several.Count == 0 || several.Count > most) return false;
                    foreach (object one in several) if (!GoodText(one)) return false;
                }
                else if (!GoodText(answer)) return false;
            }
            return true;
        }

        // The parts of an answer that is a list (an object[], a string[] or any list), or null when it isn't one.
        static List<object> Several(object answer)
        {
            if (answer == null || answer is string || answer is Dictionary<string, object>) return null;
            var items = answer as System.Collections.IEnumerable;
            if (items == null) return null;
            var list = new List<object>();
            foreach (object item in items) list.Add(item);
            return list;
        }

        // The answers as Claude Code takes them: a multi-select's parts joined with ", ".
        static Dictionary<string, object> Printed(Dictionary<string, object> answers)
        {
            var printed = new Dictionary<string, object>();
            foreach (KeyValuePair<string, object> pair in answers)
            {
                List<object> several = Several(pair.Value);
                printed[pair.Key] = several == null ? pair.Value : string.Join(", ", several.Select(one => (string)one));
            }
            return printed;
        }

        static bool GoodText(object value)
        {
            string s = value as string;
            return s != null && s.Trim() != "" && s.Length <= MaxAnswer;
        }

        static string Message(string message)
        {
            if (string.IsNullOrEmpty(message) || message.Length > MaxMessage) return DeniedMessage;
            foreach (char c in message) if (char.IsControl(c)) return DeniedMessage;
            return message;
        }
    }
}
