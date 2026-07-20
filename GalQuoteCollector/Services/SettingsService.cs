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
            return config ?? new HotkeyConfig();
        }
        catch
        {
            return new HotkeyConfig();
        }
    }

    public void SaveHotkeyConfig(HotkeyConfig config)
    {
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_filePath, json);
    }
}
