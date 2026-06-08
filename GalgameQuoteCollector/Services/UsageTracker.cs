using System.Diagnostics;
using System.IO;
using System.Text.Json;
using GalgameQuoteCollector.Models;

namespace GalgameQuoteCollector.Services;

public class UsageTracker : IDisposable
{
    private readonly string _filePath;
    private readonly GameDetectService _gameDetect;
    private UsageData _data = new();
    private Timer? _timer;
    private bool _running;
    private readonly object _lock = new();
    private const string ToolKey = "__tool__";

    public event Action<UsageData>? OnDataSaved;

    public UsageTracker(string dataDir, GameDetectService gameDetect)
    {
        _filePath = Path.Combine(dataDir, "usage.json");
        _gameDetect = gameDetect;
        Load();
    }

    public void Start()
    {
        if (_running) return;
        _running = true;

        // Record tool runtime immediately on start (so it's never 0 across sessions)
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        lock (_lock) { _data.AddSeconds(today, ToolKey, "工具运行", 0); }
        Save();

        _timer = new Timer(Tick, null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
    }

    public void Stop()
    {
        _running = false;
        _timer?.Dispose();
        _timer = null;
        Save();
    }

    public UsageData GetData() { lock (_lock) { return _data; } }

    /// <summary>Today's tool runtime (summed across all sessions).</summary>
    public int GetTodayRuntimeSeconds()
    {
        lock (_lock)
        {
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            var day = _data.GetDay(today);
            if (day != null && day.TryGetValue(ToolKey, out var rec))
                return rec.Seconds;
            return 0;
        }
    }

    private void Tick(object? state)
    {
        try
        {
            var hwnd = CaptureService.GetForegroundWindowHandle();
            if (hwnd == IntPtr.Zero) return;

            // Get process name from the foreground window
            GetWindowThreadProcessId(hwnd, out uint pid);
            string? processName = null;
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                processName = proc.ProcessName + ".exe";
            }
            catch { return; }

            // Check blacklist
            lock (_lock)
            {
                if (_data.Blacklist.Contains(processName, StringComparer.OrdinalIgnoreCase))
                    return;
            }

            // Skip if process name is mostly non-alphabetic (system processes, special chars)
            if (!IsValidRecordName(processName!)) return;

            // Get display name via GameDetectService
            var title = CaptureService.GetWindowTitle(hwnd);
            var displayName = string.IsNullOrWhiteSpace(title)
                ? processName
                : _gameDetect.DetectGameName(title);
            if (string.IsNullOrWhiteSpace(displayName)) displayName = processName;
            if (!IsValidRecordName(displayName)) displayName = processName!;

            var date = DateTime.Now.ToString("yyyy-MM-dd");
            lock (_lock)
            {
                _data.AddSeconds(date, processName!, displayName!, 60);
                _data.AddSeconds(date, ToolKey, "工具运行", 60);
            }
            Save();
        }
        catch { }
    }

    public void Save()
    {
        lock (_lock)
        {
            var json = JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_filePath, json);
            OnDataSaved?.Invoke(_data);
        }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                _data = JsonSerializer.Deserialize<UsageData>(json) ?? new UsageData();
            }
        }
        catch { _data = new UsageData(); }
    }

    /// <summary>Name should have at least some normal characters to be valid.</summary>
    private static bool IsValidRecordName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        int letterCount = name.Count(c => char.IsLetter(c));
        int total = name.Length;
        return letterCount >= 2 && (double)letterCount / total >= 0.3;
    }

    public void Dispose()
    {
        Stop();
        Save();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
