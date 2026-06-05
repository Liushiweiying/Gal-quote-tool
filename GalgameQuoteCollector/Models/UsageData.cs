namespace GalgameQuoteCollector.Models;

/// <summary>Per-process daily record.</summary>
public class ProcessRecord
{
    public string Name { get; set; } = "";    // display name (game or process)
    public int Seconds { get; set; }
}

/// <summary>Full usage data persisted to disk.</summary>
public class UsageData
{
    public Dictionary<string, Dictionary<string, ProcessRecord>> Records { get; set; } = new(); // date → processKey → record
    public List<string> Blacklist { get; set; } = new(); // process names to ignore

    /// <summary>Get records for a specific date.</summary>
    public Dictionary<string, ProcessRecord>? GetDay(string date) =>
        Records.TryGetValue(date, out var day) ? day : null;

    /// <summary>Ensure a date entry exists.</summary>
    public Dictionary<string, ProcessRecord> GetOrCreateDay(string date)
    {
        if (!Records.ContainsKey(date)) Records[date] = new();
        return Records[date];
    }

    public void AddSeconds(string date, string processKey, string displayName, int sec)
    {
        var day = GetOrCreateDay(date);
        if (!day.ContainsKey(processKey))
            day[processKey] = new ProcessRecord { Name = displayName };
        day[processKey].Seconds += sec;
    }
}
