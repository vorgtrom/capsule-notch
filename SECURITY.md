# Security

Capsule reads the Claude and Codex sign-in files on your PC and keeps your Notion and Google secrets, encrypted with
Windows (DPAPI), in `%USERPROFILE%\.capsule`. [What it reads, and where it connects](README.md#what-it-reads-and-where-it-connects)
lists every file it reads and every address it calls.

## Reporting a vulnerability

Please don't open a public issue. Use **Report a vulnerability** on the repository's
[Security tab](https://github.com/vorgtrom/capsule-notch/security), which reaches the maintainer privately.

Include what you found, the steps to reproduce it, and the Capsule version (tray menu, or the release you downloaded).
Once a fix is released, the CHANGELOG credits the report unless you'd rather it didn't.

## Supported versions

Only the latest release gets fixes. Update from the tray menu or with `scoop update capsule`.
