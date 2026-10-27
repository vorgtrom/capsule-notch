# Capsule capture design

Date: 23 October 2026
Status: written design approved by the user on 23 October 2026
Base: main at 5c7f51be6c2416b05730351d4e55394896f365de
Branch: codex/capture-scroll

## Intended result

The user wants a normal screenshot tool and scrolling capture in Capsule. Scrolling capture should produce one tall image of a webpage, instead of several separate screenshots.

The user approved the concept and asked to build it. This document makes the implementation choices reviewable. It does not record approval of a built feature.

Success means a user can select a visible area, capture it once or follow it down a webpage, inspect the resulting image, crop it, and explicitly copy or save it. Capture must be cancellable. An incomplete or uncertain result must be identified.

## Scope of the first version

- Screenshot mode captures a rectangular area of the desktop.
- Scrolling capture targets the webpage already open in a browser. Initial desktop validation covers Edge and Chrome.
- The user selects the webpage's visible content area, excluding browser toolbars and unrelated page panels.
- Scrolling capture works vertically at the current browser width and zoom. It does not stitch horizontal scrolling.
- Preview supports pan, zoom, crop, Copy, Save PNG, Retake, and Close.
- A Capture action is available from the Capsule menu and panel. A separate configurable shortcut opens the same mode chooser.
- Existing Ideas and focus shortcuts keep their actions. The default proposed capture shortcut is Ctrl+Alt+S. A conflict leaves menu access available and shows an explanation.
- Copy works even when clipboard history is off. Capture never enables clipboard history.
- No image is saved automatically. Screen content, browser addresses, and captured pixels are not logged or uploaded.

Other scrollable apps, PDF-specific capture, video, OCR, annotations, cloud sharing, and a new capture ring are outside this version.

## Approach

Use native desktop capture and combine overlapping images as the browser scrolls. Work on the page the user already opened. Do not launch a second browser, copy cookies, ask for a URL, or require remote debugging.

| Approach | Tradeoff | Choice |
|---|---|---|
| Native selected-area capture and scrolling | Works with the current signed-in page and the existing Windows app. Needs careful overlap matching and handling of sticky elements. | First version |
| Browser extension | Gives access to page geometry, but adds a separate install, permissions, and browser integration to maintain. | Deferred |
| Separate browser or debugging connection | Can expose browser screenshot APIs, but does not transparently reuse an ordinary tab without setup. | Deferred |

Windows already supports bitmap screen capture through Graphics.CopyFromScreen. Its input API can send scroll events, but input can be blocked by Windows integrity boundaries. Treat failures as unsupported capture, not a request to elevate Capsule.

Sources:
- [Microsoft Graphics.CopyFromScreen](https://learn.microsoft.com/en-us/dotnet/api/system.drawing.graphics.copyfromscreen?view=netframework-4.8.1)
- [Microsoft SendInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput)
- [Chrome DevTools Protocol page capture](https://chromedevtools.github.io/devtools-protocol/tot/Page/)

## User flow

1. Open Capture from the menu, the panel, or its shortcut.
2. Choose Screenshot or Scrolling capture.
3. Hide Capsule's panel, hover card, notch, and capture chooser before taking source images. Wait for the overlay to leave the composed desktop before reading pixels.
4. Draw a rectangle over the desired content. Escape cancels. Keyboard instructions and a visible focus state accompany the selection.
5. Screenshot mode captures that rectangle and opens the preview.
6. Scrolling mode identifies the browser window beneath the rectangle, waits for shortcut modifiers to be released, and brings that same window to the front.
7. Display a small progress control outside the captured rectangle, with Stop and Cancel. If no safe outside space exists, exclude the control from each source frame by hiding it before capture.
8. Scroll to the top of the selected page area with a bounded sequence of upward wheel events. Do not click page content or send arbitrary text/navigation shortcuts.
9. Capture successive overlapping sections while scrolling down.
10. Stop when the page stops moving, the user selects Stop, a limit is reached, or a failure makes further stitching unsafe.
11. Show the image and the reason capture stopped. Copy or Save acts only when selected.

The page visibly scrolls during capture and may remain at its final position. The first version must not promise restoration of the browser's scroll position. Restore Capsule visibility according to the current setting and fullscreen state when capture ends.

## Capture and matching rules

- Use physical pixel rectangles for source images. Convert WPF selection coordinates through the selected monitor's actual scale.
- Restrict one capture to one monitor. A selection crossing monitors is refused with a short explanation before scrolling starts.
- Keep the selected browser window and content rectangle fixed. Cancel if the browser is closed, minimized, moved, resized, or replaced as the foreground window.
- Scroll only after a source frame is captured. Never assume that a wheel event moved a fixed number of pixels.
- Match the content overlap between consecutive frames, then append only newly revealed rows.
- Require a clearly preferred match across multiple horizontal sample areas. A uniform or repetitive section must not cause a guessed join.
- Detect unchanged top or bottom bands separately from moving content. Keep a fixed header once at the start and a fixed footer once at the end when the boundaries can be established reliably.
- Wait for a stable frame after scrolling, with a bounded wait for animation or lazy loading. Local moving elements must not justify an incorrect seam.
- If content shifts horizontally, overlap is ambiguous, or the next section cannot be matched, stop. Offer the verified portion as a partial capture, with an explanation.
- A page that stops changing is a stop signal, not proof that every item in an infinite feed was loaded. Avoid an unconditional "entire page captured" success label.
- Stop at the first of 32 million output pixels, 32,000 output rows, 120 accepted source frames, or 90 seconds. Check limits before allocating or appending.
- Reaching a limit opens a partial-result preview. The user chooses whether to keep it.
- Screenshot capture does not scroll or inject clicks.

## Cancellation and ownership

One capture session can run at a time. Escape and Cancel discard the in-progress capture. Stop keeps the verified portion and opens preview.

Cancel on foreground-window change, so Capsule does not keep scrolling after the user returns to another task. Pause scroll injection when the pointer leaves the selected content area. Keep the progress controls usable while paused, and show how to resume by returning the pointer. Progress controls must not steal focus from the target browser. Cancellation must stop future input before doing disposal or UI restoration.

Use an explicit state flow: Choosing, Selecting, Capturing, Previewing, Closed. Closing Capsule cancels capture before releasing native resources. Dispose frame buffers and native bitmap handles in success, cancellation, and failure paths.

A held AI approval stays subject to its existing timeout and host fallback. Capture must not approve, answer, or extend it. Hiding Capsule while capture runs must not leave invisible approval requests held.

## Preview, crop, export, and clipboard

Use a separate preview window so a tall image does not expand the main Capsule panel. Match Capsule's fonts and theme, and use actual keyboard-operable buttons.

Fit the image initially, with 100% zoom and pan available for seam inspection. Crop uses original image pixels rather than the reduced preview. Retain the original only while preview is open, to allow resetting the crop.

Save PNG uses a file picker and overwrite confirmation. Report a locked or unwritable file without discarding the preview. Close releases pixels and leaves no temporary capture file.

Copy uses the existing STA clipboard path. Only an explicit Copy writes an image to the system clipboard. If clipboard history is enabled, route that copy through its existing history policy without inserting a second duplicate.

The current ClipboardHistory.FromImage thumbnail references the full source image. Before allowing captured images into history, create an independent thumbnail. Limit one history image to 16 million pixels and 8 MiB of PNG data, and all stored PNGs together to 32 MiB. Preserve the existing five-image maximum. An oversized image may remain available for explicit Copy or Save while being skipped by Capsule's own history. Apply this policy to the clipboard watcher too, so it cannot reinsert a skipped capture. Do not turn this into an unrelated clipboard redesign.

## Integration points

Keep pixel matching independent of desktop input and WPF windows so it can be tested with synthetic frames.

Expected code areas:
- New capture session and stitcher classes under src/.
- New native capture adapter with explicit ownership of bitmap resources.
- New WPF area-selection, progress, and preview views.
- Controller.cs for menu/panel entry points, shortcut registration, lifecycle, and temporary visibility.
- PanelView.cs for the Capture action and shortcut setting.
- Settings.cs for the capture shortcut only. Capture pixels and session state are not persisted.
- Hotkey.cs reused for the additional shortcut without changing the existing shortcut.
- ClipboardHistory.cs for safe thumbnail ownership and bounded image insertion.
- Tests.cs and new capture test groups, following the existing assertion harness.
- Preview.cs, README.md, CHANGELOG.md, and privacy documentation for the new user flow and limits.

Use the existing .NET Framework/C# 5 build and built-in Windows/WPF APIs. No new browser runtime or package is required by this design.

## Required checks before calling the feature ready

Automated:
- Known-offset frames produce an exact combined image without duplicate or missing rows.
- Partial final scroll, top/bottom fixed bands, repeated content, blank regions, failed overlap, and horizontal movement.
- Frames beyond pixel, row, time, or count limits stop before allocation.
- Stop keeps a partial result; Cancel discards it; both prevent further scroll input.
- Focus loss and target-window changes cancel. Fake native adapters prove no later input is sent.
- Selection and crop coordinates at 100%, 125%, 150%, and 200% scaling.
- Screenshot mode sends no scroll events.
- Shortcut conflicts and existing shortcut behavior.
- Preview keyboard actions, crop/reset, Copy failure, save failure, and overwrite confirmation.
- Clipboard history disabled stays disabled. Enabled history receives at most one item and keeps an independent bounded thumbnail.
- Capture hides/restores Capsule and preserves AI host fallback behavior.
- Existing suite passes through build.cmd dev. Use isolated data and pipe names.

Desktop:
- Edge and Chrome with finite local test pages containing known first, middle, and final markers.
- Short page, long page, sticky header/footer, lazy-loaded images, nested scrolling area, and an infinite feed.
- Compare output dimensions and known markers, then inspect seams at 100% zoom.
- Cancel during capture and switch away while capture is active.
- Test actual monitor scaling and native preview/copy/save behavior in the patched build.

Use local generated pages and synthetic content first. Do not capture a user's private desktop or signed-in content automatically for tests.

## Review and delivery

Prepare the implementation on codex/capture-scroll. Run the isolated dev build and desktop checks. Present the diff, test evidence, and a build the user can try.

Do not merge a PR or replace the user's installed Capsule without the user's separate instruction.
