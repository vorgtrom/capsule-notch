# What's new in Capsule

Each version's notes, newest first. The [Releases page](https://github.com/vorgtrom/capsule-notch/releases) has the
download for each one. A release takes its notes from its section here.

## Unreleased

- **How long you'll be locked out.** When a limit will fill before it resets, the hover card says for how long:
  "At this pace: full at 4:10 PM · locked 41 min".
- **Even pace while there's no forecast yet.** Before Capsule has seen enough of a limit rise to forecast it, the
  hover card compares your use with spreading the limit evenly over its window: "81% of the window gone · 1.1× even
  pace". Above 1.0×, keeping up the same average would use it up before the reset.

## v1.6.0

- **Review edits and plans from the capsule.** A file edit shows its change line by line, red for what's taken out and
  green for what's put in; a new file shows all of its lines. A plan shows in full, with its headings, lists and code.
  Allow or deny an edit, approve a plan, or type feedback and send Claude back to planning. Anything too long to show
  whole still goes to Claude's own prompt.
- **Richer session rows.** Each session shows the start of your last prompt and what Claude did last, such as
  "Edited middleware.ts".
- **Sounds.** A chime when a session needs you, a softer one when it finishes. Untick **Sounds** in the menu to turn
  them off.
- **The Codex ring pulses amber again** while Capsule holds a Codex / Work request. It had stopped since requests came
  to the capsule.
- **Usage history counts more carefully.** A forked Codex chat's copied turns count once, while separate chats with
  turns of the same size each count; archived Codex chats are counted; archiving a chat no longer counts it twice; and a
  log that is busy during the first count keeps its older days, however long it stays busy.
- **Forecasts hide when the numbers are stale.** After a failed usage check, the hover card and the Usage page show no
  "full at" forecast until fresh numbers arrive.

## v1.5.0

- **Music on the notch.** Show Spotify and YouTube Music playback through Windows media sessions, with artwork,
  track details, progress, previous/play/pause/next, and a source switcher on the hover card and full panel.
  Browser players use their browser's name. No extra sign-in, API key, or saved listening history. The Music cell
  shows only while something plays (and for 2 minutes after a pause).
- **Usage history.** Click the Claude or Codex tile for tokens per day by model, for a week, a month or a year, read
  from the logs Claude Code and Codex already write on your PC. Cache reads are shown apart, in Details.
- **Pace.** The hover card and the Usage page say when a limit will be full at the current pace, or about how full
  it'll be at its reset.
- **Limit alerts** at 80% and 95%, and when you're on pace to run out before the reset. Untick **Limit alerts** in
  the menu to turn them off.
- **Install with Scoop**: `scoop bucket add capsule https://github.com/vorgtrom/scoop-capsule`, then
  `scoop install capsule/capsule`. It picks up each new release by itself.

## v1.4.1

- **Connect to Codex / Work** now refuses an install folder whose path holds `&`, `<`, `>`, `(`, `)`, `@`, `^` or `|`,
  and says why. Windows' `cmd`, which launches the hook, misreads those: in a folder such as `Capsule (1)` the hook
  failed without a word and Codex asked as usual, and a `&` split the path into two commands. Rename the folder and
  connect again.
- A release whose version has no section here now publishes with GitHub's list of merged pull requests, instead of
  failing.
- README: Codex on **Full access** never asks, so nothing reaches the capsule; uninstalling now covers Codex; what
  the Codex connection reads and changes.

## v1.4.0

- Connect Codex and local ChatGPT Work to approve commands and answer questions from the Codex ring.
- Command cards show the command, working folder and reason. Choose Allow, Deny, or Answer in Codex / Work.
- Answer with an option or type your own text. Free-text-only questions also work.
- Claude and Codex requests use separate queues. Stale card actions cannot affect another request.
- Connect backs up the host's hooks.json and preserves other hooks. Review and trust Capsule's hooks in the host,
  then start a new local chat.
- Existing timeout and foreground-window fallbacks apply. File edits, plans, secret questions and wider grants
  stay in the host. Cloud orchestration and ordinary ChatGPT web chats are outside this connection.

Question answers reach the model as hook feedback, rather than a native structured answer record. Command
decisions run before the host's normal approval reviewer. See the README for setup and limits.

## v1.3.0

**Usage you can trust at a glance.**

- The ring and the panel show whichever limit is closest to full. A weekly limit at 99% can no longer hide behind a
  5-hour session at 10%. The tile names the limit it shows.
- Each usage tile says where its numbers came from and how old they are, such as "API · 2 min ago" or "Logs · 3 hr
  ago". Numbers from Codex's logs, or kept after a failed check, dim once they're over ten minutes old.
- The first launch asks whether Capsule should start with Windows. **No** is the default; change it later in the menu.
- The calendar link option now says it's read-only: no Google Tasks, and no adding, editing or deleting events.
- Usage bars keep their proportions when the panel scrolls, and pages stay inside the panel's rounded corners.

Includes everything in v1.2.1 and v1.2.2.

## v1.2.2

**Settings stay open while you copy things.**

- Settings, and an open add or edit form on the month page, stay open when you switch to another app, so you can copy
  a Notion token or a Google client ID without losing what you typed. **Esc** and **Back** still close them.
- The panel, settings and month page fit your screen and scroll when they're taller. Their headers stay in view.

## v1.2.1

**Safer approvals.**

- The capsule only offers **Allow** for what it can show in full: Bash and PowerShell commands up to 4,000 characters,
  and Claude's questions. File edits, plans, other tools, longer commands, and commands with hidden control
  characters go to Claude's own prompt instead, so you never approve something you couldn't see.
- Hover a command for its exact text, spaces and line breaks included. **Always allow** shows the full rule it would
  add, in a scrollable box.

## v1.2.0

**Update notices.**

- A downloaded Capsule checks once a day whether a newer release is out. When one is, you get a balloon and the
  menu's first item opens its download page. **Check for updates** in the menu turns this off. It asks GitHub only
  for the newest version number and sends nothing about you.
- Builds from source are labelled "dev" and never check; you update them with `git pull`.

## v1.1.0

**The month page, faster to use.**

- Keys: ← → move a day, ↑ ↓ a week, Page Up and Page Down change the month. In the add or edit form, **Enter** saves
  and **Esc** cancels.
- A row's pencil and bin appear when you hover it.
- An edited event shows its new title and times at once, and goes back if Google refuses.
- If the Google Tasks API is off in your Cloud project, the page says to turn it on, instead of asking you to sign in
  again.
- Ticking a task while a form is saving no longer closes the form.

## v1.0.0

**The first downloadable release.**

- **Usage:** a ring each for Claude Code and Codex on the edge of your screen, with every limit and when it resets on
  hover.
- **Claude Code sessions:** see when one is working or waiting on you, and answer Claude's questions and permission
  prompts from the capsule.
- **Google Calendar:** your next event on the capsule, today's and tomorrow's events and tasks in the panel and on
  hover, and a month page where you add, edit and delete events and Google Tasks.
- **Ideas:** jot an idea with **Ctrl+Alt+N** and it lands in your Notion database.
- Every release is built and tested on Windows by GitHub, with the zip's SHA-256 next to it.
