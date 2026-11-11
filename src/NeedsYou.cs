using System;
using System.Collections.Generic;

namespace Capsule
{
    internal sealed class PendingReview
    {
        public string Provider, Project, Type;
        public int Id, Queued;
        public long Arrived;

        public string ProviderName { get { return Provider == "codex" ? "Codex / Work" : "Claude"; } }

        public string Wait(long now)
        {
            long seconds = Math.Max(0, now - Arrived) / 1000;
            return seconds < 60 ? "Waiting " + seconds + " s" : "Waiting " + seconds / 60 + " min " + seconds % 60 + " s";
        }
    }

    internal sealed class NeedsYouTile
    {
        public readonly List<PendingReview> Requests = new List<PendingReview>();
        public GitHubChecks GitHub;
        public long Now;

        public static NeedsYouTile From(PromptBroker claude, PromptBroker codex, GitHubChecks checks, long now)
        {
            var result = new NeedsYouTile { GitHub = checks, Now = now };
            Add(result, "claude", claude);
            Add(result, "codex", codex);
            result.Requests.Sort((a, b) => a.Arrived == b.Arrived ? a.Id.CompareTo(b.Id) : a.Arrived.CompareTo(b.Arrived));
            return result;
        }

        static void Add(NeedsYouTile tile, string provider, PromptBroker broker)
        {
            HeldPrompt held = broker == null ? null : broker.Current;
            if (held == null || held.Request.Provider != provider) return;
            string project = PromptCardModel.Safe(held.Request.Project);
            tile.Requests.Add(new PendingReview
            {
                Provider = provider,
                Project = project.Length == 0 ? "Unknown project" : PromptCardModel.Shorten(project, 80),
                Type = held.IsPlan ? "Plan review" : held.IsQuestion ? "Question" : "Approval",
                Id = held.Id,
                Queued = broker.Count - 1,
                Arrived = held.Arrived,
            });
        }

        // Run the same expiry and foreground checks the broker normally runs on its dispatcher timer.
        internal static HeldPrompt Review(PendingReview row, PromptBroker claude, PromptBroker codex, long now)
        {
            PromptBroker broker = row.Provider == "claude" ? claude : row.Provider == "codex" ? codex : null;
            if (broker == null) return null;
            broker.Tick(now);
            HeldPrompt held = broker.Current;
            return held != null && held.Id == row.Id && held.Request.Provider == row.Provider ? held : null;
        }
    }
}
