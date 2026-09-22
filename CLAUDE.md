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
- 版本号走 **1.2.x / 1.3.x**（当前 v1.3.4，2026-09-21 发布；勿再使用 1.4.x 命名）。安装包输出目录用 `publish-v134` 形式（去掉小数点）。
- **更新器按部署形态升级（v1.2.4 起）**：`UpdateService.DetectInstallForm()` 判定 Installer（有 unins000.exe 或注册表 InstallLocation 命中）/ SingleFile / Folder，并据此选择资产（Setup.exe / 同名 exe / publish-folder.zip）；`StartApply` 写一个 PowerShell 辅助脚本，等本进程退出后执行「运行安装器 / 替换自身 / 解压覆盖」并重启。改动更新逻辑时务必保持这三种形态都能原地升级。
- 四种安装包：`Gal-quote-tool.exe`（FDD 单文件）、`Gal-quote-tool_selfcontained.exe`（SCD 单文件）、`Gal-quote-tool_Setup.exe`（Inno Setup，源目录 `publish-installer\*`）、`publish-folder.zip`（SCD 文件夹压缩）。生成后同步到仓库根目录。
- 仓库卫生：`bin/`、`obj/`、`publish-*/`、根目录四个产物均已加入 `.gitignore`，不要提交构建产物。

### 教程与「关于」对话框 GUI 优化（✅ 已实现，2026-08-15）
- 已改：新建 `Views/InfoDialog.xaml(.cs)`（无边框圆角卡片 + 阴影 + MDL2 主题色图标 + 蓝色主按钮/红色危险按钮 + 可拖动 + Enter/Esc），**替换了全应用 51 处 `MessageBox.Show`**（含教程三处、关于、版本更新、所有确认/文件处理弹窗），UI 完全统一。
- 用法：`InfoDialog.Show(owner, 标题, 正文, InfoDialogButtons.OK/OKCancel/YesNo, InfoDialogIcon.Information/Question/Warning/Error, dangerConfirm: true)` 返回 `InfoDialogResult.OK/Yes/No/Cancel`。删除类确认传 `dangerConfirm: true`（红色"是"）。按钮顺序约定：**左是右否**（是/确定在左，否/取消在右）。
- 后续可选项：教程/关于窗口做成"分节排版 + 图标 + 版本号 + 仓库链接"的专属窗口（当前已统一为 InfoDialog，够用）。

### 下次要做（用户指定，2026-09-12；v1.2.5 已完成前两项）
1. **默认截图方式改为「当前显示器」**（✅ v1.2.5 已完成）
   - 原因（用户实测）：**Magpie 超分不会让游戏窗口的标题栏消失**，按窗口区域/窗口内容截取会把顶栏一起截进去；按显示器整体截图才是 Magpie 场景下的正确默认。
   - 已改：`HotkeyConfig.CaptureMode` 默认 `"monitor"`；`CaptureService.ParseMode` 空串/未知 → `Monitor`；`SettingsService.MigrateCaptureDefaults()` 一次性迁移（`CaptureDefaultsMigratedV125` 标记）把旧的 `auto`/空值 → `monitor`，并把旧的 `PreferNativeCaptureWhenMagpie=true`（旧默认值，会带上标题栏）改为 false。
   - `MainViewModel.EffectiveCaptureMode()`：monitor/auto + 勾选「优先原生分辨率」+ Magpie 在跑 → 返回 `window`（原生分辨率，含标题栏）。设置里截图方式下拉顺序改为：当前显示器（推荐）/ 窗口内容 / 窗口可见区域 / 自动 / 整个屏幕。
2. **Magpie 回想超分设置项**（✅ v1.2.5 起，v1.2.6 修正目标窗口）
   - 实现：`Services/MagpieService.cs` + 回想窗口 `ToggleFullscreen()` 钩子（**只有全屏才超分**）；配置项 `MagpieUpscaleSlideshow` / `MagpieScaleHotkey`（留空自动读）/ `MagpiePath`（留空自动探测）。
   - 关键情报（都实测过）：Magpie 热键存在 `%LOCALAPPDATA%\Magpie\config\v4\config.json` 的 `shortcuts.scale`，是 **uint32**：低 8 位 = 虚拟键码，`0x100/0x200/0x400/0x800` = Win/Ctrl/Alt/Shift（Magpie 源码 `AppSettings.cpp` 的 `EncodeShortcut`）。用户本机 = `1111` = **Alt+W**（不是默认的 Alt+Shift+A）。Magpie 默认值：Scale=Alt+Shift+A、窗口化缩放=Alt+Shift+Q、工具栏=Alt+Shift+D、截图=Alt+Shift+S。
   - 热键窗口类名 `Magpie_Hotkey`（消息窗口），`ShortcutAction` 枚举 Scale=0/WindowedModeScale=1/Toolbar=2/TakeScreenshot=3；日志在 `<Magpie.exe 所在目录>\logs\magpie.log`（UTF-8、约 500KB 轮转，写得很及时），热键激活会写「热键 Scale 激活（Keyboard Hook）」，缩放会话写「缩放开始 / 缩放结束 / 源矩形 / 源窗口已销毁 / 源窗口状态改变」——**这就是我们判断状态的唯一手段**。
   - **Magpie 热键是开关语义，不是「切换目标」**（实测）：`State()==Scaling` 时按下只会 **ToggleScaling → 停掉当前会话**。所以「游戏正在超分 + 打开全屏回想」的场景下第一下只是把游戏停了，必须再按一次才会超分我们的窗口 → `EnableMagpieUpscaleAsync()` 用日志确认（`LogHasScalingStartedSince`）后自动补发一次。
   - **Magpie 会在源窗口状态改变或销毁时自己结束缩放**（实测：全屏→窗口化触发「源窗口状态改变 → 缩放结束」；窗口销毁触发「源窗口已销毁 → 缩放结束」）。所以退出全屏时**不能盲目补发热键**（会把刚结束的会话又开到小窗口上）——`ReleaseMagpieUpscaleAsync()` 先 `IsScalingActive()` 查日志，它自己停了就不动。
   - **管理员权限不是问题**：v1.2.5 曾写「Magpie 以管理员运行时热键注入会被拦」，实测**是错的**——用户本机 Magpie 是管理员权限（日志「管理员: 是」），热键照样触发（日志有「热键 Scale 激活（Keyboard Hook）」）。被 UIPI 拦的只有 `PostMessage`（实测返回 False）。`IsProbablyElevated()` 现在只用在失败提示里。
   - 诊断开关：`--open-slideshow` = 打开回想（强制开超分）+ 自动进全屏→等 12s→退全屏→关闭，用于验证整条链路（**会真的发 Magpie 热键，会打断用户正在进行的游戏超分，慎用**）。
3. **（仍未做）窗口内容截图裁掉标题栏**：`CaptureMode.WindowContent` 走 `PrintWindow(PW_RENDERFULLCONTENT)`，会把非客户区（顶栏）一起截进去。可用 `GetWindowRect` 与 `ClientToScreen`/`GetClientRect` 的偏移量在位图上裁掉标题栏（注意 DPI 感知：进程若为 DPI-unaware，虚拟化坐标会与物理像素不一致，需先确认）。

### 下次要做（用户指定，2026-09-13；**本轮只记录，不处理**）
1. **Magpie 超分「第一次往往不成功，要点两次」**
   - 用户原话：开始测试时 Magpie 响应太快，导致要测试两次才能成功。
   - 现象是「第一次触发没生效、再来一次就好」，怀疑点（**均未验证**）：
     - a) `EnableMagpieUpscaleAsync()` 发完键只用 1.3s 窗口查日志（`LogHasScalingStartedSince`）判断有没有真的开始缩放；若 Magpie 这条日志落盘稍慢就会误判成「没开始」→ 自动补发第二次 → 那一下恰好把刚开起来的会话**停掉**，表现就是「没生效」。
     - b) 设置里的「测试」按钮在 InfoDialog 关闭后立刻发键，此刻前台窗口可能还在切换；Magpie 缩放的永远是**当前前台窗口**，可能缩到了别的窗口上。
     - c) Magpie 内部 `ShortcutService::_FireShortcut` 有 **100ms 限流**（同一热键 100ms 内只触发一次）。
   - 建议改法：把「是否已开始缩放」的判断改成**发键后轮询日志最多 3~4 秒**（确认到 `缩放开始` 就停），而不是固定等 1.3s 就下结论；发键前再 `Activate()` + 多等 200~300ms 确保前台是本窗口。
2. **回想顶栏的全屏按钮（⛶）按下也要触发超分**
   - 用户反馈：按按钮时没有超分（F11 那条链路是通的）。
   - 现状（代码层面）：`SlideshowWindow.xaml` 的 `FullscreenBtn.Click → OnToggleFullscreen → ToggleFullscreen()`，而 `ToggleFullscreen()` 里**已经**调了 `EnableMagpieUpscaleAsync()`/`ReleaseMagpieUpscaleAsync()`——理论上按钮和 F11 是同一条路径，所以很可能是与第 1 条同因（鼠标点击后的前台/激活时机、或日志误判导致补发把会话停掉）。
   - 下次实测：点 ⛶ 进全屏后看 `startup.log` 里有没有 `slideshow magpie: scaling fullscreen window with …`，以及是否出现「no scaling start seen, retrying once」。
3. **「使用时长统计」窗口改成 iOS「屏幕使用时间」那种卡片式页面**（✅ v1.3.0 已完成，2026-09-16）
   - 已实现：整页重做（`UsageStatsWindow` + 新控件 `Views/Controls/UsageBarChart.cs` + `Services/UsageAggregator.cs` + `Services/UsageRules.cs`），iOS 式卡片、悬浮数值气泡、锁屏绿色分段、多时间段（今天/7 天/本月/今年/自选范围）、应用名映射窗口（`Views/ProcessMapWindow`）、图标多来源解析（`Services/AppIconService.cs`）、响应式两列布局。
   - 参考图的 `UNLOCKS` → 换成「采集」（条数 + 每小时柱状图）；`EXTENSIONS` → 换成「统计范围」（区间/使用时长/锁屏时长/应用数/工具运行/黑名单）。
   - 关键情报与坑记录在下面「### 使用时间页（v1.3.0 起）」一节。

### 下次要做（用户指定，2026-09-19；**只记录，未实现**）
1. **回想（Slideshow）右下角显示日期时间**（用户想法，攒到下一批做）
   - 位置：右下角；窗口化与 F11 全屏都要显示（全屏时同样贴右下，注意 `FsOverlay` 底部浮层避让）
   - 内容可配置：
     - 日期格式：`年-月-日` / `月-日` / `年-月`（三种，可含"不显示日期"）
     - 时间格式：`时:分` / `时:分:秒`（可含"不显示时间"）
     - **默认显示什么**要能在设置里选（例如：不显示 / 仅日期 / 仅时间 / 日期+时间，且各自记住格式选择）
   - 实现要点：
     - 配置项加到 `HotkeyConfig`：`SlideshowClockMode`（0=不显示,1=仅日期,2=仅时间,3=日期+时间）、`SlideshowClockDateFormat`（0=yyyy-MM-dd,1=MM-dd,2=yyyy-MM）、`SlideshowClockShowSeconds`（bool）；记得在 `Clone()` 里带上
     - `SlideshowWindow.xaml` 加一个右下角 `TextBlock`（`HorizontalAlignment=Right`,`VerticalAlignment=Bottom`,`IsHitTestVisible=False`,半透明底或阴影，避免压在浅色图片上看不清；字号随窗口缩放可后续再说）
     - 刷新：用 `DispatcherTimer`——显示秒时 1 秒一跳，否则 10~30 秒或每分钟一跳即可（别用每秒刷新浪费）；窗口加载/切换图片时立即刷新一次
     - 设置界面在「回想」分组里加：显示内容下拉 + 日期格式下拉 + 秒勾选框（选"不显示"时把下面两项灰掉）
     - 注意别把时钟算进截图/OCR 内容（回想窗口本身不参与采集，无需处理）；Magpie 超分时会被一起放大，属预期
   - 参考：用户原话「回想在右下角显示日期时间，当然也可以自己选择显示年月日/月日/年月，时分（秒），并且有默认显示什么的设置。」
### 使用时间页（v1.3.0 起，改动前先读这节）
- **数据**（`Models/UsageData.cs`，`%LOCALAPPDATA%\GalQuoteCollector\usage.json`）：`Records[日期][进程key] = {Name, Seconds, Hourly[24], Path}`；两个保留 key：`__tool__`（工具运行）、`__locked__`（锁屏），都不计入「使用时长」。
  - 内层字典**必须大小写不敏感**：反序列化出来的默认字典是区分大小写的，`GetOrCreateDay()` 会重建。**注意不能用 `new Dictionary(day, OrdinalIgnoreCase)`**——旧数据里本来就有 `Steam.exe`/`steam.exe` 这种只差大小写的重复键，那样构造会抛 `An item with the same key has already been added`（v1.3.0 踩过，整段迁移被异常中断且不报错给用户）。要逐个搬并就地合并（`MergeRecord`）。
  - 一次性整理（`Normalize` = `MergeCaseDuplicateKeys` + `MoveLockProcesses`）：合并大小写重复、把锁屏进程的时长并入 `__locked__`（用户历史里 40.4 小时的「Windows 默认锁屏界面」= `LockApp.exe` 就是这么迁走的）。`MoveLockProcesses` 幂等且每次都跑，用户以后新加锁屏进程也会迁移。
- **锁屏识别**（`Services/UsageTracker.cs`，v1.3.2 重做；**不要再用「输入桌面不是 Default」单独判锁屏**）：
  1. **真正的会话锁定事件**：`SystemEvents.SessionSwitch` 里**只认 `SessionLock`/`SessionUnlock`**；`ConsoleDisconnect`/`RemoteDisconnect` 等一律忽略（会被远程工具/会话切换误触发）。事件丢失时靠下面第 3 条的"输入桌面回到 Default"自动恢复。
  2. **前台就是锁屏程序**：进程名在锁屏列表里（默认 `LockApp.exe`/`LogonUI.exe`，可在「应用名 / 锁屏进程」里加）→ 记锁屏。这是最直观的信号。
  3. **输入桌面非 Default 且 ≥2 分钟无任何输入**（`OpenInputDesktop` + `GetLastInputInfo`）→ 记锁屏。**必须有"无输入"这道约束**：v1.3.0/v1.3.1 只用桌面名，实测在没有真实锁屏事件的情况下也成立，把用户整晚玩游戏的时间（一天 229 分钟里约 145 分钟）记成了锁屏。屏保（桌面名 `Screen-saver`）算"离开"，符合预期。
  - 每次记锁屏都会写日志说明原因；`wallpaper64.exe` 这类平时也在跑的进程**不要**加进锁屏列表（会把正常时间算成锁屏；它当屏保时靠第 3 条即可识别）。
  - **Magpie 超分不影响统计**（实测 2026-09-19）：Magpie 的缩放窗口用 `SWP_NOACTIVATE` 显示，前台窗口仍是游戏本体（日志「缩放开始」期间 `GetForegroundWindow` 的进程 = 游戏），所以计时/锁屏判定都不受影响。
- **规则**（`Services/UsageRules.cs`，静态单例，`MainViewModel` 启动时 `Load(cfg)`、设置保存后重新 `Load`）：锁屏进程列表 + 进程名 → 显示名映射（`HotkeyConfig.UsageLockProcesses` / `UsageNameMap`）。
  - `GroupKey()`：设了自定义名就按自定义名归并（可把不同 exe 合成一条），否则按进程名 → **应用列表按进程名归组**（曾经按显示名归组，导致浏览器标签/歌名把同一应用拆成多条）。
  - `AutoName()`：历史里明显是窗口标题的名字（含 ` - `/`–`/`—`、以 `?`/`*` 开头、以 `.exe` 结尾）自动退回进程名。
- **图表控件**（`Views/Controls/UsageBarChart.cs`，自绘 FrameworkElement）：
  - 只有**绘图区**内才弹气泡（避开顶部气泡预留区与底部时间刻度条）；点击用**更严的柱子矩形命中**（`HitBarIndex`）——点在柱子之间的空白要取消钉住，只有真的点在柱子上才钉住/换一根；`Esc`/点图表以外由 `UsageStatsWindow` 统一取消（`_charts` 列表 + `Unpin`）。
  - 必须重写 `HitTestCore` 返回整块命中，否则 FrameworkElement 只在"画出来的内容"上可命中，空白处点击会漏掉。
  - 刻度密度按控件宽度自适应（窄 → 只显示 0/12/23），柱高 = `clamp(width*0.22, 112, 200)`。
- **图标**（`Services/AppIconService.cs`）：解析顺序 = 用户自定义 → 记录路径 → 运行中进程（`MainModule`/`QueryFullProcessImageName`）→ MuiCache（游戏命中率高）→ 卸载信息 → App Paths → Everything HTTP（`127.0.0.1:80/8080`，需用户开 HTTP 服务）→（手动）常见目录扫描。位图在内存缓存（含负缓存），重建列表不会先闪首字母；右键行可自定义（图片/exe/自动搜索/恢复）。
- **应用行点击**：`BuildAppRow` 里 `outer.Background = Brushes.Transparent` 是**整行可点**的关键（StackPanel 无背景时只有文字本身可命中，点在名称与时长之间的空白会漏到卡片上）；点击 → `OpenAppDetail()` → `Views/AppUsageWindow`（单应用详情：平均每天 / 区间合计 / 该应用自己的柱状图 / 统计范围含「占同期总时长百分比」，自带时间段切换）。`UsageAggregator.Build(..., onlyKeys:)` 支持只统计指定进程（详情页用）。
- **滚动**：`Services/SmoothScroll.cs` 在 `App.OnStartup` 里用类级处理器把滚轮/触摸板滚动改成半速按像素滚动（`Factor = 0.5`）。
- **窗口行为**：点窗口外自动关闭（`Deactivated` → 等 650ms 再确认：仍失活、无本程序其它窗口在前台、没有菜单抓鼠标才关，并写日志 `usage window: 点窗口外 → 自动关闭`）；子对话框/文件对话框期间用 `_childDialogs` 计数保护。
- 诊断：`--open-usage`（真实数据）、`--usage-demo`（**只读**，注入合成的锁屏/使用样例，用来在没真锁屏时验证渲染）。注意 demo 只读不落盘，但追踪器本身仍会正常保存真实数据。

### 内网网页 + 每日备份（v1.3.3 起，改动前先读这节）
- **内网网页**（`Services/WebServerService.cs` + `Services/WebPage.cs`）：在**程序进程内**用 `TcpListener` 起一个极简 HTTP/1.1 服务（**不需要管理员、不需要 URL ACL**，便携版也能用），手机/电脑浏览器打开 `http://本机IP:端口/` 即可查看/搜索/修改/导出语录。
  - 设置项（`HotkeyConfig`）：`WebEnabled`（默认 **false**）、`WebPort`（默认 8088）、`WebAccessCode`（默认空 = **局域网免密**；填了就要求 `?k=码`，页面响应会带 `Set-Cookie: k=…`）、`WebUseHttps`（默认 **true**）。
  - **HTTPS**（用户要求）：同一端口上用 `SslStream`（TLS1.2/1.3）+ **自签证书**；证书首次启动自动生成到 `<数据目录>\web-cert.pfx`（CN=Gal Quote Collector Web，SAN = 机器名 + localhost + 127.0.0.1 + 所有本机 IPv4，5 年有效）。设置里有「导出证书（给手机安装）」→ 导出 `.cer` 并用 explorer `/select` 定位；Android 装到「CA 证书」、iPhone 装描述文件后到「证书信任设置」里打开开关。
  - **注意**：开 HTTPS 后该端口只讲 TLS，浏览器必须写 `https://`（写 `http://` 会 ERR_EMPTY_RESPONSE）；不想用时设置里关掉即可回到明文 HTTP。
  - 连接排障（实测踩过）：本机 `http(s)://本机IP:端口` 通、防火墙规则也允许（`Gal-quote-tool` Allow + Profile=Private，WLAN 是 Private）时，手机连不上通常是**网络层**：AP 隔离/客户端隔离、访客网络、手机侧 VPN/代理（用户手机也装了代理类工具）、或电脑侧 FlClash 的 TUN 模式。设置界面已写这些提示。
  - 路由：`GET /`（内嵌单页）、`GET /api/meta`、`GET /api/quotes?q=&game=&group=&tag=&offset=&limit=`、`GET /api/quotes/{id}`、`PUT /api/quotes/{id}`（改 text/gameName/notes/capturedAt + groups/tags/newNames，按名字自动建分组标签）、`DELETE /api/quotes/{id}`、`GET /api/shot/{id}`（原图）、`GET /api/export?format=json|md`（复用 `ExportService`）。
  - **只改语录**（用户选定）：不暴露设置、映射、黑名单、游戏名规则。
  - 数据库安全：直接复用程序自己的 `StorageService`（它内部有 `_sync` 锁，线程安全），**不要**另外开第二个进程/连接写库。
  - `DELETE` **只删数据行，不删截图文件**（有意为之：截图更宝贵，误删可从「未关联截图」找回）。
  - 网页是单文件内嵌 HTML/CSS/JS（内网可能没外网，**不能用 CDN**）；响应式断点 720px（单列、弹窗贴底、缩略图 200px）与 520px（标题独占一行、按钮换行不裁切）；缩略图 `max-height` 桌面 280px / 手机 200px + 点击放大。
  - 安装版在 `installer.iss` 的 `[Run]` 里用 `netsh advfirewall` 放行 **8088**（profile=private）；用户改端口需要自己放行，便携版第一次启动 Windows 会弹防火墙允许框。
  - 想让公网访问：让用户**自己设访问码**（当前无 HTTPS/双因素），并建议走路由器端口映射 + 反向代理加 HTTPS。
- **每日首次启动自动备份**（`Services/BackupService.cs`）：`HotkeyConfig.BackupEnabled`（默认 **true**）/`BackupDirectory`（留空 = 数据目录下 `backups`）。
  - 内容：`quotes.db` + `usage.json` + `settings.json`，**外加截图目录整份复制**到 `backups\backup-yyyy-MM-dd_HHmmss\screenshots\`（用户要求含截图，体积大；截图复制失败只写日志、不影响数据文件）。
  - 每天只备份一次（同一天再启动跳过）；`PruneOldBackups` 只保留**最近 3 天**（按目录名日期判断，删不掉就忽略）。
  - 设置界面有「浏览…/打开/立即备份」按钮（`BrowseForFolder` 复用 Shell.Application 那套）。
  - 备份路径解析注意：`ScreenshotDirectory` 为空时用「图片\GalQuoteCollector」，与截图目录设置保持一致。
### 热键与截图（v1.3.2 起）
- **吞掉截图热键**（`HotkeyService.SwallowHotkeys`，默认开，设置里可关）：匹配到截图热键时钩子 `return 1`，并**连抬起一起吞**（避免前台程序收到悬空按键）。很多引擎自带 `Alt+E` 之类热键，用户设置的截图热键若撞上，游戏会同时弹窗。
  - **本程序自己的窗口里永远不吞也不触发**（`IsOwnProcessForeground()` 比对前台窗口 PID）。
  - 用户热键是 `Alt+E`（settings.json 里 `Alt=true`、`VirtualKey=69`）。`ConsoleDisconnect`/`RemoteDisconnect` 不再影响锁屏状态。
- **改键期间挂起**：`MainViewModel` 打开设置窗口时 `_hotkeyService.Suspend()`、关闭后 `Resume()`——否则用户在设置里按新组合会被当成一次截图。
- **历史数据修正开关**（App.OnStartup 解析，改完写 `startup.log`）：
  - `--fix-lock <yyyy-MM-dd> <进程名> [起小时 止小时]`：把某天（可选小时范围）误记的锁屏时长改记到指定应用。
  - `--move-usage <yyyy-MM-dd> <源进程> <目标进程> [起小时 止小时]`：把某天的时长从一个应用改记到另一个；目标当天没有记录时会从其它日期沿用显示名和图标路径。
  - 注意：进程名带空格时必须整体加引号（`Start-Process -ArgumentList` 不会自动加）；**运行完要立刻退出进程**，否则追踪器会把内存里的旧数据写回去（首个 tick 在 60 秒后，8 秒内杀掉即可）。
  - 改历史前先备份 `usage.json`（`%TEMP%\usage-before-*.json`）。
  - 已知小瑕疵：`--fix-lock` 在目标应用当天没有记录时会新建一条、显示名直接用 key 原文（`Xxx.exe`），不像 `--move-usage` 会去别的日期沿用显示名——用完手工改一下 Name 即可（或用「应用名 / 锁屏进程」窗口改）。
  - 2026-09-18 实测数据（用户确认过的正确拆分）：夏空カナタ_chs.exe 151 分 + Hollow Knight Silksong 84 分 + 其他 ≈ 工具运行 269 分，锁屏 0。

### Magpie 日志是缓冲写盘的（v1.3.3 实测，重要）
- **不要用 Magpie 日志判断"超分是否已开始"**：`缩放开始` 那行可能几秒后才落盘，实测 4 秒轮询也读不到 → 误判成"没开始" → 补发热键 → 恰好把刚开起来的会话**停掉**（这就是"要点两次 / ⛶ 按钮没反应"的真凶）。
- 改用**实时信号** `MagpieService.IsScalingWindowVisible()`：枚举顶层窗口，找可见的 `Window_Magpie_*` 且尺寸 ≥60% 屏幕（工具栏之类小窗不算）。
- 逻辑（用户要求，保持简单）：Magpie 在跑 → 直接发超分热键；**按之前若正在缩放别的窗口**（窗口枚举可见）→ 再补发一次（热键是开关语义，第一下只会停掉原会话）；Magpie 没跑 → 只提示一句，不做别的放大。
- 退出全屏：先等缩放窗口消失（Magpie 会自己结束），再决定是否补发；`WaitForScalingWindowAsync(false, 2500)`。

### 截图黑边 + 回想白底（v1.3.3 起）
- `Services/BlackBarCropper.cs`：检测四周"整行/整列 ≥98.5% 像素 RGB<34"的黑边；裁掉面积 >45% 或剩余过小 → 判定暗色画面，放弃。
  - 文件级：`Crop(path)`（就地，先写 `.crop.tmp` 再替换）、`WhiteFill(path)`（涂白、尺寸不变）；采集后自动裁（配置 `CropBlackBars`，默认开）；「···」→「裁掉截图黑边（全部）」批量；诊断开关 `--crop-bars <文件|目录>`。
- 回想显示级（**不改文件**）：`Views/Controls/SlideshowImageBars.Apply(bitmap, mode)`，mode 0 原样 / 1 `CroppedBitmap` / 2 复制像素把黑边写成白；顶栏按钮或 `B` 键切换，配置 `SlideshowBarsMode` 记忆；全屏背景随模式黑/白。诊断开关 `--bars-test <文件> <0|1|2>`。
- 网页端同样有：`GET /api/shot/{id}?bars=0|1|2` 在内存里处理（手机回想模式用这个）。

### 内网网页的回想模式（v1.3.3）
- `WebPage.cs` 里的 `#viewer` 全屏层：左右滑动/点击两侧切换、自动播放（5s）、显示游戏名+正文+位置、`原样/裁掉黑边/黑边涂白` 切换；`?view=1` 直接进回想（手机可收藏书签）；服务端 `GET /api/quotes/{id}/export|POST /api/import` 支持单条含图导入导出。
### 自动更新与代码签名（2026-08-21 起）
- 自动更新：`Services/UpdateService.cs`（GitHub latest API → 优先 `*_Setup.exe` 资产直链 + sha256 digest 校验下载）+ `Views/UpdateDialog`（更新日志 / 进度条 / 下载 / 跳过此版本 / 立即安装→退出并启动安装器）。入口：「···」菜单 → 检查更新；启动时自动检查；跳过版本存 settings.json 的 `SkippedUpdateVersion`。
- 代码签名：自签名证书 `CN=Gal Quote Collector`（CurrentUser\My，指纹 `1C3987F6C7A8E67FF6C191AD220C7A6EDE4FC7A7`；pfx/cer 在本地 `cert/`，gitignored；pfx 密码 `GalQuote2026-CodeSign`）。signtool：`C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe`，命令 `signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /sha1 <指纹> <file>`。
- **发布顺序（重要）**：打包 → 先签 `publish-installer\Gal-quote-tool.exe` → 再 Compress-Archive 生成 zip → 再跑 ISCC → 再签三个 exe（FDD/SCD/Setup）→ curl 上传（curl 需 `--ssl-no-revoke`；180MB 文件上传慢，后台任务 + 大超时）。
- 上传发布附件：删除旧附件（DELETE /releases/assets/{id}）→ POST `upload_url?name=xxx`；**GitHub 令牌用 CredRead 从 Windows 凭据管理器读**（target `git:https://github.com`，用户 `Liushiweiying`，令牌 40 位）：`Advapi32!CredRead(target, 1, 0, out ptr)` + `CREDENTIAL.CredentialBlob`（Unicode）。**不要用 `"protocol=https..." | git credential fill`**——PowerShell 把字符串管道喂给 native exe 的 stdin 现在会失败，git 报 `refusing to work with credential missing protocol field`（v1.3.1 发布时踩过；`cmd /c "git credential fill < file"` 也可用，但 CredRead 更省事）。
- **上传务必用 curl 配置文件（`curl.exe --ssl-no-revoke -sS -K xxx.cfg`）**：本机 shell 会把带空格的参数拆开（`-H "Authorization: token gho_..."` 被拆成 3 个参数 → curl 把 token 当成 URL → `curl: (3) URL rejected: Bad hostname`，**四个文件全部静默失败**，而 PowerShell 仍打印自定义的“uploaded”）。cfg 写法：`url = "https://uploads.github.com/repos/<owner>/<repo>/releases/<id>/assets?name=<name>"` / `request = "POST"` / `header = "Authorization: token <token>"` / `header = "Content-Type: application/octet-stream"` / `data-binary = "@D:/path/file.exe"`（路径用正斜杠，避免 cfg 里的反斜杠转义）。上传后必须用 API 复核 `assets` 的 name/size/state，别只看脚本自己的日志。

### 已修的坑（避免重复踩）
- **网络**：用户平时挂加速器（代理）才能稳定访问 GitHub；**加速器没开时**会出现 `git credential fill` 报 `refusing to work with credential missing protocol field`、GitHub API/上传超时等怪现象。遇到这类报错先问一句是不是没开加速器，再考虑改代码/换方案。
- **WPF Slider 的 `ValueChanged` 会在 `InitializeComponent()` 期间触发**（设置 `Minimum` 时把默认值 0 钳到 Minimum）。若处理器引用了 XAML 中**声明在后面**的元素 → NullReferenceException → 打开窗口即崩溃。已给 `SettingsWindow` 的 Jpeg/Delay 两个滑杆加空值保护。
- `App.OnStartup` 已注册 `DispatcherUnhandledException` / `AppDomain.UnhandledException` / `TaskScheduler.UnobservedTaskException`，异常写入 `%LOCALAPPDATA%\GalQuoteCollector\startup.log`（UI 线程异常不再闪退）。
- 诊断开关：`Gal-quote-tool.exe --open-settings` 启动后自动打开设置窗口（用于复现/排查）。
- **测试前必须先 `taskkill /IM Gal-quote-tool.exe /F`**：程序在运行时 exe 被锁，`dotnet build` 会静默失败（仍是旧二进制），导致"修了却没生效"的假象。构建后核对 exe 时间戳。
- **诊断开关补充**：`--minimized`（托盘启动）、`--fix-ttb`（跑完整 TTB 开机修复流程，等 shell → 等 20s → 重启 → 像素校验，约 10~40s）。TTB 采样在"任务栏被全屏/Magpie 缩放窗口遮住"时会全黑，此时 `CanTell()` 返回 false、跳过补重启，属预期。
- 引用 `Microsoft.Win32.Registry` / `System.Drawing` 等类型时不要新加 `using` 去和 WPF 的 `Application`、`MessageBox` 冲突；`MagpieService` 里用的是全限定名。
