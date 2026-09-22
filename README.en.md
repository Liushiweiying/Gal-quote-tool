# Gal Quote Collector

[中文](README.md) | [日本語](README.ja.md)

![Platform](https://img.shields.io/badge/platform-Windows-lightgrey)
![.NET](https://img.shields.io/badge/.NET-8.0-purple)

A desktop tool for capturing, organizing, and revisiting visual novel quotes. Features screenshot capture, OCR text recognition, tags, groups, slideshow, and export.

![Main window](images/main.png)

## Features

| Feature | Description |
|---|---|
| **One-key capture** | Global hotkey `Ctrl+Win+Z` (customizable), screenshot + OCR + auto-save; OCR engine selectable: Windows built-in / local Ollama model / RapidOCR |
| **Game name detection** | Strip engine/date suffixes from window title; custom matching rules supported |
| **Tags** | Tag quotes (e.g. emotional, funny), filter by tag |
| **Groups** | Create curated collections, one quote can belong to multiple groups |
| **Slideshow** | Full-screen recollection mode, keyboard navigation, random jump, always-on-top |
| **Export** | Markdown / JSON, all or by group |
| **Import** | Import exported Markdown / JSON files |
| **Statistics** | By game / tag / group / time (monthly or yearly) |
| **Auto-start** | Start with Windows, minimize to tray |
| **Capture delay** | Customizable delay (0-2000ms) |

## Downloads

From the [Releases](../../releases) page:

| File | Size | Notes |
|---|---|---|
| `Gal-quote-tool.exe` | ~28MB | Single file, requires .NET 8 runtime |
| `Gal-quote-tool_selfcontained.exe` | ~190MB | Single file, self-contained, no runtime needed |
| `Gal-quote-tool_Setup.exe` | ~55MB | Inno Setup installer (self-contained, with uninstaller) |
| `publish-folder.zip` | ~77MB | Self-contained ZIP, extract and run |

> Data is stored at `%LOCALAPPDATA%\GalQuoteCollector\` (database, screenshots, settings).

## Usage

1. Run the exe. The app minimizes to system tray.
2. Open any visual novel, press `Ctrl+Win+Z` to capture.
3. A toast notification appears at top-right (click to open main window).

### Key bindings

| Key | In slideshow / fullscreen |
|---|---|
| `Ctrl+Win+Z` | Capture (configurable in settings) |
| `Left / Right arrow` | Previous / Next |
| `Space` | Next |
| `Enter` | Random jump |
| `F11` | Toggle fullscreen |
| `F2` | Toggle always-on-top |
| `Esc` | Exit fullscreen / Close |

### Groups
- Enter group name in detail panel -> Create group
- Click gray tag -> Join group (turns green)
- Click green tag -> Leave group
- Right-click group tag -> Delete group
- Select group in toolbar -> Filter by group

### Game name rules
Add custom rules in Settings. E.g. `Summer Pockets -> Summer Pockets REFLECTION BLUE` maps any window title containing "Summer Pockets" to that game name.

## Development

### Prerequisites
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Windows 10+ (with Chinese OCR language pack for OCR)

### Build & Run
```bash
cd GalQuoteCollector
dotnet build
dotnet run
```

### Publish
```bash
# Framework-dependent (smaller)
dotnet publish -r win-x64 -c Release --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./publish

# Self-contained (portable)
dotnet publish -r win-x64 -c Release --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./publish
```

## Tech Stack
- **.NET 8 + WPF** - Desktop framework
- **Windows.Media.Ocr** - Native OCR (Chinese support; switchable to Ollama / RapidOCR)
- **SQLite** - Local database
- **CommunityToolkit.Mvvm** - MVVM architecture
- **Hardcodet.NotifyIcon.Wpf** - System tray
- **System.Drawing.Common** - Screenshot & image processing

## Data Location
```
%LOCALAPPDATA%\GalQuoteCollector\
├── quotes.db          - SQLite database
├── settings.json      - Hotkey, rules, delay config
└── screenshots\       - PNG screenshots
```

## Changelog

### v1.3.4 (2026-09-21)
- **Improved auto-backup rules**: backups go to `Gal Quote Tool Backup` next to the install folder; one backup per day and **only when the quote database content actually changed** (content hash, because SQLite touches the file timestamp on every open); **retention count is configurable** (default 3); **settings are not backed up and settings changes do not trigger a backup**
- **New settings sidebar**: quick navigation to sections (hotkeys / general / slideshow & upscaling / capture & bars / data & backup / web / OCR / game rules)
- **New slideshow settings button (⚙, icon only)**: black-bar handling, **text panel style (black-on-white / white-on-black) with adjustable opacity and restore-default**, loop, fonts, Magpie upscaling and its hotkey
- **New single-quote ZIP export/import** (image included as a real file, compatible with the existing bundled export/import)
- **Fix** Magpie re-scaling briefly after leaving fullscreen (no more extra hotkey; a fast 200 ms poll stops Magpie's own restart)
- **Fix** the ⚙ button being clipped in the slideshow top bar

### v1.3.3 (2026-09-21)
- **New built-in LAN web page (phone / desktop browser)**: the app serves a small web UI (optional HTTPS), so any device on the same Wi-Fi can open `http://PC-IP:8088/` to **browse, search, edit, import and export** quotes; the layout adapts to phone and desktop
  - **Slideshow mode**: tap "回想" or a card thumbnail for a full-screen viewer with swipe/tap navigation, auto-play, game name and text
  - **Black-bar handling without touching files**: switch between "as-is / crop bars / paint bars white" in the viewer — processed in memory on the server
  - **Single-quote import/export (with image)**: export one quote as JSON with the image embedded as base64, then import it on any device
  - **Inverted filters**: exclude a game / group / tag; quotes without OCR text now show only the screenshot (the placeholder text is not rendered)
  - **HTTPS** on by default with a self-signed certificate (generated on first run); trust it on this PC with one button, or export it for phones
- **New daily auto-backup on first launch**: quotes DB, usage data, settings **and screenshots**, once a day, keeping the last 3 days in a folder you choose
- **New black-bar handling for captures**: screenshots are auto-cropped on capture (toggleable) and there is a batch "crop all screenshots" command; mostly-dark images are skipped
- **Improved Magpie upscaling in the slideshow**: one hotkey does it — if Magpie is running the hotkey is sent (with one extra press when it was scaling another window); the log-based check is gone (Magpie buffers its log, which caused the old "press twice" bug)
- **Improved slideshow window**: toggle "as-is / crop / paint white" with the top-bar button or the `B` key (**files are never modified**); the fullscreen background turns white too
- **Fix** the per-app "average per day" now divides by **days that actually have records**
### v1.3.2 (2026-09-19)
- **Fix** lock-screen time was heavily over-counted (a v1.3.0/v1.3.1 bug)
  - Cause: the old rule treated "input desktop is not Default" as locked; without any real lock event this still fired, crediting a whole evening of gaming to the lock bucket (about 145 of 229 minutes on one day)
  - Locked is now decided by three signals, most reliable first: (1) a real session lock event (`SessionLock`/`SessionUnlock`; console/remote disconnect no longer counts) (2) the lock-screen app itself is in the foreground (`LockApp.exe` / `LogonUI.exe`, user-extensible) (3) the input desktop is not Default **and** there has been no user input for at least 2 minutes
  - Every locked minute now logs its reason to `startup.log`, and a missed unlock event is recovered when the desktop returns to Default
- **New** the capture hotkey is no longer passed on to games (toggleable in Settings): engines with their own `Alt+E` hotkey used to pop their own window on every capture; that key press is now swallowed (never inside our own windows)
- **Fix** rebinding the hotkey in Settings no longer fires a capture (hotkeys are suspended while the dialog is open)
- **New** command-line helpers to repair history: `--fix-lock <date> <process> [fromHour toHour]` and `--move-usage <date> <fromProcess> <toProcess> [fromHour toHour]`
### v1.3.1 (2026-09-16)
- **New** click an app row to open a **per-app detail page** (like tapping an app in iOS Screen Time)
  - Big "Average daily" figure, total for the period, and the app's own bar chart (hourly for today, daily for 7 days / this month, monthly for this year)
  - Scope table: range / total / average per day / days with records / busiest day / last used / **share of total time** / process names
  - A `›` chevron marks clickable rows; the detail page has its own period switcher and closes with `Esc` or "← Back"
- **Fix** app rows were only clickable on the text itself (the gap between name and duration did nothing) — the whole row is clickable now
### v1.3.0 (2026-09-16)
- **Reworked** the "Usage time" page into an iOS Screen Time style card layout
  - Big total + comparison with the previous period + hourly / daily / weekly / monthly bar charts; hover a bar for a value bubble (click to pin, Esc or click elsewhere to unpin) while the other bars dim
  - Axis labels adapt to the window width, chart height scales with width, the app list becomes two columns on wide windows, window size is remembered
- **New** lock-screen time tracked separately (green)
  - While the session is locked only "Locked" is recorded — no more time credited to a game/app; `LockApp.exe` (the Windows lock screen) and `LogonUI.exe` are folded into it, and existing data is migrated once
  - Bars stack blue (usage) over green (locked); the card, legend and scope section all show locked time
  - Detected via session events + input-desktop checks (works even when a wallpaper tool draws the lock screen) + a user-editable lock-process list
- **New** periods: Today / Last 7 days / This month / This year / **Custom range (calendar)**, each compared with the previous equal-length period
- **New** "App names / lock processes" window: rename any process (per-row save/undo, sort by time or by name); same app recorded under different process names or casing is no longer split
- **New** app icons resolved from many sources (recorded path → running process → MuiCache → uninstall info → App Paths → Everything HTTP), falling back to a coloured initial; right-click a row to pick an image, an exe, auto-search, or reset
- **Improved** all windows scroll at half speed with smooth pixel scrolling
- **Improved** blacklist window redesigned (lists recorded apps, one click to block, asks whether to delete existing data)
- **Removed** the High process priority on auto-start (a resident High process steals CPU from games)
### v1.2.6 (2026-09-13)
- **Fix** the slideshow upscaling targeted the **wrong window**: it fired as soon as the slideshow opened, upscaling the small window. It now fires when you enter **fullscreen (F11)** — the fullscreen slideshow is the window that gets upscaled — and is released when you leave fullscreen or close the slideshow
- **Fix** the result is now confirmed through Magpie's log: Magpie's hotkey is a toggle, so **pressing it while another window is being scaled only stops that session** — the app now sends it again to move the upscaling onto the fullscreen slideshow and says so in the status bar
- **Fix** leaving fullscreen no longer sends a blind hotkey press (it used to restart a just-finished session on the small window); Magpie's log is checked first
- **Correction** v1.2.5 claimed that a Magpie running as administrator blocks the hotkey — that is wrong, injection works there too in practice
- **New** diagnostic switch `--open-slideshow` (opens the slideshow with upscaling forced on, enters fullscreen, then leaves it)
### v1.2.5 (2026-09-13)
- **Fix** the boot-time TranslucentTB repair did nothing: it restarted TTB about one second after logon, before the shell was ready. It now waits for the taskbar tray area, waits 20 more seconds, restarts TTB and checks the taskbar pixels — retrying once 30 seconds later if the appearance did not change
- **Change** the default capture method is now **Current monitor**: Magpie upscaling does not hide the game's title bar, so window-based captures include it
  - Existing settings are migrated once (`auto`/empty → Current monitor) and can still be changed back
  - "Prefer the game's native resolution when Magpie is running" now defaults to off (it includes the title bar); turn it on if you want native resolution
- **New** Magpie upscaling for the slideshow: entering **fullscreen (F11)** triggers Magpie on that fullscreen window, and leaving fullscreen releases it
  - The hotkey is read from Magpie's own config (or typed manually); Magpie is started automatically if it is not running
  - Settings offers "Read Magpie config" and "Test": the test sends the hotkey once and checks Magpie's log for confirmation
  - If Magpie is already scaling a game, the upscaling is moved to the fullscreen slideshow window (the game's session stops)
- **New** slideshow images now use a high quality scaling algorithm
### v1.2.4 (2026-09-12)
- **New** the updater now upgrades in the **same form as the current install**:
  - Installer build → downloads Setup and upgrades in place (same directory; quotes/screenshots/settings untouched)
  - Single-file build → downloads that exe, then replaces itself and restarts
  - Folder build → downloads publish-folder.zip, then overwrites the folder and restarts
  - The dialog shows the detected upgrade style and the file that will be downloaded
- **Fix** a helper script now waits for this process to fully exit before acting, avoiding file-in-use failures

### v1.2.3 (2026-09-12)
- **Fix** the TranslucentTB repair never worked: the Store build cannot be started by its exe path — it now performs a real restart (AUMID activation, classic installs supported) and verifies the process came back
- **New** "Repair now" button in Settings (test it without rebooting)
- **New** the TranslucentTB option is always visible (it used to be hidden unless TTB was running); every step is logged to startup.log
- **New** when Magpie is running, Auto mode now grabs the game's native resolution (PrintWindow); can be disabled in Settings
- **New** status/toast after capture shows the actual resolution and capture method (e.g. `1280×720 窗口内容（原生）`)

### v1.2.2 (2026-09-12)
- **New** selectable capture method: Auto / Window content (native resolution) / Window visible region / Current monitor / Entire screen — use "Window content" when streaming clients or engines like minori capture only part of the picture
- **Fix** capture now uses the DWM extended frame bounds, so scaled / partially off-screen / oversized windows are no longer cropped or offset
- **Fix** "copy failed" (clipboard held by another app) — clipboard writes now retry
- **Fix** usage chart bars now use an absolute scale (adaptive full-scale label) instead of always filling the width
- **New** calendar highlights days that have usage records

### v1.2.1 (2026-08-13)
- **New** RapidOCR offline OCR engine (Settings → OCR engine; requires a Python with `rapidocr-onnxruntime`)
- **New** Undo delete: restore recently deleted quotes (last 20, with tags/groups/screenshots) from the toolbar
- **New** Newly captured quote is auto-selected
- **New** Main window position/size is remembered across restarts
- **New** One-click copy of quote text (detail button / list right-click)
- **New** Drag & drop .md/.json/.zip files onto the window to import
- **New** Search debounce + multi-keyword (space-separated, all must match)
- **New** Toast notifications fade in/out and stack
- **New** Screenshot right-click → "Open containing folder"
- **New** Settings: "Reset to defaults" and "View log"
- **Fix** Uninstaller now moves deleted data to the recycle bin (recoverable)
- **Fix** SQLite multi-thread race (background load + hotkey capture could error concurrently)
- **Fix** hotkey display (e.g. F5 shown as "t")
- **Fix** capture hotkey colliding with add-screenshot hotkey had no warning
- **Fix** bundled import could be path-traversed and silently overwrite local settings/usage data (now asks for confirmation)
- **Fix** fullscreen capture only captured the primary monitor; negative-coordinate (secondary) monitors
- **Fix** windowed games were force-captured fullscreen (now only when the window covers ≥90% of a monitor)
- **Fix** tag/group filtering was slow with many quotes (removed per-quote queries)
- **Fix** batch delete did not ask about screenshot files (now consistent with single delete)
- **Fix** main window stayed minimized after a failed capture / no window detected
- **Fix** 0ms capture delay could screenshot our own window (min 100ms enforced)
- **Tweak** bounded startup log & OCR cache; version read from assembly (single source)