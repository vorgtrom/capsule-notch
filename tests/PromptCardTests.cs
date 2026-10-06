using System;
using System.Collections.Generic;

namespace Capsule
{
    public static class PromptCardTests
    {
        public static void Run()
        {
            SaysWhatClaudeWantsToDo();
            SummarisesTheToolCall();
            ShortensLongSummaries();
            CountsDownAndCountsTheQueue();
            DescribesAnApproval();
            DescribesAPlan();
            DescribesAQuestion();
            AQuestionItCantShowIsOnlyNamed();
            TheCardShowsNoHiddenCharacters();
            ShortenNeverSplitsAnEmoji();
            ALongQuestionCantPushTheButtonsOff();
            RelativePathsNeverHideWhereAFileIs();
            TheAlwaysAllowTipIsShortAndNamesDirectories();
            AlwaysAllowSaysWhatItDoes();
            ALineBreakInACommandIsShownAsOne();
            TheWholeCommandIsKeptForTheTooltip();
        }

        static void TheAlwaysAllowTipIsShortAndNamesDirectories()
        {
            PromptRequest r = Request("Bash", "{\"command\":\"npm test\"}");
            r.Rules.Add(new string('x', 1000));
            PromptCardModel m = PromptCardModel.From(new HeldPrompt { Request = r }, 1, 45000);
            TestRunner.Check(m.AlwaysAllowTip.Length <= 300 && m.AlwaysAllowTip.EndsWith("…", StringComparison.Ordinal), "a long tip is cut to 300 characters (" + m.AlwaysAllowTip.Length + ")");
            r.Rules.Clear();
            r.Rules.Add("Accept all edits");
            r.Rules.Add("Access to C:\\work\\confetti\\docs");
            r.Rules.Add("Access to D:\\data");
            m = PromptCardModel.From(new HeldPrompt { Request = r }, 1, 45000);
            TestRunner.Eq("Applies: Accept all edits, Access to docs, Access to D:\\data", m.AlwaysAllowTip, "a mode and directories are named, a directory in the project from the project's folder");
        }

        // Always allow says what it does when that is more than "don't ask again for this rule" (G1, G2).
        static void AlwaysAllowSaysWhatItDoes()
        {
            Func<string[], string[], PromptCardModel> card = delegate(string[] rules, string[] where)
            {
                PromptRequest r = Request("Bash", "{\"command\":\"npm test\"}");
                r.Rules.AddRange(rules);
                if (where != null) r.Destinations.AddRange(where);
                return PromptCardModel.From(new HeldPrompt { Request = r }, 1, 45000);
            };
            PromptCardModel m = card(new[] { "Bash(npm test:*)" }, new[] { "project" });
            TestRunner.Eq("Always allow", m.AlwaysAllowLabel, "a rule alone: the pill says Always allow");
            TestRunner.Eq("Don't ask again: Bash(npm test:*) (saved for this project)", m.AlwaysAllowTip, "and its tooltip names where the rule is kept");
            TestRunner.Eq("", m.AlwaysAllowAlso, "with no line under the pills");

            m = card(new[] { "Accept all edits" }, new[] { "session" });
            TestRunner.Eq("Allow all edits", m.AlwaysAllowLabel, "accepting edits: the pill's own words say so");
            TestRunner.Eq("Applies: Accept all edits (for this session)", m.AlwaysAllowTip, "and the tooltip says for how long");
            TestRunner.Check(m.CanAlwaysAllow, "it is offered");

            m = card(new[] { "Bash(npm test:*)", "Accept all edits" }, new[] { "user", "user" });
            TestRunner.Eq("Allow all edits", m.AlwaysAllowLabel, "a rule and accepting edits: the pill names the bigger thing");
            TestRunner.Eq("Applies: Bash(npm test:*), Accept all edits (saved for you)", m.AlwaysAllowTip, "one destination for all of it is said once");

            m = card(new[] { "Bash(npm test:*)", "Accept all edits" }, new[] { "project", "session" });
            TestRunner.Eq("Applies: Bash(npm test:*) (saved for this project), Accept all edits (for this session)", m.AlwaysAllowTip, "different ones are each named");

            m = card(new[] { "Bash(npm test:*)", "Read(*.md)" }, new[] { "", "user" });
            TestRunner.Eq("Don't ask again: Bash(npm test:*), Read(*.md) (saved for you)", m.AlwaysAllowTip, "a part with no known destination says none");
            m = card(new[] { "Bash(npm test:*)" }, null);
            TestRunner.Eq("Don't ask again: Bash(npm test:*)", m.AlwaysAllowTip, "none known: nothing is said about where");
            m = card(new[] { "Bash(npm test:*)" }, new[] { "<b>" });
            TestRunner.Eq("Don't ask again: Bash(npm test:*)", m.AlwaysAllowTip, "a destination that isn't one of the card's words is left out");

            m = card(new[] { "Bash(npm test:*)", "Access to C:\\work\\confetti\\docs", "Access to D:\\data" }, new[] { "session", "session", "session" });
            TestRunner.Eq("Always allow", m.AlwaysAllowLabel, "directories with a rule: still Always allow");
            TestRunner.Eq("Also gives access to docs, D:\\data", m.AlwaysAllowAlso, "but the card says on its face which directories, shortened as elsewhere");
            m = card(new[] { "Access to C:\\x\u202Ey" }, new[] { "session" });
            TestRunner.Eq("Also gives access to C:\\xy", m.AlwaysAllowAlso, "a directory is cleaned of what a reader can't see");
            m = card(new[] { "Access to " + new string('d', 500) }, new[] { "session" });
            TestRunner.Check(m.AlwaysAllowAlso.Length == PromptCardModel.MaxWhat && m.AlwaysAllowAlso.EndsWith("…", StringComparison.Ordinal), "and is capped (" + m.AlwaysAllowAlso.Length + ")");
            TestRunner.Check(m.AlwaysAllowTip.Length <= PromptCardModel.MaxTip, "as its tooltip is");

            m = card(new string[0], null);
            TestRunner.Check(!m.CanAlwaysAllow && m.AlwaysAllowAlso == "", "no rules: no Always allow, no line");
        }

        // A line break is a visible mark, not a space, so a command that runs several things can't read as one (F7).
        static void ALineBreakInACommandIsShownAsOne()
        {
            TestRunner.Eq("Bash: echo a ⏎ rm b", PromptCardModel.Summary(Request("Bash", "{\"command\":\"echo a\\nrm b\"}")), "a two-line command shows ⏎ between the lines");
            TestRunner.Eq("Bash: echo a ⏎ rm b", PromptCardModel.Summary(Request("Bash", "{\"command\":\"echo a\\r\\n   rm b\"}")), "a Windows line break too, and the indent is one space");
            TestRunner.Eq("PowerShell: a ⏎ ⏎ b", PromptCardModel.Summary(Request("PowerShell", "{\"command\":\"a\\n\\nb\"}")), "each break of a blank line counts");
            TestRunner.Eq("Bash: npm test", PromptCardModel.Summary(Request("Bash", "{\"command\":\"npm test\\n\"}")), "a break at the very end hides nothing, and isn't shown");
        }

        static void TheWholeCommandIsKeptForTheTooltip()
        {
            PromptCardModel m = PromptCardModel.From(new HeldPrompt { Request = Request("Bash", "{\"command\":\"echo a\\nrm b\"}") }, 1, 60000);
            TestRunner.Check(!m.WhatCut && m.WhatFull == "echo a\nrm b", "a command that fits: not cut, and the whole command with its line breaks is there");
            string text = string.Join("\\n", new[] { new string('a', 100), new string('b', 100) });
            m = PromptCardModel.From(new HeldPrompt { Request = Request("Bash", "{\"command\":\"" + text + "\"}") }, 1, 60000);
            TestRunner.Check(m.WhatCut && m.What.EndsWith("…", StringComparison.Ordinal) && m.WhatFull == new string('a', 100) + "\n" + new string('b', 100), "a cut one says so, and keeps the whole of it");
            m = PromptCardModel.From(new HeldPrompt { Request = Request("Bash", "{\"command\":\"" + new string('x', 6000) + "\"}") }, 1, 60000);
            TestRunner.Check(m.WhatFull.Length == PromptCardModel.MaxFull && m.WhatFull.EndsWith("…", StringComparison.Ordinal), "the whole is itself capped at 4000 (" + m.WhatFull.Length + ")");
            m = PromptCardModel.From(new HeldPrompt { Request = Request("Bash", "{\"command\":\"a\\u202Eb\"}") }, 1, 60000);
            TestRunner.Eq("ab", m.WhatFull, "and it is cleaned of what a reader can't see, as the summary is");
            m = PromptCardModel.From(new HeldPrompt { Request = Request("Read", "{\"file_path\":\"C:\\\\work\\\\confetti\\\\a.txt\"}") }, 1, 60000);
            TestRunner.Eq("", m.WhatFull, "only a command has one");
        }

        // Sample requests only.
        static PromptRequest Request(string tool, string input)
        {
            var r = new PromptRequest { SessionId = "a", ToolName = tool, Project = "confetti", Cwd = @"C:\work\confetti", At = 1000 };
            r.ToolInput = Json.Obj(Json.Parse(input));
            return r;
        }

        static void SaysWhatClaudeWantsToDo()
        {
            TestRunner.Eq("Claude wants to run a command", PromptCardModel.TitleFor("Bash"), "Bash");
            TestRunner.Eq("Claude wants to run a command", PromptCardModel.TitleFor("PowerShell"), "PowerShell");
            foreach (string tool in new[] { "Edit", "MultiEdit", "Write", "NotebookEdit" })
                TestRunner.Eq("Claude wants to edit a file", PromptCardModel.TitleFor(tool), tool);
            TestRunner.Eq("Claude wants to read a file", PromptCardModel.TitleFor("Read"), "Read");
            TestRunner.Eq("Claude wants to open a web page", PromptCardModel.TitleFor("WebFetch"), "WebFetch");
            TestRunner.Eq("Claude wants to search the web", PromptCardModel.TitleFor("WebSearch"), "WebSearch");
            TestRunner.Eq("Claude wants to use a tool", PromptCardModel.TitleFor("mcp__github__create_pull_request"), "any other tool");
            TestRunner.Eq("Claude's plan is ready", PromptCardModel.TitleFor("ExitPlanMode"), "a plan");
            TestRunner.Eq("Claude asked you a question", PromptCardModel.TitleFor("AskUserQuestion"), "a question");
        }

        static void SummarisesTheToolCall()
        {
            TestRunner.Eq("Bash: npm test", PromptCardModel.Summary(Request("Bash", "{\"command\":\"npm test\",\"description\":\"Run the tests\"}")), "a command");
            TestRunner.Eq("PowerShell: Get-ChildItem -Force", PromptCardModel.Summary(Request("PowerShell", "{\"command\":\"Get-ChildItem -Force\"}")), "in PowerShell");
            TestRunner.Eq(@"Edit: src\app.ts", PromptCardModel.Summary(Request("Edit", "{\"file_path\":\"C:\\\\work\\\\confetti\\\\src\\\\app.ts\",\"old_string\":\"a\",\"new_string\":\"b\"}")), "a file in the project, from the project's folder");
            TestRunner.Eq(@"Write: C:\other\notes.md", PromptCardModel.Summary(Request("Write", "{\"file_path\":\"C:\\\\other\\\\notes.md\",\"content\":\"secret\"}")), "a file elsewhere, in full; never the content");
            TestRunner.Eq("NotebookEdit: analysis.ipynb", PromptCardModel.Summary(Request("NotebookEdit", "{\"notebook_path\":\"C:\\\\work\\\\confetti\\\\analysis.ipynb\"}")), "a notebook");
            TestRunner.Eq("WebFetch: https://example.com/docs", PromptCardModel.Summary(Request("WebFetch", "{\"url\":\"https://example.com/docs\",\"prompt\":\"Summarise\"}")), "a web page");
            TestRunner.Eq("mcp__github__create_issue: Fix the build", PromptCardModel.Summary(Request("mcp__github__create_issue", "{\"count\":3,\"title\":\"Fix the build\",\"body\":\"Long text\"}")), "another tool: its first text");
            TestRunner.Eq("SomeTool", PromptCardModel.Summary(Request("SomeTool", "{\"count\":3}")), "a tool without text: its name");
            TestRunner.Eq("Bash: npm test ⏎ && npm run build", PromptCardModel.Summary(Request("Bash", "{\"command\":\"npm test\\n    && npm run build\"}")), "a command over several lines, on one, each break shown");
        }

        static void ShortensLongSummaries()
        {
            string longest = PromptCardModel.Summary(Request("Bash", "{\"command\":\"" + new string('x', 500) + "\"}"));
            TestRunner.Eq(PromptCardModel.MaxWhat, longest.Length, "a long command is cut");
            TestRunner.Check(longest.StartsWith("Bash: xxx", StringComparison.Ordinal) && longest.EndsWith("…", StringComparison.Ordinal), "and ends in …");
            TestRunner.Eq("short", PromptCardModel.Shorten("short", 10), "a short one is left as it is");
            TestRunner.Eq("abcd…", PromptCardModel.Shorten("abcdefghij", 5), "shortened to its limit, … included");
        }

        static void CountsDownAndCountsTheQueue()
        {
            TestRunner.Eq("Goes to Claude in 60 s", PromptCardModel.CountdownText(60000), "60 s");
            TestRunner.Eq("Goes to Claude in 45 s", PromptCardModel.CountdownText(44001), "a part second counts as a second");
            TestRunner.Eq("Goes to Claude in 0 s", PromptCardModel.CountdownText(-5), "never below 0");
            TestRunner.Eq("", PromptCardModel.PositionText(1), "one request: no count");
            TestRunner.Eq("1 of 3", PromptCardModel.PositionText(3), "several: 1 of 3");
        }

        static void DescribesAnApproval()
        {
            PromptRequest r = Request("Bash", "{\"command\":\"npm test\"}");
            r.Rules.Add("Bash(npm test:*)");
            PromptCardModel m = PromptCardModel.From(new HeldPrompt { Request = r }, 2, 45000);
            TestRunner.Eq(PromptCardModel.Approval, m.Kind, "an approval");
            TestRunner.Eq("Claude wants to run a command|Bash: npm test|confetti", m.Title + "|" + m.What + "|" + m.Project, "its title, what it wants, and the project");
            TestRunner.Check(m.CanAlwaysAllow && m.AlwaysAllowTip.Contains("Bash(npm test:*)"), "Always allow, naming the rule it applies");
            TestRunner.Eq("1 of 2|Goes to Claude in 45 s", m.Position + "|" + m.Countdown, "the queue and the countdown");
            r.Rules.Clear();
            TestRunner.Check(!PromptCardModel.From(new HeldPrompt { Request = r }, 1, 45000).CanAlwaysAllow, "no Always allow without a suggested rule");
        }

        static void DescribesAPlan()
        {
            PromptCardModel m = PromptCardModel.From(new HeldPrompt { Request = Request("ExitPlanMode", "{\"plan\":\"# A long plan\"}") }, 1, 60000);
            TestRunner.Eq(PromptCardModel.Plan, m.Kind, "a plan");
            TestRunner.Eq("Claude's plan is ready|", m.Title + "|" + m.What, "the plan itself isn't shown");
            TestRunner.Check(!m.CanAlwaysAllow, "and has no Always allow");
        }

        const string Questions = "{\"questions\":[" +
            "{\"question\":\"Which colour?\",\"header\":\"Colour\",\"multiSelect\":false,\"options\":[{\"label\":\"Red\",\"description\":\"Warm\"},{\"label\":\"Blue\",\"description\":\"Cool\"}]}," +
            "{\"question\":\"Which sizes?\",\"header\":\"Sizes\",\"multiSelect\":true,\"options\":[{\"label\":\"S\"},{\"label\":\"M\"}]}]}";

        static void DescribesAQuestion()
        {
            PromptRequest r = Request("AskUserQuestion", Questions);
            var flow = new QuestionFlow(QuestionFlow.Parse(r.ToolInput));
            flow.Choose(1);
            PromptCardModel m = PromptCardModel.From(new HeldPrompt { Request = r, Flow = flow }, 1, 60000);
            TestRunner.Eq(PromptCardModel.Question, m.Kind, "a question");
            TestRunner.Eq("Colour|Which colour?|Question 1 of 2", m.Header + "|" + m.QuestionText + "|" + m.Step, "its header, its text, and which of Claude's questions it is");
            TestRunner.Check(m.Options.Count == 2 && m.Options[0].Label == "Red" && m.Options[0].Description == "Warm" && !m.Options[0].Chosen && m.Options[1].Chosen, "its options, with the one chosen");
            TestRunner.Check(!m.MultiSelect && !m.IsLast && m.CanAdvance && !m.OtherOpen, "single choice, not the last, answered");
            flow.Next();
            flow.ToggleOther();
            flow.SetOther("XL");
            m = PromptCardModel.From(new HeldPrompt { Request = r, Flow = flow }, 1, 60000);
            TestRunner.Check(m.MultiSelect && m.IsLast && m.Step == "Question 2 of 2" && m.OtherOpen && m.OtherText == "XL", "the second: a multi-select, the last, with the typed text");
        }

        static void AQuestionItCantShowIsOnlyNamed()
        {
            PromptCardModel m = PromptCardModel.From(new HeldPrompt { Request = Request("AskUserQuestion", "{\"questions\":\"odd\"}") }, 1, 60000);
            TestRunner.Eq(PromptCardModel.Asked, m.Kind, "a question the card can't show");
            TestRunner.Eq("Claude asked you a question", m.Title, "says only that there is one");
            TestRunner.Eq(0, m.Options.Count, "with nothing to choose");
        }

        // Characters that are invisible or that reorder what is shown. Built from code points, not typed.
        static readonly string Rlo = ((char)0x202E).ToString(), Zwsp = ((char)0x200B).ToString(), Isolate = ((char)0x2066).ToString(),
            Bom = ((char)0xFEFF).ToString(), Lone = ((char)0xD800).ToString(), Tag = char.ConvertFromUtf32(0xE0041), Emoji = char.ConvertFromUtf32(0x1F600);

        static void TheCardShowsNoHiddenCharacters()
        {
            TestRunner.Eq("a b", PromptCardModel.Safe("a \r\n\t b"), "whitespace and controls become one space");
            TestRunner.Eq("abc", PromptCardModel.Safe("a" + Rlo + "b" + Isolate + "c"), "bidi controls are dropped");
            TestRunner.Eq("abc", PromptCardModel.Safe("a" + Zwsp + "b" + Bom + "c"), "so are zero-width characters and the BOM");
            TestRunner.Eq("ab", PromptCardModel.Safe("a" + Tag + "b"), "so are tag characters, a surrogate pair");
            TestRunner.Eq("ab", PromptCardModel.Safe("a" + Lone + "b"), "and a lone surrogate");
            TestRunner.Eq("a" + Emoji + "b", PromptCardModel.Safe("a" + Emoji + "b"), "but an emoji stays");
            TestRunner.Eq("a b", PromptCardModel.Safe("a " + Zwsp + " b"), "dropped ones don't leave a gap, or add one");
            TestRunner.Eq("", PromptCardModel.Safe(null), "nothing is nothing");
            TestRunner.Eq("a" + Emoji + "b", PromptCardModel.Typed("a" + Rlo + Emoji + Lone + "b"), "typed text is cleaned the same way");
            TestRunner.Eq("a  b", PromptCardModel.Typed("a  b"), "but keeps its own spaces");
            TestRunner.Eq("line one line two", PromptCardModel.Typed("line one\r\nline two"), "a pasted line break is a space, so the words stay apart");
            TestRunner.Eq("a b", PromptCardModel.Typed("a\n\n\nb"), "a run of line breaks is one space");
            TestRunner.Eq("a b c d", PromptCardModel.Typed("a\tb" + (char)0x2028 + "c" + (char)0x2029 + "d"), "so are a tab and the Unicode line and paragraph separators");
            TestRunner.Eq("a b", PromptCardModel.Typed("a \nb"), "next to a space of its own, it adds none");
            TestRunner.Eq("a b", PromptCardModel.Typed("a" + (char)0x07 + "b"), "and any other control character");

            // Every piece of text on the card goes through it.
            var r = new PromptRequest { SessionId = "a", ToolName = "Ba" + Zwsp + "sh", Project = "con" + Rlo + "fetti", Cwd = @"C:\work\confetti", At = 1000 };
            r.ToolInput = Json.Obj(Json.Parse("{\"command\":\"npm" + "\\u202E" + " test\"}"));
            r.Rules.Add("Bash(npm" + Rlo + " test:*)");
            PromptCardModel m = PromptCardModel.From(new HeldPrompt { Request = r }, 1, 60000);
            TestRunner.Eq("Bash: npm test", m.What, "the summary, tool name included");
            TestRunner.Eq("confetti", m.Project, "the project");
            TestRunner.Eq("Don't ask again: Bash(npm test:*)", m.AlwaysAllowTip, "the always-allow tip");

            string input = "{\"questions\":[{\"question\":\"Which" + "\\u202E" + " colour?\",\"header\":\"Col" + "\\u200B" + "our\",\"multiSelect\":false," +
                "\"options\":[{\"label\":\"Re" + "\\u202E" + "d\",\"description\":\"Wa" + "\\u200B" + "rm\"},{\"label\":\"Blue\"}]}]}";
            PromptRequest ask = Request("AskUserQuestion", input);
            var flow = new QuestionFlow(QuestionFlow.Parse(ask.ToolInput));
            flow.ToggleOther();
            flow.SetOther("X" + Rlo + "L");
            m = PromptCardModel.From(new HeldPrompt { Request = ask, Flow = flow }, 1, 60000);
            TestRunner.Eq("Colour|Which colour?|Red|Warm|XL", m.Header + "|" + m.QuestionText + "|" + m.Options[0].Label + "|" + m.Options[0].Description + "|" + m.OtherText,
                "a question's header, text, option labels and descriptions, and the typed text");
        }

        static void ShortenNeverSplitsAnEmoji()
        {
            string text = new string('x', 8) + Emoji + "tail";   // the emoji is characters 8 and 9
            TestRunner.Eq(new string('x', 8) + "…", PromptCardModel.Shorten(text, 10), "a cut that would split an emoji comes one earlier");
            TestRunner.Eq(new string('x', 8) + Emoji + "…", PromptCardModel.Shorten(text, 11), "one that doesn't, doesn't");
            string cut = PromptCardModel.Shorten(text, 10);
            bool lone = false;
            for (int i = 0; i < cut.Length; i++)
                if (char.IsHighSurrogate(cut[i]) && (i + 1 >= cut.Length || !char.IsLowSurrogate(cut[i + 1]))) lone = true;
            TestRunner.Check(!lone, "no half emoji is left behind");
            TestRunner.Eq("", PromptCardModel.Shorten("abc", 0), "no room: nothing");
            TestRunner.Eq("", PromptCardModel.Shorten("abc", -3), "less than none: nothing");
            TestRunner.Eq("…", PromptCardModel.Shorten("abc", 1), "room for one: just the …");
        }

        static void ALongQuestionCantPushTheButtonsOff()
        {
            string input = "{\"questions\":[{\"question\":\"" + new string('q', 900) + "\",\"header\":\"" + new string('h', 90) + "\",\"multiSelect\":false," +
                "\"options\":[{\"label\":\"" + new string('l', 300) + "\",\"description\":\"" + new string('d', 700) + "\"},{\"label\":\"Short\"}]}]}";
            PromptRequest ask = Request("AskUserQuestion", input);
            var flow = new QuestionFlow(QuestionFlow.Parse(ask.ToolInput));
            PromptCardModel m = PromptCardModel.From(new HeldPrompt { Request = ask, Flow = flow }, 1, 60000);
            TestRunner.Eq(PromptCardModel.MaxQuestion, m.QuestionText.Length, "a long question is cut");
            TestRunner.Eq(PromptCardModel.MaxHeader, m.Header.Length, "a long header too");
            TestRunner.Eq(PromptCardModel.MaxLabel, m.Options[0].Label.Length, "a long option label too");
            TestRunner.Eq(PromptCardModel.MaxDescription, m.Options[0].Description.Length, "and its description");
            TestRunner.Check(m.QuestionText.EndsWith("…", StringComparison.Ordinal) && m.Options[0].Label.EndsWith("…", StringComparison.Ordinal), "each ending in …");
            TestRunner.Eq("Short", m.Options[1].Label, "a short one is left alone");
        }

        static string Summed(string tool, string key, string path, string cwd)
        {
            var r = new PromptRequest { SessionId = "a", ToolName = tool, Project = "confetti", Cwd = cwd, At = 1000 };
            r.ToolInput[key] = path;
            return PromptCardModel.Summary(r);
        }

        static void RelativePathsNeverHideWhereAFileIs()
        {
            const string cwd = @"C:\work\confetti";
            TestRunner.Eq(@"Edit: src\app.ts", Summed("Edit", "file_path", @"C:\work\confetti\src\app.ts", cwd), "a file inside the project is shown from its folder");
            TestRunner.Eq(@"Edit: src\app.ts", Summed("Edit", "file_path", @"c:\WORK\Confetti\src\app.ts", cwd), "whatever the case");
            TestRunner.Eq(@"Edit: src\app.ts", Summed("Edit", "file_path", @"C:\work\confetti\src\app.ts", cwd + @"\"), "with a trailing slash on the folder");
            TestRunner.Eq(@"Edit: src\app.ts", Summed("Edit", "file_path", "C:/work/confetti/src/app.ts", cwd), "with forward slashes");
            TestRunner.Eq(@"Edit: C:\work\confetti\..\..\Users\x\.ssh\config", Summed("Edit", "file_path", @"C:\work\confetti\..\..\Users\x\.ssh\config", cwd), "a path that climbs out of the project is shown in full");
            TestRunner.Eq(@"Edit: C:\work\confetti\src\..\..\x", Summed("Edit", "file_path", @"C:\work\confetti\src\..\..\x", cwd), "wherever in the path it climbs");
            TestRunner.Eq("Edit: C:/work/confetti/../x", Summed("Edit", "file_path", "C:/work/confetti/../x", cwd), "with forward slashes too");
            TestRunner.Eq(@"Edit: C:\work\confetti\.\src\x", Summed("Edit", "file_path", @"C:\work\confetti\.\src\x", cwd), "a . segment as well");
            TestRunner.Eq(@"Edit: C:\work\confetti\.. \x", Summed("Edit", "file_path", @"C:\work\confetti\.. \x", cwd), "and .. with a trailing space, which Windows ignores");
            TestRunner.Eq(@"Edit: ..\..\Users\x\.ssh\config", Summed("Edit", "file_path", @"..\..\Users\x\.ssh\config", cwd), "a relative path stays as it is");
            TestRunner.Eq(@"Edit: C:\work\confetti-evil\x.ts", Summed("Edit", "file_path", @"C:\work\confetti-evil\x.ts", cwd), "a folder that only starts with the project's name is another folder");
            TestRunner.Eq(@"Edit: C:\Users\x\notes.md", Summed("Edit", "file_path", @"C:\Users\x\notes.md", @"C:\"), "with a drive as the project, the full path");
            TestRunner.Eq(@"Edit: C:\Users\x\notes.md", Summed("Edit", "file_path", @"C:\Users\x\notes.md", "C:"), "however the drive is written");
            TestRunner.Eq(@"Edit: /etc/hosts", Summed("Edit", "file_path", "/etc/hosts", "/"), "and with the filesystem root");
            TestRunner.Eq(@"Edit: x.ts", Summed("Edit", "file_path", @"C:\work\confetti\x.ts", cwd), "a file straight in the project");
            TestRunner.Eq(@"Edit: C:\work\confetti", Summed("Edit", "file_path", @"C:\work\confetti", cwd), "the project's folder itself is shown in full");
        }
    }
}
