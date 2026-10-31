# Daily Picks (Jellyfin 12.x plugin)

10 movies and shows picked every day, based on what your users watch.

- **Web client** (browser, Jellyfin Media Player, LG webOS, Samsung Tizen): a "Daily Picks" row on the home screen, right after
  "My Media", with **personal picks for each user** and why each was picked ("Because you watched Coraline").
- **Native TV apps** (Android TV / Google TV / Fire TV, Roku, Apple TV): these build their own home screen, so the plugin keeps a
  **Daily Picks library** with **household picks**. Every app shows a "Recently added in Daily Picks" row for it.

Picks are the same all day (also after a restart) and avoid repeats from the last 7 days.
They are made at 00:01 (server time) and again whenever the settings are saved.

## How smart picks work

1. **Taste profile** per user from what they watched (recent viewing counts more), favorites and ratings (count extra), and
   titles they quit early (count slightly against similar titles). From those titles: genres, top actors, directors, studios,
   tags and decade.
2. **Scoring**: every title they haven't seen is scored against the profile; picks are drawn from the best matches with some
   randomness, so they change from day to day.
3. **Mix** (default 10): 6 matching their taste, 2 popular in the house (watched by others in the last 30 days), 2 random
   discoveries. New users with no history get popular and random picks.
4. **Skipped**: movies they've seen or started (Continue Watching covers those) and shows they've started (Next Up covers those).
   Optionally, favorites not watched in a year can come back.
5. **Household picks** (TV library): everyone's taste counts equally, titles most of the house has seen are skipped, and only
   titles **every user is allowed to see** are used (library access and parental ratings), because the library is shared.

Settings → *Users whose viewing shapes the picks* lets you leave out guest accounts. *Pick mode: Random* brings back plain random
picks. Picks are remembered in `plugins/Jellyfin.Plugin.DailyPicks/pick-history.json` (60 days).

The taste logic is in `Recommendations/TasteEngine.cs` and is covered by tests in `Jellyfin.Plugin.DailyPicks.Tests`
(`dotnet test`).

## How the library works

Jellyfin 12 refuses `.strm` files that point to local files, so the library holds **hard links** to the picked files:
they take no extra disk space, play exactly like the original, and deleting them never touches the original.
Hard links only work within one drive, so the plugin creates a `Jellyfin Daily Picks` folder at the root of each drive
holding picked media (e.g. `J:\Jellyfin Daily Picks`). If a hard link isn't possible it tries a symbolic link, and
otherwise skips that title. External subtitles next to the file are linked too. Titles, plots, posters and backdrops are
copied from your existing metadata (NFO + images), so nothing is downloaded.

Things to know:
- A pick shows up twice in search (original + Daily Picks), and watch progress is tracked separately for each.
- The plugin won't use a `Jellyfin Daily Picks` folder that sits inside another library (e.g. a library pointing at a whole drive),
  so picks never appear twice inside a regular library.
- In the web client, the library's own "Recently added" row is hidden, since the custom row already shows the picks.
- A hard link is the same file as the original, so it can't be removed while anyone has the original open (someone watching it,
  another server reading it). Such a title is left intact and removed at a later refresh (every 3 hours).

## Install

Requires Jellyfin 12.4 or newer. Add this plugin repository in **Dashboard → Plugins → Repositories**:

```
https://raw.githubusercontent.com/dodocousin/jellyfin-plugin-dailypicks/main/manifest.json
```

then install **Daily Picks** from the catalog and restart Jellyfin. Step-by-step guide: [INSTALL.md](INSTALL.md).

Settings: **Dashboard → Plugins → Daily Picks**. Scheduled task: **Dashboard → Scheduled Tasks → Refresh Daily Picks**.

## Build and release

```powershell
dotnet test                 # 15 tests for the taste engine
dotnet build -c Release     # Jellyfin.Plugin.DailyPicks/bin/Release/net10.0/Jellyfin.Plugin.DailyPicks.dll
```

To release, push a version tag. GitHub Actions (`.github/workflows/release.yml`) tests, builds with the .NET 10 SDK,
publishes `daily-picks_<version>.zip` as a GitHub release and adds it to `manifest.json`, so servers offer the update:

```powershell
git tag -a v1.0.1 -m "What changed"
git push origin v1.0.1
```

## Home row not showing in the web client?

The plugin adds a `<script>` tag to `jellyfin-web\index.html` at startup. If Jellyfin can't write that file (common when it
runs from `C:\Program Files` as a normal user), the server log says so. Either give Jellyfin write access to that folder
(see [INSTALL.md](INSTALL.md), step 6), or add this line yourself just before `</body>`:

```html
<script plugin="DailyPicks" defer src="../DailyPicks/ClientScript"></script>
```

A Jellyfin update replaces `index.html`; the plugin adds the tag again on the next start.
