using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using GalQuoteCollector.Models;

namespace GalQuoteCollector.Services;

/// <summary>
/// 设置信息的导出 / 导入（v1.3.6 起）——换系统、换电脑、双系统共用时把配置一起搬过去。
///   · 导出包是一个 zip：<c>settings.json</c>（完整设置，含访问码与两步验证密钥）+ <c>settings.md</c>（人可读摘要，密钥打码）+ <c>说明.txt</c>
///   · <c>settings.md</c> 可以单独拿去分享/贴给客服，因为里面的敏感信息已经打码
///   · 导入接受 .zip（取里面的 settings.json）或直接 .json
/// </summary>
public static class SettingsIo
{
    public const string JsonEntry = "settings.json";
    public const string MarkdownEntry = "settings.md";
    public const string ReadmeEntry = "说明.txt";

    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        // 窗口尺寸"未设置"是 NaN，必须走这个转换器才能写成合法 JSON（null）
        Converters = { new NaNDoubleConverter() },
    };

    /// <summary>导出成一个 zip 包，返回写出的文件路径。</summary>
    public static string ExportZip(HotkeyConfig cfg, string targetZipPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(targetZipPath))!);
        using var fs = File.Create(targetZipPath);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create, false);

        AddText(zip, JsonEntry, JsonSerializer.Serialize(cfg, WriteOpts));
        AddText(zip, MarkdownEntry, BuildMarkdown(cfg, maskSecrets: true));
        AddText(zip, ReadmeEntry, ReadmeText());
        return targetZipPath;
    }

    /// <summary>导出成两个独立文件（settings.json + settings.md），返回两个路径。</summary>
    public static (string json, string md) ExportFiles(HotkeyConfig cfg, string directory, string baseName)
    {
        Directory.CreateDirectory(directory);
        var jsonPath = Path.Combine(directory, baseName + ".json");
        var mdPath = Path.Combine(directory, baseName + ".md");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(cfg, WriteOpts), new UTF8Encoding(false));
        File.WriteAllText(mdPath, BuildMarkdown(cfg, maskSecrets: true), new UTF8Encoding(false));
        return (jsonPath, mdPath);
    }

    /// <summary>从 .zip（找 settings.json）或 .json 读设置；读不出来返回 null。</summary>
    public static HotkeyConfig? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var text = path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                ? ReadJsonFromZip(path)
                : File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(text)) return null;
            return JsonSerializer.Deserialize<HotkeyConfig>(text, WriteOpts);
        }
        catch (Exception ex)
        {
            AppLog.Write($"settings import: 读取失败 {ex.Message}");
            return null;
        }
    }

    private static string? ReadJsonFromZip(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.Entries.FirstOrDefault(e => string.Equals(e.Name, JsonEntry, StringComparison.OrdinalIgnoreCase))
                    ?? zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
        if (entry == null) return null;
        using var sr = new StreamReader(entry.Open(), Encoding.UTF8);
        return sr.ReadToEnd();
    }

    private static void AddText(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var s = entry.Open();
        using var sw = new StreamWriter(s, new UTF8Encoding(false));
        sw.Write(content);
    }

    private static string ReadmeText() => $"""
Gal Quote Tool 设置导出

· settings.json —— 完整设置，可以直接用「···」菜单 → 导入设置… 读回来。
  ⚠ 里面含网页访问码和两步验证（TOTP）密钥，属于敏感文件，别随便发出去。
· settings.md   —— 同一份设置的人可读摘要，敏感字段已打码，可以拿去分享或排查问题。
· 本文件

注意：这里只有"设置"，不含语录库和截图。要搬数据请另外用「打包导出（含截图）」，
或者直接把数据目录整个拷过去（设置界面 → 常规 里能看到数据目录在哪）。
""";

    /// <summary>生成人可读的设置摘要；<paramref name="maskSecrets"/> = true 时访问码/动态码密钥打码。</summary>
    public static string BuildMarkdown(HotkeyConfig c, bool maskSecrets)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Gal Quote Tool 设置摘要");
        sb.AppendLine();
        sb.AppendLine($"- 导出时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- 数据目录：`{AppPaths.DataDirectory}`（{AppPaths.Source}）");
        sb.AppendLine($"- 截图目录：`{(string.IsNullOrWhiteSpace(c.ScreenshotDirectory) ? "（默认：图片\\GalQuoteCollector）" : c.ScreenshotDirectory)}`");
        sb.AppendLine();

        sb.AppendLine("## 快捷键");
        sb.AppendLine($"- 采集：`{Mods(c.Alt, c.Control, c.Shift, c.Win)}{KeyName(c.VirtualKey)}`（吞掉该按键：{(c.SwallowCaptureHotkey ? "是" : "否")}）");
        sb.AppendLine($"- 补拍：`{Mods(c.AddShotAlt, c.AddShotControl, c.AddShotShift, c.AddShotWin)}{KeyName(c.AddShotVirtualKey)}`");
        sb.AppendLine($"- 采集延迟：{c.CaptureDelayMs} ms");
        sb.AppendLine();

        sb.AppendLine("## 截图与黑边");
        sb.AppendLine($"- 格式：{c.ScreenshotFormat.ToUpperInvariant()}" + (c.ScreenshotFormat == "jpg" ? $"（质量 {c.JpegQuality}）" : ""));
        sb.AppendLine($"- 方式：{CaptureModeText(c.CaptureMode)}");
        sb.AppendLine($"- 黑边处理：{BarModeText(c.CaptureBarMode)}；四边微调 左{c.BarAdjustLeft} 右{c.BarAdjustRight} 上{c.BarAdjustTop} 下{c.BarAdjustBottom}");
        sb.AppendLine($"- 检测到 Magpie 时改抓原生分辨率：{(c.PreferNativeCaptureWhenMagpie ? "是" : "否")}");
        sb.AppendLine();

        sb.AppendLine("## 数据与备份");
        sb.AppendLine($"- 自动备份：{(c.BackupEnabled ? "开" : "关")}；位置：`{(string.IsNullOrWhiteSpace(c.BackupDirectory) ? "（默认：安装目录上一级\\Gal Quote Tool Backup）" : c.BackupDirectory)}`");
        sb.AppendLine($"- 保留份数：{c.BackupKeepCount}；打包成 zip：{(c.BackupAsZip ? "是" : "否")}");
        sb.AppendLine();

        sb.AppendLine("## 网页与手机");
        sb.AppendLine($"- 开关：{(c.WebEnabled ? "开" : "关")}；端口：{c.WebPort}；访问协议：{TlsModeText(c.EffectiveTlsMode)}");
        sb.AppendLine($"- 局域网访问：{(c.WebAllowLan ? "允许" : "只本机")}；公网访问：{(c.WebAllowExternal ? "允许（只读）" : "禁止")}；强制只读：{(c.WebReadOnly ? "是" : "否")}");
        sb.AppendLine($"- 访问码：{Mask(c.WebAccessCode, maskSecrets)}");
        sb.AppendLine($"- 两步验证：{(c.WebTotpEnabled && c.EffectiveTotpSecret.Length > 0 ? "开" : "关")}；密钥：{Mask(c.EffectiveTotpSecret, maskSecrets)}；免验证 {c.WebTotpSessionHours} 小时");
        sb.AppendLine();

        sb.AppendLine("## 回想");
        sb.AppendLine($"- 顺序：{(c.SlideshowMode == 1 ? "随机" : "时间顺序")}；循环：{(c.SlideshowLoop ? "是" : "否")}；黑边：{BarModeText(c.SlideshowBarsMode)}");
        sb.AppendLine($"- 文字底：{(c.SlideshowTextStyle == 1 ? "白底黑字" : "黑底白字")}；透明度：{c.SlideshowTextOpacity:0.00}");
        sb.AppendLine($"- 字体：中文 {c.SlideshowChineseFont} / 英文 {c.SlideshowEnglishFont}；界面字体：{c.FontFamily}");
        sb.AppendLine();

        sb.AppendLine("## 超分（Magpie）");
        sb.AppendLine($"- 回想时超分：{(c.MagpieUpscaleSlideshow ? "开" : "关")}；热键：`{(string.IsNullOrWhiteSpace(c.MagpieScaleHotkey) ? "自动读取" : c.MagpieScaleHotkey)}`");
        sb.AppendLine($"- Magpie 路径：`{(string.IsNullOrWhiteSpace(c.MagpiePath) ? "自动探测" : c.MagpiePath)}`");
        sb.AppendLine();

        sb.AppendLine("## OCR");
        sb.AppendLine($"- 引擎：{OcrText(c.OcrEngine)}");
        if (c.OcrEngine == "local")
            sb.AppendLine($"- 服务地址：{c.LocalOcrUrl}；模型：{c.LocalOcrModel}");
        if (c.OcrEngine == "rapid")
            sb.AppendLine($"- RapidOCR Python：{(string.IsNullOrWhiteSpace(c.RapidOcrPython) ? "未设置" : c.RapidOcrPython)}");
        sb.AppendLine();

        sb.AppendLine("## 使用统计");
        sb.AppendLine($"- 记录使用时长：{(c.EnableUsageTracking ? "开" : "关")}；锁屏进程：{(c.UsageLockProcesses.Count == 0 ? "（默认）" : string.Join(", ", c.UsageLockProcesses))}");
        sb.AppendLine($"- 应用名映射：{c.UsageNameMap.Count} 条；自定义图标：{c.UsageIconOverrides.Count} 条");
        sb.AppendLine($"- 时间范围记忆：{c.UsagePeriodMode}；窗口尺寸：{(int)c.UsageWindowWidth}×{(int)c.UsageWindowHeight}");
        sb.AppendLine();

        sb.AppendLine("## 游戏名规则");
        if (c.GameNameRules.Count == 0) sb.AppendLine("- （无）");
        else foreach (var r in c.GameNameRules) sb.AppendLine($"- `{r.Match}` → **{r.Name}**");
        sb.AppendLine();

        sb.AppendLine("## 其它");
        sb.AppendLine($"- 开机自启：{(c.AutoStart ? "是" : "否")}；搜索结果排除「[未识别到文字]」：{(c.HideUnrecognized ? "是" : "否")}");
        sb.AppendLine($"- 透明任务栏修复：{(c.EnableTranslucentTbFix ? "开" : "关")}；接收测试版更新：{(c.UpdateIncludePrerelease ? "是" : "否")}");
        sb.AppendLine($"- 主窗口位置/大小：{c.WindowLeft:0},{c.WindowTop:0} {c.WindowWidth:0}×{c.WindowHeight:0}");
        if (maskSecrets) sb.AppendLine("\n> 本文档中访问码与两步验证密钥已打码；完整设置见同包的 settings.json。");
        return sb.ToString();
    }

    private static string Mask(string value, bool mask)
    {
        if (string.IsNullOrEmpty(value)) return "（未设置）";
        if (!mask) return value;
        return value.Length <= 4 ? "****" : $"{value[..2]}****{value[^2..]}（{value.Length} 位，已打码）";
    }

    private static string Mods(bool alt, bool ctrl, bool shift, bool win)
    {
        var parts = new List<string>();
        if (ctrl) parts.Add("Ctrl");
        if (alt) parts.Add("Alt");
        if (shift) parts.Add("Shift");
        if (win) parts.Add("Win");
        return parts.Count == 0 ? "" : string.Join("+", parts) + "+";
    }

    private static string KeyName(uint vk)
    {
        try { return System.Windows.Input.KeyInterop.KeyFromVirtualKey((int)vk).ToString(); }
        catch { return "0x" + vk.ToString("X2"); }
    }

    private static string CaptureModeText(string mode) => mode switch
    {
        "window" => "窗口内容",
        "region" => "窗口可见区域",
        "auto" => "自动",
        "screen" => "整个屏幕",
        _ => "当前显示器",
    };

    private static string BarModeText(int mode) => mode switch
    {
        1 => "涂黑",
        2 => "涂白",
        3 => "裁掉",
        _ => "不操作",
    };

    private static string TlsModeText(int mode) => mode switch
    {
        1 => "仅 HTTP",
        2 => "仅 HTTPS",
        _ => "自动（HTTP/HTTPS）",
    };

    private static string OcrText(string engine) => engine switch
    {
        "local" => "本地模型（Qwen · Ollama）",
        "rapid" => "RapidOCR（本地离线）",
        _ => "Win OCR",
    };
}
