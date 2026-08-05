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
    public int SlideshowMode { get; set; } // 0=时间顺序, 1=随机顺序
    public bool SlideshowLoop { get; set; }
    public string FontFamily { get; set; } = "Segoe UI";
    public string SlideshowChineseFont { get; set; } = "Microsoft YaHei";
    public string SlideshowEnglishFont { get; set; } = "Segoe UI";
    public bool EnableUsageTracking { get; set; }
    public bool HideUnrecognized { get; set; }
    public string ScreenshotDirectory { get; set; } = "";
    public string ScreenshotFormat { get; set; } = "png";
    // 开机时自动重启一次 TranslucentTB，修复任务栏透明偶尔失效
    public bool EnableTranslucentTbFix { get; set; }
    // Secondary hotkey for adding screenshot to current quote
    public bool AddShotAlt { get; set; } = true;
    public bool AddShotControl { get; set; } = true;
    public bool AddShotShift { get; set; }
    public bool AddShotWin { get; set; }
    public uint AddShotVirtualKey { get; set; } = 0x5A; // Z
    public List<GameNameRule> GameNameRules { get; set; } = new();

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
        sb.Append((char)VirtualKey);
        return sb.ToString();
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
        sb.Append((char)AddShotVirtualKey);
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
            SlideshowMode = SlideshowMode,
            SlideshowLoop = SlideshowLoop,
            FontFamily = FontFamily,
            SlideshowChineseFont = SlideshowChineseFont,
            SlideshowEnglishFont = SlideshowEnglishFont,
            EnableUsageTracking = EnableUsageTracking,
            HideUnrecognized = HideUnrecognized,
            ScreenshotDirectory = ScreenshotDirectory,
            ScreenshotFormat = ScreenshotFormat,
            EnableTranslucentTbFix = EnableTranslucentTbFix,
            AddShotAlt = AddShotAlt, AddShotControl = AddShotControl,
            AddShotShift = AddShotShift, AddShotWin = AddShotWin,
            AddShotVirtualKey = AddShotVirtualKey,
            GameNameRules = GameNameRules.Select(r => new GameNameRule { Match = r.Match, Name = r.Name }).ToList()
        };
    }
}
