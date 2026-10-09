using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Capsule
{
    public sealed class PromptOptionRow
    {
        public string Label = "";
        public string Description = "";
        public bool Chosen;
    }

    // Everything the card's request view shows (act-from-notch spec §3), as plain text and flags. Built here; drawn by
    // CardView.ShowPrompt. What it holds is shown on screen only.
    public sealed class PromptCardModel
    {
        public const string Approval = "approval", Plan = "plan", Question = "question", Asked = "asked";
        public const int MaxFull = PromptRequest.MaxCommand;
        public const string LineBreakMark = "⏎";   // a line break in a command, drawn so it can be seen
        public const int MaxWhat = 160;   // the card shows at most two lines of it; this keeps the text itself short too
        public const int MaxHeader = 40, MaxQuestion = 300, MaxLabel = 80, MaxDescription = 160;   // so a long question can't push the buttons off the card

        public int Id;                    // the HeldPrompt this is built for: every action on the card carries it
        public string Provider = "claude";
        public string AppName = "Claude";
        public string ProjectFull = "", Detail = "", DetailFull = "";
        public string Kind = Approval;
        public string Title = "";
        public string What = "";          // an approval's "Bash: npm test"
        public string Project = "";
        public string Position = "";      // "1 of 3", or "" when only one request is held
        public string Countdown = "";     // "Goes to Claude in 45 s"
        public string WhatFull = "";      // a command's whole text, for the summary's tooltip
        public bool WhatCut;              // the summary is shorter than the command
        public bool CanAlwaysAllow;
        public string AlwaysAllowTip = "";
        public const string AlwaysAllowText = "Always allow", AllowAllEditsText = "Allow all edits";
        public string AlwaysAllowLabel = AlwaysAllowText;   // the pill's own words: it says "Allow all edits" when that is what it does
        public string AlwaysAllowAlso = "";                  // "Also gives access to <directories>", shown under the pills, or ""
        // A question the card can show:
        public string Header = "";
        public string QuestionText = "";
        public string Step = "";          // "Question 1 of 2", or "" for a single question
        public bool MultiSelect;
        public List<PromptOptionRow> Options = new List<PromptOptionRow>();
        public bool OtherOpen;
        public string OtherText = "";
        public bool IsLast;
        public bool CanAdvance;

        // The request shown (the oldest held), how many are held, and how long until it goes to the app.
        public static PromptCardModel From(HeldPrompt held, int count, long msLeft)
        {
            PromptRequest r = held.Request;
            var m = new PromptCardModel { Id = held.Id, Provider = r.Provider, AppName = r.Provider == "codex" ? "Codex / Work" : "Claude" };
            m.Title = TitleFor(r.ToolName).Replace("Claude", m.AppName);
            m.Project = Safe(r.Project);
            m.ProjectFull = r.Cwd;
            m.Detail = Cap(r.Description, MaxWhat);
            m.DetailFull = r.Description;
            m.Position = PositionText(count);
            m.Countdown = CountdownText(msLeft, m.AppName);
            if (held.IsPlan) m.Kind = Plan;
            else if (held.IsQuestion) m.Kind = held.Flow != null ? Question : Asked;
            else
            {
                bool cut;
                m.What = Summary(r, out cut);
                m.WhatCut = cut;
                if (IsCommand(r)) m.WhatFull = Shorten(Text(r, "command"), MaxFull);
                m.CanAlwaysAllow = r.Rules.Count > 0;
                if (m.CanAlwaysAllow)
                {
                    bool edits = r.Rules.Contains(PromptRequest.AcceptEditsName);
                    if (edits) m.AlwaysAllowLabel = AllowAllEditsText;
                    m.AlwaysAllowTip = Typed(AlwaysAllowTipText(r, edits));
                    List<string> directories = DirectoryTexts(r);
                    if (directories.Count > 0) m.AlwaysAllowAlso = Shorten(Safe("Also gives access to " + string.Join(", ", directories)), MaxWhat);
                }
            }
            QuestionFlow flow = held.Flow;
            if (m.Kind == Question)
            {
                PromptQuestion q = flow.Current;
                m.Header = Cap(q.Header, MaxHeader);
                m.QuestionText = Cap(q.Text, MaxQuestion);
                m.Step = flow.Count > 1 ? "Question " + (flow.Index + 1) + " of " + flow.Count : "";
                m.MultiSelect = q.MultiSelect;
                for (int i = 0; i < q.Options.Count; i++)
                    m.Options.Add(new PromptOptionRow { Label = Cap(q.Options[i].Label, MaxLabel), Description = Cap(q.Options[i].Description, MaxDescription), Chosen = flow.IsChosen(i) });
                m.OtherOpen = flow.OtherOpen;
                m.OtherText = Typed(flow.OtherText);
                m.IsLast = flow.IsLast;
                m.CanAdvance = flow.CanAdvance;
            }
            return m;
        }

        public static string TitleFor(string tool)
        {
            switch (tool ?? "")
            {
                case ToolNames.Bash:
                case ToolNames.PowerShell:
                    return "Claude wants to run a command";
                case ToolNames.Edit:
                case ToolNames.MultiEdit:
                case ToolNames.Write:
                case ToolNames.NotebookEdit:
                    return "Claude wants to edit a file";
                case ToolNames.Read:
                    return "Claude wants to read a file";
                case ToolNames.WebFetch:
                    return "Claude wants to open a web page";
                case ToolNames.WebSearch:
                    return "Claude wants to search the web";
                case ToolNames.ExitPlanMode:
                    return "Claude's plan is ready";
                case ToolNames.AskUserQuestion:
                    return "Claude asked you a question";
                default:
                    return "Claude wants to use a tool";
            }
        }

        // "Bash: npm test", "Edit: src\app.ts": the tool, then the one part of its input that says what it will do (a file
        // inside the project from the project's folder), on one line, shortened to MaxWhat. Never a file's new content.
        public static string Summary(PromptRequest r)
        {
            bool cut;
            return Summary(r, out cut);
        }

        static bool IsCommand(PromptRequest r) { return r.ToolName == ToolNames.Bash || r.ToolName == ToolNames.PowerShell; }

        // The names of what Always allow would do: a directory in the project from the project's folder, as a file is.
        static List<string> RuleTexts(PromptRequest r)
        {
            var texts = new List<string>();
            foreach (string rule in r.Rules)
                texts.Add(rule.StartsWith(PromptRequest.AccessPrefix, StringComparison.Ordinal)
                    ? PromptRequest.AccessPrefix + Relative(Typed(rule.Substring(PromptRequest.AccessPrefix.Length)), r.Cwd) : rule);
            return texts;
        }

        // What the tooltip of the Always allow pill says: what it does, and where that is kept. One destination for all
        // of it is said once; otherwise each part says its own, and a part with none known says nothing.
        static string AlwaysAllowTipText(PromptRequest r, bool edits)
        {
            List<string> texts = RuleTexts(r);
            string first = DestinationText(r, 0);
            bool same = first != "";
            for (int i = 1; i < texts.Count && same; i++) if (DestinationText(r, i) != first) same = false;
            string body;
            if (same) body = string.Join(", ", texts) + " (" + first + ")";
            else
            {
                var parts = new List<string>();
                for (int i = 0; i < texts.Count; i++)
                {
                    string where = DestinationText(r, i);
                    parts.Add(where == "" ? texts[i] : texts[i] + " (" + where + ")");
                }
                body = string.Join(", ", parts);
            }
            return (edits ? "Applies: " : "Don't ask again: ") + body;
        }

        // "for this session", "saved for this project", "saved for you" for the rule at index, or "" when it is unknown.
        static string DestinationText(PromptRequest r, int index)
        {
            string d = index < r.Destinations.Count ? r.Destinations[index] : "";
            switch (d)
            {
                case PromptRequest.DestSession: return "for this session";
                case PromptRequest.DestProject: return "saved for this project";
                case PromptRequest.DestUser: return "saved for you";
                default: return "";
            }
        }

        // The directories Always allow would give access to, a directory in the project from the project's folder.
        static List<string> DirectoryTexts(PromptRequest r)
        {
            var texts = new List<string>();
            foreach (string rule in r.Rules)
                if (rule.StartsWith(PromptRequest.AccessPrefix, StringComparison.Ordinal))
                    texts.Add(Relative(Safe(rule.Substring(PromptRequest.AccessPrefix.Length)), r.Cwd));
            return texts;
        }

        // The line breaks of text, each line cleaned as Safe cleans it. Blank lines at the end go: they hide nothing.
        static List<string> Lines(string text)
        {
            var lines = new List<string>();
            if (string.IsNullOrEmpty(text)) return lines;
            string breaks = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace((char)0x2028, '\n').Replace((char)0x2029, '\n').Replace((char)0x85, '\n');
            foreach (string part in breaks.Split('\n')) lines.Add(Safe(part));
            while (lines.Count > 0 && lines[lines.Count - 1] == "") lines.RemoveAt(lines.Count - 1);
            return lines;
        }

        // A command on one line, each line break shown as ⏎ so that a command of several lines can't read as one.
        static string OneLine(string command)
        {
            return string.Join(" " + LineBreakMark + " ", Lines(command)).Replace("  ", " ").Trim();
        }

        // cut: the summary is shorter than what it summarises.
        static string Summary(PromptRequest r, out bool cut)
        {
            string detail;
            switch (r.ToolName)
            {
                case ToolNames.Bash:
                case ToolNames.PowerShell:
                    detail = OneLine(Text(r, "command"));
                    break;
                case ToolNames.Edit:
                case ToolNames.MultiEdit:
                case ToolNames.Write:
                case ToolNames.Read:
                    detail = Relative(Safe(Text(r, "file_path")), r.Cwd);
                    break;
                case ToolNames.NotebookEdit:
                    detail = Relative(Safe(Text(r, "notebook_path")), r.Cwd);
                    break;
                case ToolNames.WebFetch:
                    detail = Text(r, "url");
                    break;
                case ToolNames.WebSearch:
                    detail = Text(r, "query");
                    break;
                default:
                    detail = "";
                    foreach (KeyValuePair<string, object> pair in r.ToolInput)
                    {
                        string s = pair.Value as string;
                        if (!string.IsNullOrEmpty(s)) { detail = s; break; }
                    }
                    break;
            }
            string tool = Safe(r.ToolName), line = Safe(detail);
            string full = line == "" ? tool : tool + ": " + line;
            string shown = Shorten(full, MaxWhat);
            cut = shown != full;
            return shown;
        }

        static string Text(PromptRequest r, string key) { return Json.Str(Json.Get(r.ToolInput, key)) ?? ""; }

        // A file inside the project, from the project's folder; anything else in full. A path that climbs out ("." or ".."
        // in what follows the folder) is always shown in full, so a trimmed path can't hide where a file really is, and
        // a project that is a drive or the filesystem root has nothing to trim.
        static string Relative(string path, string cwd)
        {
            if (string.IsNullOrEmpty(cwd) || string.IsNullOrEmpty(path)) return path;
            string root = cwd.Replace('/', '\\').TrimEnd('\\');
            if (root.Length == 0 || (root.Length == 2 && root[1] == ':')) return path;
            string p = path.Replace('/', '\\');
            if (p.Length <= root.Length + 1 || !p.StartsWith(root, StringComparison.OrdinalIgnoreCase) || p[root.Length] != '\\') return path;
            string rest = p.Substring(root.Length + 1);
            foreach (string segment in rest.Split('\\'))
                if (segment.IndexOf('.') >= 0 && segment.Trim(' ', '.').Length == 0) return path;   // ".", "..", ".. " (Windows ignores the space)
            return rest;
        }

        // Line breaks, tabs and runs of spaces as one space, and nothing a reader can't see: Unicode format characters
        // (bidi controls, zero-width characters, the BOM), tag characters and a lone surrogate are dropped. Everything the
        // card shows that came from Claude or the project goes through this, so it can't be made to read as something else.
        public static string Safe(string s) { return Clean(s, true); }

        // What was typed in the Other… box: the same cleaning, but it keeps its own spaces. A line break, a tab or another
        // control character (a multi-line paste) becomes one space, so the words around it stay apart.
        public static string Typed(string s) { return Clean(s, false); }

        static string Clean(string s, bool oneLine)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder();
            bool space = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                string piece;
                if (char.IsHighSurrogate(c))
                {
                    if (i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1])) continue;   // a lone one
                    int code = char.ConvertToUtf32(c, s[i + 1]);
                    piece = s.Substring(i, 2);
                    i++;
                    if ((code >= 0xE0000 && code <= 0xE007F) || char.GetUnicodeCategory(piece, 0) == UnicodeCategory.Format) continue;
                }
                else
                {
                    if (char.IsLowSurrogate(c)) continue;                                // a lone one
                    if (char.GetUnicodeCategory(c) == UnicodeCategory.Format) continue;
                    if (char.IsWhiteSpace(c) || char.IsControl(c))
                    {
                        if (!oneLine)
                        {
                            bool lineBreak = char.IsControl(c) || c == '\u2028' || c == '\u2029';
                            if (!lineBreak) sb.Append(c);
                            else if (sb.Length == 0 || sb[sb.Length - 1] != ' ') sb.Append(' ');   // a run of them is one space
                            continue;
                        }
                        space = true;
                        continue;
                    }
                    piece = c.ToString();
                }
                if (space && sb.Length > 0) sb.Append(' ');
                space = false;
                sb.Append(piece);
            }
            return sb.ToString();
        }

        // Safe, and at most max characters.
        static string Cap(string s, int max) { return Shorten(Safe(s), max); }

        // At most max characters, ending in … when cut; never between the halves of a surrogate pair.
        public static string Shorten(string text, int max)
        {
            if (max <= 0) return "";
            if (text == null || text.Length <= max) return text ?? "";
            int cut = max - 1;
            if (cut > 0 && char.IsHighSurrogate(text[cut - 1])) cut--;
            return text.Substring(0, cut) + "…";
        }

        public static string CountdownText(long msLeft, string appName = "Claude")
        {
            long seconds = Math.Max(0, (msLeft + 999) / 1000);
            return "Goes to " + appName + " in " + seconds.ToString(CultureInfo.InvariantCulture) + " s";
        }

        // The card always shows the oldest request, so it is always the first of the count.
        public static string PositionText(int count)
        {
            return count <= 1 ? "" : "1 of " + count;
        }
    }
}
