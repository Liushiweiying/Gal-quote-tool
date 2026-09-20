using System.IO;

namespace GalQuoteCollector.Services;

/// <summary>
/// 数据备份：每天首次启动时把全部数据文件复制到备份目录，只保留最近 3 天。
/// 备份内容 = quotes.db（语录库）+ usage.json（使用记录）+ settings.json（设置/规则/映射）。
/// 截图不备份（体积大），需要的话可以手动复制。
/// </summary>
public static class BackupService
{
    /// <summary>参与备份的文件（相对数据目录）。</summary>
    private static readonly string[] Files = { "quotes.db", "usage.json", "settings.json" };

    /// <summary>保留天数。</summary>
    public const int KeepDays = 3;

    public static string DefaultDirectory(string dataDir) => Path.Combine(dataDir, "backups");

    public static string ResolveDirectory(Models.HotkeyConfig cfg, string dataDir) =>
        string.IsNullOrWhiteSpace(cfg.BackupDirectory) ? DefaultDirectory(dataDir) : cfg.BackupDirectory.Trim();

    /// <summary>今天是否已经备份过。</summary>
    public static bool HasBackupToday(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return false;
            var prefix = "backup-" + DateTime.Now.ToString("yyyy-MM-dd");
            return Directory.EnumerateDirectories(directory, prefix + "*").Any();
        }
        catch { return false; }
    }

    /// <summary>每天首次启动备份一次。返回 (是否做了备份, 说明)。</summary>
    public static (bool did, string message) BackupIfNeeded(Models.HotkeyConfig cfg, string dataDir)
    {
        if (!cfg.BackupEnabled) return (false, "已关闭自动备份");
        var dir = ResolveDirectory(cfg, dataDir);
        if (HasBackupToday(dir)) return (false, "今天已经备份过");

        var (ok, path, message) = BackupNow(cfg, dataDir);
        return (ok, message + (ok ? $" → {path}" : ""));
    }

    /// <summary>立刻备份一次（设置里的「立即备份」也走这里）。</summary>
    public static (bool ok, string path, string message) BackupNow(Models.HotkeyConfig cfg, string dataDir)
    {
        var dir = ResolveDirectory(cfg, dataDir);
        try
        {
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, "backup-" + DateTime.Now.ToString("yyyy-MM-dd_HHmmss"));
            Directory.CreateDirectory(target);

            int copied = 0;
            foreach (var name in Files)
            {
                var src = Path.Combine(dataDir, name);
                if (!File.Exists(src)) continue;
                File.Copy(src, Path.Combine(target, name), true);
                copied++;
            }
            if (copied == 0)
            {
                try { Directory.Delete(target, true); } catch { }
                return (false, target, "没有可备份的数据文件");
            }

            // 截图（体积大：跟着备份一起复制，失败也不影响数据文件）
            var shots = 0; long shotBytes = 0;
            try
            {
                var shotDir = ResolveScreenshotDir(cfg);
                if (shotDir.Length > 0 && Directory.Exists(shotDir))
                {
                    var dst = Path.Combine(target, "screenshots");
                    Directory.CreateDirectory(dst);
                    foreach (var file in Directory.EnumerateFiles(shotDir))
                    {
                        var info = new FileInfo(file);
                        File.Copy(file, Path.Combine(dst, info.Name), true);
                        shots++; shotBytes += info.Length;
                    }
                }
            }
            catch (Exception ex) { AppLog.Write($"backup: 截图备份失败 {ex.Message}"); }

            PruneOldBackups(dir);
            return (true, target, shots > 0
                ? $"已备份 {copied} 个数据文件 + {shots} 张截图（{shotBytes / 1024 / 1024} MB）"
                : $"已备份 {copied} 个数据文件");
        }
        catch (Exception ex)
        {
            return (false, dir, "备份失败: " + ex.Message);
        }
    }

    /// <summary>截图目录（设置里的截图目录，留空则用「图片\GalQuoteCollector」）。</summary>
    private static string ResolveScreenshotDir(Models.HotkeyConfig cfg)
    {
        if (!string.IsNullOrWhiteSpace(cfg.ScreenshotDirectory)) return cfg.ScreenshotDirectory.Trim();
        try
        {
            var pics = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            return Path.Combine(pics, "GalQuoteCollector");
        }
        catch { return ""; }
    }

    /// <summary>只保留最近 <see cref="KeepDays"/> 天的备份。</summary>
    public static int PruneOldBackups(string directory)
    {
        int removed = 0;
        try
        {
            var cutoff = DateTime.Today.AddDays(-(KeepDays - 1)); // 含今天在内共 3 天
            foreach (var d in Directory.EnumerateDirectories(directory, "backup-*"))
            {
                var name = Path.GetFileName(d);
                var stamp = name.Length >= 17 ? name.Substring(7, 10) : ""; // backup-yyyy-MM-dd_...
                if (!DateTime.TryParse(stamp, out var date))
                {
                    // 名字不合规范：按目录创建时间判断
                    try { date = Directory.GetCreationTime(d).Date; } catch { continue; }
                }
                if (date.Date < cutoff)
                {
                    try { Directory.Delete(d, true); removed++; } catch { }
                }
            }
        }
        catch { }
        return removed;
    }
}
