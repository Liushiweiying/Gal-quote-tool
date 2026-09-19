using System.IO;
using System.Text.Json;
using GalQuoteCollector.Models;

namespace GalQuoteCollector.Services;

/// <summary>
/// 使用记录的手工修正（诊断开关用）：把某一天误记的「锁屏」时长改记到指定应用。
/// 用在锁屏误判已经写进历史数据、又无法自动判断归属的时候（比如远程串流被当成锁屏）。
/// </summary>
public static class UsageRepair
{
    /// <summary>
    /// 把 date（yyyy-MM-dd）当天、小时范围 [fromHour, toHour] 内的锁屏时长改记到 processKey。
    /// 默认整天；给范围是为了只搬"误判"的那几小时（比如日志显示 19:13 之后是误判、
    /// 而 17:33-18:57 是真锁屏）。
    /// </summary>
    public static (bool ok, string detail) MoveLockToApp(string dataDir, string date, string processKey,
        int fromHour = 0, int toHour = 23)
    {
        var file = Path.Combine(dataDir, "usage.json");
        if (!File.Exists(file)) return (false, $"找不到 {file}");

        UsageData? data;
        try
        {
            data = JsonSerializer.Deserialize<UsageData>(File.ReadAllText(file));
        }
        catch (Exception ex) { return (false, "读取失败: " + ex.Message); }
        if (data == null) return (false, "usage.json 解析失败");

        var day = data.GetDay(date);
        if (day == null) return (false, $"{date} 没有记录");
        if (!day.TryGetValue(UsageData.LockedKey, out var locked) || locked.Seconds <= 0)
            return (false, $"{date} 没有锁屏记录");

        var key = UsageRules.Normalize(processKey);
        if (key.Length == 0) return (false, "进程名无效");

        if (!day.TryGetValue(key, out var target))
        {
            target = new ProcessRecord { Name = key };
            day[key] = target;
        }

        var lh = locked.HourlyOrEmpty();
        var ths = target.HourlyOrEmpty();
        int moved = 0;
        for (int h = Math.Max(0, fromHour); h <= Math.Min(23, toHour); h++)
        {
            if (lh[h] <= 0) continue;
            ths[h] += lh[h];
            moved += lh[h];
            lh[h] = 0;
        }
        if (moved <= 0) return (false, $"{date} 在 {fromHour}:00-{toHour}:59 没有锁屏记录");

        target.Seconds += moved;
        locked.Seconds -= moved;
        if (locked.Seconds <= 0) day.Remove(UsageData.LockedKey);

        File.WriteAllText(file, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        return (true, $"已把 {date} {fromHour}:00-{toHour}:59 的 {moved / 60} 分钟锁屏时长改记到 {key}" +
                      $"（该天锁屏剩余 {Math.Max(0, locked.Seconds) / 60} 分钟）");
    }

    /// <summary>
    /// 把某天、指定小时范围里 fromKey 的时长改记到 toKey（应用 → 应用）。
    /// 目标如果当天还没有记录，就从其它日期里找同名进程，沿用它的显示名和图标路径。
    /// </summary>
    public static (bool ok, string detail) MoveUsage(string dataDir, string date,
        string fromKey, string toKey, int fromHour = 0, int toHour = 23)
    {
        var file = Path.Combine(dataDir, "usage.json");
        if (!File.Exists(file)) return (false, $"找不到 {file}");

        UsageData? data;
        try { data = JsonSerializer.Deserialize<UsageData>(File.ReadAllText(file)); }
        catch (Exception ex) { return (false, "读取失败: " + ex.Message); }
        if (data == null) return (false, "usage.json 解析失败");

        var day = data.GetDay(date);
        if (day == null) return (false, $"{date} 没有记录");

        var from = UsageRules.Normalize(fromKey);
        var to = UsageRules.Normalize(toKey);
        if (from.Length == 0 || to.Length == 0) return (false, "进程名无效");
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return (false, "源和目标相同");
        if (!day.TryGetValue(from, out var src) || src.Seconds <= 0) return (false, $"{date} 没有 {from} 的记录");

        if (!day.TryGetValue(to, out var dst))
        {
            // 从别的日期找回这个进程的显示名 / 图标路径
            string name = "", path = "";
            foreach (var d in data.Records.Values)
            {
                if (d.TryGetValue(to, out var r) && !string.IsNullOrWhiteSpace(r.Name))
                {
                    name = r.Name;
                    path = r.Path;
                    break;
                }
            }
            if (string.IsNullOrWhiteSpace(name))
                name = to.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? to[..^4] : to;
            dst = new ProcessRecord { Name = name, Path = path };
            day[to] = dst;
        }

        var sh = src.HourlyOrEmpty();
        var dh = dst.HourlyOrEmpty();
        int moved = 0;
        for (int h = Math.Max(0, fromHour); h <= Math.Min(23, toHour); h++)
        {
            if (sh[h] <= 0) continue;
            dh[h] += sh[h];
            moved += sh[h];
            sh[h] = 0;
        }
        if (moved <= 0) return (false, $"{date} 在 {fromHour}:00-{toHour}:59 没有 {from} 的记录");

        src.Seconds -= moved;
        dst.Seconds += moved;
        if (src.Seconds <= 0) day.Remove(from);

        File.WriteAllText(file, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        return (true, $"已把 {date} {fromHour}:00-{toHour}:59 的 {moved / 60} 分钟从 {from} 改记到 {to}");
    }
}
