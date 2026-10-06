using System;
using System.Collections.Generic;
using System.Linq;

namespace Capsule
{
    // What the card asks of the request it shows (act-from-notch spec §3): a button, an option, the Other… box and what
    // is typed in it.
    public sealed class PromptAction
    {
        public const string Allow = "allow", AlwaysAllow = "always", Deny = "deny", InClaude = "in-claude",
            Choose = "choose", Other = "other", OtherText = "other-text", Next = "next", Send = "send",
            Touch = "touch";   // a click anywhere on the card: it only restarts the countdown

        public string Kind = "";
        public int Target;         // the id of the HeldPrompt the card was showing when this was done
        public int Option;         // Choose: which option
        public string Text = "";   // OtherText: what the box holds now

        public static PromptAction Of(string kind, int target) { return new PromptAction { Kind = kind, Target = target }; }
        public static PromptAction Chose(int option, int target) { return new PromptAction { Kind = Choose, Option = option, Target = target }; }
        public static PromptAction Typed(string text, int target) { return new PromptAction { Kind = OtherText, Text = text ?? "", Target = target }; }
    }

    // A request Capsule holds, and how to answer it.
    public sealed class HeldPrompt
    {
        public int Id;
        public PromptRequest Request;
        public QuestionFlow Flow;   // a question the card can show; null for anything else
        public long Deadline;       // when it goes to the app, unless the card is used first
        public long Arrived;        // when Capsule got it
        // Its countdown never runs past this: MaxLifeMs after the hook sent the request (Request.At, in Clock ms, the same
        // clock as Arrived), or after it arrived if that is earlier or the request has no time of its own. The hook gives up
        // 90 s after it sent it, however long it took to get here.
        public long Cap { get { return Math.Min(Arrived, Request.At > 0 ? Request.At : Arrived) + PromptBroker.MaxLifeMs; } }
        internal Action<PromptReply> Reply;

        public bool IsQuestion { get { return Request.ToolName == ToolNames.AskUserQuestion; } }
        public bool IsPlan { get { return Request.ToolName == ToolNames.ExitPlanMode; } }
    }

    // The rules for held requests (spec §3), on the UI thread, with no window and no pipe of its own:
    // - a request that arrives while Capsule should step aside (the Claude app's window in front) goes straight back;
    // - the others queue oldest first; the card shows the oldest;
    // - each goes back to the app when its countdown (60 s, restarted by anything done on its card) ends, when the Claude
    //   app comes to the front, when its session moves on (it was answered in the app), on Answer in Claude, and on quit;
    // - one whose hook went away is dropped.
    // Every request gets exactly one reply, and "goes back" means the reply pass: the hook prints nothing and the app asks.
    public sealed class PromptBroker
    {
        public const long HoldMs = 60 * 1000;
        public const long MaxLifeMs = 80 * 1000;      // the hook waits 90 s: the answer must be sent before it gives up
        public const long ReadMs = 10 * 1000;         // a request that waited behind another gets at least this long on the card

        // The events that show a session went on after a request: its own prompt was answered (in the app, or the tool ran
        // or was refused), or the turn ended or began again. Anything else, a Notification above all, doesn't: Claude Code
        // sends Notification permission_prompt about 6 s after a PermissionRequest whose dialog is still open.
        static readonly HashSet<string> MovedOn = new HashSet<string>
        {
            "PostToolUse", "PostToolUseFailure", "PermissionDenied", "UserPromptSubmit", "Stop", "StopFailure",
        };

        readonly Func<bool> stepAside;
        readonly List<HeldPrompt> queue = new List<HeldPrompt>();
        int lastId;
        long lastNow;            // the latest time the broker was told, for what it does without one
        HeldPrompt shown;        // the request the card showed last

        public event Action Changed;   // the card has something else to show; not raised by the countdown or by typing

        // stepAside: whether Capsule should let the app ask (the Claude app's window is in front). If it throws, Capsule steps aside.
        public PromptBroker(Func<bool> stepAside) { this.stepAside = stepAside; }

        public HeldPrompt Current { get { return queue.Count > 0 ? queue[0] : null; } }
        public int Count { get { return queue.Count; } }

        // How long until the request shown goes to the app.
        public long MsLeft(long now) { return Current == null ? 0 : Math.Max(0, Current.Deadline - now); }

        // A request arrives with the way to answer it. Null when Capsule stepped aside and has already passed it back.
        public HeldPrompt Arrive(PromptRequest request, Action<PromptReply> reply, long now)
        {
            lastNow = now;
            if (request == null)
            {
                Send(reply, PromptReply.Passing());   // the hook is waiting for exactly one reply
                return null;
            }
            if (ShouldStepAside())
            {
                Log.Info("prompts: passed to the app at once (Capsule steps aside)");
                Send(reply, PromptReply.Passing());
                return null;
            }
            var held = new HeldPrompt { Id = ++lastId, Request = request, Arrived = now, Reply = reply };
            held.Deadline = Math.Min(now + HoldMs, held.Cap);
            if (held.IsQuestion)
            {
                List<PromptQuestion> questions = QuestionFlow.Parse(request.ToolInput);
                if (questions != null) held.Flow = new QuestionFlow(questions);
            }
            queue.Add(held);
            Log.Info("prompts: held (" + queue.Count + " waiting)");
            Raise();
            return held;
        }

        // From the card, about the request it shows. Everything counts as using the card, and restarts its countdown
        // (never past the request's cap, MaxLifeMs after it arrived); a click anywhere on it is PromptAction.Touch.
        // An action names the request the card was built for (Target); if that one is no longer the one shown (its card was
        // released and another took its place), the click is stale and is ignored, whatever it was.
        public void Act(PromptAction action, long now)
        {
            lastNow = now;
            HeldPrompt held = Current;
            if (held == null || action == null || action.Target != held.Id) return;
            held.Deadline = Math.Min(now + HoldMs, held.Cap);
            QuestionFlow flow = held.Flow;
            switch (action.Kind)
            {
                case PromptAction.Allow:
                    if (!held.IsQuestion) Finish(held, PromptReply.Allowing());
                    break;
                case PromptAction.AlwaysAllow:
                    if (!held.IsQuestion && held.Request.Rules.Count > 0) Finish(held, PromptReply.AlwaysAllowing());
                    break;
                case PromptAction.Deny:
                    Finish(held, PromptReply.Denying(held.IsPlan ? HookReply.KeepPlanningMessage : HookReply.DeniedMessage));
                    break;
                case PromptAction.InClaude:
                    Finish(held, PromptReply.Passing());
                    break;
                case PromptAction.Choose:
                    if (flow == null) break;
                    flow.Choose(action.Option);
                    Raise();
                    break;
                case PromptAction.Other:
                    if (flow == null) break;
                    flow.ToggleOther();
                    Raise();
                    break;
                case PromptAction.OtherText:
                    if (flow != null) flow.SetOther(PromptCardModel.Typed(action.Text));   // as the card shows it; no redraw: the box keeps its caret
                    break;
                case PromptAction.Next:
                    if (flow != null && flow.Next()) Raise();
                    break;
                case PromptAction.Send:
                    if (flow == null || !flow.IsLast) break;
                    Dictionary<string, object> answers = flow.Answers();
                    if (answers != null) Finish(held, PromptReply.Answering(answers));
                    break;
            }
        }

        // Often (every 250 ms while anything is held): the Claude app coming to the front takes everything; a countdown
        // that has ended takes its request.
        public void Tick(long now)
        {
            lastNow = now;
            if (queue.Count == 0) return;
            if (ShouldStepAside()) Release(queue.ToList(), "Capsule steps aside");
            else Release(queue.Where(h => h.Deadline <= now).ToList(), "its countdown ended");
        }

        // The sessions' status files changed. A later event of a held request's session that shows the session went on
        // (MovedOn, or a Notification that left it idle: Done) means the app's own prompt was answered meanwhile, so
        // Capsule lets go. A Notification that left it Waiting is only Claude Code reminding about the open prompt.
        public void SessionsChanged(IEnumerable<SessionStatus> sessions)
        {
            if (queue.Count == 0 || sessions == null) return;
            List<SessionStatus> list = sessions.ToList();
            foreach (HeldPrompt held in queue.ToList())
            {
                SessionStatus later = list.FirstOrDefault(s => s.SessionId == held.Request.SessionId && s.At > held.Request.At && WentOn(s));
                // The event is one of the whitelist's names (or Notification), never anything Claude wanted.
                if (later != null) Release(new List<HeldPrompt> { held }, "its session moved on: " + later.Event);
            }
        }

        static bool WentOn(SessionStatus s)
        {
            if (s.Event == "Notification") return s.State == States.Done;
            return MovedOn.Contains(s.Event);
        }

        // The hook went away (Claude Code stopped waiting, or it gave up): its request is dropped without a reply.
        public void Gone(int id)
        {
            HeldPrompt held = queue.FirstOrDefault(h => h.Id == id);
            if (held == null) return;
            queue.Remove(held);
            Log.Info("prompts: dropped (the hook went away)");
            Raise();
        }

        // Capsule quits, or something failed: everything goes to the app.
        public void ReleaseAll() { Release(queue.ToList(), "Capsule stopped"); }

        // The log says how many and why; never what Claude wanted to do.
        void Release(List<HeldPrompt> list, string why)
        {
            if (list.Count == 0) return;
            foreach (HeldPrompt held in list)
            {
                queue.Remove(held);
                Send(held.Reply, PromptReply.Passing());
            }
            Log.Info("prompts: " + list.Count + " passed to the app (" + why + ")");
            Raise();
        }

        void Finish(HeldPrompt held, PromptReply reply)
        {
            queue.Remove(held);
            Send(held.Reply, reply);
            Log.Info("prompts: " + (reply.Kind == PromptReply.Pass ? "passed to the app (Answer in Claude)"
                : "answered from the capsule (" + reply.Kind + (reply.Always ? ", always" : "") + (reply.Answers != null ? ", with answers" : "") + ")"));
            Raise();
        }

        bool ShouldStepAside()
        {
            try { return stepAside != null && stepAside(); }
            catch (Exception) { return true; }
        }

        static void Send(Action<PromptReply> reply, PromptReply answer)
        {
            if (reply == null) return;
            try { reply(answer); }
            catch (Exception e) { Log.Error("prompts: a reply couldn't be sent (" + e.GetType().Name + ")", null); }
        }

        // A request that has just come to the front (the one before it was answered or went) may have waited a while
        // behind it: it gets at least ReadMs on the clock, still within its own cap.
        void Settle()
        {
            HeldPrompt now = Current;
            if (now == shown) return;
            shown = now;
            if (now != null) now.Deadline = Math.Max(now.Deadline, Math.Min(lastNow + ReadMs, now.Cap));
        }

        void Raise()
        {
            Settle();
            if (Changed != null) Changed();
        }
    }
}
