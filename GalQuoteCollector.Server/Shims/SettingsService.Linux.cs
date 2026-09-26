using System.Text.Json;
using GalQuoteCollector.Models;

namespace GalQuoteCollector.Services;

/// <summary>
/// 非 Windows 端的设置读取（真身 SettingsService 还管开机自启 / 注册表 / 截图目录迁移，那些在 Linux 上没意义）。
/// 被链接的源码只用到 <see cref="LoadHotkeyConfig"/>：读数据目录里的 settings.json，
/// 所以直接把电脑上的那份拷到盒子的数据目录即可，端口 / 访问码 / 两步验证 / 截图目录都会跟着走。
/// </summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public SettingsService(string dataDir) => DataDirectory = dataDir;

    public string DataDirectory { get; }
    public string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    public HotkeyConfig LoadHotkeyConfig()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<HotkeyConfig>(File.ReadAllText(SettingsPath), JsonOpts)
                       ?? new HotkeyConfig();
        }
        catch (Exception ex) { AppLog.Write($"settings: 读取失败，用默认值（{ex.Message}）"); }
        return new HotkeyConfig { WebEnabled = true };
    }

    public void SaveHotkeyConfig(HotkeyConfig cfg)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { AppLog.Write($"settings: 保存失败（{ex.Message}）"); }
    }
}
