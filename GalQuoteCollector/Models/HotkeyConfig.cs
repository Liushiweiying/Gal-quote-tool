using System.Text;
using System.Text.Json.Serialization;

namespace GalQuoteCollector.Models;

public class HotkeyConfig
{
    public bool Alt { get; set; }
    public bool Control { get; set; } = true;
    public bool Shift { get; set; }
    public bool Win { get; set; } = true;
    public uint VirtualKey { get; set; } = 0x5A; // Z
    public bool AutoStart { get; set; }
    public int CaptureDelayMs { get; set; } = 200;
    // 触发截图热键时是否吞掉这次按键（很多游戏引擎也用 Alt+E 之类的键，不吞会同时弹出游戏窗口）
    public bool SwallowCaptureHotkey { get; set; } = true;
    // 截图后自动裁掉四周纯黑边（游戏比例和显示器不一致时的左右/上下黑框）
    public bool CropBlackBars { get; set; } = true;
    // 每天首次启动自动备份（quotes.db / usage.json / settings.json），只保留最近 3 天
    public bool BackupEnabled { get; set; } = true;
    public string BackupDirectory { get; set; } = ""; // 留空 = 安装目录上一级的 Gal Quote Tool Backup
    public int BackupKeepCount { get; set; } = 3; // 最多保留几份备份
    // 内网网页（手机/电脑访问）：默认关闭，端口可改，访问码留空 = 局域网免密
    public bool WebEnabled { get; set; }
    public int WebPort { get; set; } = 8088;
    public string WebAccessCode { get; set; } = "";
    // 用 HTTPS（自签证书，手机第一次会提示不安全，继续访问即可；也可导出发到手机安装）
    public bool WebUseHttps { get; set; } = true;
    public int SlideshowMode { get; set; } // 0=时间顺序, 1=随机顺序
    public bool SlideshowLoop { get; set; }
    /// <summary>
    /// 回想显示时的黑边处理：0=原样, 1=裁掉黑边, 2=黑边涂白。
    /// 只在显示时生效，**不修改文件**；回想窗口里按 B 键循环切换。
    /// </summary>
    public int SlideshowBarsMode { get; set; }
    /// <summary>回想文字底样式：0=黑底白字（默认），1=白底黑字。</summary>
    public int SlideshowTextStyle { get; set; }
    /// <summary>回想文字底透明度 0.0-1.0（默认 0.5）。</summary>
    public double SlideshowTextOpacity { get; set; } = 0.5;
    public string FontFamily { get; set; } = "Segoe UI";
    public string SlideshowChineseFont { get; set; } = "Microsoft YaHei";
    public string SlideshowEnglishFont { get; set; } = "Segoe UI";
    public bool EnableUsageTracking { get; set; }
    public bool HideUnrecognized { get; set; }
    public string ScreenshotDirectory { get; set; } = "";
    public string ScreenshotFormat { get; set; } = "png";
    // JPG 截图质量（50-100，仅 JPG 格式生效）
    public int JpegQuality { get; set; } = 90;
    // 截图方式: auto / window / region / monitor / screen
    // v1.2.5 起默认「当前显示器」：Magpie 超分不会让游戏顶栏消失，按窗口截取会把标题栏截进去
    public string CaptureMode { get; set; } = "monitor";
    // 检测到 Magpie 运行时改用「窗口内容」抓游戏原生分辨率画面（默认关：抓超分后的显示器画面）
    public bool PreferNativeCaptureWhenMagpie { get; set; }
    // v1.2.5 一次性迁移标记（截图方式默认值从 auto 改为 monitor）
    public bool CaptureDefaultsMigratedV125 { get; set; }

    // ── 回想时用 Magpie 超分 ──
    // 打开回想窗口后自动触发 Magpie 缩放该窗口
    public bool MagpieUpscaleSlideshow { get; set; }
    // Magpie「缩放窗口」热键；留空则自动读取 Magpie 配置，读不到时用 Alt+Shift+A（Magpie 默认值）
    public string MagpieScaleHotkey { get; set; } = "";
    // Magpie.exe 路径；留空自动探测
    public string MagpiePath { get; set; } = "";

    // 使用统计里每个应用的自定义图标：进程名 → exe / 图片路径（留空 = 自动解析）
    public Dictionary<string, string> UsageIconOverrides { get; set; } = new();
    // 使用时间页：上次选的时间段（day/week/month/year/custom）与自选区间、窗口尺寸
    public string UsagePeriodMode { get; set; } = "day";
    public DateTime? UsageRangeFrom { get; set; }
    public DateTime? UsageRangeTo { get; set; }
    public double UsageWindowWidth { get; set; }
    public double UsageWindowHeight { get; set; }
    // 除默认锁屏进程（LockApp.exe / LogonUI.exe）外，用户额外指定的锁屏相关进程（如壁纸软件）
    public List<string> UsageLockProcesses { get; set; } = new();
    // 进程名 → 显示名 的自定义映射（如 msedge.exe → Edge）
    public Dictionary<string, string> UsageNameMap { get; set; } = new();
    // 用户选择跳过的更新版本 tag（自动检查时不再提示）
    public string SkippedUpdateVersion { get; set; } = "";
    // 开机时自动重启一次 TranslucentTB，修复任务栏透明偶尔失效
    public bool EnableTranslucentTbFix { get; set; }
    // OCR 引擎: "win" = Windows 内置 OCR, "local" = 本地模型 (Ollama), "rapid" = RapidOCR (本地离线)
    public string OcrEngine { get; set; } = "win";
    public string LocalOcrUrl { get; set; } = "http://localhost:11434";
    public string LocalOcrModel { get; set; } = "qwen2.5vl:7b";
    // 装有 RapidOCR 的 python.exe 路径；留空则自动探测
    public string RapidOcrPython { get; set; } = "";
    // Secondary hotkey for adding screenshot to current quote
    public bool AddShotAlt { get; set; } = true;
    public bool AddShotControl { get; set; } = true;
    public bool AddShotShift { get; set; }
    public bool AddShotWin { get; set; }
    public uint AddShotVirtualKey { get; set; } = 0x5A; // Z
    public List<GameNameRule> GameNameRules { get; set; } = new();

    // Persisted main-window bounds (NaN = never saved yet / use default)
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public double WindowWidth { get; set; } = double.NaN;
    public double WindowHeight { get; set; } = double.NaN;

    /// <summary>
    /// Convert to modifier flags for RegisterHotKey.
    /// </summary>
    public uint ToModifiers()
    {
        uint mod = 0;
        if (Alt) mod |= 0x0001;
        if (Control) mod |= 0x0002;
        if (Shift) mod |= 0x0004;
        if (Win) mod |= 0x0008;
        return mod;
    }

    /// <summary>
    /// Human-readable display string, e.g. "Ctrl+Win+Z".
    /// </summary>
    public string ToDisplayString()
    {
        var sb = new StringBuilder();
        if (Control) sb.Append("Ctrl+");
        if (Alt) sb.Append("Alt+");
        if (Shift) sb.Append("Shift+");
        if (Win) sb.Append("Win+");
        sb.Append(KeyName(VirtualKey));
        return sb.ToString();
    }

    /// <summary>
    /// Map a virtual-key code to a human-readable name (letters, digits, F-keys,
    /// arrows, punctuation). Previously the raw VK code was cast to char, which
    /// rendered e.g. F5 (0x74) as "t".
    /// </summary>
    public static string KeyName(uint vk)
    {
        if (vk >= 'A' && vk <= 'Z') return ((char)vk).ToString();
        if (vk >= '0' && vk <= '9') return ((char)vk).ToString();
        if (vk >= 0x70 && vk <= 0x87) return $"F{vk - 0x6F}";
        return vk switch
        {
            0x08 => "Backspace", 0x09 => "Tab", 0x0D => "Enter", 0x1B => "Esc",
            0x20 => "Space", 0x21 => "PageUp", 0x22 => "PageDown", 0x23 => "End",
            0x24 => "Home", 0x25 => "←", 0x26 => "↑", 0x27 => "→", 0x28 => "↓",
            0x2D => "Insert", 0x2E => "Delete",
            0xBA => ";", 0xBB => "=", 0xBC => ",", 0xBD => "-", 0xBE => ".",
            0xBF => "/", 0xC0 => "`", 0xDB => "[", 0xDC => "\\", 0xDD => "]",
            0xDE => "'",
            _ => ((char)vk).ToString()
        };
    }

    /// <summary>
    /// Whether at least one modifier is selected and key is not a modifier-only press.
    /// </summary>
    public uint ToAddModifiers()
    {
        uint mod = 0;
        if (AddShotAlt) mod |= 0x0001;
        if (AddShotControl) mod |= 0x0002;
        if (AddShotShift) mod |= 0x0004;
        if (AddShotWin) mod |= 0x0008;
        return mod;
    }

    public string ToAddShotDisplay()
    {
        var sb = new StringBuilder();
        if (AddShotControl) sb.Append("Ctrl+");
        if (AddShotAlt) sb.Append("Alt+");
        if (AddShotShift) sb.Append("Shift+");
        if (AddShotWin) sb.Append("Win+");
        sb.Append(KeyName(AddShotVirtualKey));
        return sb.ToString();
    }

    public bool IsValid()
    {
        return (Alt || Control || Shift || Win) && VirtualKey > 0;
    }

    public HotkeyConfig Clone()
    {
        return new HotkeyConfig
        {
            Alt = Alt,
            Control = Control,
            Shift = Shift,
            Win = Win,
            VirtualKey = VirtualKey,
            AutoStart = AutoStart,
            CaptureDelayMs = CaptureDelayMs,
            SwallowCaptureHotkey = SwallowCaptureHotkey,
            CropBlackBars = CropBlackBars,
            BackupEnabled = BackupEnabled,
            BackupDirectory = BackupDirectory,
            BackupKeepCount = BackupKeepCount,
            WebEnabled = WebEnabled,
            WebPort = WebPort,
            WebAccessCode = WebAccessCode,
            WebUseHttps = WebUseHttps,
            SlideshowMode = SlideshowMode,
            SlideshowLoop = SlideshowLoop,
            SlideshowBarsMode = SlideshowBarsMode,
            SlideshowTextStyle = SlideshowTextStyle,
            SlideshowTextOpacity = SlideshowTextOpacity,
            FontFamily = FontFamily,
            SlideshowChineseFont = SlideshowChineseFont,
            SlideshowEnglishFont = SlideshowEnglishFont,
            EnableUsageTracking = EnableUsageTracking,
            HideUnrecognized = HideUnrecognized,
            ScreenshotDirectory = ScreenshotDirectory,
            ScreenshotFormat = ScreenshotFormat,
            JpegQuality = JpegQuality,
            CaptureMode = CaptureMode,
            PreferNativeCaptureWhenMagpie = PreferNativeCaptureWhenMagpie,
            CaptureDefaultsMigratedV125 = CaptureDefaultsMigratedV125,
            MagpieUpscaleSlideshow = MagpieUpscaleSlideshow,
            MagpieScaleHotkey = MagpieScaleHotkey,
            MagpiePath = MagpiePath,
            UsageIconOverrides = new Dictionary<string, string>(UsageIconOverrides),
            UsagePeriodMode = UsagePeriodMode,
            UsageRangeFrom = UsageRangeFrom,
            UsageRangeTo = UsageRangeTo,
            UsageWindowWidth = UsageWindowWidth,
            UsageWindowHeight = UsageWindowHeight,
            UsageLockProcesses = new List<string>(UsageLockProcesses),
            UsageNameMap = new Dictionary<string, string>(UsageNameMap),
            SkippedUpdateVersion = SkippedUpdateVersion,
            EnableTranslucentTbFix = EnableTranslucentTbFix,
            OcrEngine = OcrEngine,
            LocalOcrUrl = LocalOcrUrl,
            LocalOcrModel = LocalOcrModel,
            RapidOcrPython = RapidOcrPython,
            AddShotAlt = AddShotAlt, AddShotControl = AddShotControl,
            AddShotShift = AddShotShift, AddShotWin = AddShotWin,
            AddShotVirtualKey = AddShotVirtualKey,
            WindowLeft = WindowLeft, WindowTop = WindowTop,
            WindowWidth = WindowWidth, WindowHeight = WindowHeight,
            GameNameRules = GameNameRules.Select(r => new GameNameRule { Match = r.Match, Name = r.Name }).ToList()
        };
    }
}
