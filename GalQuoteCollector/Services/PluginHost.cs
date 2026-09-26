using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using GalQuoteCollector.Plugins;

namespace GalQuoteCollector.Services;

/// <summary>
/// 插件加载器：扫描「程序目录\plugins」和「数据目录\plugins」，加载实现了
/// <see cref="IGalQuotePlugin"/> 的 DLL。全靠反射，程序不引用任何插件，缺了也一切照常。
/// 加载失败只写日志，绝不因为插件问题影响主程序启动。
/// </summary>
public static class PluginHost
{
    private static readonly List<IGalQuotePlugin> _plugins = new();
    private static readonly object _lock = new();
    private static bool _loaded;

    /// <summary>扫描过的目录（日志/提示里用）。</summary>
    public static IReadOnlyList<string> Directories { get; private set; } = Array.Empty<string>();
    public static IReadOnlyList<IGalQuotePlugin> Plugins => _plugins;
    /// <summary>可选的二维码插件（没装就是 null）。</summary>
    public static IQrCodePlugin? QrCode { get; private set; }
    /// <summary>最近一次加载失败的原因（给设置界面提示用）。</summary>
    public static string? LastError { get; private set; }

    public static void EnsureLoaded()
    {
        lock (_lock)
        {
            if (_loaded) return;
            _loaded = true;
        }

        var dirs = new List<string>();
        try { dirs.Add(Path.Combine(AppContext.BaseDirectory, "plugins")); } catch { }
        try
        {
            var dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GalQuoteCollector");
            dirs.Add(Path.Combine(dataDir, "plugins"));
        }
        catch { }
        Directories = dirs;

        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            string[] dlls;
            try { dlls = Directory.GetFiles(dir, "*.dll"); }
            catch (Exception ex) { LastError = ex.Message; continue; }

            foreach (var dll in dlls)
            {
                try
                {
                    var asm = Assembly.LoadFrom(dll);
                    foreach (var type in asm.GetTypes())
                    {
                        if (!type.IsClass || type.IsAbstract || !typeof(IGalQuotePlugin).IsAssignableFrom(type)) continue;
                        if (Activator.CreateInstance(type) is not IGalQuotePlugin plugin) continue;
                        if (_plugins.Any(p => p.GetType() == plugin.GetType())) continue;
                        _plugins.Add(plugin);
                        if (plugin is IQrCodePlugin qr && QrCode == null) QrCode = qr;
                        AppLog.Write($"plugin: 已加载 {plugin.Name} v{plugin.Version}（{Path.GetFileName(dll)}）");
                    }
                }
                catch (Exception ex)
                {
                    LastError = $"{Path.GetFileName(dll)}: {ex.Message}";
                    AppLog.Write($"plugin: 加载失败 {LastError}");
                }
            }
        }

        AppLog.Write(_plugins.Count > 0
            ? $"plugin: 共 {_plugins.Count} 个插件，二维码={QrCode?.Name ?? "无"}，目录={string.Join(" | ", dirs)}"
            : $"plugin: 没有可用插件（找过：{string.Join(" | ", dirs)}）");
    }
}
