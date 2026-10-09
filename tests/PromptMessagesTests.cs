using System;
using System.Collections.Generic;
using System.Text;

namespace Capsule
{
    public static class PromptMessagesTests
    {
        public static void Run()
        {
            ARequestTakesWhatTheCardNeeds();
            ARequestGoesThereAndBack();
            RepliesGoThereAndBack();
            PassPrintsNothing();
            AllowAndDenyPrintTheDecision();
            AlwaysAllowAppliesClaudeCodesOwnSuggestions();
            AnswersGoBackWithTheOriginalQuestions();
            AnswersThatDontFitPrintNothing();
            ARequestKeepsItsRulesThroughThePipe();
            ARuleWithoutANameIsNamedByItsParts();
            ADenyMessageHasLimits();
            AnswersHaveLimits();
            AnswersMayComeAsAnyKindOfList();
            AlwaysAllowDoesNothingForAQuestion();
            ADenyAnswersAQuestionToo();
            EverySuggestionShapeIsNamedOrAlwaysAllowIsNotOffered();
            EachNameKnowsWhereItWouldBeKept();
            AlwaysAllowEchoesTheOriginalSuggestions();
            TheHookSendsOnlyWhatTheCardShows();
            IncompleteApprovalsPassEvenAfterAnAllowReply();
            CommandsAreCompleteAtTheBoundary();
            HiddenCommandsAndScopesStayWithClaude();
        }

        static void HiddenCommandsAndScopesStayWithClaude()
        {
            foreach (string command in new[] { "", "   ", "echo a\u202Eb", "echo a\u200Bb", "echo a\0b", "echo a\ud800" })
            {
                Dictionary<string, object> root = Input(Bash);
                root["tool_input"] = new Dictionary<string, object> { { "command", command } };
                TestRunner.Check(PromptRequest.FromHookInput(root, 1000) == null, "a blank or hidden command does not reach Capsule");
                TestRunner.Eq("", HookReply.Output(PromptReply.Allowing(), root), "a reply cannot approve an invisible command");
            }
            foreach (string scope in new[] { new string('x', 4001), "Bash(a\u202Eb)" })
            {
                Dictionary<string, object> root = Input(Bash);
                root["suggestions"] = new object[] { new Dictionary<string, object> { { "rule", scope } } };
                TestRunner.Eq(0, PromptRequest.FromHookInput(root, 1000).Rules.Count, "an unreviewable scope hides Always allow");
                TestRunner.Eq("", HookReply.Output(PromptReply.AlwaysAllowing(), root), "a stale reply cannot apply the hidden scope");
                TestRunner.Eq("allow", Json.Str(Json.Get(Decision(HookReply.Output(PromptReply.Allowing(), root)), "behavior")), "Allow once still works for its complete command");
            }
        }

        static PromptRequest WithSuggestions(string suggestions)
        {
            string hook = Bash.Substring(0, Bash.IndexOf("\"suggestions\"")) + "\"suggestions\":[" + suggestions + "]}";
            return PromptRequest.FromHookInput(Input(hook), 1000);
        }

        const string AddRules = "{\"type\":\"addRules\",\"rules\":[{\"toolName\":\"Bash\",\"ruleContent\":\"npm test:*\"}],\"behavior\":\"allow\",\"destination\":\"localSettings\"}";

        // Claude Code's suggestions are plain {rule} entries or typed permission updates; Always allow is offered only when
        // every one of them can be named on the card (F6).
        static void EverySuggestionShapeIsNamedOrAlwaysAllowIsNotOffered()
        {
            Func<string, string> named = delegate(string suggestions)
            {
                PromptRequest r = WithSuggestions(suggestions);
                return r == null ? null : string.Join("|", r.Rules);
            };
            TestRunner.Eq("Bash(npm test:*)", named(AddRules), "addRules: its rules");
            TestRunner.Eq("Bash(npm test:*)", named("{\"rule\":\"Bash(npm test:*)\"}"), "a plain rule: as before");
            TestRunner.Eq("Accept all edits", named("{\"type\":\"setMode\",\"mode\":\"acceptEdits\",\"destination\":\"session\"}"), "setMode acceptEdits is the one mode that is named");
            TestRunner.Eq("", named("{\"type\":\"setMode\",\"mode\":\"bypassPermissions\"}"), "setMode bypassPermissions can't be named: Always allow is hidden");
            TestRunner.Eq("", named("{\"type\":\"setMode\",\"mode\":\"plan\"}"), "setMode plan can't be named");
            TestRunner.Eq("", named("{\"type\":\"setMode\",\"mode\":\"default\"}"), "setMode default can't be named");
            TestRunner.Eq("", named(AddRules + ",{\"type\":\"setMode\",\"mode\":\"bypassPermissions\",\"destination\":\"session\"}"), "bypassPermissions after a rule hides Always allow, though the rule could be named");
            TestRunner.Eq("", named("{\"type\":\"setMode\",\"mode\":\"bypassPermissions\"}," + AddRules), "and before one");
            TestRunner.Eq("", named("{\"type\":\"setMode\",\"mode\":\"sneakyMode\"}"), "an unknown mode can't be named");
            TestRunner.Eq("", named("{\"type\":\"setMode\"}"), "nor a setMode with no mode");
            TestRunner.Eq("Access to C:\\work\\confetti\\docs|Access to D:\\data", named("{\"type\":\"addDirectories\",\"directories\":[\"C:\\\\work\\\\confetti\\\\docs\",\"D:\\\\data\"],\"destination\":\"session\"}"), "addDirectories: each directory");
            TestRunner.Eq("", named("{\"type\":\"addDirectories\",\"directories\":[]}"), "an addDirectories with none can't be named");
            TestRunner.Eq("", named("{\"type\":\"addDirectories\",\"directories\":[\"\"]}"), "nor one with a blank directory");
            TestRunner.Eq("Bash(npm test:*)|Accept all edits", named(AddRules + ",{\"type\":\"setMode\",\"mode\":\"acceptEdits\"}"), "a mixed list names each");
            TestRunner.Eq("", named(AddRules + ",{\"type\":\"setMode\",\"mode\":\"plan\"}"), "and a mode that can't be named hides the lot");
            TestRunner.Eq("", named(AddRules + ",{\"type\":\"replaceRules\",\"rules\":[{\"toolName\":\"Bash\"}],\"behavior\":\"allow\"}"), "an unknown type hides Always allow, though another could be named");
            foreach (string other in new[] { "replaceRules", "removeRules", "removeDirectories", "somethingNew" })
                TestRunner.Eq("", named("{\"type\":\"" + other + "\",\"rules\":[{\"toolName\":\"Bash\"}],\"directories\":[\"C:\\\\x\"]}"), other + " can't be named");
            TestRunner.Eq("", named("{\"type\":\"addRules\",\"rules\":[{\"ruleContent\":\"npm test:*\"}]}"), "a rule with no tool can't be named");
            TestRunner.Eq("", named("{\"type\":\"addRules\",\"rules\":[{\"toolName\":\"Bash\"},{\"ruleContent\":\"x\"}]}"), "nor can one whose other rule is missing its tool");
            TestRunner.Eq("", named(AddRules.Replace("\"allow\"", "\"deny\"")), "a rule that denies isn't a don't-ask-again");
            TestRunner.Eq("", named("{\"type\":7}"), "a type that isn't text can't be named");
            PromptRequest none = WithSuggestions(AddRules + ",{\"type\":\"removeRules\"}");
            TestRunner.Check(none != null && none.Rules.Count == 0, "so the request has no rules, and the card no Always allow");
        }

        // Where each part would be kept comes with its name (G2); a destination the card doesn't know is left out.
        static void EachNameKnowsWhereItWouldBeKept()
        {
            Func<string, string> kept = delegate(string suggestions)
            {
                PromptRequest r = WithSuggestions(suggestions);
                return r == null ? null : string.Join("|", r.Destinations);
            };
            Func<string, string> rule = delegate(string destination) { return AddRules.Replace("\"localSettings\"", destination); };
            TestRunner.Eq("session", kept(rule("\"session\"")), "session");
            TestRunner.Eq("project", kept(rule("\"projectSettings\"")), "projectSettings is the project's");
            TestRunner.Eq("project", kept(rule("\"localSettings\"")), "and so is localSettings");
            TestRunner.Eq("user", kept(rule("\"userSettings\"")), "userSettings is the user's");
            TestRunner.Eq("", kept(rule("\"somewhereNew\"")), "an unknown destination is left out");
            TestRunner.Eq("", kept("{\"type\":\"addRules\",\"rules\":[{\"toolName\":\"Bash\"}],\"behavior\":\"allow\"}"), "and a missing one");
            TestRunner.Eq("", kept("{\"rule\":\"Bash(npm test:*)\"}"), "a plain rule has none");
            TestRunner.Eq("session|session|user", kept("{\"type\":\"addDirectories\",\"directories\":[\"C:\\\\a\",\"C:\\\\b\"],\"destination\":\"session\"}," + rule("\"userSettings\"")), "each name has its own suggestion's");
            PromptRequest sent = WithSuggestions(rule("\"userSettings\""));
            PromptRequest got = PromptRequest.Parse(sent.ToJson());
            TestRunner.Eq("user", got == null ? null : string.Join("|", got.Destinations), "the destinations come out of the pipe as they went in");
            var odd = new PromptRequest { SessionId = "s-1", ToolName = "Bash" };
            odd.Rules.Add("x");
            Dictionary<string, object> wire = Json.Obj(Json.Parse(odd.ToJson()));
            wire["destinations"] = new List<object> { "<b>hi</b>", "user" };
            PromptRequest parsed = PromptRequest.Parse(Json.Write(wire));
            TestRunner.Eq("|user", parsed == null ? null : string.Join("|", parsed.Destinations), "a destination that isn't one of the card's words is dropped on the way in");
        }

        // Always allow sends back Claude Code's own suggestion objects, whatever their shape, unchanged.
        static void AlwaysAllowEchoesTheOriginalSuggestions()
        {
            string mode = "{\"type\":\"setMode\",\"mode\":\"acceptEdits\",\"destination\":\"session\"}";
            string dirs = "{\"type\":\"addDirectories\",\"directories\":[\"C:\\\\work\\\\confetti\\\\docs\"],\"destination\":\"session\"}";
            string hook = Bash.Substring(0, Bash.IndexOf("\"suggestions\"")) + "\"suggestions\":[" + AddRules + "," + mode + "," + dirs + ",{\"rule\":\"Read(*.md)\"}]}";
            Dictionary<string, object> input = Input(hook);
            Dictionary<string, object> d = Decision(HookReply.Output(PromptReply.AlwaysAllowing(), input));
            object[] sent = d == null ? null : Json.Arr(Json.Get(d, "updatedPermissions"));
            TestRunner.Check(sent != null && sent.Length == 4, "all four suggestions go back");
            if (sent == null || sent.Length != 4) return;
            TestRunner.Eq(Json.Write(Json.Parse(AddRules)), Json.Write(sent[0]), "an addRules, as it came");
            TestRunner.Eq(Json.Write(Json.Parse(mode)), Json.Write(sent[1]), "a setMode, as it came");
            TestRunner.Eq(Json.Write(Json.Parse(dirs)), Json.Write(sent[2]), "an addDirectories, as it came");
            TestRunner.Eq("Read(*.md)", Json.Str(sent[3]), "a plain rule as its rule");
            string odd = Bash.Substring(0, Bash.IndexOf("\"suggestions\"")) + "\"suggestions\":[" + AddRules + ",{\"type\":\"removeRules\"}]}";
            TestRunner.Eq("", HookReply.Output(PromptReply.AlwaysAllowing(), Input(odd)), "with a suggestion that couldn't be named, an always-allow reply prints nothing: Claude Code asks");
        }

        const string BigHook = "{\"session_id\":\"s-9\",\"hook_event_name\":\"PermissionRequest\",\"cwd\":\"C:\\\\work\\\\confetti\",\"tool_name\":\"TOOL\"," +
            "\"tool_input\":INPUT,\"tool_use_id\":\"toolu_9\"}";

        static PromptRequest Sent(string tool, string input)
        {
            return PromptRequest.FromHookInput(Input(BigHook.Replace("TOOL", tool).Replace("INPUT", input)), 1000);
        }

        // Capsule is sent what the card shows, not whole files (F8).
        static void TheHookSendsOnlyWhatTheCardShows()
        {
            string wire = Json.Write(PromptRequest.ForTheCard("Write", Input("{\"file_path\":\"C:\\\\work\\\\a.txt\",\"content\":\"SECRET-CONTENT\"}")));
            TestRunner.Check(!wire.Contains("SECRET-CONTENT") && wire.Contains("a.txt"), "a Write's content isn't sent, its file is");
            Dictionary<string, object> edit = PromptRequest.ForTheCard("Edit", Input("{\"file_path\":\"C:\\\\work\\\\a.txt\",\"old_string\":\"OLD-TEXT\",\"new_string\":\"NEW-TEXT\",\"replace_all\":true}"));
            string editWire = Json.Write(edit);
            TestRunner.Check(!editWire.Contains("OLD-TEXT") && !editWire.Contains("NEW-TEXT") && editWire.Contains("a.txt"), "an Edit's old and new text aren't sent");
            string multi = Json.Write(PromptRequest.ForTheCard("MultiEdit", Input("{\"file_path\":\"C:\\\\work\\\\a.txt\",\"edits\":[{\"old_string\":\"OLD-TEXT\",\"new_string\":\"NEW-TEXT\"}]}")));
            TestRunner.Check(!multi.Contains("OLD-TEXT") && !multi.Contains("NEW-TEXT") && !multi.Contains("edits"), "nor a MultiEdit's edits");
            string notebook = Json.Write(PromptRequest.ForTheCard("NotebookEdit", Input("{\"notebook_path\":\"C:\\\\work\\\\n.ipynb\",\"new_source\":\"NEW-SOURCE\",\"cell_id\":\"c1\"}")));
            TestRunner.Check(!notebook.Contains("NEW-SOURCE") && notebook.Contains("n.ipynb"), "nor a notebook's new source");
            TestRunner.Eq("C:\\work\\a.txt", Json.Str(Json.Get(Json.Parse(editWire), "file_path")), "and the projection still has the file to name");
            string plan = Json.Write(PromptRequest.ForTheCard("ExitPlanMode", Input("{\"plan\":\"SECRET-PLAN\"}")));
            TestRunner.Check(!plan.Contains("SECRET-PLAN"), "a plan isn't sent: the card doesn't show it");
            Dictionary<string, object> unknown = PromptRequest.ForTheCard("mcp__x__y", Input("{\"title\":\"" + new string('t', 5000) + "\",\"count\":3,\"nested\":{\"deep\":\"BULK\"}}"));
            string title = Json.Str(Json.Get(unknown, "title"));
            TestRunner.Check(title != null && title.Length == 2000, "another tool's text is cut to 2000 characters (" + (title == null ? -1 : title.Length) + ")");
            TestRunner.Check(!Json.Write(unknown).Contains("BULK"), "and what the card can't use isn't in the projection");
            PromptRequest bash = Sent("Bash", "{\"command\":\"npm test\",\"description\":\"Run the tests\",\"timeout\":5000}");
            TestRunner.Eq("npm test", Json.Str(Json.Get(bash.ToolInput, "command")), "a command is sent");
            PromptRequest question = Sent("AskUserQuestion", "{\"questions\":[{\"question\":\"Which?\",\"header\":\"H\",\"multiSelect\":false,\"options\":[{\"label\":\"A\"}]}],\"metadata\":{\"source\":\"sample\"}}");
            TestRunner.Check(QuestionFlow.Parse(question.ToolInput) != null, "and a question's own input, which the card needs whole");

            // The reply never takes the tool input from Capsule: an AskUserQuestion's updatedInput is Claude Code's own input.
            string questionHook = BigHook.Replace("TOOL", "AskUserQuestion").Replace("INPUT", "{\"questions\":[{\"question\":\"Which?\",\"header\":\"H\",\"multiSelect\":false,\"options\":[{\"label\":\"A\"}]}],\"metadata\":{\"source\":\"sample\"}}");
            var answers = new Dictionary<string, object>();
            answers["Which?"] = "A";
            Dictionary<string, object> d = Decision(HookReply.Output(PromptReply.Answering(answers), Input(questionHook)));
            Dictionary<string, object> updated = d == null ? null : Json.Obj(Json.Get(d, "updatedInput"));
            Dictionary<string, object> original = Json.Obj(Json.Get(Input(questionHook), "tool_input"));
            bool same = updated != null && original != null;
            if (same) foreach (KeyValuePair<string, object> pair in original) if (Json.Write(updated.ContainsKey(pair.Key) ? updated[pair.Key] : null) != Json.Write(pair.Value)) same = false;
            TestRunner.Check(same && Json.Str(Json.Get(updated, "metadata", "source")) == "sample", "the reply's updatedInput still holds Claude Code's own whole input");
            TestRunner.Eq("", HookReply.Output(PromptReply.Allowing(), Input(BigHook.Replace("TOOL", "Write").Replace("INPUT", "{\"file_path\":\"a\",\"content\":\"X\"}"))), "a filename-only Write preview cannot authorize Claude's full input");
        }

        static void IncompleteApprovalsPassEvenAfterAnAllowReply()
        {
            foreach (string tool in new[] { "Bash", "PowerShell", "Write", "Edit", "MultiEdit", "NotebookEdit", "ExitPlanMode", "mcp__sample__change" })
            {
                var input = new Dictionary<string, object> {
                    { "command", new string('x', 4001) }, { "file_path", "synthetic.txt" }, { "notebook_path", "synthetic.ipynb" },
                    { "nested", new Dictionary<string, object> { { "secret", "SYNTHETIC-CONTENT" } } }
                };
                Dictionary<string, object> root = Input(Bash);
                root["tool_name"] = tool;
                root["tool_input"] = input;
                TestRunner.Check(PromptRequest.FromHookInput(root, 1000) == null, tool + ": an incomplete approval stays with Claude before card truncation");
                TestRunner.Eq("", HookReply.Output(PromptReply.Allowing(), root), tool + ": a stale or fake Allow cannot authorize an incomplete approval");
                TestRunner.Eq("", HookReply.Output(PromptReply.AlwaysAllowing(), root), tool + ": a stale or fake AlwaysAllow cannot authorize an incomplete approval");
            }
        }

        static void CommandsAreCompleteAtTheBoundary()
        {
            foreach (string tool in new[] { "Bash", "PowerShell" })
            {
                Dictionary<string, object> root = Input(Bash);
                root["tool_name"] = tool;
                string command = new string('x', 4000);
                root["tool_input"] = new Dictionary<string, object> { { "command", command } };
                PromptRequest request = PromptRequest.FromHookInput(root, 1000);
                TestRunner.Eq(command, request == null ? null : Json.Str(Json.Get(request.ToolInput, "command")), tool + ": all 4000 original characters are shown");
                TestRunner.Eq("allow", Json.Str(Json.Get(Decision(HookReply.Output(PromptReply.Allowing(), root)), "behavior")), tool + ": a complete 4000-character command can be allowed");
                TestRunner.Eq("allow", Json.Str(Json.Get(Decision(HookReply.Output(PromptReply.AlwaysAllowing(), root)), "behavior")), tool + ": the complete boundary command still supports AlwaysAllow");
            }
        }

        static Dictionary<string, object> Answered(string colour, object sizes)
        {
            var answers = new Dictionary<string, object>();
            answers["Which colour?"] = colour;
            answers["Which sizes?"] = sizes;
            return answers;
        }

        static void ARequestKeepsItsRulesThroughThePipe()
        {
            var sent = new PromptRequest { SessionId = "s-1", ToolName = "Bash", Project = "confetti" };
            sent.Rules.Add("Bash(npm test:*)");
            sent.Rules.Add("Read(*.md)");
            PromptRequest got = PromptRequest.Parse(sent.ToJson());
            TestRunner.Eq("Bash(npm test:*)|Read(*.md)", got == null ? null : string.Join("|", got.Rules), "the rules come out of the pipe as they went in");
        }

        static void ARuleWithoutANameIsNamedByItsParts()
        {
            string[] tails =
            {
                "{\"description\":\"Yes, and don't ask again\"}",
                "{\"type\":\"addRules\",\"rules\":[{\"toolName\":\"Bash\",\"ruleContent\":\"npm test:*\"}]}",
                "{\"type\":\"addRules\",\"rules\":[{\"toolName\":\"Bash\",\"ruleContent\":\"npm test:*\"},{\"toolName\":\"Read\"}]}",
                "{\"type\":\"addRules\",\"rules\":[{\"toolName\":\"Edit\",\"ruleContent\":\"\"}]}",
                "{\"type\":\"addRules\",\"rules\":[]}",
                "{}"
            };
            string[] names = { "Yes, and don't ask again", "Bash(npm test:*)", "Bash(npm test:*), Read", "Edit", null, null };
            for (int i = 0; i < tails.Length; i++)
            {
                string hook = Bash.Substring(0, Bash.IndexOf("\"suggestions\"")) + "\"suggestions\":[" + tails[i] + "]}";
                PromptRequest r = PromptRequest.FromHookInput(Input(hook), 1000);
                TestRunner.Eq(names[i], r == null || r.Rules.Count != 1 ? null : r.Rules[0], "a suggestion is named, or not offered: " + names[i]);
            }
        }

        static string DenyMessage(string message)
        {
            Dictionary<string, object> d = Decision(HookReply.Output(PromptReply.Denying(message), Input(Bash)));
            return d == null ? null : Json.Str(Json.Get(d, "message"));
        }

        static void ADenyMessageHasLimits()
        {
            TestRunner.Eq(HookReply.DeniedMessage, DenyMessage(""), "a deny with no message says the usual one");
            TestRunner.Eq(HookReply.DeniedMessage, DenyMessage(null), "so does a missing one");
            string longest = new string('x', HookReply.MaxMessage);
            TestRunner.Eq(longest, DenyMessage(longest), "a message of exactly 200 characters goes as it is");
            TestRunner.Eq(HookReply.DeniedMessage, DenyMessage(longest + "x"), "one more and it is the usual one");
        }

        static void AnswersHaveLimits()
        {
            string longest = new string('a', HookReply.MaxAnswer);
            TestRunner.Check(HookReply.Output(PromptReply.Answering(Answered(longest, new object[] { "S" })), Input(Question)) != "", "an answer of exactly 2000 characters is taken");
            TestRunner.Eq("", HookReply.Output(PromptReply.Answering(Answered(longest + "a", new object[] { "S" })), Input(Question)), "one more character: nothing");
            TestRunner.Eq("", HookReply.Output(PromptReply.Answering(Answered("Red", new object[] { "S", new string('a', HookReply.MaxAnswer + 1) })), Input(Question)), "nor is a part of a list over 2000");
            TestRunner.Eq("", HookReply.Output(PromptReply.Answering(Answered("Red", new object[0])), Input(Question)), "an empty list of answers: nothing");
            // Each part is checked, not what they make together: three parts of 1500 are fine, joined they are 4504.
            var parts = new object[] { new string('a', 1500), new string('b', 1500), new string('c', 1500) };
            Dictionary<string, object> d = Decision(HookReply.Output(PromptReply.Answering(Answered("Red", parts)), Input(Question)));
            string joined = d == null ? null : Json.Str(Json.Get(d, "updatedInput", "answers", "Which sizes?"));
            TestRunner.Eq(4504, joined == null ? -1 : joined.Length, "parts are checked one by one, not as the joined text");
            // At most one part for each option, and one more for typed text: 3 options here.
            var four = new object[] { "S", "M", "L", "typed" };
            TestRunner.Check(HookReply.Output(PromptReply.Answering(Answered("Red", four)), Input(Question)) != "", "all three options and typed text: taken");
            TestRunner.Eq("", HookReply.Output(PromptReply.Answering(Answered("Red", new object[] { "S", "M", "L", "typed", "more" })), Input(Question)), "more parts than options and one typed: nothing");
            var five = new StringBuilder("{\"session_id\":\"s-5\",\"hook_event_name\":\"PermissionRequest\",\"tool_name\":\"AskUserQuestion\",\"tool_input\":{\"questions\":[");
            var all = new Dictionary<string, object>();
            for (int i = 1; i <= 5; i++)
            {
                if (i > 1) five.Append(',');
                five.Append("{\"question\":\"Q" + i + "?\",\"multiSelect\":false,\"options\":[{\"label\":\"A\"}]}");
                all["Q" + i + "?"] = "A";
            }
            five.Append("]}}");
            TestRunner.Eq("", HookReply.Output(PromptReply.Answering(all), Input(five.ToString())), "answers to five questions, more than Claude asks: nothing");
        }

        // The reply comes through the pipe as object[], but the cap is on what it means: any list of text is taken.
        static void AnswersMayComeAsAnyKindOfList()
        {
            foreach (object sizes in new object[] { new object[] { "S", "M" }, new List<object> { "S", "M" }, new string[] { "S", "M" }, new List<string> { "S", "M" } })
            {
                Dictionary<string, object> d = Decision(HookReply.Output(PromptReply.Answering(Answered("Red", sizes)), Input(Question)));
                TestRunner.Eq("S, M", d == null ? null : Json.Str(Json.Get(d, "updatedInput", "answers", "Which sizes?")), "a multi-select as " + sizes.GetType().Name + " goes out joined with a comma and a space");
            }
            Dictionary<string, object> one = Decision(HookReply.Output(PromptReply.Answering(Answered("Red", new object[] { "S" })), Input(Question)));
            TestRunner.Eq("S", one == null ? null : Json.Str(Json.Get(one, "updatedInput", "answers", "Which sizes?")), "one part is just that part");
            TestRunner.Eq("Red", one == null ? null : Json.Str(Json.Get(one, "updatedInput", "answers", "Which colour?")), "and a single choice stays as it is");
        }

        static void AlwaysAllowDoesNothingForAQuestion()
        {
            var reply = PromptReply.Answering(SampleAnswers());
            reply.Always = true;
            Dictionary<string, object> d = Decision(HookReply.Output(reply, Input(Question)));
            TestRunner.Check(d != null && Json.Str(Json.Get(d, "behavior")) == "allow" && Json.Obj(Json.Get(d, "updatedInput")) != null, "answers with always: the answers go through");
            TestRunner.Check(d != null && !d.ContainsKey("updatedPermissions"), "and no rule is applied");
            var other = PromptReply.Answering(SampleAnswers());
            other.Always = true;
            TestRunner.Eq("", HookReply.Output(other, Input(Bash)), "answers with always for another tool: nothing");
        }

        static void ADenyAnswersAQuestionToo()
        {
            Dictionary<string, object> d = Decision(HookReply.Output(PromptReply.Denying("Not now"), Input(Question)));
            TestRunner.Check(d != null && Json.Str(Json.Get(d, "behavior")) == "deny" && Json.Str(Json.Get(d, "message")) == "Not now", "a deny on a question goes back as a deny");
            TestRunner.Check(d != null && !d.ContainsKey("updatedInput"), "with no answers");
        }

        // Sample hook input only: never a real request.
        const string Bash = "{\"session_id\":\"s-1\",\"hook_event_name\":\"PermissionRequest\",\"cwd\":\"C:\\\\work\\\\confetti\",\"permission_mode\":\"default\"," +
            "\"tool_name\":\"Bash\",\"tool_input\":{\"command\":\"npm test\",\"description\":\"Run the tests\"},\"tool_use_id\":\"toolu_1\"," +
            "\"suggestions\":[{\"rule\":\"Bash(npm test:*)\",\"description\":\"Yes, and don't ask again for npm test\",\"applies\":\"project\"}]}";
        const string Question = "{\"session_id\":\"s-2\",\"hook_event_name\":\"PermissionRequest\",\"cwd\":\"C:\\\\work\\\\confetti\",\"tool_name\":\"AskUserQuestion\"," +
            "\"tool_input\":{\"questions\":[" +
            "{\"question\":\"Which colour?\",\"header\":\"Colour\",\"multiSelect\":false,\"options\":[{\"label\":\"Red\",\"description\":\"Warm\"},{\"label\":\"Blue\",\"description\":\"Cool\"}]}," +
            "{\"question\":\"Which sizes?\",\"header\":\"Sizes\",\"multiSelect\":true,\"options\":[{\"label\":\"S\",\"description\":\"\"},{\"label\":\"M\",\"description\":\"\"},{\"label\":\"L\",\"description\":\"\"}]}" +
            "],\"metadata\":{\"source\":\"sample\"}},\"tool_use_id\":\"toolu_2\"}";

        static Dictionary<string, object> Input(string json) { return Json.Obj(Json.Parse(json)); }

        static Dictionary<string, object> Decision(string output)
        {
            return Json.Obj(Json.Get(Json.TryParse(output), "hookSpecificOutput", "decision"));
        }

        static void ARequestTakesWhatTheCardNeeds()
        {
            PromptRequest r = PromptRequest.FromHookInput(Input(Bash), 1000);
            TestRunner.Check(r != null, "a PermissionRequest makes a request");
            if (r == null) return;
            TestRunner.Eq("s-1", r.SessionId, "its session");
            TestRunner.Eq("confetti", r.Project, "the project folder's name");
            TestRunner.Eq("C:\\work\\confetti", r.Cwd, "the folder itself, to shorten file paths");
            TestRunner.Eq("Bash", r.ToolName, "the tool");
            TestRunner.Eq("npm test", Json.Str(Json.Get(r.ToolInput, "command")), "its input");
            TestRunner.Eq("toolu_1", r.ToolUseId, "the tool call");
            TestRunner.Eq(1000L, r.At, "when the hook wrote the session's status");
            TestRunner.Eq(1, r.Rules.Count, "the one suggested rule");
            TestRunner.Eq("Bash(npm test:*)", r.Rules.Count > 0 ? r.Rules[0] : null, "named by its rule");
            string older = Bash.Replace("\"suggestions\"", "\"permission_suggestions\"");
            TestRunner.Eq(1, PromptRequest.FromHookInput(Input(older), 1000).Rules.Count, "suggestions are also found under permission_suggestions");
            TestRunner.Check(PromptRequest.FromHookInput(Input(Bash.Replace("PermissionRequest", "PreToolUse")), 1000) == null, "any other event makes none");
            TestRunner.Check(PromptRequest.FromHookInput(Input(Bash.Replace("s-1", "../x")), 1000) == null, "nor does a bad session id");
            TestRunner.Check(PromptRequest.FromHookInput(Input(Bash.Replace("\"tool_name\":\"Bash\",", "")), 1000) == null, "nor a request without a tool");
            TestRunner.Check(PromptRequest.FromHookInput(null, 1000) == null, "nor nothing");
        }

        static void ARequestGoesThereAndBack()
        {
            PromptRequest sent = PromptRequest.FromHookInput(Input(Question), 2000);
            PromptRequest got = PromptRequest.Parse(sent.ToJson());
            TestRunner.Check(got != null, "a request reads back");
            if (got == null) return;
            TestRunner.Eq(sent.SessionId + "|" + sent.Project + "|" + sent.Cwd + "|" + sent.ToolName + "|" + sent.ToolUseId + "|" + sent.At,
                got.SessionId + "|" + got.Project + "|" + got.Cwd + "|" + got.ToolName + "|" + got.ToolUseId + "|" + got.At, "with everything it had");
            TestRunner.Eq(Json.Write(sent.ToolInput), Json.Write(got.ToolInput), "its tool input too");
            TestRunner.Eq(0, got.Rules.Count, "and no rules when none were suggested");
            TestRunner.Check(PromptRequest.Parse("not json") == null, "garbage reads as no request");
            TestRunner.Check(PromptRequest.Parse("{\"session_id\":\"s\",\"tool_input\":{}}") == null, "so does one without a tool");
        }

        static void RepliesGoThereAndBack()
        {
            var answers = new Dictionary<string, object>();
            answers["Which colour?"] = "Red";
            foreach (PromptReply sent in new[] { PromptReply.Passing(), PromptReply.Allowing(), PromptReply.AlwaysAllowing(), PromptReply.Denying("No thanks"), PromptReply.Answering(answers) })
            {
                PromptReply got = PromptReply.Parse(sent.ToJson());
                string what = sent.Kind + (sent.Always ? " always" : "") + (sent.Answers != null ? " with answers" : "");
                TestRunner.Check(got != null && got.Kind == sent.Kind && got.Always == sent.Always && got.Message == sent.Message
                    && (got.Answers == null) == (sent.Answers == null), "a reply reads back: " + what);
            }
            TestRunner.Check(PromptReply.Parse(null) == null, "no reply is no reply");
            TestRunner.Check(PromptReply.Parse("{}") == null, "a reply must say what it is");
            TestRunner.Check(PromptReply.Parse("{\"reply\":\"maybe\"}") == null, "and be pass, allow or deny");
            TestRunner.Check(PromptReply.Parse("{\"reply\":\"allow\",\"always\":\"yes\"}") == null, "always is true or false");
            TestRunner.Check(PromptReply.Parse("{\"reply\":\"allow\",\"answers\":[\"Red\"]}") == null, "answers are by question");
        }

        static void PassPrintsNothing()
        {
            TestRunner.Eq("", HookReply.Output(PromptReply.Passing(), Input(Bash)), "pass prints nothing");
            TestRunner.Eq("", HookReply.Output(null, Input(Bash)), "nor does no reply");
            TestRunner.Eq("", HookReply.Output(PromptReply.Allowing(), null), "nor a reply to nothing");
        }

        static void AllowAndDenyPrintTheDecision()
        {
            string allow = HookReply.Output(PromptReply.Allowing(), Input(Bash));
            TestRunner.Eq("PermissionRequest", Json.Str(Json.Get(Json.TryParse(allow), "hookSpecificOutput", "hookEventName")), "the decision is for PermissionRequest");
            Dictionary<string, object> d = Decision(allow);
            TestRunner.Check(d != null && Json.Str(Json.Get(d, "behavior")) == "allow", "allow");
            TestRunner.Check(d != null && d.Count == 1, "and nothing else: no rules, no changed input");
            d = Decision(HookReply.Output(PromptReply.Denying("Denied from the capsule"), Input(Bash)));
            TestRunner.Check(d != null && Json.Str(Json.Get(d, "behavior")) == "deny", "deny");
            TestRunner.Eq("Denied from the capsule", d == null ? null : Json.Str(Json.Get(d, "message")), "with its message");
            d = Decision(HookReply.Output(PromptReply.Denying(new string('x', 500)), Input(Bash)));
            TestRunner.Eq(HookReply.DeniedMessage, d == null ? null : Json.Str(Json.Get(d, "message")), "a message that is too long becomes the usual one");
            d = Decision(HookReply.Output(PromptReply.Denying("line\nbreak"), Input(Bash)));
            TestRunner.Eq(HookReply.DeniedMessage, d == null ? null : Json.Str(Json.Get(d, "message")), "so does one with control characters");
            TestRunner.Eq("", HookReply.Output(PromptReply.Allowing(), Input(Question.Replace("AskUserQuestion", "ExitPlanMode"))), "a plan without a full preview is returned to Claude");
        }

        static void AlwaysAllowAppliesClaudeCodesOwnSuggestions()
        {
            Dictionary<string, object> d = Decision(HookReply.Output(PromptReply.AlwaysAllowing(), Input(Bash)));
            object[] rules = d == null ? null : Json.Arr(Json.Get(d, "updatedPermissions"));
            TestRunner.Check(d != null && Json.Str(Json.Get(d, "behavior")) == "allow", "always allow allows");
            TestRunner.Check(rules != null && rules.Length == 1 && Json.Str(rules[0]) == "Bash(npm test:*)", "and applies exactly the rule Claude Code suggested");
            string shaped = Bash.Replace("{\"rule\":\"Bash(npm test:*)\",\"description\":\"Yes, and don't ask again for npm test\",\"applies\":\"project\"}",
                "{\"type\":\"addRules\",\"rules\":[{\"toolName\":\"Bash\",\"ruleContent\":\"npm test:*\"}],\"behavior\":\"allow\",\"destination\":\"localSettings\"}");
            d = Decision(HookReply.Output(PromptReply.AlwaysAllowing(), Input(shaped)));
            rules = d == null ? null : Json.Arr(Json.Get(d, "updatedPermissions"));
            TestRunner.Check(rules != null && rules.Length == 1 && Json.Str(Json.Get(rules[0], "type")) == "addRules"
                && Json.Str(Json.Get(rules[0], "destination")) == "localSettings", "a suggestion without a rule string goes back as it came");
            TestRunner.Eq("", HookReply.Output(PromptReply.AlwaysAllowing(), Input(Question.Replace("AskUserQuestion", "ExitPlanMode"))), "always allow cannot approve a plan without a full preview");
        }

        static Dictionary<string, object> SampleAnswers()
        {
            var answers = new Dictionary<string, object>();
            answers["Which colour?"] = "Red";
            answers["Which sizes?"] = new object[] { "S", "Extra large" };
            return answers;
        }

        static void AnswersGoBackWithTheOriginalQuestions()
        {
            string output = HookReply.Output(PromptReply.Answering(SampleAnswers()), Input(Question));
            Dictionary<string, object> d = Decision(output);
            TestRunner.Check(d != null && Json.Str(Json.Get(d, "behavior")) == "allow", "answers allow the question");
            Dictionary<string, object> updated = d == null ? null : Json.Obj(Json.Get(d, "updatedInput"));
            TestRunner.Check(updated != null, "with an updated input");
            if (updated == null) return;
            TestRunner.Eq(Json.Write(Json.Get(Input(Question), "tool_input", "questions")), Json.Write(Json.Get(updated, "questions")), "that has Claude's questions as they came");
            TestRunner.Eq("sample", Json.Str(Json.Get(updated, "metadata", "source")), "and the rest of Claude's input");
            TestRunner.Eq("Red", Json.Str(Json.Get(updated, "answers", "Which colour?")), "a single choice is its label");
            TestRunner.Eq("S, Extra large", Json.Str(Json.Get(updated, "answers", "Which sizes?")), "a multi-select is its labels, typed text included, joined with a comma and a space");
        }

        static void AnswersThatDontFitPrintNothing()
        {
            var unknown = SampleAnswers();
            unknown["Which shape?"] = "Round";
            TestRunner.Eq("", HookReply.Output(PromptReply.Answering(unknown), Input(Question)), "an answer to a question Claude didn't ask: nothing");
            var missing = new Dictionary<string, object>();
            missing["Which colour?"] = "Red";
            TestRunner.Eq("", HookReply.Output(PromptReply.Answering(missing), Input(Question)), "a question left unanswered: nothing");
            var blank = SampleAnswers();
            blank["Which colour?"] = " ";
            TestRunner.Eq("", HookReply.Output(PromptReply.Answering(blank), Input(Question)), "a blank answer: nothing");
            var odd = SampleAnswers();
            odd["Which sizes?"] = new object[] { "S", 3 };
            TestRunner.Eq("", HookReply.Output(PromptReply.Answering(odd), Input(Question)), "an answer that isn't text: nothing");
            TestRunner.Eq("", HookReply.Output(PromptReply.Answering(SampleAnswers()), Input(Bash)), "answers for any other tool: nothing");
            TestRunner.Eq("", HookReply.Output(PromptReply.Allowing(), Input(Question)), "a question allowed without answers: nothing");
        }
    }
}
