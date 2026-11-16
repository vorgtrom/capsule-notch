using System;

namespace Capsule
{
    internal sealed class HookHealth
    {
        const string CodexTrustHint = "No events yet: trust the hook in Codex (/hooks), then start a new chat";
        long last;

        internal void Record(long at) { last = Math.Max(last, at); }

        internal static bool SetCodexTrustPending(Config config, bool pending)
        {
            if (config.CodexTrustPending == pending) return false;
            config.CodexTrustPending = pending;
            return true;
        }

        internal static bool RecordCodexRequest(Config config, long at)
        {
            bool changed = SetCodexTrustPending(config, false);
            if (at <= config.LastCodexRequestMs) return changed;
            config.LastCodexRequestMs = at;
            return true;
        }

        internal static string CodexMenuText(Config config, long now)
        {
            if (config.CodexTrustPending) return CodexTrustHint;
            return config.LastCodexRequestMs > 0 ? "Last request " + Format.Ago(config.LastCodexRequestMs, now).Replace(" hr ago", " h ago")
                : "No approval requests yet";
        }

        internal static string CodexPanelHint(Config config) { return config.CodexTrustPending ? CodexTrustHint : ""; }

        internal bool NeedsPanelHint(long now) { return last == 0 || now - last >= Sessions.DeleteAfterMs; }

        internal string Text(long now)
        {
            if (last > 0) return "Last event " + Format.Ago(last, now);
            return "No events yet: start a new chat";
        }
    }
}
