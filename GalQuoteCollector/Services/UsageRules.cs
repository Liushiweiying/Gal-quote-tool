using GalQuoteCollector.Models;

namespace GalQuoteCollector.Services;

/// <summary>
/// 使用统计的规则：哪些进程算「锁屏」、进程名 → 显示名的自定义映射。
/// 应用内只有一份（由 MainViewModel 从 settings.json 载入，改完立刻生效，追踪器也读这里）。
/// </summary>
public static class UsageRules
{
    /// <summary>默认锁屏进程：LockApp.exe 是 Win10/11 的锁屏界面，LogonUI.exe 是登录界面。</summary>
    public static readonly string[] DefaultLockProcesses = { "LockApp.exe", "LogonUI.exe" };

    private static List<string> _lockProcesses = new(DefaultLockProcesses);
    private static Dictionary<string, string> _nameMap = new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> LockProcesses => _lockProcesses;

    public static IReadOnlyDictionary<string, string> NameMap => _nameMap;

    /// <summary>从配置载入（进程名列表 + 名称映射）。</summary>
    public static void Load(HotkeyConfig? cfg)
    {
        var locks = new List<string>(DefaultLockProcesses);
        if (cfg?.UsageLockProcesses != null)
        {
            foreach (var p in cfg.UsageLockProcesses)
            {
                var name = Normalize(p);
                if (name.Length > 0 && !locks.Contains(name, StringComparer.OrdinalIgnoreCase))
                    locks.Add(name);
            }
        }
        _lockProcesses = locks;

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (cfg?.UsageNameMap != null)
        {
            foreach (var (k, v) in cfg.UsageNameMap)
            {
                var key = Normalize(k);
                if (key.Length > 0 && !string.IsNullOrWhiteSpace(v)) map[key] = v.Trim();
            }
        }
        _nameMap = map;
    }

    /// <summary>把用户新增的锁屏进程写回配置对象（调用方负责保存 settings.json）。</summary>
    public static void SaveTo(HotkeyConfig cfg)
    {
        cfg.UsageLockProcesses = _lockProcesses
            .Where(p => !DefaultLockProcesses.Contains(p, StringComparer.OrdinalIgnoreCase))
            .ToList();
        cfg.UsageNameMap = _nameMap.ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    public static bool IsLockProcess(string processKey)
    {
        foreach (var p in _lockProcesses)
            if (p.Equals(processKey, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>进程名规范化：补 .exe。</summary>
    public static string Normalize(string raw)
    {
        var name = (raw ?? "").Trim();
        if (name.Length == 0) return "";
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
    }

    /// <summary>显示名：优先用户自定义映射，否则用记录里的名字，最后退回进程名（去掉 .exe）。</summary>
    public static string DisplayName(string processKey, string? recordedName)
    {
        if (_nameMap.TryGetValue(processKey, out var custom) && !string.IsNullOrWhiteSpace(custom))
            return custom;
        return AutoName(processKey, recordedName);
    }

    /// <summary>
    /// 自动名：历史数据里同一进程可能每天记的是窗口标题（"a - Microsoft Edge"、"某首歌名"、
    /// "? galgame-quote-tool"），那样会把同一个应用拆成好几条。这类明显是标题的名字
    /// 一律退回进程名；真正的游戏名（自定义规则/引擎后缀识别出来的）保持原样。
    /// </summary>
    public static string AutoName(string processKey, string? recordedName)
    {
        var baseName = processKey.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? processKey[..^4] : processKey;
        if (string.IsNullOrWhiteSpace(recordedName)) return baseName;

        var r = recordedName.Trim();
        if (r.Length == 0) return baseName;
        if (r.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return baseName;   // 记成了进程名
        if (r.StartsWith("?") || r.StartsWith("＊") || r.StartsWith("*")) return baseName;
        if (r.Contains(" - ") || r.Contains(" – ") || r.Contains(" — ")) return baseName; // "a - Microsoft Edge"
        if (r.Equals(baseName, StringComparison.OrdinalIgnoreCase)) return baseName;
        return r;
    }

    /// <summary>归组用的 key：设了自定义名就按自定义名归并（可把不同 exe 合成一条），否则按进程名。</summary>
    public static string GroupKey(string processKey) =>
        _nameMap.TryGetValue(processKey, out var custom) && !string.IsNullOrWhiteSpace(custom)
            ? custom.Trim()
            : processKey;

    /// <summary>是否设置了自定义名。</summary>
    public static bool HasCustomName(string processKey) =>
        _nameMap.TryGetValue(processKey, out var v) && !string.IsNullOrWhiteSpace(v);

    public static void SetName(string processKey, string? displayName)
    {
        var key = Normalize(processKey);
        if (key.Length == 0) return;
        if (string.IsNullOrWhiteSpace(displayName)) _nameMap.Remove(key);
        else _nameMap[key] = displayName.Trim();
    }

    public static void SetLockProcesses(IEnumerable<string> processes)
    {
        var locks = new List<string>(DefaultLockProcesses);
        foreach (var p in processes)
        {
            var name = Normalize(p);
            if (name.Length > 0 && !locks.Contains(name, StringComparer.OrdinalIgnoreCase))
                locks.Add(name);
        }
        _lockProcesses = locks;
    }
}
