using System.IO;
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
            CodexTrustSurvivesRestart(now);

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

        static void CodexTrustSurvivesRestart(long now)
        {
            const long hour = 60 * 60 * 1000;
            const string trust = "No events yet: trust the hook in Codex (/hooks), then start a new chat";
            string path = Path.Combine(TestRunner.NewTempDir(), "config.json");
            File.WriteAllText(path, "{}");
            Config config = Config.Load(path);
            TestRunner.Eq("No approval requests yet", HookHealth.CodexMenuText(config, now), "old config does not imply untrusted hooks");
            TestRunner.Eq("", HookHealth.CodexPanelHint(config), "a quiet Codex hook needs no panel hint");

            TestRunner.Check(HookHealth.SetCodexTrustPending(config, true) && config.Save(path),
                "connect or migration marks trust pending and saves it");
            config = Config.Load(path);
            TestRunner.Eq(trust, HookHealth.CodexMenuText(config, now), "trust reminder survives restart");
            TestRunner.Eq(trust, HookHealth.CodexPanelHint(config), "pending trust is shown in the panel");

            TestRunner.Check(HookHealth.RecordCodexRequest(config, now) && config.Save(path),
                "the first request clears trust and saves its time");
            config = Config.Load(path);
            TestRunner.Check(!config.CodexTrustPending && config.LastCodexRequestMs == now, "restart preserves cleared trust and last request");
            TestRunner.Eq("Last request 3 h ago", HookHealth.CodexMenuText(config, now + 3 * hour), "shows the time of the last request");
            TestRunner.Eq("", HookHealth.CodexPanelHint(config), "receiving a request clears the panel hint");
            TestRunner.Eq("", HookHealth.CodexPanelHint(Config.Load(path)), "the panel hint stays clear after restart");
            TestRunner.Check(HookHealth.CodexMenuText(config, now + 24 * hour) == "Last request 1 d ago" &&
                HookHealth.CodexPanelHint(config) == "", "a day without a Codex request leaves the panel quiet");

            TestRunner.Check(HookHealth.SetCodexTrustPending(config, true), "a later migration can require trust again");
            TestRunner.Eq(trust, HookHealth.CodexMenuText(config, now + 24 * hour), "trust pending takes priority over prior requests");
            TestRunner.Check(HookHealth.SetCodexTrustPending(config, false) && config.LastCodexRequestMs == now,
                "disconnect clears the hint and preserves the last request time");
        }

        static PromptRequest Request(string provider)
        {
            var request = new PromptRequest { SessionId = provider, Provider = provider, ToolName = "Bash" };
            request.ToolInput["command"] = "echo sample";
            return request;
        }
    }
}
