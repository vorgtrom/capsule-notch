using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Capsule
{
    // Richer session rows, sounds, and plan and file-edit review.
    public static class SessionRowsTests
    {
        public static void Run()
        {
            NamesWhatClaudeDid();
            ShowsThePromptAndTheLastAction();
        }

        static Dictionary<string, object> In(string json) { return Json.Obj(Json.Parse(json)); }

        static void NamesWhatClaudeDid()
        {
            TestRunner.Eq("Edited middleware.ts", HookEvents.Activity("Edit", In("{\"file_path\":\"C:\\\\work\\\\src\\\\middleware.ts\"}")), "an edit, by file name");
            TestRunner.Eq("Wrote notes.md", HookEvents.Activity("Write", In("{\"file_path\":\"/home/me/notes.md\"}")), "a new file");
            TestRunner.Eq("Read app.ts", HookEvents.Activity("Read", In("{\"file_path\":\"app.ts\"}")), "a read");
            TestRunner.Eq("Run the tests", HookEvents.Activity("Bash", In("{\"command\":\"npm test -- --secret\",\"description\":\"Run the tests\"}")), "a command, by its description, never its text");
            TestRunner.Eq("Ran a command", HookEvents.Activity("Bash", In("{\"command\":\"rm -rf build\"}")), "a command without one");
            TestRunner.Eq("Searched the code", HookEvents.Activity("Grep", In("{\"pattern\":\"token\"}")), "a search");
            TestRunner.Eq("Used a connector", HookEvents.Activity("mcp__notion__search", In("{}")), "a connector's tool");
            TestRunner.Eq("Read a web page", HookEvents.Activity("WebFetch", In("{\"url\":\"https://example.com/private?q=1\"}")), "a web page, without its address");
        }

        static void ShowsThePromptAndTheLastAction()
        {
            var s = new SessionStatus { State = States.Working, Prompt = "fix the auth bug in middleware", Activity = "Edited middleware.ts" };
            TestRunner.Eq("\u201Cfix the auth bug in middleware\u201D · Edited middleware.ts", SessionRow.DetailFor(s), "the prompt quoted, then what it did");
            s.State = States.Waiting;
            TestRunner.Eq("\u201Cfix the auth bug in middleware\u201D", SessionRow.DetailFor(s), "while it waits on you, the status says why; the prompt stays");
            TestRunner.Eq("", SessionRow.DetailFor(new SessionStatus()), "nothing known: no second line");
            var longPrompt = new SessionStatus { Prompt = new string('w', 200) };
            TestRunner.Check(SessionRow.DetailFor(longPrompt).Length < 70, "a long prompt is cut");
            int unused;
            var rows = SessionRow.ListFor(new List<SessionStatus> { new SessionStatus { SessionId = "a", State = States.Working, Project = "web", Prompt = "go", Since = 1, At = 1 } }, 2, out unused);
            TestRunner.Eq("\u201Cgo\u201D", rows[0].Detail, "the row carries it");
        }
    }

    public static class SoundsTests
    {
        public static void Run()
        {
            MakesAWav();
            CuesOnTheRightChanges();
        }

        static void MakesAWav()
        {
            byte[] wav = Sounds.Wav(new[] { 659.25, 987.77 });
            TestRunner.Check(wav.Length > 44 && wav[0] == 'R' && wav[1] == 'I' && wav[8] == 'W' && wav[12] == 'f', "a WAV file in memory");
            TestRunner.Eq(wav.Length - 8, BitConverter.ToInt32(wav, 4), "its RIFF size matches");
            TestRunner.Eq(wav.Length - 44, BitConverter.ToInt32(wav, 40), "and its data size");
        }

        static List<SessionStatus> S(params string[] idState)
        {
            var list = new List<SessionStatus>();
            for (int i = 0; i < idState.Length; i += 2) list.Add(new SessionStatus { SessionId = idState[i], State = idState[i + 1] });
            return list;
        }

        static void CuesOnTheRightChanges()
        {
            var cues = new SessionCues();
            TestRunner.Eq(Cue.None, cues.Next(S("a", States.Waiting)), "the first look plays nothing");
            TestRunner.Eq(Cue.None, cues.Next(S("a", States.Waiting)), "still waiting: nothing again");
            TestRunner.Eq(Cue.None, cues.Next(S("a", States.Working)), "back to work: nothing");
            TestRunner.Eq(Cue.Done, cues.Next(S("a", States.Done)), "work finished: done");
            TestRunner.Eq(Cue.None, cues.Next(S("a", States.Done, "b", States.Idle)), "a new idle session: nothing");
            TestRunner.Eq(Cue.Attention, cues.Next(S("a", States.Done, "b", States.Waiting)), "a session that starts waiting on you");
            TestRunner.Eq(Cue.Attention, cues.Next(S("a", States.Working, "b", States.Working, "c", States.Waiting)), "a brand-new session already waiting");
            TestRunner.Eq(Cue.Attention, cues.Next(S("a", States.Done, "b", States.Working, "c", States.Working, "d", States.Waiting)), "needing you beats finishing");
            TestRunner.Eq(Cue.None, cues.Next(S("a", States.Done)), "sessions that ended: nothing");
        }
    }

    public static class ReviewCardTests
    {
        public static void Run()
        {
            DiffsLines();
            KeepsCodeVisibleAndHonest();
            BuildsEditAndFileCards();
            BuildsAPlanCard();
            SendsPlanFeedback();
            DrawsTheReview();
        }

        static PromptRequest R(string tool, string input)
        {
            return new PromptRequest { ToolName = tool, SessionId = "s", Cwd = "C:\\work\\web", Project = "web", ToolInput = Json.Obj(Json.Parse(input)) };
        }

        static string Show(List<DiffLine> d) { return string.Join("|", d.Select(l => l.Kind + l.Text)); }

        static void DiffsLines()
        {
            TestRunner.Eq(" a|-b|+B|+B2| c", Show(PromptCardModel.DiffOf("a\nb\nc", "a\nB\nB2\nc")), "shared lines stay, the rest is taken out and put in");
            TestRunner.Eq("-old", Show(PromptCardModel.DiffOf("old", "")), "a removal");
            TestRunner.Eq("+new", Show(PromptCardModel.DiffOf("", "new")), "an insertion");
            TestRunner.Eq(" x| x", Show(PromptCardModel.DiffOf("x\nx", "x\nx")), "no change: nothing taken out or put in");
            TestRunner.Eq(" a|-b|+c", Show(PromptCardModel.DiffOf("a\r\nb", "a\nc")), "Windows line ends are lines too");
        }

        static void KeepsCodeVisibleAndHonest()
        {
            List<string> lines = PromptCardModel.CodeLines("\tif (x) {\n    run(\"a\u202Eb\");\u200B\n}\n\n");
            TestRunner.Eq("    if (x) {", lines[0], "a tab is four spaces; indentation kept");
            TestRunner.Eq("    run(\"ab\");", lines[1], "bidi controls and zero-width characters are dropped");
            TestRunner.Eq(3, lines.Count, "blank lines at the end go");
        }

        static void BuildsEditAndFileCards()
        {
            PromptCardModel edit = PromptCardModel.From(new HeldPrompt { Request = R("Edit", "{\"file_path\":\"C:\\\\work\\\\web\\\\src\\\\auth.ts\",\"old_string\":\"jwt.verify(token);\",\"new_string\":\"if (!token) throw new Error();\\nreturn jwt.verify(token);\"}") }, 1, 45000);
            TestRunner.Eq(PromptCardModel.Approval, edit.Kind, "an edit is an approval");
            TestRunner.Eq("Claude wants to edit a file", edit.Title, "its title");
            TestRunner.Eq("Edit: src\\auth.ts", edit.What, "the file, from the project");
            TestRunner.Eq("-jwt.verify(token);|+if (!token) throw new Error();|+return jwt.verify(token);", Show(edit.Diff), "and the change, line by line");
            TestRunner.Eq("", edit.FileNote, "no note for a plain edit");
            PromptCardModel all = PromptCardModel.From(new HeldPrompt { Request = R("Edit", "{\"file_path\":\"a.ts\",\"old_string\":\"a\",\"new_string\":\"b\",\"replace_all\":true}") }, 1, 45000);
            TestRunner.Eq("Every match is replaced", all.FileNote, "replace_all says so");
            PromptCardModel multi = PromptCardModel.From(new HeldPrompt { Request = R("MultiEdit", "{\"file_path\":\"a.ts\",\"edits\":[{\"old_string\":\"a\",\"new_string\":\"b\"},{\"old_string\":\"c\",\"new_string\":\"d\"}]}") }, 1, 45000);
            TestRunner.Eq("-a|+b|~\u22EF|-c|+d", Show(multi.Diff), "a MultiEdit's edits, one after the other");
            PromptCardModel fresh = PromptCardModel.From(new HeldPrompt { Request = R("Write", "{\"file_path\":\"a.ts\",\"content\":\"one\\ntwo\",\"exists\":false}") }, 1, 45000);
            TestRunner.Check(Show(fresh.Diff) == "+one|+two" && fresh.FileNote == "New file", "a new file: all its lines, said to be new");
            PromptCardModel over = PromptCardModel.From(new HeldPrompt { Request = R("Write", "{\"file_path\":\"a.ts\",\"content\":\"x\",\"exists\":true}") }, 1, 45000);
            TestRunner.Eq("Replaces the whole file", over.FileNote, "a file Write replaces says so");
            PromptCardModel bash = PromptCardModel.From(new HeldPrompt { Request = R("Bash", "{\"command\":\"npm test\"}") }, 1, 45000);
            TestRunner.Check(bash.Diff == null && bash.FileNote == "", "a command has no diff");
        }

        static void BuildsAPlanCard()
        {
            var held = new HeldPrompt { Request = R("ExitPlanMode", "{\"plan\":\"# Fix auth\\n- Check the token\\n- Add a test\"}") };
            PromptCardModel m = PromptCardModel.From(held, 1, 45000);
            TestRunner.Eq(PromptCardModel.Plan, m.Kind, "a plan");
            TestRunner.Eq("Claude's plan is ready", m.Title, "its title");
            TestRunner.Check(m.PlanLines != null && m.PlanLines.Count == 3 && m.PlanLines[0] == "# Fix auth", "its lines, as written");
            TestRunner.Check(!m.OtherOpen, "the feedback box starts closed");
            held.FeedbackOpen = true;
            held.Feedback = "also cover the refresh token";
            m = PromptCardModel.From(held, 1, 45000);
            TestRunner.Check(m.OtherOpen && m.OtherText == "also cover the refresh token", "open, it shows what was typed");
        }

        static void SendsPlanFeedback()
        {
            PromptReply reply = null;
            var broker = new PromptBroker(() => false);
            HeldPrompt held = broker.Arrive(R("ExitPlanMode", "{\"plan\":\"# P\"}"), delegate(PromptReply r) { reply = r; }, 1000);
            broker.Act(PromptAction.Of(PromptAction.Other, held.Id), 1100);
            TestRunner.Check(held.FeedbackOpen, "Give feedback… opens the box");
            broker.Act(PromptAction.Typed("Also  cover\nthe refresh token", held.Id), 1200);
            broker.Act(PromptAction.Of(PromptAction.Deny, held.Id), 1300);
            TestRunner.Check(reply != null && reply.Kind == PromptReply.Deny, "Keep planning denies");
            TestRunner.Eq("Keep planning. Feedback from the capsule: Also  cover the refresh token", reply == null ? null : reply.Message, "with the feedback for Claude, on one line");
            string output = HookReply.Output(reply, Json.Obj(Json.Parse("{\"hook_event_name\":\"PermissionRequest\",\"session_id\":\"s\",\"tool_name\":\"ExitPlanMode\",\"tool_input\":{\"plan\":\"# P\"}}")));
            TestRunner.Check(output.Contains("Feedback from the capsule"), "and the hook passes it on");

            PromptReply plain = null;
            HeldPrompt quiet = broker.Arrive(R("ExitPlanMode", "{\"plan\":\"# P\"}"), delegate(PromptReply r) { plain = r; }, 2000);
            broker.Act(PromptAction.Of(PromptAction.Deny, quiet.Id), 2100);
            TestRunner.Eq(HookReply.KeepPlanningMessage, plain == null ? null : plain.Message, "without feedback, the usual message");
            var huge = new HeldPrompt { Request = R("ExitPlanMode", "{}"), Feedback = new string('f', 5000) };
            TestRunner.Check(huge.KeepPlanningMessage().Length <= HookReply.MaxMessage, "long feedback is cut to fit");
        }

        static TextBlock FindText(DependencyObject root, Func<string, bool> match)
        {
            var t = root as TextBlock;
            if (t != null && match(new System.Windows.Documents.TextRange(t.ContentStart, t.ContentEnd).Text)) return t;   // its runs too, not only Text
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                TextBlock found = FindText(VisualTreeHelper.GetChild(root, i), match);
                if (found != null) return found;
            }
            return null;
        }

        static void Lay(FrameworkElement e)
        {
            e.Measure(new Size(CardView.CardWidth, double.PositiveInfinity));
            e.Arrange(new Rect(e.DesiredSize));
            e.UpdateLayout();
        }

        static void DrawsTheReview()
        {
            var view = new PromptView();
            view.Show(PromptCardModel.From(new HeldPrompt { Id = 1, Request = R("Edit", "{\"file_path\":\"a.ts\",\"old_string\":\"old line\",\"new_string\":\"new line\"}") }, 1, 45000));
            Lay(view);
            TestRunner.Check(FindText(view, s => s == "- old line") != null && FindText(view, s => s == "+ new line") != null, "the card draws the change");
            TestRunner.Check(FindText(view, s => s == "Allow") != null, "with Allow");
            var plan = new PromptView();
            plan.Show(PromptCardModel.From(new HeldPrompt { Id = 2, Request = R("ExitPlanMode", "{\"plan\":\"# Fix auth\\n- Check the **token**\"}") }, 1, 45000));
            Lay(plan);
            TestRunner.Check(FindText(plan, s => s == "Fix auth") != null, "the plan's heading, without its #");
            TestRunner.Check(FindText(plan, s => s.StartsWith("\u2022 Check the ", StringComparison.Ordinal)) != null, "a bullet");
            TestRunner.Check(FindText(plan, s => s == "Approve plan") != null && FindText(plan, s => s == "Give feedback\u2026") != null, "Approve plan, and Give feedback…");
        }
    }
}
