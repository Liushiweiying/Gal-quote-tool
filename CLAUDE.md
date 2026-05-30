# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

```bash
cd GalgameQuoteCollector
dotnet build
dotnet run
```

## Publish

```bash
# Framework-dependent (small, ~27MB, needs .NET 8 runtime)
dotnet publish -r win-x64 -c Release --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./publish

# Self-contained single file (~181MB, no runtime needed)
dotnet publish -r win-x64 -c Release --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./publish

# All 3 types at once (v{N}):
rm -rf publish-tmp publish-tmp2 && mkdir -p publish-v{N} && \
dotnet publish -r win-x64 -c Release --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish-v{N} --force && \
dotnet publish -r win-x64 -c Release --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish-tmp --force && \
mv publish-tmp/GalgameQuoteCollector.exe publish-v{N}/GalgameQuoteCollector_selfcontained.exe && \
dotnet publish -r win-x64 -c Release --self-contained true -o publish-tmp2 --force && \
powershell -Command "Compress-Archive -Path 'publish-tmp2/*' -DestinationPath 'publish-v{N}/publish-folder.zip' -Force"
```

## Target Framework

`net8.0-windows10.0.19041.0` — WPF + WinRT API access (Windows.Media.Ocr). Must be built on Windows.

## Dependencies

| Package | Purpose |
|---|---|
| CommunityToolkit.Mvvm | MVVM source generators ([ObservableProperty], [RelayCommand]) |
| Hardcodet.NotifyIcon.Wpf | System tray icon |
| Microsoft.Data.Sqlite | SQLite database |
| System.Drawing.Common | Screenshot capture & OCR image preprocessing |

## Architecture

### Pattern: MVVM (CommunityToolkit.Mvvm)

- **ViewModels/MainViewModel.cs** — Single ViewModel for main window. Handles capture, tags, groups, export, import, settings, slideshow, stats, dark mode, data migration.
- **Views/*.xaml / .cs** — WPF windows. MainWindow (main UI), SettingsWindow (settings dialog), SlideshowWindow (fullscreen recollection), StatsWindow (statistics), ToastWindow (capture notification popup).
- **Models/** — Data models: Quote, Tag, QuoteGroup, GameNameRule, HotkeyConfig.
- **Services/** — Business logic layer:
  - `StorageService` — SQLite CRUD for quotes, tags, groups
  - `CaptureService` — Screenshot capture via Graphics.CopyFromScreen
  - `OcrService` — Windows.Media.Ocr with image preprocessing (grayscale, upscale, contrast)
  - `HotkeyService` — Low-level keyboard hook (WH_KEYBOARD_LL) for global hotkey
  - `GameDetectService` — Window title → game name (engine suffix stripping, custom rules)
  - `SettingsService` — JSON config (hotkey, rules, theme, data dir)
  - `ExportService` — JSON/Markdown export & import parsing
- **Converters/** — BoolToVisibilityConverter (handles bool, null, int, object references).

### Data Flow

Capture: Global hotkey → CaptureService (screenshot) → OcrService (text) → StorageService (SQLite insert) → UI refresh. Original window title saved as `WindowTitle`, detected game name as `GameName`.

### Database (SQLite)

Stored at `%LOCALAPPDATA%\GalgameQuoteCollector\quotes.db`. Tables: Quotes, Tags, QuoteTags, GroupsTable, QuoteGroupMaps. Migration via ALTER TABLE ADD COLUMN in try-catch.

### Key Behaviors

- **Auto-start**: VBS script in Startup folder (`WScript.Shell.Run "...exe --minimized", 0, False`)
- **Dark mode**: Runtime resource swap via `App.ApplyTheme(bool)` — replaces SolidColorBrush values
- **Data directory**: Configurable via settings, migrates files automatically
- **Capture delay**: 0-2000ms configurable delay before screenshot
- **Slideshow**: Fullscreen with keyboard (arrows/space/enter/F11/F2), left-click=next, right-click=prev
- **Window close**: Hides to tray instead of quitting (tray "退出" to actually exit)
