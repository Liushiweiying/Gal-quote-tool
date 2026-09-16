using GalQuoteCollector.Models;

namespace GalQuoteCollector.Services;

/// <summary>区间聚合的粒度。</summary>
public enum UsageGranularity { Hourly, Daily, Weekly, Monthly }

/// <summary>一个柱子（桶）。</summary>
public class UsageBucket
{
    public DateTime Start { get; init; }
    public DateTime End { get; init; }
    public int ActiveSeconds { get; set; }
    public int LockedSeconds { get; set; }
    /// <summary>x 轴短标签（自适应时可能为空）。</summary>
    public string Label { get; set; } = "";
    /// <summary>气泡副标题（完整区间）。</summary>
    public string FullLabel { get; set; } = "";
}

/// <summary>应用排行项（同名应用会把不同进程名合并进来）。</summary>
public class UsageAppStat
{
    /// <summary>组内进程名（可能不止一个：Steam.exe / steam.exe，或用户把两个 exe 归到同一个显示名）。</summary>
    public List<string> Keys { get; init; } = new();
    /// <summary>代表进程名（时长最长的那个，用于取图标/加黑名单）。</summary>
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public int Seconds { get; set; }
    /// <summary>组内进程是否都在黑名单里。</summary>
    public bool AllBlacklisted { get; set; }
}

/// <summary>一次统计结果。</summary>
public class UsageReport
{
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public UsageGranularity Granularity { get; init; }
    public List<UsageBucket> Buckets { get; } = new();
    public List<UsageAppStat> Apps { get; } = new();
    public int ActiveSeconds { get; set; }
    public int LockedSeconds { get; set; }
    public int ToolSeconds { get; set; }
    /// <summary>对比用的上一个等长区间的活跃秒数。</summary>
    public int PreviousActiveSeconds { get; set; }
    public string CompareLabel { get; set; } = "比上一个区间";
}

/// <summary>
/// 把「按天 + 按小时」的原始记录聚合成任意区间的柱子（小时/日/周/月）。
/// </summary>
public static class UsageAggregator
{
    /// <summary>按跨度选粒度：1 天看小时，一个月内看天，120 天内看周，再长看月。</summary>
    public static UsageGranularity PickGranularity(DateTime from, DateTime to)
    {
        int days = (int)Math.Ceiling((to.Date - from.Date).TotalDays) + 1;
        if (days <= 1) return UsageGranularity.Hourly;
        if (days <= 31) return UsageGranularity.Daily;
        if (days <= 120) return UsageGranularity.Weekly;
        return UsageGranularity.Monthly;
    }

    public static UsageReport Build(UsageData data, DateTime from, DateTime to, UsageGranularity? granularity = null,
        int maxBuckets = 64)
    {
        from = from.Date;
        to = to.Date;
        if (to < from) (from, to) = (to, from);

        var g = granularity ?? PickGranularity(from, to);
        // 桶太多就提高粒度，避免柱子细到看不见
        while (g != UsageGranularity.Monthly && EstimateBuckets(from, to, g) > maxBuckets)
            g = g switch
            {
                UsageGranularity.Hourly => UsageGranularity.Daily,
                UsageGranularity.Daily => UsageGranularity.Weekly,
                _ => UsageGranularity.Monthly,
            };

        var report = new UsageReport { From = from, To = to, Granularity = g };

        // 应用排行（整个区间）：**按进程名归组**（用户设了自定义名就按自定义名归并），
        // 这样同一个应用不会被每天不同的窗口标题拆成好几条（浏览器标签、音乐播放器的歌名）。
        var apps = new Dictionary<string, UsageAppStat>(StringComparer.OrdinalIgnoreCase);
        var bestSeconds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); // 组内用来挑自动名
        var bestName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var d = from; d <= to; d = d.AddDays(1))
        {
            var day = data.GetDay(d.ToString("yyyy-MM-dd"));
            if (day == null) continue;
            foreach (var kv in day)
            {
                if (kv.Key == UsageData.ToolKey || kv.Key == UsageData.LockedKey) continue;

                var groupKey = UsageRules.GroupKey(kv.Key);
                if (string.IsNullOrWhiteSpace(groupKey)) groupKey = kv.Key;

                if (!apps.TryGetValue(groupKey, out var stat))
                {
                    stat = new UsageAppStat { Name = groupKey };
                    apps[groupKey] = stat;
                }
                if (!stat.Keys.Contains(kv.Key, StringComparer.OrdinalIgnoreCase))
                    stat.Keys.Add(kv.Key);

                // 代表进程名/图标：取时长最长的那个
                if (string.IsNullOrWhiteSpace(stat.Key) || kv.Value.Seconds > stat.Seconds)
                {
                    stat.Key = kv.Key;
                    if (!string.IsNullOrWhiteSpace(kv.Value.Path)) stat.Path = kv.Value.Path;
                }
                else if (string.IsNullOrWhiteSpace(stat.Path) && !string.IsNullOrWhiteSpace(kv.Value.Path))
                {
                    stat.Path = kv.Value.Path;
                }

                // 自动名：用「时间最长的那天」记录的名字，再过滤掉窗口标题样的名字
                bestSeconds.TryGetValue(kv.Key, out var prevBest);
                if (kv.Value.Seconds > prevBest)
                {
                    bestSeconds[kv.Key] = kv.Value.Seconds;
                    bestName[kv.Key] = kv.Value.Name ?? "";
                }

                stat.Seconds += kv.Value.Seconds;
            }
        }

        foreach (var (groupKey, stat) in apps)
        {
            // 自定义名优先；否则用组内代表进程名对应的自动名
            var custom = UsageRules.HasCustomName(stat.Key) ? UsageRules.NameMap[stat.Key] : null;
            stat.Name = !string.IsNullOrWhiteSpace(custom)
                ? custom!
                : UsageRules.AutoName(stat.Key, bestName.TryGetValue(stat.Key, out var n) ? n : null);
            stat.AllBlacklisted = stat.Keys.Count > 0 &&
                stat.Keys.All(k => data.Blacklist.Contains(k, StringComparer.OrdinalIgnoreCase));
        }
        report.Apps.AddRange(apps.Values.OrderByDescending(a => a.Seconds));

        // 柱子
        foreach (var (bucketStart, bucketEnd) in EnumerateBuckets(from, to, g))
        {
            var bucket = new UsageBucket
            {
                Start = bucketStart,
                End = bucketEnd,
                Label = ShortLabel(bucketStart, g),
                FullLabel = LongLabel(bucketStart, bucketEnd, g),
            };
            for (var d = bucketStart.Date; d <= bucketEnd.Date; d = d.AddDays(1))
            {
                var key = d.ToString("yyyy-MM-dd");
                if (g == UsageGranularity.Hourly)
                {
                    var day = data.GetDay(key);
                    if (day != null)
                    {
                        foreach (var kv in day)
                        {
                            if (kv.Key == UsageData.LockedKey)
                            {
                                bucket.LockedSeconds += kv.Value.HourlyOrEmpty()[bucketStart.Hour];
                                continue;
                            }
                            if (kv.Key == UsageData.ToolKey) continue;
                            bucket.ActiveSeconds += kv.Value.HourlyOrEmpty()[bucketStart.Hour];
                        }
                    }
                }
                else
                {
                    bucket.ActiveSeconds += data.TotalSeconds(key);
                    bucket.LockedSeconds += data.LockedSeconds(key);
                }
            }
            report.Buckets.Add(bucket);
        }

        report.ActiveSeconds = report.Buckets.Sum(b => b.ActiveSeconds);
        report.LockedSeconds = report.Buckets.Sum(b => b.LockedSeconds);
        for (var d = from; d <= to; d = d.AddDays(1))
            report.ToolSeconds += data.ToolSeconds(d.ToString("yyyy-MM-dd"));

        // 上一个等长区间
        int span = (to - from).Days + 1;
        var prevTo = from.AddDays(-1);
        var prevFrom = prevTo.AddDays(-(span - 1));
        report.PreviousActiveSeconds = data.TotalRange(prevFrom, prevTo).Active;
        return report;
    }

    private static int EstimateBuckets(DateTime from, DateTime to, UsageGranularity g)
    {
        var span = (to - from).Days + 1;
        return g switch
        {
            UsageGranularity.Hourly => 24,
            UsageGranularity.Daily => span,
            UsageGranularity.Weekly => (int)Math.Ceiling(span / 7.0),
            _ => (int)Math.Ceiling(span / 28.0),
        };
    }

    private static IEnumerable<(DateTime Start, DateTime End)> EnumerateBuckets(DateTime from, DateTime to, UsageGranularity g)
    {
        switch (g)
        {
            case UsageGranularity.Hourly:
                // 只有单日才有意义的 24 小时视图
                for (int h = 0; h < 24; h++)
                {
                    var start = from.AddHours(h);
                    yield return (start, start.AddHours(1).AddTicks(-1));
                }
                break;

            case UsageGranularity.Daily:
                for (var d = from; d <= to; d = d.AddDays(1))
                    yield return (d, d.AddDays(1).AddTicks(-1));
                break;

            case UsageGranularity.Weekly:
            {
                // 以周一为一周起点
                var cursor = from;
                int offset = ((int)cursor.DayOfWeek + 6) % 7;
                cursor = cursor.AddDays(-offset);
                while (cursor <= to)
                {
                    var end = cursor.AddDays(6);
                    yield return (cursor < from ? from : cursor, (end > to ? to : end).AddDays(1).AddTicks(-1));
                    cursor = cursor.AddDays(7);
                }
                break;
            }

            default:
            {
                var cursor = new DateTime(from.Year, from.Month, 1);
                while (cursor <= to)
                {
                    var end = cursor.AddMonths(1).AddDays(-1);
                    yield return (cursor < from ? from : cursor, (end > to ? to : end).AddDays(1).AddTicks(-1));
                    cursor = cursor.AddMonths(1);
                }
                break;
            }
        }
    }

    private static string ShortLabel(DateTime start, UsageGranularity g) => g switch
    {
        UsageGranularity.Hourly => $"{start.Hour:00}:00",
        UsageGranularity.Daily => $"{start:M/d}",
        UsageGranularity.Weekly => $"{start:M/d}",
        _ => $"{start:yyyy/M}",
    };

    private static string LongLabel(DateTime start, DateTime end, UsageGranularity g) => g switch
    {
        UsageGranularity.Hourly => $"{start:HH:00} - {start.AddHours(1):HH:00}",
        UsageGranularity.Daily => start.ToString("yyyy-MM-dd ddd"),
        UsageGranularity.Weekly => $"{start:M/d} - {end:M/d}",
        _ => start.ToString("yyyy 年 M 月"),
    };

    /// <summary>「比上一个区间」的文案。</summary>
    public static string CompareText(int current, int previous, string what)
    {
        if (previous <= 0) return current <= 0 ? "还没有记录" : $"还没有可对比的{what}数据";
        int diff = current - previous;
        if (Math.Abs(diff) < 60) return $"与{what}基本持平";
        int mins = (int)Math.Round(Math.Abs(diff) / 60.0);
        var text = mins < 60 ? $"{mins} 分钟" : (mins % 60 == 0 ? $"{mins / 60} 小时" : $"{mins / 60} 小时 {mins % 60} 分");
        return diff > 0 ? $"比{what}多 {text}" : $"比{what}少 {text}";
    }
}
