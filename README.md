# Gal Quote Collector

[中文](README.zh.md) | [日本語](README.ja.md)

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

### v1.4.4 (2026-08-13)
- **New** RapidOCR offline OCR engine (Settings → OCR engine; requires a Python with `rapidocr-onnxruntime`)
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