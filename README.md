# Daily Picks (Jellyfin 12.x plugin)

10 movies and shows picked every day, based on what your users watch.

Picks are made at 00:01 (server time), stay the same all day (also after a restart), and avoid repeats from the last 7 days.

## What you get

Jellyfin apps come in two kinds, and the plugin reaches each in its own way:

- **Web clients** run Jellyfin's web interface, so the plugin can add a row to their home screen.
- **Native TV apps** build their own home screen, which no plugin can change. For them, the plugin keeps a
  **Daily Picks library**, and the apps show it as a row like any other library.

| | Web clients | Native TV apps |
|---|---|---|
| **Apps** | Web browser, Jellyfin Media Player, LG webOS, Samsung Tizen (the Jellyfin Android and iOS apps also use the web interface) | Android TV / Google TV / Fire TV, Roku, Apple TV (Swiftfin), and other native apps such as Infuse or Kodi |
| **Where** | A **Daily Picks** row on the home screen, right after **My Media** | A **"Recently added in Daily Picks"** row on the home screen, plus a **Daily Picks** tile in My Media |
| **Whose picks** | **Personal**: each user gets their own picks | **Household**: one shared set for everyone who can see the library |
| **Why it was picked** | Shown under each poster: *Because you watched Coraline*, *Popular in the house*, *Something different*, *Worth another watch* | Not shown (TV apps have no place for it) |
| **Opening a pick** | Opens the original title | Opens the Daily Picks copy (a hard link to the same file) |
| **Watch progress** | Saved on the original title, as usual | Saved on the Daily Picks copy, separate from the original |
| **Shows** | The whole show | The first season (continue in your regular TV library after that) |
| **Library tile** | — | A collage of today's 10 posters with the title, or your own picture |
| **Permissions** | Only titles that user may watch (library access and parental ratings) | Only titles every user who can see the library may watch |
| **Turn on/off** | *Show a home row in the web client* | *Keep a Daily Picks library for TV apps* |
| **Server needs** | Jellyfin must be able to add one line to its web page (see [INSTALL.md](INSTALL.md), step 5) | A Windows server, with media on drives that allow hard links |

In the web client, the Daily Picks library's own "Recently added" row is hidden, since the personal row already shows the picks.

## Settings

All settings are in **Dashboard → Plugins → Daily Picks**.

### General

| Setting | Default | What it does |
|---|---|---|
| **Title** | Daily Picks | Shown above the web row, and used as the library name when the library is created. To rename an existing library, rename it in Dashboard → Libraries. |
| **Titles per day** | 10 | How many titles are picked each day, for each user and for the household. Movies and shows are mixed when both exist (taste picks follow what the user watches: someone who only watches movies mostly gets movies). |
| **Avoid repeating picks from the last N days** | 7 | A title picked in the last N days isn't picked again (unless the library is too small for that). 0 allows repeats. |

### How titles are picked

| Setting | Default | What it does |
|---|---|---|
| **Pick mode** | Smart | **Smart**: picks based on what your users watch (see [How smart picks work](#how-smart-picks-work)). **Random**: plain random picks; everyone, web and TV, sees the same household picks, without reasons. The settings below only apply to Smart mode. |
| **Picks matching their taste** | 6 | How many picks match the viewer's taste. |
| **Picks popular in the house** | 2 | How many picks are titles others in the house watched in the last 30 days. The rest of *Titles per day* are random **discoveries**, so picks don't get stuck in one genre. |
| **Show why each title was picked** | On | Shows the reason under each poster in the web row. |
| **Suggest favorites again after a while** | Off | Lets a favorite (or a title rated 7+) that the user hasn't watched in a long time come back, as *Worth another watch*. Web row only. |
| **Days before a favorite can come back** | 365 | How long ago such a favorite must have been watched. |
| **Users whose viewing shapes the picks** | All ticked | Untick guest or shared accounts. They still get their own picks; their viewing just doesn't count for the household picks or for *Popular in the house*. |

### Where picks show up

| Setting | Default | What it does |
|---|---|---|
| **Show a home row in the web client** | On | The Daily Picks row in browsers and other web clients. Users must reload the page (Ctrl+F5) after it is first installed. |
| **Keep a Daily Picks library for TV apps** | On | The library native TV apps show. Unticking it **deletes** the library and its `Jellyfin Daily Picks` folders (your media is never touched). |
| **Library tile image** | Collage of today's picks | **Collage of today's picks**: today's posters in a grid with the title, redrawn every night. **My own picture**: choose a PNG, JPEG or WebP file (up to 10 MB; 16:9 such as 960×540 fits best). It's applied right away, with a preview of the current tile; *Remove picture* goes back to the collage. Only admins can upload. |

### Buttons

- **Save**: applies the settings. If a setting that affects the picks changed (mode, counts, users, repeats, favorites),
  today's picks are made again with everyone's latest watch history. Other changes (like the tile image) keep today's picks.
- **Reshuffle today's picks**: makes a new set of picks for today right away.

The **Refresh Daily Picks** scheduled task (Dashboard → Scheduled Tasks) makes the picks at 00:01, runs at startup, and runs
every 3 hours to tidy up. You can also run it by hand.

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
   titles **every user who can see the Daily Picks library is allowed to watch** are used (library access and parental
   ratings), because the library is shared.

Picks are remembered in `plugins/Jellyfin.Plugin.DailyPicks/pick-history.json` (60 days), which is how repeats are avoided.

The taste logic is in `Recommendations/TasteEngine.cs` and is covered by tests in `Jellyfin.Plugin.DailyPicks.Tests`
(`dotnet test`).

## How the library works

Jellyfin 12 refuses `.strm` files that point to local files, so the library holds **hard links** to the picked files:
they take no extra disk space, play exactly like the original, and deleting them never touches the original.
Hard links only work within one drive, so the plugin creates a `Jellyfin Daily Picks` folder at the root of each drive
holding picked media (e.g. `D:\Jellyfin Daily Picks`). If a hard link isn't possible it tries a symbolic link, and
otherwise skips that title. For a picked show only the **first season** is linked (the lowest regular season with files;
specials only if there is nothing else), so the nightly refresh and scan stay small however long the show is. To keep
watching after that season, continue in the regular TV library. External subtitles next to the file are linked too. Titles, plots, posters and backdrops are
copied from your existing metadata (NFO + images), so nothing is downloaded. After each scan the images are moved into
Jellyfin's own metadata folder (on the server's disk), so TV apps don't wait for the media drive or NAS to load them.

Things to know:
- A pick shows up twice in search (original + Daily Picks), and watch progress is tracked separately for each.
- The plugin won't use a `Jellyfin Daily Picks` folder that sits inside another library (e.g. a library pointing at a whole drive),
  so picks never appear twice inside a regular library.
- A hard link is the same file as the original, so it can't be removed while anyone has the original open (someone watching it,
  another server reading it). Such a title is left intact and removed at a later refresh (every 3 hours).

## Install

Requires Jellyfin 12.4 or newer on Windows (Linux and Docker aren't supported yet). Add this plugin repository in
**Dashboard → Plugins → Manage Repositories**:

```
https://raw.githubusercontent.com/dodocousin/Jellyfin-plugin-dailypicks/main/manifest.json
```

then install **Daily Picks** from the catalog and restart Jellyfin. Step-by-step guide: [INSTALL.md](INSTALL.md).

Then see [Settings](#settings).

## Home row not showing in the web client?

The plugin adds a `<script>` tag to `jellyfin-web\index.html` at startup. If Jellyfin can't write that file (common when it
runs from `C:\Program Files` as a normal user), the server log says so. Either give Jellyfin write access to that folder
(see [INSTALL.md](INSTALL.md), step 5), or add this line yourself just before `</body>`:

```html
<script plugin="DailyPicks" defer src="../DailyPicks/ClientScript"></script>
```

A Jellyfin update replaces `index.html`; the plugin adds the tag again on the next start.
