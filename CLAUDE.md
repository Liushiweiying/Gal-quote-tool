# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

```bash
cd GalQuoteCollector
dotnet build
dotnet run
```

## Publish

```bash
# Framework-dependent single file (~27MB, needs .NET 8 runtime)
dotnet publish -r win-x64 -c Release -p:PublishSingleFile=true --self-contained false -p:IncludeNativeLibrariesForSelfExtract=true -o ./publish

# Self-contained single file (~181MB, no runtime needed)
dotnet publish -r win-x64 -c Release -p:PublishSingleFile=true --self-contained true -p:IncludeNativeLibrariesForSelfExtract=true -o ./publish

# All 3 at once:
rm -rf publish-tmp publish-tmp2 && mkdir -p publish-v{N} && \
dotnet publish -r win-x64 -c Release --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish-v{N} --force && \
dotnet publish -r win-x64 -c Release --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish-tmp --force && \
mv publish-tmp/GalQuoteCollector.exe publish-v{N}/GalQuoteCollector_selfcontained.exe && \
dotnet publish -r win-x64 -c Release --self-contained true -o publish-tmp2 --force && \
powershell -Command "Compress-Archive -Path 'publish-tmp2/*' -DestinationPath 'publish-v{N}/publish-folder.zip' -Force"
```

## Target Framework

`net8.0-windows10.0.19041.0` — WPF + WinRT API access for `Windows.Media.Ocr`. Must build on Windows.

## Dependencies

| Package | Purpose |
|---|---|
| CommunityToolkit.Mvvm | MVVM source generators |
| Hardcodet.NotifyIcon.Wpf | System tray icon |
| Microsoft.Data.Sqlite | SQLite database |
| System.Drawing.Common | Screenshot capture & image processing |

## Architecture

### Structure

```
Models/         — Quote, Tag, QuoteGroup, GameNameRule, HotkeyConfig, UsageData
Services/       — StorageService, OcrService, CaptureService, HotkeyService,
                  GameDetectService, SettingsService, ExportService, UsageTracker
ViewModels/     — MainViewModel.cs (single ~900-line ViewModel)
Views/          — MainWindow, SettingsWindow, SlideshowWindow, StatsWindow,
                  UsageStatsWindow, ToastWindow, BlacklistWindow
Converters/     — BoolToVisibilityConverter, ThumbnailConverter, SearchHighlightConverter
```

### Services

- **StorageService** — SQLite CRUD for quotes, tags, groups. Migration via `ALTER TABLE ADD COLUMN` in try-catch.
- **CaptureService** — `Graphics.CopyFromScreen` via saved window handle. `PrintWindow` removed (caused blank captures).
- **OcrService** — `Windows.Media.Ocr` with 3 preprocessing modes: normal, high-contrast, binarized. Full-image fallback.
- **HotkeyService** — Low-level keyboard hook (`WH_KEYBOARD_LL`). Keys pass through to other apps (not blocked).
- **GameDetectService** — Custom rules (longest match wins), engine suffix stripping, date suffix stripping.
- **UsageTracker** — Per-minute timer checking foreground window → process name → game display name. Blacklist support.

### Data Flow

**Capture**: Global hotkey (WH_KEYBOARD_LL) → save game hwnd → minimize window → delay → CaptureService (CopyFromScreen) → OcrService (3-pass) → StorageService (SQLite insert) → UI refresh. Original window title saved as `WindowTitle`, detected game name as `GameName`.

**Usage tracking** (optional, per settings): Timer ticks every 60s → get foreground window handle → process name → GameDetectService → accumulate per day/per process → save to `usage.json`.

### Database

`%LOCALAPPDATA%\GalQuoteCollector\quotes.db` — Quotes, Tags, QuoteTags, GroupsTable, QuoteGroupMaps.

### Key Behaviors

- **Auto-start**: VBS script in Startup folder (`WScript.Shell.Run "...exe --minimized", 0, False`). Task Scheduler fallback removed (access denied).
- **Single instance**: Named mutex. Second launch brings existing window to front.
- **Capture delay**: Slider 0-2000ms, enforced minimum 100ms to avoid self-capture.
- **Window close**: Hides to tray instead of quitting. Tray "退出" to exit.
- **Slideshow**: White theme. Chronological or shuffled order (setting). Group/tag filters. Enter = random jump. Fullscreen F11.
- **Search highlight**: Keyword matches shown with yellow background in list preview.
- **Thumbnail loading**: Asynchronous via `ThumbnailConverter` (DecodePixelWidth=84).
- **Screenshot path**: `C:\Users\...\Pictures\GalQuoteCollector\` (migrated from AppData on first run).
