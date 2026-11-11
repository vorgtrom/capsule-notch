# Capsule panel workflow design

Add three features to Capsule's Windows panel. Put agent requests and build failures together, give files a place to wait, and let users choose what the panel shows first.

## Approaches

- Extend the existing panel. The approval cards, capture preview, clipboard tile and settings already have owners. New panel tiles can reuse them and keep the existing shortcuts. This is the proposed approach.
- Give each feature a separate window or ring. This would make each feature easier to find from the screen edge, but add window, focus and layout work. It would also separate builds from agent requests.

## Needs you and GitHub checks

Show the current request for each enabled provider, oldest first across providers. Each row shows the provider, project, request type and wait time. A count shows how many other requests that provider holds. If neither provider has a request, show "No pending requests."

Review opens the existing approval card. Before opening it, run the broker's existing timeout and foreground checks on the UI thread. Recheck the provider and request ID. If the request expired or advanced, refresh the panel and show that the row is no longer pending. A click must never open the next request in its place. Preserve provider ring settings, broker queue order, timeouts and host fallback. Store no prompt text or approval history for this feature.

GitHub monitoring starts off. Settings accepts one public GitHub repository in owner/repo form. Query at most 30 open PRs and 30 recent Actions PR runs per scan. Run both requests off the UI thread, no more than once every three minutes. Back off after rate limits and discard results if the repository changes mid-request. Explain unavailable or incomplete results in the tile. The feature reads public metadata and requires no token.

Match runs to the current head of an open PR. Use a run's PR association when available. Capsule's live Actions responses can omit that association. In that case, require a matching head repository, branch and SHA that identify one PR. Select the latest run for each PR and workflow before filtering failures. Show failed and timed-out runs. A rerun in progress or a later success clears an old failure. Open the run with a link built from the configured repository and numeric run ID. Show the last successful check time.

## Shelf

Drop files from Explorer onto the Shelf tile. The shelf holds references and leaves the original files in place. Drag one row or the whole shelf into another app. Use Windows file drop with the copy effect. Keep the panel open through the drag. Explain when a referenced file is no longer available.

Keep on shelf in Capture Preview exports the current image, including any crop, as an app-owned temporary PNG. Use the existing PNG export path. Reserve space for up to 12 shelf entries and 64 MiB of generated PNGs for the session. Reject and clean up an export that exceeds the remaining space. Keep generated files available until Capsule exits so the destination can finish reading them. Removing a shelf row removes it from the visible list. On normal exit, delete only the files Capsule staged. A crash can leave staged PNGs in the Windows temporary directory.

The shelf is empty on restart. It does not include screenshot markup, an upload service or file monitoring.

## Panel opening preference

Settings offers Default, Needs you, Calendar and Clipboard. Move the selected visible section to the first row and keep the remaining sections in their usual order. If the selected section is hidden or unavailable, use the default order. Keep the two usage tiles together.

Save the choice in the existing settings file. Treat an absent or unrecognized value as Default. Return the panel to the top on an ordinary open. The Ideas shortcut still focuses the Ideas field and scrolls to it.

## Implementation and acceptance

1. Add the GitHub poller and fixtures. Cover empty associations, forks with the same branch name, a shared SHA, a replaced PR head, a closed PR, a rerun, an outage and a repository change during polling. Verify the public response shape against Capsule's repo.
2. Add the shelf model and the capture preview action. Check original file safety, duplicate drops, missing references, bounds and disposal. Verify dragging a regular file and a cropped capture between Capsule and another Windows app.
3. Add both panel tiles and Settings controls. Check provider isolation, expiry and stale clicks. Check that the saved first section preserves the other rows and Ideas focus. Update the README, privacy docs and changelog.
4. Run the isolated Windows development suite. Open the PR and verify GitHub CI on its final commit. Leave the merge for the owner's review.
