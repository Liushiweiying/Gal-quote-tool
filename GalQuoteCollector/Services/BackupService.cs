using System.IO;
using System.Text.Json;

namespace GalQuoteCollector.Services;

/// <summary>
/// 数据备份（用户要求，v1.3.4 起）：
/// - 位置：**安装目录的上一级**下的 <c>Gal Quote Tool Backup</c>（写不进去时退回数据目录）
/// - 时机：每天首次启动时，**只有较上次备份有文件变动**才备份
/// - 份数：最多保留 N 份（默认 3，设置里可调）
/// - 内容：quotes.db / usage.json / settings.json + 截图目录整份
/// </summary>
public static class BackupService
{
    // 备份内容：语录库 + 使用记录（用户要求：**不含设置**，设置变动也不触发备份）
    private static readonly string[] Files = { "quotes.db", "usage.json" };
    /// <summary>判断"是否有变动"只看语录库（usage.json 每次启动都会被重写，settings.json 按用户要求不参与）。</summary>
    private static readonly string[] ChangeWatch = { "quotes.db" };

    public const int DefaultKeep = 3;
    public const string FolderName = "Gal Quote Tool Backup";

    /// <summary>备份目录：自定义目录优先；否则"安装目录上一级\Gal Quote Tool Backup"；不可写则退回数据目录。</summary>
    public static string ResolveDirectory(Models.HotkeyConfig cfg, string dataDir)
    {
        if (!string.IsNullOrWhiteSpace(cfg.BackupDirectory)) return cfg.BackupDirectory.Trim();

        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Directory.GetParent(baseDir)?.FullName;
        var target = Path.Combine(parent ?? dataDir, FolderName);
        try
        {
            Directory.CreateDirectory(target);
            var probe = Path.Combine(target, ".write-test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return target;
        }
        catch
        {
            var fallback = Path.Combine(dataDir, "backups");
            AppLog.Write($"backup: {target} 不可写，改用 {fallback}");
            return fallback;
        }
    }

    public static bool HasBackupToday(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return false;
            return Directory.EnumerateDirectories(dir, "backup-" + DateTime.Now.ToString("yyyy-MM-dd") + "*").Any();
        }
        catch { return false; }
    }

    /// <summary>每天首次启动：有变动才备份。返回 (是否备份, 说明)。</summary>
    public static (bool did, string message) BackupIfNeeded(Models.HotkeyConfig cfg, string dataDir)
    {
        if (!cfg.BackupEnabled) return (false, "已关闭自动备份");
        var dir = ResolveDirectory(cfg, dataDir);
        if (HasBackupToday(dir)) return (false, "今天已经备份过");

        var now = Fingerprint(cfg, dataDir);
        var last = LastFingerprint(dir);
        if (last != null && last == now)
            return (false, "数据与上次备份相同，跳过");

        var (ok, path, message) = BackupNow(cfg, dataDir, now);
        return (ok, message + (ok ? $" → {path}" : ""));
    }

    /// <summary>立刻备份一次（设置里的「立即备份」也走这里）。</summary>
    public static (bool ok, string path, string message) BackupNow(Models.HotkeyConfig cfg, string dataDir,
        string? fingerprint = null)
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

            // 截图（体积大；失败不影响数据文件）
            int shots = 0; long shotBytes = 0;
            try
            {
                var shotDir = ResolveScreenshotDir(cfg, dataDir);
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

            try
            {
                File.WriteAllText(Path.Combine(target, "manifest.json"),
                    JsonSerializer.Serialize(new { time = DateTime.Now, fingerprint = fingerprint ?? Fingerprint(cfg, dataDir) }));
            }
            catch { }

            int removed = PruneOldBackups(dir, cfg.BackupKeepCount <= 0 ? DefaultKeep : cfg.BackupKeepCount);
            return (true, target, $"已备份 {copied} 个数据文件 + {shots} 张截图（{shotBytes / 1024 / 1024} MB）" +
                                  (removed > 0 ? $"，清理旧备份 {removed} 份" : ""));
        }
        catch (Exception ex)
        {
            return (false, dir, "备份失败: " + ex.Message);
        }
    }

    /// <summary>只保留最近 <paramref name="keep"/> 份备份。</summary>
    public static int PruneOldBackups(string directory, int keep)
    {
        int removed = 0;
        try
        {
            if (keep < 1) keep = 1;
            var dirs = Directory.EnumerateDirectories(directory, "backup-*")
                .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal).ToList();
            foreach (var d in dirs.Skip(keep))
            {
                try { Directory.Delete(d, true); removed++; } catch { }
            }
        }
        catch { }
        return removed;
    }

    /// <summary>最近一份备份记录的数据指纹（没有就是 null）。</summary>
    private static string? LastFingerprint(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return null;
            var latest = Directory.EnumerateDirectories(dir, "backup-*")
                .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal).FirstOrDefault();
            if (latest == null) return null;
            var manifest = Path.Combine(latest, "manifest.json");
            if (!File.Exists(manifest)) return null; // 老备份没有指纹 → 当作有变化
            using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
            return doc.RootElement.TryGetProperty("fingerprint", out var f) ? f.GetString() : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// 数据指纹：语录库的**内容哈希** + 截图份数与最新文件名。
    /// 不能用"大小+修改时间"——SQLite 每次打开都可能刷新文件时间戳，会天天都判成"有变动"。
    /// </summary>
    private static string Fingerprint(Models.HotkeyConfig cfg, string dataDir)
    {
        var parts = new List<string>();
        foreach (var name in ChangeWatch)
        {
            var path = Path.Combine(dataDir, name);
            if (!File.Exists(path)) { parts.Add(name + ":none"); continue; }
            try
            {
                using var fs = File.OpenRead(path);
                var hash = System.Security.Cryptography.SHA256.HashData(fs);
                parts.Add($"{name}:{Convert.ToHexString(hash)}");
            }
            catch { parts.Add($"{name}:unreadable"); }
        }
        try
        {
            var shotDir = ResolveScreenshotDir(cfg, dataDir);
            if (shotDir.Length > 0 && Directory.Exists(shotDir))
            {
                var files = Directory.EnumerateFiles(shotDir).Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal).ToList();
                parts.Add($"shots:{files.Count}:{files.LastOrDefault()}");
            }
        }
        catch { }
        return string.Join("|", parts);
    }

    /// <summary>截图目录（跟设置保持一致）。</summary>
    private static string ResolveScreenshotDir(Models.HotkeyConfig cfg, string dataDir)
    {
        if (!string.IsNullOrWhiteSpace(cfg.ScreenshotDirectory)) return cfg.ScreenshotDirectory.Trim();
        try { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "GalQuoteCollector"); }
        catch { return ""; }
    }

    public static string DefaultDirectory(string dataDir) => ResolveDirectory(new Models.HotkeyConfig(), dataDir);
}
