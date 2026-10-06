using System;
using System.Collections.Generic;
using System.Globalization;

namespace Capsule
{
    public sealed class CardRow
    {
        public string Label = "";
        public string ResetText = "";
        public string UsedText = "";
        public string Color = Palette.Green;
        public double Used;
    }

    public sealed class SessionRow
    {
        public string Project = "";
        public string Status = "";
        public string Color = Palette.Secondary;

        // The rows the card and the panel show: waiting first, then working, then recently done or idle, at most
        // Sessions.MaxRows of them. more: how many didn't fit.
        public static List<SessionRow> ListFor(IEnumerable<SessionStatus> sessions, long now, out int more)
        {
            var rows = new List<SessionRow>();
            foreach (SessionStatus s in Sessions.ForCard(sessions, now, out more))
            {
                var line = new SessionRow();
                line.Project = s.Project != "" ? s.Project : "Claude";
                line.Status = Sessions.RowText(s, now);
                line.Color = s.State == States.Waiting ? Palette.Amber : s.State == States.Working ? Palette.Text : Palette.Secondary;
                rows.Add(line);
            }
            return rows;
        }
    }

    // Everything the hover card shows, as plain text and numbers. Built here; drawn by CardView.
    public sealed class CardModel
    {
        public string Provider = "";
        public string Title = "";
        public string Plan = "";
        public List<CardRow> Rows = new List<CardRow>();
        public List<SessionRow> SessionRows;   // null for Codex
        public int MoreSessions;
        public string SessionsHint = "";
        public string Footer = "";

        public static CardModel From(Reading r, IList<SessionStatus> sessions, bool hooksConnected, long now, CultureInfo culture)
        {
            var m = new CardModel();
            m.Provider = r.Provider;
            m.Title = r.Provider == "codex" ? "Codex" : "Claude";
            m.Plan = r.Plan;
            foreach (LimitWindow w in r.Windows)
            {
                var row = new CardRow();
                row.Label = w.Label;
                row.ResetText = w.Note != "" ? w.Note : Format.ResetText(w.ResetsAtMs, now, culture);
                row.Used = w.Used;
                row.UsedText = Format.Percent(w) + " used";
                row.Color = Palette.ForUsed(w.Used);
                m.Rows.Add(row);
            }
            if (r.Provider == "claude")
            {
                m.SessionRows = new List<SessionRow>();
                if (!hooksConnected) m.SessionsHint = "Connect to Claude Code (tray menu) to see when Claude is working";
                else
                {
                    int more;
                    m.SessionRows = SessionRow.ListFor(sessions ?? new List<SessionStatus>(), now, out more);
                    m.MoreSessions = more;
                }
            }
            m.Footer = FooterText(r, now, culture);
            return m;
        }

        static string FooterText(Reading r, long now, CultureInfo culture)
        {
            if (r.Status == "signin") return r.Note != "" ? r.Note : "Sign in needed — click the capsule, then Sign in";
            var parts = new List<string>();
            if (r.DataAtMs > 0) parts.Add(r.FromLogs ? "As of " + AsOf(r.DataAtMs, now, culture) : "Updated " + Format.Ago(r.DataAtMs, now));
            if (r.Note != "") parts.Add(r.Note);
            return parts.Count > 0 ? string.Join(" · ", parts) : "Checking…";
        }

        // The local time of a log snapshot, with its day when that isn't today: the weekday within the
        // last 6 days ("Mon 10:09 PM"), otherwise the short date ("9/21/2026 10:09 PM").
        static string AsOf(long atMs, long now, CultureInfo culture)
        {
            DateTime at = Clock.FromMs(atMs).ToLocalTime();
            DateTime today = Clock.FromMs(now).ToLocalTime().Date;
            string time = at.ToString("t", culture);
            if (at.Date == today) return time;
            return (at.Date > today.AddDays(-7) ? at.ToString("ddd", culture) : at.ToString("d", culture)) + " " + time;
        }
    }
}
