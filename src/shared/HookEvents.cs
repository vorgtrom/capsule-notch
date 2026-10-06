using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Capsule
{
    public static class States
    {
        public const string Idle = "idle";
        public const string Working = "working";
        public const string Waiting = "waiting";
        public const string Done = "done";
    }

    // What one hook event does to its session's status file.
    public sealed class HookOutcome
    {
        public bool Delete;      // SessionEnd: remove the file
        public bool Ignore;      // change nothing
        public bool OnlyIfNew;   // SessionStart: create the file, never overwrite it
        public string State = States.Idle;
        public string Reason = "";

        public static HookOutcome Set(string state, string reason)
        {
            var o = new HookOutcome();
            o.State = state;
            o.Reason = reason ?? "";
            return o;
        }

        public static HookOutcome Nothing() { var o = new HookOutcome(); o.Ignore = true; return o; }
        public static HookOutcome Remove() { var o = new HookOutcome(); o.Delete = true; return o; }
    }

    // One Claude Code session as its status file (sessions\<session_id>.json) describes it.
    public sealed class SessionStatus
    {
        public string SessionId = "";
        public string State = States.Idle;
        public string Event = "";
        public string Project = "";
        public string Reason = "";
        public long Since;   // when the current state began (ms)
        public long At;      // time of the latest event (ms)

        public static SessionStatus Read(string path)
        {
            var obj = Json.Obj(Json.TryParse(Files.ReadText(path)));
            if (obj == null) return null;
            var s = new SessionStatus();
            s.SessionId = Json.Str(Json.Get(obj, "session_id")) ?? "";
            s.State = Json.Str(Json.Get(obj, "state")) ?? States.Idle;
            s.Event = Json.Str(Json.Get(obj, "event")) ?? "";
            s.Project = Json.Str(Json.Get(obj, "project")) ?? "";
            s.Reason = Json.Str(Json.Get(obj, "reason")) ?? "";
            s.Since = (long)(Json.Num(Json.Get(obj, "since")) ?? 0);
            s.At = (long)(Json.Num(Json.Get(obj, "at")) ?? 0);
            return s;
        }

        public string ToJson()
        {
            var d = new Dictionary<string, object>();
            d["session_id"] = SessionId;
            d["state"] = State;
            d["since"] = Since;
            d["at"] = At;
            d["event"] = Event;
            d["project"] = Project;
            d["reason"] = Reason;
            return Json.Write(d);
        }
    }

    // The names of Claude Code's tools that Capsule treats in its own way, in one place: the hook, the broker and the card
    // all name them.
    public static class ToolNames
    {
        public const string Bash = "Bash", PowerShell = "PowerShell", Edit = "Edit", MultiEdit = "MultiEdit", Write = "Write",
            NotebookEdit = "NotebookEdit", Read = "Read", WebFetch = "WebFetch", WebSearch = "WebSearch",
            AskUserQuestion = "AskUserQuestion", ExitPlanMode = "ExitPlanMode";
    }

    // Claude Code hook events -> session states (spec section 4.3), and what capsule-hook.exe does with one event.
    public static class HookEvents
    {
        // The event Capsule answers from the notch: its hook is given longer than the others, and it is the only one that
        // asks Capsule anything.
        public const string PermissionRequest = "PermissionRequest";

        // Registered by "Connect to Claude Code": event name and matcher (null = no matcher).
        public static readonly string[][] Wiring =
        {
            new[] { "SessionStart", null },
            new[] { "UserPromptSubmit", null },
            new[] { "PreToolUse", ToolNames.AskUserQuestion + "|" + ToolNames.ExitPlanMode },
            new[] { PermissionRequest, "*" },
            new[] { "Notification", null },
            new[] { "PostToolUse", "*" },
            new[] { "PostToolUseFailure", "*" },
            new[] { "PermissionDenied", "*" },
            new[] { "Stop", null },
            new[] { "StopFailure", null },
            new[] { "SessionEnd", null },
        };

        // Every candidate event, registered with --probe during the verification task (Task 4).
        public static readonly string[][] ProbeWiring =
        {
            new[] { "SessionStart", null },
            new[] { "UserPromptSubmit", null },
            new[] { "PreToolUse", "*" },
            new[] { PermissionRequest, "*" },
            new[] { "PermissionDenied", "*" },
            new[] { "PostToolUse", "*" },
            new[] { "PostToolUseFailure", "*" },
            new[] { "PostToolBatch", null },
            new[] { "Notification", null },
            new[] { "Elicitation", null },
            new[] { "SubagentStart", null },
            new[] { "SubagentStop", null },
            new[] { "Stop", null },
            new[] { "StopFailure", null },
            new[] { "SessionEnd", null },
        };

        static readonly HashSet<string> WaitingNotifications = new HashSet<string>
        {
            "permission_prompt", "elicitation_dialog", "elicitation_url_dialog", "agent_needs_input"
        };

        public static HookOutcome Map(string eventName, string notificationType, string toolName, string message)
        {
            switch (eventName ?? "")
            {
                case "SessionStart":
                    HookOutcome start = HookOutcome.Set(States.Idle, "");
                    start.OnlyIfNew = true;
                    return start;
                case "UserPromptSubmit":
                case "PostToolUse":
                case "PostToolUseFailure":
                case "PermissionDenied":
                    return HookOutcome.Set(States.Working, "");
                case "PreToolUse":
                case PermissionRequest:
                    if (toolName == ToolNames.AskUserQuestion) return HookOutcome.Set(States.Waiting, "asked you a question");
                    if (toolName == ToolNames.ExitPlanMode) return HookOutcome.Set(States.Waiting, "wants plan approval");
                    if (eventName == "PreToolUse") return HookOutcome.Nothing();
                    return HookOutcome.Set(States.Waiting, "needs permission: " + (string.IsNullOrEmpty(toolName) ? "a tool" : toolName));
                case "Notification":
                    if (notificationType == "idle_prompt") return HookOutcome.Set(States.Done, "");
                    if (notificationType != null && WaitingNotifications.Contains(notificationType)) return HookOutcome.Set(States.Waiting, Cut(message, 80));
                    return HookOutcome.Nothing();
                case "Stop":
                case "StopFailure":
                    return HookOutcome.Set(States.Done, "");
                case "SessionEnd":
                    return HookOutcome.Remove();
                default:
                    return HookOutcome.Nothing();
            }
        }

        // The status to write after an event (existing = null when the session has no file yet), or null to write nothing.
        public static SessionStatus Next(SessionStatus existing, HookOutcome outcome, string sessionId, string eventName, string project, long now)
        {
            if (outcome.Ignore || outcome.Delete) return null;
            if (outcome.OnlyIfNew && existing != null) return null;
            var s = new SessionStatus();
            s.SessionId = sessionId;
            s.State = outcome.State;
            s.Event = eventName;
            s.Reason = outcome.Reason;
            s.At = now;
            s.Project = !string.IsNullOrEmpty(project) ? project : (existing != null ? existing.Project : "");
            s.Since = existing != null && existing.State == outcome.State ? existing.Since : now;
            return s;
        }

        public static bool IsValidSessionId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 128) return false;
            foreach (char c in id)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_';
                if (!ok) return false;
            }
            return true;
        }

        public static string ProjectName(string cwd)
        {
            if (string.IsNullOrEmpty(cwd)) return "";
            string trimmed = cwd.TrimEnd('\\', '/');
            int cut = Math.Max(trimmed.LastIndexOf('\\'), trimmed.LastIndexOf('/'));
            return cut >= 0 ? trimmed.Substring(cut + 1) : trimmed;
        }

        static string Cut(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
        }

        // Everything capsule-hook.exe does with one event, apart from reading stdin.
        public static string Handle(string input, bool probe, long now)
        {
            var root = Json.Obj(Json.TryParse(input));
            if (root == null) return "unparsed";
            if (probe) AppendProbeLine(root, now);
            string sessionId = Json.Str(Json.Get(root, "session_id")) ?? "";
            if (!IsValidSessionId(sessionId)) return "bad-session";
            string eventName = Json.Str(Json.Get(root, "hook_event_name")) ?? "";
            HookOutcome outcome = Map(eventName,
                Json.Str(Json.Get(root, "notification_type")),
                Json.Str(Json.Get(root, "tool_name")),
                Json.Str(Json.Get(root, "message")));
            string path = Path.Combine(Paths.SessionsDir, sessionId + ".json");
            if (outcome.Delete)
            {
                try { File.Delete(path); } catch (Exception) { }
                return "deleted";
            }
            if (outcome.Ignore) return "ignored";
            SessionStatus next = Next(SessionStatus.Read(path), outcome, sessionId, eventName, ProjectName(Json.Str(Json.Get(root, "cwd"))), now);
            if (next == null) return "unchanged";
            return Files.WriteAtomic(path, next.ToJson()) ? next.State : "write-failed";
        }

        // One line per event for the verification task. Names and types only: never prompts, messages, or tool input/output.
        static void AppendProbeLine(Dictionary<string, object> root, long now)
        {
            var parts = new List<string>();
            parts.Add(Clock.FromMs(now).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
            foreach (string key in new[] { "hook_event_name", "notification_type", "tool_name", "permission_mode", "source", "agent_type" })
            {
                string value = Json.Str(Json.Get(root, key));
                if (!string.IsNullOrEmpty(value)) parts.Add(key + "=" + value);
            }
            // SessionEnd's reason is a fixed word; other events' reasons can be free text quoting a command.
            if (Json.Str(Json.Get(root, "hook_event_name")) == "SessionEnd")
            {
                string reason = Json.Str(Json.Get(root, "reason"));
                if (!string.IsNullOrEmpty(reason)) parts.Add("reason=" + reason);
            }
            string sid = Json.Str(Json.Get(root, "session_id")) ?? "";
            parts.Add("session=" + (sid.Length > 8 ? sid.Substring(0, 8) : sid));
            if (Json.Get(root, "agent_id") != null) parts.Add("subagent");
            try { File.AppendAllText(Paths.ProbeLogFile, string.Join(" ", parts) + Environment.NewLine, Files.Utf8); }
            catch (Exception) { }
        }
    }
}
