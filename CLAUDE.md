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
mv publish-tmp/Gal-quote-tool.exe publish-v{N}/Gal-quote-tool_selfcontained.exe && \
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

## Pending Changes（下次改）

### 卸载器数据删除进回收站（✅ 已实现，2026-08-13）
- 已改：`installer.iss` 的 `CurUninstallStepChanged` 用 `DeleteWithRecycle`（SHFileOperationW + FOF_ALLOWUNDO）替换 `DelTree`；失败/中止时回退 `DelTree`。二次确认文案改为"将移入回收站，可在回收站中恢复"。
- 注意：记录里 `fAnyOperationsAborted` 声明为 Integer（Inno 脚本里 BOOL 类型不兼容）；`pFrom := PathName + #0`（隐式+显式双空终止）。ISCC 编译通过，但**卸载实际路径未在本机冒烟测试**——若用户反馈数据没进回收站，优先检查 Inno 对记录内 string 字段的封送。
- 打包约定不变：重跑 ISCC 输出 `publish-v121\Gal-quote-tool_Setup.exe`，四种安装包同步根目录。
- 应用侧（C#）的用户内容文件删除**均已**走 `FileSystem.DeleteFile(..., RecycleOption.SendToRecycleBin)`（MainViewModel.cs 的 DeleteScreenshots/DeleteScreenshot/DeleteUnassociatedScreenshots/MigrateScreenshots），无需改动；自启 VBS/lnk、临时文件、日志截断为永久删除属合理范围。

### 发布约定（用户要求，2026-08-13）
- 版本号走 **1.2.x**（当前 v1.2.4；勿再使用 1.4.x 命名）。安装包输出目录用 `publish-v124` 形式（去掉小数点）。
- **更新器按部署形态升级（v1.2.4 起）**：`UpdateService.DetectInstallForm()` 判定 Installer（有 unins000.exe 或注册表 InstallLocation 命中）/ SingleFile / Folder，并据此选择资产（Setup.exe / 同名 exe / publish-folder.zip）；`StartApply` 写一个 PowerShell 辅助脚本，等本进程退出后执行「运行安装器 / 替换自身 / 解压覆盖」并重启。改动更新逻辑时务必保持这三种形态都能原地升级。
- 四种安装包：`Gal-quote-tool.exe`（FDD 单文件）、`Gal-quote-tool_selfcontained.exe`（SCD 单文件）、`Gal-quote-tool_Setup.exe`（Inno Setup，源目录 `publish-installer\*`）、`publish-folder.zip`（SCD 文件夹压缩）。生成后同步到仓库根目录。
- 仓库卫生：`bin/`、`obj/`、`publish-*/`、根目录四个产物均已加入 `.gitignore`，不要提交构建产物。

### 教程与「关于」对话框 GUI 优化（✅ 已实现，2026-08-15）
- 已改：新建 `Views/InfoDialog.xaml(.cs)`（无边框圆角卡片 + 阴影 + MDL2 主题色图标 + 蓝色主按钮/红色危险按钮 + 可拖动 + Enter/Esc），**替换了全应用 51 处 `MessageBox.Show`**（含教程三处、关于、版本更新、所有确认/文件处理弹窗），UI 完全统一。
- 用法：`InfoDialog.Show(owner, 标题, 正文, InfoDialogButtons.OK/OKCancel/YesNo, InfoDialogIcon.Information/Question/Warning/Error, dangerConfirm: true)` 返回 `InfoDialogResult.OK/Yes/No/Cancel`。删除类确认传 `dangerConfirm: true`（红色"是"）。按钮顺序约定：**左是右否**（是/确定在左，否/取消在右）。
- 后续可选项：教程/关于窗口做成"分节排版 + 图标 + 版本号 + 仓库链接"的专属窗口（当前已统一为 InfoDialog，够用）。

### 自动更新与代码签名（2026-08-21 起）
- 自动更新：`Services/UpdateService.cs`（GitHub latest API → 优先 `*_Setup.exe` 资产直链 + sha256 digest 校验下载）+ `Views/UpdateDialog`（更新日志 / 进度条 / 下载 / 跳过此版本 / 立即安装→退出并启动安装器）。入口：「···」菜单 → 检查更新；启动时自动检查；跳过版本存 settings.json 的 `SkippedUpdateVersion`。
- 代码签名：自签名证书 `CN=Gal Quote Collector`（CurrentUser\My，指纹 `1C3987F6C7A8E67FF6C191AD220C7A6EDE4FC7A7`；pfx/cer 在本地 `cert/`，gitignored；pfx 密码 `GalQuote2026-CodeSign`）。signtool：`C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe`，命令 `signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /sha1 <指纹> <file>`。
- **发布顺序（重要）**：打包 → 先签 `publish-installer\Gal-quote-tool.exe` → 再 Compress-Archive 生成 zip → 再跑 ISCC → 再签三个 exe（FDD/SCD/Setup）→ curl 上传（curl 需 `--ssl-no-revoke`；180MB 文件上传慢，后台任务 + 大超时）。
- 上传发布附件：删除旧附件（DELETE /releases/assets/{id}）→ POST `upload_url?name=xxx`，用 `curl --ssl-no-revoke --data-binary @file`；GitHub 令牌可用 `git credential fill`（protocol=https, host=github.com）获取。

### 已修的坑（避免重复踩）
- **WPF Slider 的 `ValueChanged` 会在 `InitializeComponent()` 期间触发**（设置 `Minimum` 时把默认值 0 钳到 Minimum）。若处理器引用了 XAML 中**声明在后面**的元素 → NullReferenceException → 打开窗口即崩溃。已给 `SettingsWindow` 的 Jpeg/Delay 两个滑杆加空值保护。
- `App.OnStartup` 已注册 `DispatcherUnhandledException` / `AppDomain.UnhandledException` / `TaskScheduler.UnobservedTaskException`，异常写入 `%LOCALAPPDATA%\GalQuoteCollector\startup.log`（UI 线程异常不再闪退）。
- 诊断开关：`Gal-quote-tool.exe --open-settings` 启动后自动打开设置窗口（用于复现/排查）。
- **测试前必须先 `taskkill /IM Gal-quote-tool.exe /F`**：程序在运行时 exe 被锁，`dotnet build` 会静默失败（仍是旧二进制），导致"修了却没生效"的假象。构建后核对 exe 时间戳。
