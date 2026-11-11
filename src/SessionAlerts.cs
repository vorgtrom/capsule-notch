using System;
using System.Collections.Generic;

namespace Capsule
{
    // Two heads-ups from a session's context and cache (SessionContext): once when its context passes 90%, before Claude
    // Code compacts it, and once when a session waiting on you has 5 minutes of its hour-long cache left, since an answer
    // after that re-reads the whole conversation. Each fires once: the context one again only after the session has
    // dropped below 80% (a compaction), the cache one again only after a newer reply.
    public sealed class SessionAlerts
    {
        public const int AlertPercent = 90, RearmPercent = 80;
        public const long CacheWarnMs = 5 * 60 * 1000;

        readonly HashSet<string> contextWarned = new HashSet<string>();
        readonly Dictionary<string, long> cacheWarned = new Dictionary<string, long>();   // session -> the reply it was warned for

        public List<string> Check(IEnumerable<SessionStatus> sessions, Func<string, SessionUse> use, long now, bool defer = false)
        {
            // During Focus, track which warnings can fire again without marking new ones as delivered.
            var texts = new List<string>();
            var live = new HashSet<string>();
            foreach (SessionStatus s in sessions)
            {
                if (s == null || string.IsNullOrEmpty(s.SessionId)) continue;
                live.Add(s.SessionId);
                SessionUse u = use(s.SessionId);
                if (u == null) continue;
                string name = s.Project != "" ? s.Project : "A Claude session";

                int percent = u.ContextPercent;
                if (!defer && percent >= AlertPercent && contextWarned.Add(s.SessionId))
                    texts.Add(name + " is at " + percent + "% of its context. Claude Code will compact it soon.");
                else if (percent >= 0 && percent < RearmPercent) contextWarned.Remove(s.SessionId);

                long left = u.LastReplyMs + u.CacheTtlUsedMs - now;
                long warnedFor;
                if (!defer && s.State == States.Waiting && u.LastReplyMs > 0 && u.CacheTtlUsedMs >= SessionContext.HourMs && left > 0 && left <= CacheWarnMs
                    && !(cacheWarned.TryGetValue(s.SessionId, out warnedFor) && warnedFor == u.LastReplyMs))
                {
                    cacheWarned[s.SessionId] = u.LastReplyMs;
                    long minutes = Math.Max(1, (left + 59999) / 60000);
                    texts.Add(name + " is waiting on you, and its prompt cache goes cold in about " + minutes + " min. Answering before then saves re-reading the whole conversation.");
                }
            }
            contextWarned.RemoveWhere(id => !live.Contains(id));
            foreach (string id in new List<string>(cacheWarned.Keys)) if (!live.Contains(id)) cacheWarned.Remove(id);
            return texts;
        }
    }
}
