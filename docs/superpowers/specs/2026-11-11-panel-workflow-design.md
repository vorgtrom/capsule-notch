# Capsule panel workflow design

Add three features to Capsule's Windows panel. Put agent requests and build failures together, give files a place to wait, and let users choose what the panel shows first.

## Approaches

- Extend the existing panel. The approval cards, capture preview, clipboard tile and settings already have owners. New panel tiles can reuse them and keep the existing shortcuts.
- Separate windows or rings would make the features easier to find from the screen edge, but add focus and layout work and separate builds from agent requests.

## Panel opening preference

Settings offers Default, Calendar and Clipboard in the first PR. Add Needs you when its tile ships. Move the selected visible section to the first row and keep the remaining sections in their usual order. If the selected section is hidden or unavailable, use the default order. Keep the two usage tiles together.

Save the choice in the existing settings file. Treat an absent or unrecognized value as Default. Return the panel to the top on an ordinary open. The Ideas shortcut still focuses the Ideas field and scrolls to it.

## Needs you and GitHub checks

Show the current request for each enabled provider, oldest first across providers. Each row shows the provider, project, request type and wait time. A count shows how many other requests that provider holds. While any request is pending, put Needs you first regardless of the saved opening preference.

The tile has an agent section and a GitHub section. If only agents have pending items, show their rows and the GitHub status if monitoring is on. If only GitHub has items, show "No pending requests" and the GitHub rows. With neither, show "No pending requests" and, if enabled, the GitHub status. The GitHub section is absent when monitoring is off. Hide the tile when it is empty and both agent rings and GitHub checks are off, unless Needs you is the saved opening section.

Review opens the existing approval card. Before opening it, run the broker's timeout and foreground checks on the UI thread. Recheck the provider and request ID. If the request expired or advanced, refresh the panel and show that the row is no longer pending. A click must never open the next request in its place. Preserve provider ring settings, broker queue order, timeouts and host fallback. Store no prompt text or approval history for this feature.

GitHub monitoring starts off. Settings accepts one public GitHub repository in owner/repo form. Private repositories are unsupported because the check uses no sign-in. Query at most 30 open PRs and 30 recent Actions PR runs per scan. Run both requests off the UI thread. Space scans for the same repository at least three minutes apart. A repository change skips that interval, but waits for an active scan and honors server backoff. Send If-None-Match with the stored ETag for each endpoint. On 304, reuse its cached result. Back off after rate limits and discard results and validators if the repository changes mid-request. Explain unavailable or incomplete results in the tile. The feature reads public metadata from api.github.com.

Match runs to the current head of an open PR. Use a run's PR association when available. Capsule's live Actions responses can omit that association. In that case, require a matching head repository, branch and SHA that identify one PR. Select the latest run for each PR and workflow before filtering failures. Count failure, timed_out and startup_failure as failures. Do not show cancelled or action_required as failures. A rerun in progress or a later success clears an old failure. When the run window is truncated, use the earliest matched current-head run to assess coverage. If there is no matched run, use the PR's creation time. If that time falls at or before the oldest visible run, show "couldn't see the latest run" for that PR. Do not show a stale failure or imply a clean result. Open the run with a link built from the configured repository and numeric run ID. Show the last successful check time.

## Shelf

Drag files from Explorer over the capsule to open the panel. Keep it open until the drop completes or the drag leaves. Drop files onto the Shelf tile. The shelf holds references and leaves the original files in place. Drag one row or the whole shelf into another app. Use Windows file drop with the copy effect. Keep the panel open through the drag. Explain when a referenced file is no longer available. If an outbound drop fails, explain that Windows blocks drops into apps running as administrator. Windows can also report cancellation as a failed drop, so do not claim elevation was detected unless there is evidence.

Keep on shelf in Capture Preview exports the current image, including any crop, as an app-owned temporary PNG. Use the existing PNG export path. Reserve space for up to 12 shelf entries and 64 MiB of generated PNGs for the session. Reject and clean up an export that exceeds the remaining space. Keep generated files available until Capsule exits so the destination can finish reading them. Removing a shelf row removes it from the visible list. On normal exit, delete only the files Capsule staged. A crash can leave staged PNGs in the Windows temporary directory.

The shelf is empty on restart. It does not include screenshot markup, an upload service or file monitoring.

## Implementation and acceptance

1. Panel opening preference. Check config round-trip, hidden tile fallback, normal reopen and Ideas shortcut focus.
2. Needs you and GitHub checks. Check provider isolation, expiry, stale clicks, priority and both single-section states. Use saved responses for automated checks of association, forks, shared SHA, changed heads, closed PRs, truncation, reruns, conclusions, conditional requests, outages and repository changes. Automated tests never contact GitHub. Verify the public response shape once against Capsule's repo and record that manual check in the PR.
3. Shelf. Check original file safety, duplicate drops, missing references, bounds and disposal. In Windows, drag a file from Explorer over the capsule, then drop it onto the Shelf tile. Verify dragging a regular file and a cropped capture out to another app. Check that a rejected drop explains the Windows elevation restriction.
4. For each feature, update README, docs/privacy.html and CHANGELOG under Unreleased. Run the isolated Windows dev suite, open its review PR and verify CI on its final commit. Leave each merge to the owner. Use vorgtrom's GitHub noreply identity for every commit.
