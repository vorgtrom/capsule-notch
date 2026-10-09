# What's new in Capsule

Each version's notes, newest first. The [Releases page](https://github.com/vorgtrom/capsule-notch/releases) has the
download for each one. A release takes its notes from its section here.

## Unreleased

- **Connect to Codex / Work** now refuses an install folder whose path holds `&`, `<`, `>`, `(`, `)`, `@`, `^` or `|`,
  and says why. Windows' `cmd`, which launches the hook, misreads those: in a folder such as `Capsule (1)` the hook
  failed without a word and Codex asked as usual, and a `&` split the path into two commands. Rename the folder and
  connect again.
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
