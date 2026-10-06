using System;
using System.Collections.Generic;

namespace Capsule
{
    public static class PromptBrokerTests
    {
        const long Now = 1790690940000;

        public static void Run()
        {
            StepsAsideWhenClaudeIsInFront();
            HoldsRequestsOldestFirst();
            TheCountdownRestartsOnInteraction();
            ARequestGoesToClaudeWhenItsCountdownEnds();
            EverythingGoesToClaudeWhenItComesToTheFront();
            ARequestGoesWhenItsSessionMovesOn();
            ANotificationOnlyMeansMovedOnWhenTheSessionIsIdle();
            AHookThatWentAwayIsDropped();
            QuitReleasesEverything();
            TheButtonsReply();
            QuestionsAreAnsweredOnTheCard();
            AQuestionTheCardCantShowCanOnlyGoToClaude();
            AFailingReplyDoesntStopTheBroker();
            ActionsNameTheirRequest();
            ARequestLivesAtMostEightySeconds();
            ARequestThatWaitedGetsTimeToBeRead();
            TheCapRunsFromWhenTheHookSentIt();
            ARequestWithoutAReplyPathGoesBack();
            TheLogSaysWhyButNeverWhat();
            SendOnAnUnansweredLastQuestionDoesNothing();
            QuestionActionsOnAnotherRequestDoNothing();
            ATickThatCantTellWhatIsInFrontStepsAside();
            ActingOnNothingDoesNothing();
            ADeadlineReleaseChangesTheCardOnce();
        }

        static void SendOnAnUnansweredLastQuestionDoesNothing()
        {
            PromptBroker broker = Broker(() => false);
            var replies = new Replies();
            HeldPrompt q = broker.Arrive(Request("q", "AskUserQuestion", Now), replies.For("q"), Now);
            broker.Act(PromptAction.Chose(0, q.Id), Now + 1000);
            broker.Act(PromptAction.Of(PromptAction.Next, q.Id), Now + 2000);
            TestRunner.Check(q.Flow.IsLast && !q.Flow.CanAdvance, "on the last question, still unanswered");
            int before = Changes;
            broker.Act(PromptAction.Of(PromptAction.Send, q.Id), Now + 3000);
            TestRunner.Check(replies.All == "" && broker.Count == 1, "Send does not answer with a question missing");
            TestRunner.Eq(before, Changes, "and the card isn't redrawn for it");
        }

        // Option, Other…, typing, Next and Send are for a question; on any other request they do nothing at all.
        static void QuestionActionsOnAnotherRequestDoNothing()
        {
            PromptBroker broker = Broker(() => false);
            var replies = new Replies();
            HeldPrompt b = broker.Arrive(Request("a", "Bash", Now), replies.For("a"), Now);
            int before = Changes;
            broker.Act(PromptAction.Chose(0, b.Id), Now + 1000);
            broker.Act(PromptAction.Of(PromptAction.Other, b.Id), Now + 1000);
            broker.Act(PromptAction.Typed("x", b.Id), Now + 1000);
            broker.Act(PromptAction.Of(PromptAction.Next, b.Id), Now + 1000);
            broker.Act(PromptAction.Of(PromptAction.Send, b.Id), Now + 1000);
            TestRunner.Check(replies.All == "" && broker.Count == 1 && b.Flow == null, "choosing, Other…, typing, Next and Send on a command: nothing answered");
            TestRunner.Eq(before, Changes, "and the card isn't redrawn");
            TestRunner.Eq(PromptBroker.HoldMs, broker.MsLeft(Now + 1000), "though each still counts as using the card");
        }

        static void ATickThatCantTellWhatIsInFrontStepsAside()
        {
            bool fails = false;
            PromptBroker broker = Broker(delegate { if (fails) throw new InvalidOperationException(); return false; });
            var replies = new Replies();
            broker.Arrive(Request("a", "Bash", Now), replies.For("a"), Now);
            broker.Tick(Now + 1000);
            TestRunner.Eq(1, broker.Count, "held while it can be told what is in front");
            fails = true;
            broker.Tick(Now + 2000);
            TestRunner.Check(broker.Count == 0 && replies.All == "a:pass", "when the seam throws, a tick steps aside: everything goes to the app");
        }

        static void ActingOnNothingDoesNothing()
        {
            PromptBroker broker = Broker(() => false);
            broker.Act(PromptAction.Of(PromptAction.Allow, 1), Now);
            broker.Act(PromptAction.Of(PromptAction.Touch, 0), Now);
            broker.Act(null, Now);
            broker.Tick(Now + 1000);
            TestRunner.Check(broker.Count == 0 && broker.Current == null && Changes == 0, "with nothing held, actions and ticks change nothing");
            TestRunner.Eq(0L, broker.MsLeft(Now), "and no time is left on nothing");
        }

        static void ADeadlineReleaseChangesTheCardOnce()
        {
            PromptBroker broker = Broker(() => false);
            var replies = new Replies();
            broker.Arrive(Request("a", "Bash", Now), replies.For("a"), Now);
            broker.Arrive(Request("b", "Bash", Now), replies.For("b"), Now);
            int before = Changes;
            broker.Tick(Now + PromptBroker.HoldMs);
            TestRunner.Eq("a:pass, b:pass", replies.All, "both countdowns ended together");
            TestRunner.Eq(before + 1, Changes, "the card changes once for the lot");
        }

        // For the hands-on check: why a request left the card. Never what Claude wanted to do.
        static void TheLogSaysWhyButNeverWhat()
        {
            int before = (Files.ReadText(Paths.LogFile) ?? "").Length;   // earlier tests wrote the same lines: only what this one adds counts
            var broker = new PromptBroker(() => false);
            broker.Arrive(Request("a", "Bash", Now), new Replies().For("a"), Now);
            broker.SessionsChanged(new List<SessionStatus> { Status("a", "PostToolUse", Now + 5) });
            broker.Arrive(Request("b", "Bash", Now), new Replies().For("b"), Now);
            broker.Act(PromptAction.Of(PromptAction.Deny, broker.Current.Id), Now);
            string whole = Files.ReadText(Paths.LogFile) ?? "";
            string log = whole.Length > before ? whole.Substring(before) : "";
            TestRunner.Check(log.Contains("prompts: 1 passed to the app (its session moved on: PostToolUse)"), "the log says why a request went to the app");
            TestRunner.Check(log.Contains("prompts: answered from the capsule (deny)"), "and how one was answered");
            TestRunner.Check(!log.Contains("npm test"), "never what Claude wanted to do");
        }

        // Sample requests only.
        static PromptRequest Request(string session, string tool, long at)
        {
            var r = new PromptRequest { SessionId = session, ToolName = tool, Project = "confetti", At = at };
            if (tool == "Bash") r.ToolInput["command"] = "npm test";
            if (tool == "AskUserQuestion")
                r.ToolInput = Json.Obj(Json.Parse("{\"questions\":[" +
                    "{\"question\":\"Which colour?\",\"header\":\"Colour\",\"multiSelect\":false,\"options\":[{\"label\":\"Red\"},{\"label\":\"Blue\"}]}," +
                    "{\"question\":\"Which sizes?\",\"header\":\"Sizes\",\"multiSelect\":true,\"options\":[{\"label\":\"S\"},{\"label\":\"M\"}]}]}"));
            return r;
        }

        // Records each reply as "<session>:<kind>[ always][ answers][: message]".
        sealed class Replies
        {
            public readonly List<string> Got = new List<string>();
            public readonly Dictionary<string, PromptReply> Last = new Dictionary<string, PromptReply>();

            public Action<PromptReply> For(string session)
            {
                return delegate(PromptReply r)
                {
                    Got.Add(session + ":" + r.Kind + (r.Always ? " always" : "") + (r.Answers != null ? " answers" : "") + (r.Message != "" ? ": " + r.Message : ""));
                    Last[session] = r;
                };
            }

            public string All { get { return string.Join(", ", Got); } }
        }

        // How many times the broker made by the latest Broker() call has raised Changed.
        static int Changes;

        static PromptBroker Broker(Func<bool> front)
        {
            Changes = 0;
            var broker = new PromptBroker(front);
            broker.Changed += delegate { Changes++; };
            return broker;
        }

        static void StepsAsideWhenClaudeIsInFront()
        {
            PromptBroker broker = Broker(() => true);
            var replies = new Replies();
            HeldPrompt held = broker.Arrive(Request("a", "Bash", Now), replies.For("a"), Now);
            TestRunner.Check(held == null && broker.Count == 0, "with the Claude app in front, nothing is held");
            TestRunner.Eq("a:pass", replies.All, "the request goes straight back: the app asks as usual");
            TestRunner.Eq(0, Changes, "and the card has nothing new to show");
            PromptBroker failing = new PromptBroker(delegate { throw new InvalidOperationException(); });
            failing.Arrive(Request("b", "Bash", Now), replies.For("b"), Now);
            TestRunner.Check(failing.Count == 0 && replies.All.EndsWith("b:pass"), "when it can't be told what is in front, Capsule steps aside too");
        }

        static void HoldsRequestsOldestFirst()
        {
            PromptBroker broker = Broker(() => false);
            var replies = new Replies();
            HeldPrompt a = broker.Arrive(Request("a", "Bash", Now), replies.For("a"), Now);
            broker.Arrive(Request("b", "Bash", Now), replies.For("b"), Now + 1000);
            broker.Arrive(Request("c", "Bash", Now), replies.For("c"), Now + 2000);
            TestRunner.Check(a != null && broker.Current == a && broker.Count == 3, "three held, the oldest shown");
            TestRunner.Eq(3, Changes, "each arrival changes the card");
            TestRunner.Eq("", replies.All, "none answered yet: the app's prompts wait");
            broker.Act(PromptAction.Of(PromptAction.Allow, Cur(broker)), Now + 3000);
            TestRunner.Check(broker.Current != null && broker.Current.Request.SessionId == "b" && broker.Count == 2, "answered, the next oldest is shown");
            TestRunner.Eq("a:allow", replies.All, "and only the one shown was answered");
        }

        static void TheCountdownRestartsOnInteraction()
        {
            PromptBroker broker = Broker(() => false);
            broker.Arrive(Request("a", "Bash", Now), new Replies().For("a"), Now);
            TestRunner.Eq(PromptBroker.HoldMs, broker.MsLeft(Now), "60 s to go at first");
            TestRunner.Eq(45000L, broker.MsLeft(Now + 15000), "45 s after 15");
            broker.Act(PromptAction.Of(PromptAction.Touch, Cur(broker)), Now + 15000);
            TestRunner.Eq(PromptBroker.HoldMs, broker.MsLeft(Now + 15000), "a click on the card starts it again");
            broker.Act(PromptAction.Of(PromptAction.Next, Cur(broker)), Now + 16000);
            TestRunner.Eq(PromptBroker.HoldMs, broker.MsLeft(Now + 16000), "so does anything else done on the card");
            TestRunner.Eq(0L, broker.MsLeft(Now + 500000), "and it stops at 0");
        }

        static void ARequestGoesToClaudeWhenItsCountdownEnds()
        {
            PromptBroker broker = Broker(() => false);
            var replies = new Replies();
            broker.Arrive(Request("a", "Bash", Now), replies.For("a"), Now);
            broker.Arrive(Request("b", "Bash", Now), replies.For("b"), Now + 10000);
            broker.Tick(Now + 59999);
            TestRunner.Eq("", replies.All, "held until its countdown ends");
            broker.Tick(Now + 60000);
            TestRunner.Eq("a:pass", replies.All, "then it goes to the app");
            TestRunner.Check(broker.Count == 1 && broker.Current.Request.SessionId == "b", "a later one keeps its own countdown");
            broker.Tick(Now + 70000);
            TestRunner.Eq("a:pass, b:pass", replies.All, "until it ends too");
        }

        static void EverythingGoesToClaudeWhenItComesToTheFront()
        {
            bool front = false;
            PromptBroker broker = Broker(() => front);
            var replies = new Replies();
            broker.Arrive(Request("a", "Bash", Now), replies.For("a"), Now);
            broker.Arrive(Request("b", "Bash", Now), replies.For("b"), Now);
            broker.Tick(Now + 1000);
            TestRunner.Eq(2, broker.Count, "held while another app is in front");
            front = true;
            int before = Changes;
            broker.Tick(Now + 2000);
            TestRunner.Check(broker.Count == 0 && replies.All == "a:pass, b:pass", "the Claude app comes to the front: everything goes to it");
            TestRunner.Eq(before + 1, Changes, "in one change");
        }

        static SessionStatus Status(string session, string evt, long at, string state = States.Working)
        {
            return new SessionStatus { SessionId = session, Event = evt, At = at, State = state };
        }

        // The id of the request the card shows: what its buttons would name.
        static int Cur(PromptBroker broker) { return broker.Current == null ? 0 : broker.Current.Id; }

        static void ARequestGoesWhenItsSessionMovesOn()
        {
            PromptBroker broker = Broker(() => false);
            var replies = new Replies();
            broker.Arrive(Request("a", "Bash", Now), replies.For("a"), Now + 50);
            broker.SessionsChanged(new List<SessionStatus> { Status("a", "PermissionRequest", Now) });
            TestRunner.Eq("", replies.All, "the status the hook wrote for this request leaves it held");
            broker.SessionsChanged(new List<SessionStatus> { Status("a", "PermissionRequest", Now + 5000), Status("a", "PreToolUse", Now + 6000) });
            TestRunner.Eq("", replies.All, "so does another prompt of the same session");
            broker.SessionsChanged(new List<SessionStatus> { Status("b", "PostToolUse", Now + 5000) });
            TestRunner.Eq("", replies.All, "and another session moving on");
            broker.SessionsChanged(new List<SessionStatus> { Status("a", "PostToolUse", Now + 5000) });
            TestRunner.Eq("a:pass", replies.All, "a later event of its own session: it was answered in the app, so Capsule lets go");
            foreach (string evt in new[] { "PostToolUse", "PostToolUseFailure", "Stop", "StopFailure", "UserPromptSubmit", "PermissionDenied" })
            {
                broker.Arrive(Request("c", "Bash", Now), replies.For("c"), Now + 50);
                broker.SessionsChanged(new List<SessionStatus> { Status("c", evt, Now + 1) });
                TestRunner.Check(broker.Count == 0 && replies.All.EndsWith("c:pass"), "and so does " + evt);
            }
            broker.SessionsChanged(null);
            TestRunner.Eq(0, broker.Count, "no sessions at all change nothing");
        }

        // Claude Code sends a Notification (permission_prompt) about 6 s after a PermissionRequest whose dialog is still
        // unanswered; that is not the session moving on, or every card would go back after 6 s.
        static void ANotificationOnlyMeansMovedOnWhenTheSessionIsIdle()
        {
            PromptBroker broker = Broker(() => false);
            var replies = new Replies();
            broker.Arrive(Request("a", "Bash", Now), replies.For("a"), Now + 50);
            broker.SessionsChanged(new List<SessionStatus> { Status("a", "Notification", Now + 6000, States.Waiting) });
            TestRunner.Check(broker.Count == 1 && replies.All == "", "a permission_prompt notification (waiting) leaves the request held");
            broker.SessionsChanged(new List<SessionStatus> { Status("a", "Notification", Now + 6000, States.Idle) });
            TestRunner.Check(broker.Count == 1 && replies.All == "", "so does a notification that leaves the session in any other state");
            broker.SessionsChanged(new List<SessionStatus> { Status("a", "SomethingNew", Now + 7000, States.Working) });
            TestRunner.Check(broker.Count == 1 && replies.All == "", "and an event this doesn't know");
            broker.SessionsChanged(new List<SessionStatus> { Status("a", "Notification", Now + 8000, States.Done) });
            TestRunner.Check(broker.Count == 0 && replies.All == "a:pass", "an idle-prompt notification (done) lets go");
        }

        static void AHookThatWentAwayIsDropped()
        {
            PromptBroker broker = Broker(() => false);
            var replies = new Replies();
            HeldPrompt a = broker.Arrive(Request("a", "Bash", Now), replies.For("a"), Now);
            broker.Arrive(Request("b", "Bash", Now), replies.For("b"), Now);
            int before = Changes;
            broker.Gone(a.Id);
            TestRunner.Check(broker.Count == 1 && broker.Current.Request.SessionId == "b", "a request whose hook went away is dropped");
            TestRunner.Eq("", replies.All, "without a reply: there is no one to hear it");
            TestRunner.Eq(before + 1, Changes, "and the card moves on");
            broker.Gone(a.Id);
            broker.Gone(999);
            TestRunner.Eq(before + 1, Changes, "a request not held is nothing to drop");
        }

        static void QuitReleasesEverything()
        {
            PromptBroker broker = Broker(() => false);
            var replies = new Replies();
            broker.Arrive(Request("a", "Bash", Now), replies.For("a"), Now);
            broker.Arrive(Request("b", "AskUserQuestion", Now), replies.For("b"), Now);
            broker.ReleaseAll();
            TestRunner.Check(broker.Count == 0 && replies.All == "a:pass, b:pass", "Capsule quits: every request goes to the app");
            broker.ReleaseAll();
            TestRunner.Eq("a:pass, b:pass", replies.All, "a second time does nothing");
        }

        static string ActOn(PromptRequest request, string kind)
        {
            var broker = new PromptBroker(() => false);
            var replies = new Replies();
            broker.Arrive(request, replies.For(request.SessionId), Now);
            broker.Act(PromptAction.Of(kind, Cur(broker)), Now + 1000);
            return replies.All;
        }

        static void TheButtonsReply()
        {
            PromptRequest bash = Request("a", "Bash", Now);
            TestRunner.Eq("a:allow", ActOn(bash, PromptAction.Allow), "Allow");
            TestRunner.Eq("a:deny: " + HookReply.DeniedMessage, ActOn(bash, PromptAction.Deny), "Deny");
            TestRunner.Eq("a:pass", ActOn(bash, PromptAction.InClaude), "Answer in Claude: the app asks as usual");
            TestRunner.Eq("", ActOn(bash, PromptAction.AlwaysAllow), "no Always allow without a suggested rule");
            bash.Rules.Add("Bash(npm test:*)");
            TestRunner.Eq("a:allow always", ActOn(bash, PromptAction.AlwaysAllow), "Always allow, with one");
            PromptRequest plan = Request("p", "ExitPlanMode", Now);
            TestRunner.Eq("p:allow", ActOn(plan, PromptAction.Allow), "Approve plan");
            TestRunner.Eq("p:deny: " + HookReply.KeepPlanningMessage, ActOn(plan, PromptAction.Deny), "Keep planning");
            TestRunner.Eq("", ActOn(Request("q", "AskUserQuestion", Now), PromptAction.Allow), "a question isn't simply allowed: it is answered");
        }

        static void QuestionsAreAnsweredOnTheCard()
        {
            PromptBroker broker = Broker(() => false);
            var replies = new Replies();
            HeldPrompt q = broker.Arrive(Request("q", "AskUserQuestion", Now), replies.For("q"), Now);
            TestRunner.Check(q.Flow != null && q.Flow.Count == 2, "a question arrives with its flow");
            int before = Changes;
            broker.Act(PromptAction.Chose(1, Cur(broker)), Now + 1000);
            TestRunner.Check(q.Flow.IsChosen(1) && Changes == before + 1, "an option chosen redraws the card");
            broker.Act(PromptAction.Of(PromptAction.Send, Cur(broker)), Now + 2000);
            TestRunner.Eq("", replies.All, "Send does nothing before the last question");
            broker.Act(PromptAction.Of(PromptAction.Next, Cur(broker)), Now + 3000);
            TestRunner.Check(q.Flow.Index == 1, "Next goes to the second");
            broker.Act(PromptAction.Of(PromptAction.Other, Cur(broker)), Now + 4000);
            before = Changes;
            broker.Act(PromptAction.Typed("XL", Cur(broker)), Now + 5000);
            TestRunner.Check(q.Flow.OtherOpen && q.Flow.OtherText == "XL", "Other… opens its box, and what is typed is kept");
            TestRunner.Eq(before, Changes, "typing doesn't redraw the card: the box keeps its caret");
            TestRunner.Eq(PromptBroker.HoldMs, broker.MsLeft(Now + 5000), "and restarts the countdown");
            broker.Act(PromptAction.Of(PromptAction.Send, Cur(broker)), Now + 6000);
            TestRunner.Eq("q:allow answers", replies.All, "Send on the last allows the question with the answers");
            PromptReply sent = replies.Last["q"];
            var sizes = sent.Answers["Which sizes?"] as string[];
            TestRunner.Check((sent.Answers["Which colour?"] as string) == "Blue" && sizes != null && sizes.Length == 1 && sizes[0] == "XL", "the answers chosen and typed");
            TestRunner.Eq(0, broker.Count, "and the question is done");
        }

        static void AQuestionTheCardCantShowCanOnlyGoToClaude()
        {
            PromptBroker broker = Broker(() => false);
            var replies = new Replies();
            PromptRequest odd = Request("q", "AskUserQuestion", Now);
            odd.ToolInput = Json.Obj(Json.Parse("{\"questions\":\"not a list\"}"));
            HeldPrompt held = broker.Arrive(odd, replies.For("q"), Now);
            TestRunner.Check(held != null && held.Flow == null, "a question the card can't show is still held, without a flow");
            broker.Act(PromptAction.Chose(0, Cur(broker)), Now);
            broker.Act(PromptAction.Of(PromptAction.Send, Cur(broker)), Now);
            TestRunner.Eq("", replies.All, "nothing can be chosen or sent");
            broker.Act(PromptAction.Of(PromptAction.InClaude, Cur(broker)), Now);
            TestRunner.Eq("q:pass", replies.All, "Answer in Claude sends it to the app");
        }

        static void AFailingReplyDoesntStopTheBroker()
        {
            PromptBroker broker = Broker(() => false);
            var replies = new Replies();
            broker.Arrive(Request("a", "Bash", Now), delegate { throw new System.IO.IOException(); }, Now);
            broker.Arrive(Request("b", "Bash", Now), replies.For("b"), Now);
            broker.Act(PromptAction.Of(PromptAction.Allow, Cur(broker)), Now);
            TestRunner.Check(broker.Count == 1 && broker.Current.Request.SessionId == "b", "a reply that fails still takes its request off the card");
            broker.Act(PromptAction.Of(PromptAction.Allow, Cur(broker)), Now);
            TestRunner.Eq("b:allow", replies.All, "and the next one is answered as usual");
        }

        // A click on a card that was just released must never land on the next request.
        static void ActionsNameTheirRequest()
        {
            // The next request is an approval: nothing aimed at the one before may answer it or restart its countdown.
            PromptBroker broker = Broker(() => false);
            var replies = new Replies();
            HeldPrompt a = broker.Arrive(Request("a", "Bash", Now), replies.For("a"), Now);
            a.Request.Rules.Add("Bash(npm test:*)");
            HeldPrompt b = broker.Arrive(Request("b", "Bash", Now), replies.For("b"), Now + 1000);
            b.Request.Rules.Add("Bash(npm test:*)");
            broker.Act(PromptAction.Of(PromptAction.InClaude, a.Id), Now + 2000);
            TestRunner.Check(broker.Current == b && replies.All == "a:pass", "a was released, b shown");
            foreach (string kind in new[] { PromptAction.Allow, PromptAction.AlwaysAllow, PromptAction.Deny, PromptAction.InClaude, PromptAction.Touch })
                broker.Act(PromptAction.Of(kind, a.Id), Now + 30000);
            TestRunner.Check(broker.Current == b && replies.All == "a:pass", "a click meant for a does nothing to b: no reply sent, b still held");
            TestRunner.Eq(Now + 1000 + PromptBroker.HoldMs, b.Deadline, "and doesn't restart b's countdown");
            broker.Act(PromptAction.Of(PromptAction.Touch, 0), Now + 30000);
            TestRunner.Check(broker.Current == b && replies.All == "a:pass", "an action naming nothing is ignored too");
            broker.Act(PromptAction.Of(PromptAction.Allow, b.Id), Now + 31000);
            TestRunner.Eq("a:pass, b:allow", replies.All, "one meant for b still answers it");

            // The next request is a question: nothing aimed at the one before may choose, open Other or send.
            broker = Broker(() => false);
            replies = new Replies();
            HeldPrompt first = broker.Arrive(Request("a", "Bash", Now), replies.For("a"), Now);
            HeldPrompt q = broker.Arrive(Request("q", "AskUserQuestion", Now), replies.For("q"), Now);
            broker.Act(PromptAction.Of(PromptAction.Deny, first.Id), Now + 1000);
            int before = Changes;
            broker.Act(PromptAction.Chose(0, first.Id), Now + 2000);
            broker.Act(PromptAction.Of(PromptAction.Other, first.Id), Now + 2000);
            broker.Act(PromptAction.Typed("late", first.Id), Now + 2000);
            broker.Act(PromptAction.Of(PromptAction.Next, first.Id), Now + 2000);
            TestRunner.Check(broker.Current == q && !q.Flow.IsChosen(0) && !q.Flow.OtherOpen && q.Flow.OtherText == "" && q.Flow.Index == 0, "choosing, Other…, typing and Next meant for a leave the question alone");
            TestRunner.Eq(before, Changes, "and the card isn't redrawn for them");
            broker.Act(PromptAction.Chose(0, q.Id), Now + 3000);
            TestRunner.Check(q.Flow.IsChosen(0), "the same, naming the question, counts");
            broker.Act(PromptAction.Of(PromptAction.Next, q.Id), Now + 3000);
            broker.Act(PromptAction.Chose(0, q.Id), Now + 3000);
            broker.Act(PromptAction.Of(PromptAction.Send, first.Id), Now + 4000);
            TestRunner.Check(broker.Count == 1 && replies.All == "a:deny: " + HookReply.DeniedMessage, "and a Send meant for a doesn't answer the question");

            // What is typed in Other… has nothing a reader can't see in it.
            broker.Act(PromptAction.Of(PromptAction.Other, q.Id), Now + 5000);
            broker.Act(PromptAction.Typed("X" + (char)0x202E + "L" + (char)0x200B + "\n", q.Id), Now + 5000);
            TestRunner.Eq("XL ", q.Flow.OtherText, "typed text loses its direction and zero-width characters, and a line break becomes a space, as the card shows it");
        }

        static void ARequestLivesAtMostEightySeconds()
        {
            PromptBroker broker = Broker(() => false);
            var replies = new Replies();
            broker.Arrive(Request("a", "Bash", Now), replies.For("a"), Now);
            for (long t = 5000; t <= 75000; t += 5000)
            {
                broker.Act(PromptAction.Of(PromptAction.Touch, Cur(broker)), Now + t);
                broker.Tick(Now + t);
            }
            TestRunner.Check(broker.Count == 1 && replies.All == "", "touched every 5 s, it is still held at 75 s");
            TestRunner.Eq(5000L, broker.MsLeft(Now + 75000), "with the countdown cut to what is left of its life, not 60 s");
            broker.Act(PromptAction.Of(PromptAction.Touch, Cur(broker)), Now + 79000);
            TestRunner.Eq(1000L, broker.MsLeft(Now + 79000), "touching can't go past 80 s");
            broker.Act(PromptAction.Of(PromptAction.Next, Cur(broker)), Now + 79500);
            TestRunner.Eq(500L, broker.MsLeft(Now + 79500), "nor can anything else done on the card");
            for (long t = 80000; t <= 85000; t += 5000)
            {
                broker.Act(PromptAction.Of(PromptAction.Touch, Cur(broker)), Now + t);
                broker.Tick(Now + t);
            }
            TestRunner.Eq("a:pass", replies.All, "kept touching for 85 s, it goes to the app at 80 s: the hook only waits 90");
        }

        // The hook waits 90 s from when it sent the request, not from when Capsule got it: a request that took 15 s to
        // arrive (the pipe was busy, Capsule was slow) is still released at its own At + 80 s. At is in Clock ms, the same
        // clock the broker is given.
        static void TheCapRunsFromWhenTheHookSentIt()
        {
            PromptBroker broker = Broker(() => false);
            var replies = new Replies();
            long arrival = Now + 15000;
            broker.Arrive(Request("a", "Bash", Now), replies.For("a"), arrival);
            for (long t = 20000; t < 80000; t += 5000)
            {
                broker.Act(PromptAction.Of(PromptAction.Touch, Cur(broker)), Now + t);
                broker.Tick(Now + t);
            }
            TestRunner.Check(replies.All == "", "touched, it is held until 79 s after the hook sent it");
            TestRunner.Eq(1000L, broker.MsLeft(Now + 79000), "its countdown cut to the hook's own 80 s, not 15 s later");
            broker.Act(PromptAction.Of(PromptAction.Touch, Cur(broker)), Now + 79500);
            broker.Tick(Now + 80000);
            TestRunner.Eq("a:pass", replies.All, "and it goes to the app at At + 80 s");
            broker = Broker(() => false);
            replies = new Replies();
            broker.Arrive(Request("b", "Bash", Now + 60000), replies.For("b"), Now);
            broker.Act(PromptAction.Of(PromptAction.Touch, Cur(broker)), Now + 79000);
            TestRunner.Eq(1000L, broker.MsLeft(Now + 79000), "an At later than the arrival (clocks apart) never gives more than arrival + 80 s");
            broker = Broker(() => false);
            replies = new Replies();
            broker.Arrive(Request("c", "Bash", 0), replies.For("c"), Now);
            TestRunner.Eq(PromptBroker.HoldMs, broker.MsLeft(Now), "a request with no time of its own is capped from its arrival");
            broker.Act(PromptAction.Of(PromptAction.Touch, Cur(broker)), Now + 79000);
            TestRunner.Eq(1000L, broker.MsLeft(Now + 79000), "80 s after it");
        }

        static void ARequestThatWaitedGetsTimeToBeRead()
        {
            PromptBroker broker = Broker(() => false);
            var replies = new Replies();
            HeldPrompt a = broker.Arrive(Request("a", "Bash", Now), replies.For("a"), Now);
            HeldPrompt b = broker.Arrive(Request("b", "Bash", Now), replies.For("b"), Now);
            broker.Act(PromptAction.Of(PromptAction.Allow, a.Id), Now + 55000);
            TestRunner.Check(broker.Current == b, "b is shown after 55 s of waiting");
            TestRunner.Eq(10000L, broker.MsLeft(Now + 55000), "with 10 s on its clock, not the 5 it had left");
            broker = Broker(() => false);
            a = broker.Arrive(Request("a", "Bash", Now), new Replies().For("a"), Now);
            b = broker.Arrive(Request("b", "Bash", Now), new Replies().For("b"), Now);
            broker.Act(PromptAction.Of(PromptAction.Allow, a.Id), Now + 75000);
            TestRunner.Eq(5000L, broker.MsLeft(Now + 75000), "but never past its own 80 s");
            broker = Broker(() => false);
            a = broker.Arrive(Request("a", "Bash", Now), new Replies().For("a"), Now);
            b = broker.Arrive(Request("b", "Bash", Now), new Replies().For("b"), Now);
            broker.Act(PromptAction.Of(PromptAction.Allow, a.Id), Now + 1000);
            TestRunner.Eq(59000L, broker.MsLeft(Now + 1000), "one with plenty left keeps what it has");
            broker = Broker(() => false);
            a = broker.Arrive(Request("a", "Bash", Now), new Replies().For("a"), Now);
            b = broker.Arrive(Request("b", "Bash", Now), new Replies().For("b"), Now);
            broker.Tick(Now + 55000);
            broker.SessionsChanged(new List<SessionStatus> { Status("a", "PostToolUse", Now + 1) });
            TestRunner.Check(broker.Current == b, "b is shown once a's session moved on");
            TestRunner.Eq(10000L, broker.MsLeft(Now + 55000), "the same, however the one before it went");
        }

        static void ARequestWithoutAReplyPathGoesBack()
        {
            var broker = new PromptBroker(() => false);
            var replies = new Replies();
            // The pipe handler's way of saying "no request": the hook still gets exactly one reply.
            TestRunner.Check(broker.Arrive(null, replies.For("n"), Now) == null && broker.Count == 0, "nothing is held for a missing request");
            TestRunner.Eq("n:pass", replies.All, "and the hook is told to pass");
        }
    }
}
