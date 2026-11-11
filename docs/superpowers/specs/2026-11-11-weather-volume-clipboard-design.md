# Weather, volume and clipboard categories

Three additions to Capsule's panel: clearer clipboard categories, a volume control in the music tile, and a weather
tile. Each ships as its own PR, in this order. Ideas come from other notch apps; build Capsule's own version in
Capsule's style. Copy no code, names, wording or artwork.

## 1. Clipboard categories (first PR)

The Clipboard tile's filter chips become, in this order: **All**, **Links**, **Images**, **Code**, **Text**.

- Add `ClipKind.Code`. `ClipboardHistory.FromText` checks for a link first, then a colour, then code, then falls
  back to text.
- Remove the Colours chip. A colour copy keeps `ClipKind.Colour` and its swatch, and shows under **Text** and
  **All**. The Text filter matches `Text` and `Colour`.
- Code rows show the first non-empty line in a monospace font (`Cascadia Mono, Consolas`) with a code glyph from
  `PanelView.IconFont`. Clicking copies the whole text back, as for other rows.
- A chip with no matching copies still shows "None of this kind yet", as today.

Code detection is a pure function, `ClipboardHistory.LooksLikeCode(string)`. Classify text as code when any of
these holds:
1. It contains a fenced block (a line starting with three backticks).
2. At least two lines look like code, and they make up more than half of the non-empty lines. Count a line if it meets
   any of these conditions.
   - It ends with `;`, `{`, `}`, `)` or `]`.
   - It starts with a tab or at least two spaces, followed by a non-space character.
   - It starts with a keyword: `def`, `class`, `function`, `const`, `let`, `var`, `import`, `from`, `export`, `return`,
     `if`, `for`, `while`, `public`, `private`, `using`, `namespace`, `package`, `fn`, `#include`, `select`,
     `insert`, `update`, `delete from`, `<?`, or an HTML tag (`<` followed by a letter).
3. It is one line and meets any of these conditions.
   - It starts with a lowercase command: `git`, `npm`, `npx`, `pnpm`, `yarn`, `dotnet`, `cargo`, `docker`, `kubectl`, `curl`,
     `winget`, `scoop`, or `gh`. The lowercase commands `go`, `cat`, `echo`, `cd`, `ls`, `python`, `node`, and `pip`
     also need an argument containing `-`, `/`, `\`, `.`, `=`, or a quote.
   - It starts with a case-sensitive PowerShell `Verb-Noun` cmdlet, using the verb's standard capitalisation and a
     capitalised noun, such as `Get-ChildItem`.
   - It starts with a SQL SELECT and has a FROM clause. Both keywords must be uppercase unless the line contains
     `*` or ends with `;`.
   - It contains `=>`, `();`, `::`, `!=`, `===`, or both `{` and `}`.

Multi-line keyword matches ignore case. Keywords and command names need a word boundary.

Prose isn't code. Tests must cover at least:

| Input | Kind |
|---|---|
| `git commit -m "fix"` | Code |
| `Get-ChildItem -Recurse` | Code |
| `const x = () => 1;` | Code |
| two-line C# method with braces | Code |
| Python `def` with an indented body | Code |
| `SELECT * FROM users WHERE id = 1` | Code |
| `go build ./...`, `cat file.txt`, `python -m venv .venv` | Code |
| A shopping list, street address, or email with a salutation and sign-off | Text |
| `Go home now`, `Cat food is out of stock`, `Python is great`, `echo hello` | Text |
| `NPM install`, `sort-object`, `Set-up the meeting`, `Get-together tonight` | Text |
| `Select one from the list` | Text |
| `Meeting at 3pm; bring notes.` | Text |
| `Let's go for lunch` | Text |
| a short email of three sentences | Text |
| a bulleted list ending with periods | Text |
| `#FF5733` | Colour (shown under Text) |
| `https://example.com/a` | Link |

The README's clipboard section and the CHANGELOG under Unreleased describe the new chips. Nothing changes in
privacy.html: classification runs locally on text Capsule already keeps in memory.

## 2. Volume in the music tile (second PR)

Next to the music tile's playback buttons, add a speaker button and a horizontal slider for the **Windows output
volume**: the master volume of the default playback device, the same as the taskbar's volume.

- The slider runs from 0 to 100. Dragging sets the volume as it moves. The mouse wheel over the slider changes it
  by 2. The arrow keys change it by 2 and Page Up/Down by 10.
- The speaker button mutes and unmutes. Its glyph shows mute, low, medium or high.
- Accessibility names: "Volume" for the slider, with its percentage as the value, and "Mute" or "Unmute" for the
  button.
- The control follows changes made elsewhere: the taskbar, the keyboard's volume keys, or another app. Read the
  level when the panel opens and on the music tile's existing refresh tick. Don't add a new timer while the panel
  is closed.
- Use Core Audio through COM interop: `IMMDeviceEnumerator` → default render endpoint (`eRender`, `eMultimedia`)
  → `IAudioEndpointVolume` (`GetMasterVolumeLevelScalar`, `SetMasterVolumeLevelScalar`, `GetMute`, `SetMute`).
  Pass a null event context. Add no package.
- Get the default device again each time the panel opens, and after any COM error, so a new headset or speaker is
  picked up. With no playback device, hide the volume control.
- Put the interop in its own file, `src/SystemVolume.cs`, behind a small interface. Tests use a fake: the slider
  sets the level, mute toggles, an outside change shows on the next refresh, and the control hides with no device.
- The volume control shows with the music tile, whenever the music tile shows.

Per-app volume, for the player's own app, is out of scope: matching a media session to an audio session isn't
reliable for browser players.

The README's music section describes it, and the CHANGELOG says so under Unreleased. In privacy.html: Capsule
reads and changes Windows' output volume only when you use the control.

## 3. Weather tile (third PR)

A **Weather** tile in the panel, off until the user picks a place in Settings.

### Settings
- A **Weather** section with a **Place** box and **Find**. Find looks the name up with Open-Meteo's geocoding API:
  `https://geocoding-api.open-meteo.com/v1/search?name=<text>&count=5&language=<two-letter UI language>&format=json`.
  Show up to 5 matches as "Name, Region, Country". Picking one saves the place.
- Save the place's label and its latitude and longitude, rounded to 2 decimals (about 1 km), in config.json:
  `weather_place`, `weather_lat`, `weather_lon`. Nothing else about the location is stored or sent.
- **Units:** Automatic, °C or °F, saved as `weather_units`. Automatic uses °F when `RegionInfo.CurrentRegion.IsMetric`
  is false, otherwise °C.
- **Turn off** clears the place, stops requests, and hides the tile.
- No device location. Capsule never asks Windows for the user's position.

### Data
- Forecast request, made off the UI thread with `Http.Get`:
  `https://api.open-meteo.com/v1/forecast?latitude=<lat>&longitude=<lon>&current=temperature_2m,apparent_temperature,weather_code,is_day&hourly=temperature_2m,weather_code,precipitation_probability,is_day&daily=weather_code,temperature_2m_max,temperature_2m_min&forecast_days=5&timezone=auto&temperature_unit=<celsius|fahrenheit>`
- Refresh every 30 minutes while a place is set, and when the panel opens if the data is over 15 minutes old.
  Also refresh after the PC wakes and when the network comes back, as the calendar does. Never more than once a
  minute.
- On failure, keep the last forecast. Dim it once it's over 90 minutes old, as other tiles dim stale data, and show
  "Couldn't update the weather" with the time of the last update. A 429 backs off for at least 30 minutes, or to
  any Retry-After.
- Parse defensively. A missing or wrong-type field shows "The weather service sent something Capsule couldn't
  read", never a crash or made-up numbers. Tests use saved sample responses only and never contact the network.

### Tile
- Header **Weather** with the place's name.
- Now: the current temperature, large, with a condition glyph and words ("Partly cloudy"), "Feels like …", and
  today's high and low.
- Next 6 hours: hour, glyph, temperature and chance of rain. Use each hour's `is_day` to choose its glyph.
  Show the chance only when it's 20% or more.
- Next 4 days: weekday (short, in the user's culture), glyph, high and low.
- Times use the user's 12- or 24-hour clock, as the calendar does.
- Map WMO weather codes to these groups: clear, mainly clear, partly cloudy, overcast, fog, drizzle, freezing
  drizzle, rain, freezing rain, snow, snow grains, rain showers, snow showers, thunderstorm, thunderstorm with hail.
  Clear and mainly clear get night glyphs when `is_day` is 0. Use glyphs from `PanelView.IconFont` that exist in
  both Segoe Fluent Icons and Segoe MDL2 Assets. Unknown codes show "Weather" with a neutral glyph.
- A small "Weather data by Open-Meteo.com" line links to `https://open-meteo.com/`. The data is CC BY 4.0, which
  requires credit.
- Place the tile after Calendar in the default order. Add **Weather** to **Open panel with**.

### Docs
- README: a Weather section, and a row in "What it reads, and where it connects" naming `geocoding-api.open-meteo.com`
  (the place name you search for) and `api.open-meteo.com` (the saved latitude and longitude), only once a place
  is set.
- privacy.html: the same hosts and data. No sign-in, no API key, no device location. Off by default.
  Set the policy's effective date to the date of the Weather change, 12 November 2026.
- CHANGELOG under Unreleased.

## Delivery

- Three PRs, in order: clipboard categories, volume, weather. Each is based on the previous one only if it must be;
  otherwise base each on `main`.
- Each PR: regression tests for its logic, the isolated Windows dev suite green, CI green on the final commit,
  README, privacy.html (where it applies) and CHANGELOG updated. Leave merging to the owner.
- Keep the code style: C# 5, .NET 4.8 WPF, no new packages, Palette and tile helpers from PanelView, the existing
  `Http` and `Json` helpers.
- Manual checks to record in each PR description:
  - Clipboard: copy a command, a code block, an email and a link, and check each chip.
  - Volume: drag, wheel and mute, then change the volume from the taskbar and check the slider follows. Unplug or
    switch the playback device.
  - Weather: search a place with an accent ("Zürich"), switch units, go offline, and check the stale state.
- Commit as `vorgtrom <116165059+vorgtrom@users.noreply.github.com>`.
