using System;

namespace Capsule
{
    internal sealed class HookHealth
    {
        long last;
        bool trust;

        internal void Record(long at) { last = Math.Max(last, at); }

        internal void Reset(bool askForTrust)
        {
            last = 0;
            trust = askForTrust;
        }

        internal bool NeedsPanelHint(long now) { return last == 0 || now - last >= Sessions.DeleteAfterMs; }

        internal string Text(long now)
        {
            if (last > 0) return "Last event " + Format.Ago(last, now);
            return trust ? "No events yet: trust the hook in Codex (/hooks), then start a new chat"
                : "No events yet: start a new chat";
        }
    }
}
