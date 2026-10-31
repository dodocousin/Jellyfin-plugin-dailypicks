# Installing Daily Picks on a Jellyfin server

This guide installs the plugin from its GitHub plugin catalog, so the server needs no .NET, no build and no file copying,
and updates show up in the dashboard like any other plugin.

**Requirements:** Jellyfin **12.4 or newer** on Windows, media on drives Jellyfin already uses (local or a NAS mapped drive).

---

## Part A: Publish to GitHub (once, on the development PC)

Skip this part if `https://github.com/dodocousin/jellyfin-plugin-dailypicks` already exists and has a release.

1. On github.com, create a **new public repository** named `jellyfin-plugin-dailypicks`.
   Leave it empty (no README, no .gitignore, no license), so the first push goes in cleanly.
   It must be public: the Jellyfin server downloads the plugin anonymously.
2. In PowerShell, from the repo folder:
   ```powershell
   cd G:\GitHubRepo\JellyfinProjects\jellyfin-plugin-dailypicks
   git init -b main
   git add .
   git commit -m "Daily Picks plugin"
   git remote add origin https://github.com/dodocousin/jellyfin-plugin-dailypicks.git
   git push -u origin main
   ```
3. Publish version 1.0.0:
   ```powershell
   git tag -a v1.0.0 -m "First release"
   git push origin v1.0.0
   ```
4. On GitHub, open the **Actions** tab and wait for the **Release** run to turn green (about 2 minutes). It runs the tests,
   builds the plugin, creates the release `v1.0.0` with `daily-picks_1.0.0.0.zip`, and adds it to `manifest.json`.
5. Check: open
   `https://raw.githubusercontent.com/dodocousin/jellyfin-plugin-dailypicks/main/manifest.json`
   in a browser. Under `"versions"` you should see `"version": "1.0.0.0"`.

---

## Part B: Install on the live server

Do these steps in a browser signed in to the live server **as an administrator**.

### 1. Check the Jellyfin version and how it runs

Open **Dashboard**. On the dashboard home:

- **Server version** must be **12.4.0 or newer**. Older servers won't list the plugin in the catalog.
- Under **Paths**, look at the **Data** path:
  - `C:\ProgramData\Jellyfin\Server` → Jellyfin runs **as a Windows service**.
  - `C:\Users\<name>\AppData\Local\jellyfin` → Jellyfin runs **as your Windows user** (tray app, "basic" install).

  Note which one it is; step 6 depends on it.

### 2. Add the plugin repository

1. **Dashboard → Plugins → Manage Repositories** → **+** (add).
2. **Repository Name:** `Daily Picks`
3. **Repository URL:**
   ```
   https://raw.githubusercontent.com/dodocousin/jellyfin-plugin-dailypicks/main/manifest.json
   ```
4. Save.

### 3. Install the plugin

1. **Dashboard → Plugins**, open the catalog (available plugins), find **Daily Picks** (category *General*), click it, then **Install**.
2. When it says *"The server will need to be restarted"*, restart Jellyfin: **Dashboard → Restart**
   (or right-click the Jellyfin tray icon → *Exit*, then start Jellyfin again).
3. After the restart, **Dashboard → Plugins** should list **Daily Picks** as **Active**.

On its first start the plugin makes today's picks and creates the **Daily Picks** library. That takes a minute or two.

### 4. Choose the settings

**Dashboard → Plugins → Daily Picks**:

- **Pick mode:** *Smart* (default).
- **Users whose viewing shapes the picks:** untick guest or shared accounts.
- Keep **Show a home row** and **Keep a Daily Picks library** ticked.
- **Save.** Saving makes today's picks again with the new settings.

### 5. Check the TV library

1. **Dashboard → Libraries**: a library named **Daily Picks** should be there.
2. On the NAS mapped drive, a folder **`<drive>:\Jellyfin Daily Picks`** should hold about 10 title folders
   (e.g. `J:\Jellyfin Daily Picks`). They are hard links: they take no extra space, and deleting them never touches your files.
3. If the library is missing or empty, open **Dashboard → Logs**, open the newest log and search for `Daily Picks`:

   | Log message | What to do |
   |---|---|
   | `... is inside another library, skipping ...` | One of your libraries points at a whole drive (e.g. `J:\`). The plugin won't put picks inside it, or they would show twice. Point that library at a subfolder (e.g. `J:\Media`) instead. |
   | `could not link the files of ...` | The NAS doesn't allow hard links on that share. Tell the developer which NAS/share it is. |
   | `library refreshed with 10 picks` | All good. |

### 6. Check the home row in the browser

1. Open the Jellyfin home page and press **Ctrl+F5**. A **Daily Picks** row should appear right after **My Media**,
   with a reason under each poster ("Because you watched ...").
2. If the row is missing, search the log for `cannot write`. The plugin has to add one line to
   `C:\Program Files\Jellyfin\Server\jellyfin-web\index.html`, and Windows only lets administrators change files there.
   - **Service install** (step 1): the service is normally allowed to. If not, the fix below applies to the service account.
   - **Tray / basic install**: Jellyfin runs as your Windows user, which isn't allowed by default. Fix it once:
     open **PowerShell as administrator** and run (replace `YourWindowsUser` with the Windows account that runs Jellyfin):
     ```powershell
     icacls "C:\Program Files\Jellyfin\Server\jellyfin-web" /grant "YourWindowsUser:(OI)(CI)M"
     ```
     Then restart Jellyfin and press Ctrl+F5 in the browser.
   - A Jellyfin update can replace that folder and its permissions. If the row disappears after a Jellyfin update,
     run the same command again.

### 7. Check the TV apps

On Android TV / Google TV / Fire TV, Roku and Apple TV, the picks appear as **"Recently added in Daily Picks"** on the home screen.

- Back out of the app and reopen it after the first install.
- If the row doesn't appear, look in the app's home-screen settings and enable the *Recently added* / *Latest* row
  for the **Daily Picks** library.

---

## Updating

On the development PC, after changing the code:

```powershell
cd G:\GitHubRepo\JellyfinProjects\jellyfin-plugin-dailypicks
git add .
git commit -m "What changed"
git push
git tag -a v1.0.1 -m "What changed"
git push origin v1.0.1
```

When the **Release** run on GitHub is green, the live server offers the update in **Dashboard → Plugins → Daily Picks**.
Install it and restart Jellyfin. (You can also turn on automatic updates for the plugin there.)

## After a Jellyfin update

- **Minor update (12.4 → 12.5):** press Ctrl+F5 once; if the home row is gone, redo step 6.
- **Major update (12.x → 13.0):** check **Dashboard → Plugins**. If Daily Picks shows *Malfunctioned* or *Not supported*,
  the plugin needs a rebuild for the new version. Meanwhile your server and media are unaffected; the Daily Picks library just
  keeps its last picks.

## Removing the plugin cleanly

1. **Dashboard → Plugins → Daily Picks**: untick **Keep a Daily Picks library**, **Save**.
   This deletes the Daily Picks library and the `Jellyfin Daily Picks` folders (your media is never touched).
2. Then uninstall the plugin in **Dashboard → Plugins** and restart Jellyfin.
