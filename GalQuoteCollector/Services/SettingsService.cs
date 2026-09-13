using System.IO;
using System.Text.Json;
using GalQuoteCollector.Models;

namespace GalQuoteCollector.Services;

public class SettingsService
{
    private readonly string _filePath;

    public SettingsService(string dataDir)
    {
        _filePath = Path.Combine(dataDir, "settings.json");
    }

    public HotkeyConfig LoadHotkeyConfig()
    {
        try
        {
            if (!File.Exists(_filePath))
                return new HotkeyConfig(); // defaults

            var json = File.ReadAllText(_filePath);
            var config = JsonSerializer.Deserialize<HotkeyConfig>(json);
            if (config == null) return new HotkeyConfig();

            if (MigrateCaptureDefaults(config))
            {
                // 迁移后立刻落盘（失败也不能影响本次读取到的配置）
                try { SaveHotkeyConfig(config); }
                catch (Exception ex) { AppLog.Write($"settings: migrate save failed: {ex.Message}"); }
            }

            return config;
        }
        catch
        {
            return new HotkeyConfig();
        }
    }

    /// <summary>
    /// v1.2.5 一次性迁移：截图方式默认值从「自动」改为「当前显示器」。
    ///
    /// 老配置里 CaptureMode 是 "auto"（旧默认值）或 ""（更早版本），两者都迁到 "monitor"；
    /// 同时把旧的 PreferNativeCaptureWhenMagpie=true（旧默认值）关掉——开着它时，
    /// Magpie 场景会抓窗口内容，把游戏顶栏一起截进去，正是这次要修的问题。
    /// 只迁移一次（靠 CaptureDefaultsMigratedV125 标记），之后用户的选择不会再被覆盖。
    /// </summary>
    private static bool MigrateCaptureDefaults(HotkeyConfig config)
    {
        if (config.CaptureDefaultsMigratedV125) return false;

        var mode = (config.CaptureMode ?? "").Trim().ToLowerInvariant();
        if (mode is "" or "auto") config.CaptureMode = "monitor";
        if (config.PreferNativeCaptureWhenMagpie) config.PreferNativeCaptureWhenMagpie = false;

        config.CaptureDefaultsMigratedV125 = true;
        AppLog.Write($"settings: capture defaults migrated → CaptureMode={config.CaptureMode}");
        return true;
    }

    public void SaveHotkeyConfig(HotkeyConfig config)
    {
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_filePath, json);
    }
}
