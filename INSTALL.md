# Installing Daily Picks

Daily Picks installs from its own plugin repository, like any third-party Jellyfin plugin: no build, no file copying,
and updates show up in the dashboard.

## Requirements

- **Jellyfin 12.4 or newer.** Older servers won't list the plugin.
- **A Windows server.** That's what the plugin is built and tested on. Linux and Docker are not supported yet:
  the TV library needs a folder at the root of each media drive, which doesn't work on most Linux/Docker setups.
  The home row in the web client may work there, but it's untested.
- **Media on NTFS drives**, local or on a network share (mapped drive or `\\server\share`) that allows hard links.
  Most Windows file servers and NAS devices with SMB do.

## What the plugin creates

So nothing comes as a surprise:

- A library named **Daily Picks**, created automatically. **Don't create it yourself**: a library with the same name
  stops the plugin from creating its own.
- A folder named **`Jellyfin Daily Picks`** at the root of each drive that holds picked media (e.g. `D:\Jellyfin Daily Picks`).
  It holds **hard links** to about 10 titles: they take no extra disk space, and deleting them never touches your files.
  For a show, only its first season is linked.
- One line added to the web client's `index.html`, to draw the home row.

---

## 1. Check your server

Open **Dashboard**. On the dashboard home:

- **Server version** must be **12.4.0 or newer**.
- Under **Paths**, note the **Data** path. You'll need it in step 5:
  - `C:\ProgramData\Jellyfin\Server` → Jellyfin runs **as a Windows service**.
  - `C:\Users\<name>\AppData\Local\jellyfin` → Jellyfin runs **as a Windows user** (tray app, "basic" install).

## 2. Add the plugin repository

1. **Dashboard → Plugins → Manage Repositories** → **+** (add).
2. **Repository Name:** `Daily Picks`
3. **Repository URL:**
   ```
   https://raw.githubusercontent.com/dodocousin/Jellyfin-plugin-dailypicks/main/manifest.json
   ```
4. Save.

## 3. Install the plugin

1. **Dashboard → Plugins**, open the catalog (available plugins), find **Daily Picks** (category *General*), then **Install**.
2. Restart Jellyfin when asked: **Dashboard → Restart** (or quit and start it again from the tray icon).
3. After the restart, **Dashboard → Plugins** should list **Daily Picks** as **Active**.

On its first start the plugin makes today's picks and creates the **Daily Picks** library. On a large library
(thousands of titles) this first run can take several minutes. Progress is in **Dashboard → Scheduled Tasks → Refresh Daily Picks**.

## 4. Choose the settings

**Dashboard → Plugins → Daily Picks**:

- **Pick mode:** *Smart* (based on what your users watch, the default) or *Random*.
- **Titles per day** and the mix: how many match each user's taste, how many are popular in the house, the rest are discoveries.
- **Users whose viewing shapes the picks:** untick guest or shared accounts.
- **Show a home row** (web client) and **Keep a Daily Picks library** (TV apps): keep both ticked.
- **Library tile image:** *Collage of today's picks* (redrawn every night) or *My own picture* (PNG, JPEG or WebP; 16:9 fits best).
- **Save.** Saving pick settings makes today's picks again.

## 5. Check the home row in the browser

Open the Jellyfin home page in a browser and press **Ctrl+F5** (reloads the page completely).
You should see a **Daily Picks** row right after **My Media**, with a reason under each poster ("Because you watched ...").

**If you see it, you're done with this step.** Most service installs (step 1) land here.

### If the row is missing

**Why it can happen:** to draw the row, the plugin adds one line to the Jellyfin web page, the file `index.html` in the
`jellyfin-web` folder of Jellyfin's install folder (usually `C:\Program Files\Jellyfin\Server\jellyfin-web`).
Windows protects `C:\Program Files`: only administrators and system services can change files there. A **tray / basic
install** (step 1) runs Jellyfin as your normal Windows user, so the plugin isn't allowed to add its line.

**Check which case you're in:** open **Dashboard → Logs**, open the newest log, and search for `Daily Picks:`.

| You find | Meaning |
|---|---|
| `home row script added to ...` | The line was added. Press Ctrl+F5 again; if the row is still missing, wait a minute and retry (the first picks may still be in the making, see step 3). |
| `cannot write ...` | Jellyfin isn't allowed to change the file. Use **fix A** or **fix B** below. |
| `web client not found at ...` | Jellyfin doesn't serve its own web pages (e.g. a separately hosted web client). The row can't be added there. |
| None of these | The line was already added on an earlier start, and nothing needed doing. Press Ctrl+F5 again. |

**Fix A: let Jellyfin edit its web folder** (recommended, done once)

1. Find which Windows user runs Jellyfin: open **Task Manager → Details**, find `jellyfin.exe`, and read the
   **User name** column.
2. Right-click **Start → Terminal (Admin)** or **Windows PowerShell (Admin)**, and run this command, replacing
   `YourWindowsUser` with that user name (and the path, if Jellyfin is installed somewhere else):
   ```powershell
   icacls "C:\Program Files\Jellyfin\Server\jellyfin-web" /grant "YourWindowsUser:(OI)(CI)M"
   ```
   This lets that user modify files in the `jellyfin-web` folder only, nothing else.
3. Restart Jellyfin, then press **Ctrl+F5** on the home page.

**Fix B: add the line yourself** (if you'd rather not change permissions)

1. Open **Notepad as administrator** (right-click Notepad → *Run as administrator*).
2. **File → Open** `C:\Program Files\Jellyfin\Server\jellyfin-web\index.html` (choose *All files* to see it).
3. Paste this line just before `</body>`, near the end of the file, and save:
   ```html
   <script plugin="DailyPicks" defer src="../DailyPicks/ClientScript"></script>
   ```
4. Press **Ctrl+F5** on the home page.

**After a Jellyfin update:** the update replaces the `jellyfin-web` folder. With fix A, the plugin re-adds its line on the
next start, unless the update also reset the folder's permissions: if the row is gone, run the fix A command again.
With fix B, the line is gone and you need to add it again.

## 6. Check the TV library

1. **Dashboard → Libraries** should show **Daily Picks**, and a `Jellyfin Daily Picks` folder should exist at the root of
   your media drive(s).
2. If it's missing, search the newest log for `Daily Picks`:

   | Log message | What to do |
   |---|---|
   | `library refreshed with 10 picks` / `created library` | All good. |
   | `... is inside another library, skipping ...` | One of your libraries points at a whole drive (e.g. `D:\`). The plugin won't put picks inside it, or they would show up twice. Point that library at a subfolder (e.g. `D:\Movies`) instead. |
   | `could not link the files of ...` | That drive or share doesn't allow hard links (e.g. FAT/exFAT, or a share with links disabled). Titles on it can't be used for the TV library. |
   | `no user can see the Daily Picks library` | Give at least one user access to all libraries, or to the Daily Picks library (Dashboard → Users → Access). |
   | `no picks to put in the library` | See the messages just above it for the reason. |

## 7. Check the TV apps

On Android TV / Google TV / Fire TV, Roku and Apple TV, the picks show as **"Recently added in Daily Picks"** on the home screen.

- Close and reopen the app after the first install.
- If the row doesn't appear, enable the *Recently added* / *Latest* row for the **Daily Picks** library in the app's
  home-screen settings.
- Users who can only see chosen libraries don't get new libraries automatically. To give them the Daily Picks row,
  tick **Daily Picks** in **Dashboard → Users → (user) → Access**.

---

## Good to know

- **Household vs personal picks.** The web row is personal for each user. The TV library is shared, so it holds household
  picks: everyone's taste counts equally, and only titles every user who can see the library is allowed to watch are used
  (library access and parental ratings). With a restricted kids account, the TV picks stay within what that account may watch.
- **Duplicates.** A pick shows up twice in search (original + Daily Picks), and watch progress is tracked separately for each.
  For a show, continue in your regular TV library after the first season.
- **Picks change every night at 00:01** (server time), avoid repeats from the last 7 days, and stay the same all day.
  **Reshuffle today's picks** in the settings makes new ones right away.

## Updating

When a new version is released, **Dashboard → Plugins → Daily Picks** offers the update. Install it and restart Jellyfin.
(You can also turn on automatic updates for the plugin there.)

After a **Jellyfin** update:

- **Minor update (e.g. 12.4 → 12.5):** press Ctrl+F5 once; if the home row is gone, redo step 5.
- **Major update (e.g. 12.x → 13.0):** check **Dashboard → Plugins**. If Daily Picks shows *Malfunctioned* or *Not supported*,
  wait for a plugin update for that Jellyfin version. Your server and media are unaffected; the Daily Picks library just
  keeps its last picks.

## Removing the plugin cleanly

1. **Dashboard → Plugins → Daily Picks**: untick **Keep a Daily Picks library**, **Save**.
   This deletes the Daily Picks library and the `Jellyfin Daily Picks` folders. Your media is never touched.
2. Uninstall the plugin in **Dashboard → Plugins** and restart Jellyfin.
3. Optional: remove the plugin repository in **Dashboard → Plugins → Manage Repositories**.
