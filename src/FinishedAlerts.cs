using System;
using System.Collections.Generic;

namespace Capsule
{
    internal sealed class FinishedAlerts
    {
        const long MinuteMs = 60 * 1000;

        struct Turn
        {
            public string State;
            public long StartedAt;
        }

        Dictionary<string, Turn> last = new Dictionary<string, Turn>();

        public FinishedAlerts(IEnumerable<SessionStatus> sessions)
        {
            // Keep the states read at launch, without timing turns already in progress.
            foreach (SessionStatus s in sessions) last[s.SessionId] = new Turn { State = s.State };
        }

        public List<string> Check(IEnumerable<SessionStatus> sessions, bool enabled, bool quiet, Func<bool> appInFront)
        {
            var texts = new List<string>();
            var current = new Dictionary<string, Turn>();
            foreach (SessionStatus s in sessions)
            {
                Turn before;
                last.TryGetValue(s.SessionId, out before);
                var turn = new Turn { State = s.State };
                if (s.State == States.Waiting) turn.StartedAt = before.StartedAt;
                else if (s.State == States.Working)
                    turn.StartedAt = before.State == States.Working || before.State == States.Waiting ? before.StartedAt : s.At;
                else if (s.State == States.Done && enabled && !quiet && before.StartedAt > 0 && s.At >= before.StartedAt)
                {
                    long minutes = (s.At - before.StartedAt) / MinuteMs;
                    if (minutes >= 5 && !appInFront())
                    {
                        string project = HookEvents.ProjectName(s.Project);
                        texts.Add((project != "" ? project : "Claude") + " finished after " + minutes + " min");
                    }
                }
                current[s.SessionId] = turn;
            }
            last = current;
            return texts;
        }
    }
}
