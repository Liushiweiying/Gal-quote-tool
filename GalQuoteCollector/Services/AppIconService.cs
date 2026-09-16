using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace GalQuoteCollector.Services;

/// <summary>
/// 「应用图标」解析：给一个进程名，尽量找到对应的 exe（再从 exe 里抽图标）。
///
/// 依次尝试（先便宜、命中率高的）：
///   1. 用户自定义（settings.json 的 UsageIconOverrides，可指向图片或 exe）
///   2. 记录下来的路径（跟踪时有权限就记了）
///   3. 正在运行的同名进程（MainModule / QueryFullProcessImageName）
///   4. MuiCache：资源管理器运行过的 exe 全路径（对游戏命中率很高）
///   5. 卸载信息：DisplayIcon / InstallLocation
///   6. App Paths 注册表
///   7. Everything 的 HTTP 服务（若用户开了 http://127.0.0.1 搜索）
///   8. 常见目录里按文件名扫（可选，慢，只在用户手动触发时用）
/// </summary>
public static class AppIconService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMilliseconds(600) };

    // 解析结果缓存（进程名 → exe 路径），避免重复查注册表/网络
    private static readonly Dictionary<string, string?> PathCache = new(StringComparer.OrdinalIgnoreCase);
    // 图标位图缓存（进程名 → 图标；null 表示确认没有图标）。重建列表时立刻可用，不会先闪成首字母
    private static readonly Dictionary<string, BitmapSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object CacheLock = new();

    /// <summary>取已缓存的图标（null 可能是"没缓存过"或"确认没有"，用 IsIconCached 区分）。</summary>
    public static BitmapSource? GetCachedIcon(string processKey)
    {
        var key = ExeName(processKey);
        if (key.Length == 0) return null;
        lock (CacheLock) { return IconCache.TryGetValue(key, out var bmp) ? bmp : null; }
    }

    public static bool IsIconCached(string processKey)
    {
        var key = ExeName(processKey);
        if (key.Length == 0) return false;
        lock (CacheLock) { return IconCache.ContainsKey(key); }
    }

    public static void CacheIcon(string processKey, BitmapSource? icon)
    {
        var key = ExeName(processKey);
        if (key.Length == 0) return;
        lock (CacheLock) { IconCache[key] = icon; }
    }

    /// <summary>忘掉某个进程名的解析缓存（用户改了自定义图标后调用）。</summary>
    public static void Forget(string processName)
    {
        var name = ExeName(processName);
        lock (CacheLock)
        {
            PathCache.Remove(name);
            IconCache.Remove(name);
        }
    }

    /// <summary>把进程名规范成 exe 文件名（UsageTracker 记的是 "name.exe" 形式）。</summary>
    private static string ExeName(string processName)
    {
        var n = (processName ?? "").Trim();
        if (n.Length == 0) return "";
        return n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n : n + ".exe";
    }

    private static string BaseName(string processName)
    {
        var n = ExeName(processName);
        return n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n;
    }

    /// <summary>
    /// 解析 exe 路径。overrides 是用户自定义表（进程名 → exe 或图片路径）。
    /// 只做便宜的数据源；Everything / 磁盘扫描这类慢的不在这里做。
    /// </summary>
    public static string? ResolveExePath(string processName, string? recordedPath,
        IReadOnlyDictionary<string, string>? overrides = null)
    {
        var name = ExeName(processName);
        if (name.Length == 0) return null;

        // 1) 用户自定义
        if (overrides != null && overrides.TryGetValue(processName, out var custom) &&
            !string.IsNullOrWhiteSpace(custom) && File.Exists(custom))
            return custom;

        // 2) 记录下来的路径
        if (!string.IsNullOrWhiteSpace(recordedPath) && File.Exists(recordedPath)) return recordedPath;

        lock (CacheLock)
        {
            if (PathCache.TryGetValue(name, out var cached)) return cached;
        }

        string? found = FromRunningProcess(name)
                        ?? FromMuiCache(name)
                        ?? FromUninstall(name)
                        ?? FromAppPaths(name)
                        ?? FromEverythingHttp(name);

        lock (CacheLock) { PathCache[name] = found; }
        return found;
    }

    /// <summary>同上，但额外尝试慢的来源（磁盘扫描）。用于用户手动「搜索 exe」。</summary>
    public static string? ResolveExePathDeep(string processName, string? recordedPath,
        IReadOnlyDictionary<string, string>? overrides = null)
    {
        var cheap = ResolveExePath(processName, recordedPath, overrides);
        if (cheap != null) return cheap;
        var found = ScanLikelyDirs(ExeName(processName));
        if (found != null)
        {
            lock (CacheLock) { PathCache[ExeName(processName)] = found; }
        }
        return found;
    }

    /// <summary>把搜索结果（可能多个）交给调用方选择。</summary>
    public static List<string> SearchExePaths(string exeName, int max = 40)
    {
        var results = new List<string>();
        var target = ExeName(exeName);
        if (target.Length == 0) return results;

        void TryAdd(string? dir)
        {
            if (results.Count >= max || string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, target, SearchOption.AllDirectories))
                {
                    results.Add(f);
                    if (results.Count >= max) return;
                }
            }
            catch { }
        }

        foreach (var root in LikelyRoots())
        {
            if (results.Count >= max) break;
            TryAdd(root);
        }
        return results;
    }

    private static List<string> LikelyRoots()
    {
        var roots = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
        };
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed) continue;
                var r = drive.RootDirectory.FullName;
                roots.Add(Path.Combine(r, "steam", "steamapps", "common"));
                roots.Add(Path.Combine(r, "SteamLibrary", "steamapps", "common"));
                roots.Add(Path.Combine(r, "Steam", "steamapps", "common"));
                roots.Add(Path.Combine(r, "Games"));
                roots.Add(Path.Combine(r, "gal"));
                roots.Add(Path.Combine(r, "game"));
            }
            catch { }
        }
        return roots;
    }

    private static string? ScanLikelyDirs(string exeName)
    {
        foreach (var root in LikelyRoots())
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
            try
            {
                var hit = Directory.EnumerateFiles(root, exeName, SearchOption.AllDirectories).FirstOrDefault();
                if (hit != null) return hit;
            }
            catch { }
        }
        return null;
    }

    private static string? FromRunningProcess(string exeName)
    {
        var baseName = BaseName(exeName);
        try
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(baseName))
            {
                try
                {
                    var path = p.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) return path;
                }
                catch
                {
                    // 权限不足（管理员进程）时换 QueryFullProcessImageName 再试一次
                    var path = QueryImagePath(p.Id);
                    if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) return path;
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>MuiCache：资源管理器启动过的 exe 会在这里留下全路径（绿色版、游戏都适用）。</summary>
    private static string? FromMuiCache(string exeName)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache");
            if (key == null) return null;
            foreach (var valueName in key.GetValueNames())
            {
                if (!valueName.EndsWith(".FriendlyAppName", StringComparison.OrdinalIgnoreCase)) continue;
                var path = valueName[..^".FriendlyAppName".Length];
                if (!path.EndsWith(exeName, StringComparison.OrdinalIgnoreCase)) continue;
                if (File.Exists(path)) return path;
            }
        }
        catch { }
        return null;
    }

    private static string? FromUninstall(string exeName)
    {
        string[] roots =
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        };
        foreach (var root in roots)
        {
            foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                try
                {
                    using var parent = hive.OpenSubKey(root);
                    if (parent == null) continue;
                    foreach (var sub in parent.GetSubKeyNames())
                    {
                        using var k = parent.OpenSubKey(sub);
                        if (k == null) continue;

                        // DisplayIcon 常常就是主 exe（可能带 ",0" 索引）
                        if (k.GetValue("DisplayIcon") is string icon && icon.Length > 0)
                        {
                            var p = icon.Split(',')[0].Trim('"', ' ');
                            if (p.EndsWith(exeName, StringComparison.OrdinalIgnoreCase) && File.Exists(p)) return p;
                        }

                        if (k.GetValue("InstallLocation") is string loc && loc.Length > 0)
                        {
                            var candidate = Path.Combine(loc.Trim('"'), exeName);
                            if (File.Exists(candidate)) return candidate;
                        }
                    }
                }
                catch { }
            }
        }
        return null;
    }

    private static string? FromAppPaths(string exeName)
    {
        foreach (var root in new[]
                 {
                     @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exeName,
                     @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\" + exeName,
                 })
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(root);
                if (k?.GetValue(null) is string p && p.Length > 0 && File.Exists(p)) return p;
                using var k2 = Registry.CurrentUser.OpenSubKey(root);
                if (k2?.GetValue(null) is string p2 && p2.Length > 0 && File.Exists(p2)) return p2;
            }
            catch { }
        }
        return null;
    }

    /// <summary>
    /// Everything 的 HTTP 服务（Everything → 工具 → 选项 → HTTP 服务器，默认关闭）。
    /// 开了就能一条请求查到全盘同名 exe，比我们自己扫盘快得多。
    /// </summary>
    private static string? FromEverythingHttp(string exeName)
    {
        foreach (var port in new[] { 80, 8080 })
        {
            try
            {
                var url = $"http://127.0.0.1:{port}/?search={Uri.EscapeDataString("exact:" + exeName)}" +
                          "&json=1&path_column=1&name_column=1&count=10";
                var json = Http.GetStringAsync(url).GetAwaiter().GetResult();
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("results", out var results)) continue;
                foreach (var r in results.EnumerateArray())
                {
                    var name = r.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var dir = r.TryGetProperty("path", out var p) ? p.GetString() : null;
                    if (name == null || dir == null) continue;
                    if (!name.Equals(exeName, StringComparison.OrdinalIgnoreCase)) continue;
                    var full = Path.Combine(dir, name);
                    if (File.Exists(full)) return full;
                }
            }
            catch { /* 没开 HTTP 服务就会失败，正常 */ }
        }
        return null;
    }

    /// <summary>从 exe / 图片 / ico 里取出图标位图（后台线程也可调用，返回已 Freeze 的对象）。</summary>
    public static BitmapSource? LoadIcon(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

        var ext = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp")
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(path);
                bmp.DecodePixelWidth = 64;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }

            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon == null) return null;
            var src = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch { return null; }
    }

    // QueryFullProcessImageName：比 MainModule 更容易拿到管理员进程的路径
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint flags,
        System.Text.StringBuilder exeName, ref uint size);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    private static string? QueryImagePath(int pid)
    {
        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            uint size = 1024;
            var sb = new System.Text.StringBuilder((int)size);
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        catch { return null; }
        finally { CloseHandle(h); }
    }
}
