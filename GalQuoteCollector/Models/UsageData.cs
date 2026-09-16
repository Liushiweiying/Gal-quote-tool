namespace GalQuoteCollector.Models;

/// <summary>Per-process daily record.</summary>
public class ProcessRecord
{
    public string Name { get; set; } = "";    // display name (game or process)
    public int Seconds { get; set; }

    /// <summary>每小时的秒数（下标 0-23）。v1.2.7 起才有；旧数据为空数组 = 无小时明细。</summary>
    public int[] Hourly { get; set; } = new int[24];

    /// <summary>进程 exe 路径（用于取图标，可能为空——权限不足或旧数据）。</summary>
    public string Path { get; set; } = "";

    /// <summary>补齐长度，兼容旧数据里 Hourly 为 null / 长度不对的情况。</summary>
    public int[] HourlyOrEmpty()
    {
        if (Hourly == null || Hourly.Length != 24)
        {
            var fixedArr = new int[24];
            if (Hourly != null)
                Array.Copy(Hourly, fixedArr, Math.Min(Hourly.Length, 24));
            Hourly = fixedArr;
        }
        return Hourly;
    }
}

/// <summary>Full usage data persisted to disk.</summary>
public class UsageData
{
    /// <summary>工具自身运行时长用的 key（不计入「使用时长」）。</summary>
    public const string ToolKey = "__tool__";

    /// <summary>锁屏时长用的 key（单独用绿色展示，也不计入「使用时长」）。</summary>
    public const string LockedKey = "__locked__";

    public Dictionary<string, Dictionary<string, ProcessRecord>> Records { get; set; } = new(); // date → processKey → record
    public List<string> Blacklist { get; set; } = new(); // process names to ignore

    /// <summary>v1.2.7 一次性整理标记：大小写重复 key 合并 + 锁屏进程归入「锁屏」。</summary>
    public bool KeysNormalized { get; set; }

    /// <summary>Get records for a specific date.</summary>
    public Dictionary<string, ProcessRecord>? GetDay(string date) =>
        Records.TryGetValue(date, out var day) ? day : null;

    /// <summary>Ensure a date entry exists（key 大小写不敏感，避免 Steam.exe / steam.exe 记成两个应用）。</summary>
    public Dictionary<string, ProcessRecord> GetOrCreateDay(string date)
    {
        if (!Records.TryGetValue(date, out var day) || day == null)
        {
            day = new Dictionary<string, ProcessRecord>(StringComparer.OrdinalIgnoreCase);
            Records[date] = day;
        }
        else if (day.Comparer != StringComparer.OrdinalIgnoreCase)
        {
            // 反序列化出来的字典默认区分大小写，换成不敏感的。
            // 注意：不能直接 new Dictionary(day, OrdinalIgnoreCase) —— 旧数据里存在
            // Steam.exe / steam.exe 这种只差大小写的重复键，那样构造会抛
            // "An item with the same key has already been added"。这里逐个搬并就地合并。
            var fixedDict = new Dictionary<string, ProcessRecord>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, rec) in day)
            {
                if (fixedDict.TryGetValue(key, out var exist)) MergeRecord(exist, rec);
                else fixedDict[key] = rec;
            }
            Records[date] = fixedDict;
            day = fixedDict;
        }
        return day;
    }

    /// <summary>把 src 累加到 target（秒数、每小时明细；空字段补上）。</summary>
    private static void MergeRecord(ProcessRecord target, ProcessRecord src)
    {
        target.Seconds += src.Seconds;
        var th = target.HourlyOrEmpty();
        var sh = src.HourlyOrEmpty();
        for (int h = 0; h < 24; h++) th[h] += sh[h];
        if (string.IsNullOrWhiteSpace(target.Path)) target.Path = src.Path;
        if (string.IsNullOrWhiteSpace(target.Name)) target.Name = src.Name;
    }

    /// <summary>
    /// 一次性整理：
    /// 1) 大小写不同的重复 key 合并（Steam.exe + steam.exe → 一条）；
    /// 2) 锁屏进程（LockApp.exe / LogonUI.exe / 用户自定的）并入「锁屏」桶——
    ///    这样历史里那 40 小时「Windows 默认锁屏界面」就变成锁屏时间，不再算成应用。
    /// 返回是否有改动。
    /// </summary>
    public bool Normalize(Func<string, bool> isLockProcess, out int mergedKeys, out int lockMoved)
    {
        mergedKeys = MergeCaseDuplicateKeys();
        lockMoved = MoveLockProcesses(isLockProcess);
        return mergedKeys > 0 || lockMoved > 0;
    }

    /// <summary>
    /// 一次性：把同一天里大小写不同的重复 key 合并（Steam.exe + steam.exe → 一条）。
    /// 返回合并的条数。
    /// </summary>
    public int MergeCaseDuplicateKeys()
    {
        // 标记只是"已经整理过"的快照；万一上次中途抛异常（例如构建不敏感字典失败），
        // 这里再快速扫一遍，发现还有大小写重复就重做一次。
        if (KeysNormalized && !HasCaseDuplicateKeys()) return 0;

        int mergedKeys = 0;
        foreach (var date in Records.Keys.ToList())
        {
            var day = GetOrCreateDay(date);
            foreach (var key in day.Keys.ToList())
            {
                if (!day.TryGetValue(key, out _)) continue;
                var same = day.Keys.FirstOrDefault(k =>
                    !ReferenceEquals(k, key) && string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
                if (same == null) continue;

                MergeRecord(day[key], day[same]);
                day.Remove(same);
                mergedKeys++;
            }
        }
        KeysNormalized = true;
        return mergedKeys;
    }

    private bool HasCaseDuplicateKeys()
    {
        foreach (var day in Records.Values)
        {
            if (day == null) continue;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in day.Keys)
                if (!seen.Add(key)) return true;
        }
        return false;
    }

    /// <summary>
    /// 把锁屏进程（LockApp.exe / LogonUI.exe / 用户自定的壁纸软件）的时长并入「锁屏」桶。
    /// 幂等：没有可移动的就返回 0；用户以后新加锁屏进程时可以再跑一次。
    /// </summary>
    public int MoveLockProcesses(Func<string, bool> isLockProcess)
    {
        int moved = 0;
        foreach (var date in Records.Keys.ToList())
        {
            var day = GetOrCreateDay(date);
            foreach (var key in day.Keys.ToList())
            {
                if (key == ToolKey || key == LockedKey) continue;
                if (!isLockProcess(key)) continue;

                var rec = day[key];
                if (!day.TryGetValue(LockedKey, out var locked))
                {
                    locked = new ProcessRecord { Name = "锁屏" };
                    day[LockedKey] = locked;
                }
                MergeRecord(locked, rec);
                day.Remove(key);
                moved++;
            }
        }

        Blacklist.RemoveAll(p => isLockProcess(p));
        return moved;
    }

    public void AddSeconds(string date, string processKey, string displayName, int sec, int hour = -1, string? exePath = null)
    {
        var day = GetOrCreateDay(date);
        if (!day.TryGetValue(processKey, out var rec))
        {
            rec = new ProcessRecord { Name = displayName };
            day[processKey] = rec;
        }
        rec.Seconds += sec;
        if (hour >= 0 && hour < 24) rec.HourlyOrEmpty()[hour] += sec;
        if (!string.IsNullOrWhiteSpace(exePath) && string.IsNullOrWhiteSpace(rec.Path)) rec.Path = exePath;
    }

    /// <summary>总计口径里要排除的 key（工具自身、锁屏）。</summary>
    private static bool IsMetaKey(string key) => key == ToolKey || key == LockedKey;

    /// <summary>某天所有「应用」的秒数合计（不含工具自身和锁屏）。</summary>
    public int TotalSeconds(string date, string toolKey = ToolKey)
    {
        var day = GetDay(date);
        if (day == null) return 0;
        return day.Where(kv => !IsMetaKey(kv.Key)).Sum(kv => kv.Value.Seconds);
    }

    /// <summary>某天锁屏秒数。</summary>
    public int LockedSeconds(string date)
    {
        var day = GetDay(date);
        return day != null && day.TryGetValue(LockedKey, out var rec) ? rec.Seconds : 0;
    }

    /// <summary>某天工具自身运行秒数。</summary>
    public int ToolSeconds(string date)
    {
        var day = GetDay(date);
        return day != null && day.TryGetValue(ToolKey, out var rec) ? rec.Seconds : 0;
    }

    /// <summary>某天每小时的合计秒数（不含工具自身和锁屏）；旧数据没有小时明细时返回全 0。</summary>
    public int[] HourlyTotals(string date, string toolKey = ToolKey)
    {
        var result = new int[24];
        var day = GetDay(date);
        if (day == null) return result;
        foreach (var kv in day)
        {
            if (IsMetaKey(kv.Key)) continue;
            var h = kv.Value.HourlyOrEmpty();
            for (int i = 0; i < 24; i++) result[i] += h[i];
        }
        return result;
    }

    /// <summary>某天每小时的锁屏秒数。</summary>
    public int[] HourlyLocked(string date)
    {
        var result = new int[24];
        var day = GetDay(date);
        if (day == null || !day.TryGetValue(LockedKey, out var rec)) return result;
        var h = rec.HourlyOrEmpty();
        for (int i = 0; i < 24; i++) result[i] = h[i];
        return result;
    }

    /// <summary>区间内应用/锁屏的总秒数。</summary>
    public (int Active, int Locked) TotalRange(DateTime from, DateTime to)
    {
        int active = 0, locked = 0;
        for (var d = from.Date; d <= to.Date; d = d.AddDays(1))
        {
            var key = d.ToString("yyyy-MM-dd");
            active += TotalSeconds(key);
            locked += LockedSeconds(key);
        }
        return (active, locked);
    }

    /// <summary>最近 n 天（含 endDate）的每日合计秒数，按时间正序返回。</summary>
    public List<(DateTime Date, int Seconds)> LastDays(DateTime endDate, int n, string toolKey = ToolKey)
    {
        var list = new List<(DateTime, int)>();
        for (int i = n - 1; i >= 0; i--)
        {
            var d = endDate.AddDays(-i);
            list.Add((d, TotalSeconds(d.ToString("yyyy-MM-dd"), toolKey)));
        }
        return list;
    }
}
