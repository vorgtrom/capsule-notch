using System.Windows.Input;

namespace Capsule
{
    static class HookHealthTests
    {
        internal static void Run()
        {
            const long now = 1700000000000L;
            const long minute = 60 * 1000;
            var health = new HookHealth();
            TestRunner.Eq("No events yet: start a new chat", health.Text(now), "new hook has no events");
            TestRunner.Check(health.NeedsPanelHint(now), "a connected hook with no events needs a hint");
            health.Record(now - 3 * minute);
            health.Record(now - 5 * minute);
            TestRunner.Eq("Last event 3 min ago", health.Text(now), "only the newest event wins");
            TestRunner.Check(!health.NeedsPanelHint(now), "a recent event hides the hint");
            TestRunner.Check(health.NeedsPanelHint(now + Sessions.DeleteAfterMs), "after a day without events the hint returns");
            health.Reset(askForTrust: true);
            TestRunner.Check(health.Text(now).Contains("trust the hook in Codex (/hooks)"), "migration or connect replaces old Codex history with the trust hint");
            health.Record(now);
            TestRunner.Eq("Last event just now", health.Text(now), "an arriving event replaces the trust hint");

            var claude = new PromptBroker(() => false);
            var codex = new PromptBroker(() => false);
            TestRunner.Check(Controller.ShortcutPrompt(claude, codex) == null, "no held request keeps the normal shortcut");
            codex.Arrive(Request("codex"), reply => { }, now);
            TestRunner.Check(Controller.ShortcutPrompt(claude, codex) == codex, "the shortcut can open a Codex request");
            claude.Arrive(Request("claude"), reply => { }, now - 1);
            TestRunner.Check(Controller.ShortcutPrompt(claude, codex) == claude, "the oldest held request opens first");

            TestRunner.Check(PromptView.ActivatesButton(Key.Enter, false, true), "Enter activates a settled focused button");
            TestRunner.Check(!PromptView.ActivatesButton(Key.Enter, true, true), "holding Enter cannot activate another button");
            TestRunner.Check(!PromptView.ActivatesButton(Key.Enter, false, false), "Enter cannot bypass the new request delay");
            TestRunner.Check(HoverCard.ClosesPrompt(Key.Escape) && !PromptView.ActivatesButton(Key.Escape, false, true),
                "Escape dismisses the view without activating a button");
            TestRunner.Check(new PromptView().Focusable, "the prompt itself can take initial focus");

            var timing = new TimingCounter("sample");
            timing.Stop(TimingCounter.Start());
            timing.Stop(TimingCounter.Start());
            TestRunner.Check(timing.Take().EndsWith("ms/2"), "timing counts each call");
            TestRunner.Eq("sample=0ms/0", timing.Take(), "reading totals resets the interval");
        }

        static PromptRequest Request(string provider)
        {
            var request = new PromptRequest { SessionId = provider, Provider = provider, ToolName = "Bash" };
            request.ToolInput["command"] = "echo sample";
            return request;
        }
    }
}
