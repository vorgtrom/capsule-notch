using System;
using System.Collections.Generic;
using System.Globalization;

namespace Capsule
{
    // One reply's token use, read from a Claude Code or Codex log line. Tokens leaves out cache reads, which are kept
    // apart in CacheRead.
    public sealed class UsageEntry
    {
        public string Key;        // Claude's reply key (message id + request id); for Codex, "cx:" and the running total
        public long AtMs;
        public string Model = "";
        public long Tokens;
        public long CacheRead;
    }

    // What a Codex log has told us so far: the model of the current turn, the last running total (so a repeated total
    // isn't counted twice), and the chat family it belongs to. Kept per file between scans.
    public sealed class CodexFileState
    {
        public string Model = "";
        public long[] Totals;     // input, cached input, output; null before the first count
        public string Lineage = "";   // the root chat's id from the log's own session_meta: shared by a chat and its forks
        public string Fallback = "";  // set by the scanner: this log's own key, for a log without session_meta

        public CodexFileState Clone()
        {
            return new CodexFileState { Model = Model, Totals = Totals == null ? null : (long[])Totals.Clone(), Lineage = Lineage, Fallback = Fallback };
        }
    }

    // Reads token use out of single log lines (spec: usage history §1). Never touches the disk, and never keeps any
    // text from a line except the model name and the reply key.
    public static class UsageLogs
    {
        public const string CodexKeyPrefix = "cx:";

        // A Claude Code transcript line: an assistant reply with usage, or null.
        public static UsageEntry ParseClaude(string line)
        {
            if (string.IsNullOrEmpty(line) || line.IndexOf("\"usage\"", StringComparison.Ordinal) < 0 || line.IndexOf("\"assistant\"", StringComparison.Ordinal) < 0) return null;
            object o = Json.TryParse(line);
            if (Json.Str(Json.Get(o, "type")) != "assistant") return null;
            object message = Json.Get(o, "message");
            string id = Json.Str(Json.Get(message, "id"));
            string model = Json.Str(Json.Get(message, "model"));
            object usage = Json.Get(message, "usage");
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(model) || model == "<synthetic>" || Json.Obj(usage) == null) return null;
            long at = Time(Json.Str(Json.Get(o, "timestamp")));
            if (at <= 0) return null;
            string request = Json.Str(Json.Get(o, "requestId"));
            return new UsageEntry
            {
                Key = string.IsNullOrEmpty(request) ? id : id + ":" + request,
                AtMs = at,
                Model = model,
                Tokens = N(usage, "input_tokens") + N(usage, "cache_creation_input_tokens") + N(usage, "output_tokens"),
                CacheRead = N(usage, "cache_read_input_tokens"),
            };
        }

        // A Codex rollout line. A session_meta line names the log's chat family; a turn_context line updates the file's
        // model; both return null. A token_count line with a new running total returns the turn's use. Everything else
        // returns null.
        public static UsageEntry ParseCodex(string line, CodexFileState state)
        {
            if (string.IsNullOrEmpty(line)) return null;
            bool context = line.IndexOf("\"turn_context\"", StringComparison.Ordinal) >= 0;
            bool meta = line.IndexOf("\"session_meta\"", StringComparison.Ordinal) >= 0;
            if (!context && !meta && line.IndexOf("\"token_count\"", StringComparison.Ordinal) < 0) return null;
            object o = Json.TryParse(line);
            string type = Json.Str(Json.Get(o, "type"));
            if (type == "session_meta")
            {
                // Only the log's own, first session_meta: a fork can carry a copy of its parent's further down. Codex's
                // session_id is the root chat's id, the same in every fork; older logs without it fall back to the fork's
                // parent, then to the chat's own id.
                if (state.Lineage != "") return null;
                object p = Json.Get(o, "payload");
                state.Lineage = Json.Str(Json.Get(p, "session_id")) ?? Json.Str(Json.Get(p, "forked_from_id")) ?? Json.Str(Json.Get(p, "id")) ?? "";
                return null;
            }
            if (type == "turn_context")
            {
                string model = Json.Str(Json.Get(o, "payload", "model"));
                if (!string.IsNullOrEmpty(model)) state.Model = model;
                return null;
            }
            if (type != "event_msg" || Json.Str(Json.Get(o, "payload", "type")) != "token_count") return null;
            object info = Json.Get(o, "payload", "info");
            object total = Json.Get(info, "total_token_usage"), last = Json.Get(info, "last_token_usage");
            if (Json.Obj(total) == null || Json.Obj(last) == null) return null;
            var totals = new[] { N(total, "input_tokens"), N(total, "cached_input_tokens"), N(total, "output_tokens") };
            if (state.Totals != null && totals[0] == state.Totals[0] && totals[1] == state.Totals[1] && totals[2] == state.Totals[2]) return null;
            state.Totals = totals;
            long reasoning = N(total, "reasoning_output_tokens"), all = N(total, "total_tokens");
            long at = Time(Json.Str(Json.Get(o, "timestamp")));
            if (at <= 0) return null;
            long cached = N(last, "cached_input_tokens");
            return new UsageEntry
            {
                // The chat family and the running total name the turn: a fork's copy of it, in another log, has the same
                // both. Separate chats never share a family, so equal-sized turns of theirs both count.
                Key = CodexKeyPrefix + (state.Lineage != "" ? state.Lineage : state.Fallback) + ":" + totals[0] + "." + totals[1] + "." + totals[2] + "." + reasoning + "." + all,
                AtMs = at,
                Model = state.Model == "" ? "codex" : state.Model,
                Tokens = Math.Max(0, N(last, "input_tokens") - cached) + N(last, "output_tokens"),
                CacheRead = cached,
            };
        }

        static long N(object obj, string key)
        {
            double? v = Json.Num(Json.Get(obj, key));
            return v.HasValue && v.Value > 0 ? (long)v.Value : 0;
        }

        // An ISO 8601 time as milliseconds since 1970 (UTC), or 0.
        public static long Time(string iso)
        {
            DateTime t;
            if (string.IsNullOrEmpty(iso) || !DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t)) return 0;
            return (long)(t - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        }
    }
}
