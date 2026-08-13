# Gal 语录收藏工具

[English](README.md) | [日本語](README.ja.md)

![Platform](https://img.shields.io/badge/platform-Windows-lightgrey)
![.NET](https://img.shields.io/badge/.NET-8.0-purple)

一键收藏 galgame 台词。截图 + OCR 自动识别 + 标签分组 + 回想幻灯片。

![主窗口](images/main.png)

## 功能

| 功能 | 说明 |
|---|---|
| **一键采集** | 全局热键 `Ctrl+Win+Z`（可自定义），截图 → OCR → 自动保存；OCR 可选 Windows 内置 / Ollama 本地模型 / RapidOCR 三种引擎 |
| **游戏名识别** | 自动剥离引擎/日期后缀，支持自定义匹配规则 |
| **标签** | 给语录打标签，按标签筛选 |
| **分组** | 创建分组合集，一条语录可属于多个分组 |
| **回想幻灯** | 全屏模式，键盘翻页，随机跳转，置顶 |
| **导出** | Markdown / JSON，全部或按分组 |
| **导入** | 导入已导出的 Markdown / JSON |
| **统计** | 按游戏 / 标签 / 分组 / 时间统计 |
| **开机自启** | 随 Windows 启动，最小化到托盘 |
| **采集延迟** | 0~2000ms 可调 |

## 下载

| 文件 | 大小 | 说明 |
|---|---|---|
| `Gal-quote-tool.exe` | ~28MB | 单文件，需 .NET 8 运行时 |
| `Gal-quote-tool_selfcontained.exe` | ~190MB | 单文件自包含，无需运行时 |
| `Gal-quote-tool_Setup.exe` | ~55MB | Inno Setup 安装包（自包含，含卸载程序） |
| `publish-folder.zip` | ~77MB | 自包含压缩包，解压即用 |

> 数据目录：`%LOCALAPPDATA%\GalQuoteCollector\`

## 使用

1. 运行 exe，自动缩小到系统托盘
2. 打开 galgame，按 `Ctrl+Win+Z` 采集
3. 右上角弹出通知（点击打开主窗口）

### 快捷键

| 按键 | 回想 / 全屏中 |
|---|---|
| `Ctrl+Win+Z` | 采集（可自定义） |
| `←` / `→` | 上一条 / 下一条 |
| `Space` | 下一条 |
| `Enter` | 随机跳转 |
| `F11` | 全屏切换 |
| `F2` | 置顶切换 |
| `Esc` | 退出全屏 / 关闭 |

### 分组
- 详情面板输入分组名 → 创建
- 点击灰色标签 → 加入（变绿）
- 点击绿色标签 → 移出
- 右键标签 → 删除分组
- 工具栏选分组 → 筛选

### 游戏名匹配规则
设置中添加规则。如 `Summer Pockets → Summer Pockets REFLECTION BLUE`，标题含 "Summer Pockets" 的窗口自动归到该游戏名下。

## 开发

### 环境
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Windows 10+（中文 OCR 语言包）

### 构建
```bash
cd GalQuoteCollector
dotnet build
dotnet run
```

### 发布
```bash
# 框架依赖（小）
dotnet publish -r win-x64 -c Release --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./publish

# 自包含（便携）
dotnet publish -r win-x64 -c Release --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./publish
```

## 技术栈
- **.NET 8 + WPF** — 桌面框架
- **Windows.Media.Ocr** — 原生 OCR（可切换 Ollama 本地模型 / RapidOCR）
- **SQLite** — 本地数据库
- **CommunityToolkit.Mvvm** — MVVM
- **Hardcodet.NotifyIcon.Wpf** — 系统托盘
- **System.Drawing.Common** — 截图与图像处理

## 数据目录
```
%LOCALAPPDATA%\GalQuoteCollector\
├── quotes.db
├── settings.json
└── screenshots\
```

## 更新日志

### v1.4.4（2026-08-13）
- **新增** RapidOCR 本地离线 OCR 引擎（设置 → OCR 引擎，需装有 rapidocr-onnxruntime 的 Python）
- **修复** SQLite 多线程并发访问竞态（后台加载与热键采集同时进行时可能报错）
- **修复** 热键显示错误（如 F5 显示为 "t"）
- **修复** 采集热键与补拍热键相同时无提示
- **修复** 导入压缩包可被路径穿越、静默覆盖本地设置与使用记录（现在会先确认）
- **修复** 多显示器下全屏截图只截主屏、副屏负坐标截图错误
- **修复** 窗口化游戏被强制整屏截图（改为窗口覆盖屏幕 ≥90% 才全屏）
- **修复** 大量语录时标签/分组筛选卡顿（消除逐条查询）
- **修复** 批量删除不询问截图处理（与单条删除一致）
- **修复** 采集失败/未检测到窗口时主窗口保持最小化
- **修复** 采集延迟 0ms 时可能截到自身窗口（强制 ≥100ms）
- **优化** 启动日志与 OCR 缓存设上限；版本号统一从程序集读取
