# Setting up Capsule's Google sign-in

For the maintainer. This gives release builds a one-click **Sign in with Google** button. It's done once, in the Google
account that owns the Capsule sign-in app (exxojamesyap@gmail.com), plus two GitHub secrets. Nothing secret goes in
this repository: release builds get the client ID and secret from the GitHub secrets at build time.

## 1. The Google Cloud project

1. Open the [Google Cloud console](https://console.cloud.google.com/) signed in as exxojamesyap@gmail.com, and create
   a project named **Capsule**.
2. **APIs & Services → Library:** enable **Google Calendar API** and **Google Tasks API**.

## 2. Branding and audience

Open **Google Auth Platform** (it was called OAuth consent screen) and click **Get started**.

1. **App name:** Capsule. **User support email:** your email. **Audience:** External. **Developer contact:** your email.
2. **Branding:**
   - **Application home page:** `https://vorgtrom.github.io/capsule-notch/`
   - **Privacy policy:** `https://vorgtrom.github.io/capsule-notch/privacy.html`
   - **Authorized domain:** `vorgtrom.github.io`
   - No logo. A logo makes Google review the branding before anything else.
3. **Audience → Publish app**, so the status is **In production**. In Testing, sign-ins end every 7 days.

Google wants the authorized domain verified in [Search Console](https://search.google.com/search-console) by the same
account. Add the URL-prefix property `https://vorgtrom.github.io/capsule-notch/` and choose the **HTML file** method:
put the file Google gives you in this repo's `docs` folder and merge it, then click Verify. If Google insists on the
whole domain `vorgtrom.github.io`, create a repository named `vorgtrom.github.io` on the vorgtrom account, put the
file at its root, and verify that instead.

## 3. Permissions (scopes)

**Data Access → Add or remove scopes**, then add these three and **Save**:

| Scope | What Capsule uses it for (paste as the justification) |
|---|---|
| `https://www.googleapis.com/auth/calendar.events` | Shows the user's upcoming events on the capsule and in its panel, and adds, edits or deletes an event only when the user does so on Capsule's month page. |
| `https://www.googleapis.com/auth/calendar.calendarlist.readonly` | Lists the user's calendars so they can choose which ones Capsule shows. Read-only. |
| `https://www.googleapis.com/auth/tasks` | Shows the user's Google Tasks with their events, and adds or ticks a task only when the user does so on Capsule's month page. |

All three are "sensitive" scopes (not "restricted"), so Google's review is a brand and scope review with a demo video,
without a security assessment.

## 4. The client, and the two GitHub secrets

1. **Clients → Create client → Desktop app**, named **Capsule**. Copy the **Client ID** and the **Client secret**.
2. Add them to the repository as GitHub Actions secrets. Either in the browser, under the repo's **Settings → Secrets
   and variables → Actions → New repository secret**, or in a terminal, where each command asks for the value:

```bash
gh secret set CAPSULE_GOOGLE_CLIENT_ID --repo vorgtrom/capsule-notch
```

```bash
gh secret set CAPSULE_GOOGLE_CLIENT_SECRET --repo vorgtrom/capsule-notch
```

The next release then has the button. The Release workflow's log says "Built with Capsule's Google client." The
values never appear in the log: GitHub masks them.

## 5. Google's review

In **Verification Center**, submit the app. Google asks for:

- **The scope justifications** from the table above.
- **A demo video** (an unlisted YouTube link), showing, in this order:
  1. Capsule's settings, and a click on **Sign in with Google**.
  2. The whole Google consent screen, with the app name, and the browser's address bar showing the `client_id`.
  3. Allowing access, then Capsule's settings showing "Signed in as …" and a box for each calendar
     (calendarlist.readonly).
  4. The capsule and panel showing upcoming events (calendar.events, read).
  5. On the month page: adding an event, editing it and deleting it (calendar.events, write).
  6. On the month page: a task showing, adding a task and ticking it (tasks).
  7. **Sign out** in settings.
- **The privacy policy and home page** links from step 2.

Review usually takes a few days to a few weeks, and Google may email questions. Until it's approved, users see an
"unverified app" warning and click **Advanced → Go to Capsule**, and at most 100 people can sign in. The README says so.
