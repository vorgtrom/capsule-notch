using System.Collections.Generic;
using System.Globalization;

namespace Capsule
{
    // Everything the panel shows, tile by tile, in a fixed order: Claude, Codex, Sessions, Calendar, Ideas.
    public sealed class PanelModel
    {
        public UsageTile Claude;
        public UsageTile Codex;
        public SessionsTile Sessions;
        public CalendarTile Calendar;   // null: no Calendar tile
        public IdeasTile Ideas;
    }

    // What the panel's Claude or Codex tile shows (spec §2), as plain text and numbers. Built here; drawn by PanelView.
    public sealed class UsageTile
    {
        public string Provider = "";
        public string Title = "";
        public string Plan = "";
        public string Percent = "–";
        public double Used;
        public string Color = Palette.Green;
        public bool ShowBar;
        public string ResetText = "";
        public string Note = "";     // why numbers are old or missing
        public bool Dimmed;
        public bool SignIn;          // Claude needs signing in: the tile shows a Sign in button
        public bool Missing;         // Codex isn't set up on this PC

        public static UsageTile From(Reading r, long now, CultureInfo culture)
        {
            var t = new UsageTile();
            t.Provider = r.Provider;
            t.Title = r.Provider == "codex" ? "Codex" : "Claude";
            t.Plan = r.Plan;
            if (r.Status == "signin")
            {
                t.SignIn = r.Provider == "claude";
                t.Note = r.Note != "" ? r.Note : "Sign in needed";
                return t;
            }
            if (r.Status == "none")
            {
                t.Missing = r.Provider == "codex";
                t.Note = t.Missing ? "Codex isn't set up on this PC" : "Checking…";
                return t;
            }
            LimitWindow headline = r.Headline;
            t.Percent = Format.Percent(headline);
            if (headline != null)
            {
                t.ShowBar = true;
                t.Used = headline.Used;
                t.Color = Palette.ForUsed(headline.Used);
                t.ResetText = headline.Note != "" ? headline.Note : Format.ResetText(headline.ResetsAtMs, now, culture);
            }
            t.Note = r.Note;
            t.Dimmed = r.IsDimmed(now);
            return t;
        }
    }

    // One line of the Ideas tile.
    public sealed class IdeaRow
    {
        public const string Saving = "saving", Waiting = "waiting", Saved = "saved", Rejected = "rejected";
        public string Text = "";
        public string State = "";   // one of the above, or "" for an idea read back from Notion
        public string Url = "";     // its Notion page, once it has one
    }

    // What the panel's Ideas tile shows (spec §2 and §4).
    public sealed class IdeasTile
    {
        public List<IdeaRow> Rows = new List<IdeaRow>();   // at most 5, newest first, except that a refused idea always leads
        public bool Configured;    // a Notion secret and database are set
        public int Waiting;        // ideas Notion hasn't confirmed yet
        public string Note = "";       // what went wrong, or how to set up: one line per thing to say
        public string DiskNote = "";   // ideas that aren't safe on disk yet, a line per reason: always amber (polish spec §4.3)
        public bool CanDiscard;        // Notion refused a waiting idea (it is the first row): offer to drop it
    }

    // What the panel's Sessions tile shows: the card's rules, up to 5 rows and then "+N more".
    public sealed class SessionsTile
    {
        public List<SessionRow> Rows = new List<SessionRow>();
        public int More;
        public string Hint = "";   // instead of rows: hooks not connected, or nothing to show

        public static SessionsTile From(IList<SessionStatus> sessions, bool hooksConnected, long now)
        {
            var t = new SessionsTile();
            if (!hooksConnected)
            {
                t.Hint = "Connect to Claude Code (tray menu) to see your sessions here";
                return t;
            }
            int more;
            t.Rows = SessionRow.ListFor(sessions ?? new List<SessionStatus>(), now, out more);
            t.More = more;
            if (t.Rows.Count == 0) t.Hint = "No active sessions";
            return t;
        }
    }
}
