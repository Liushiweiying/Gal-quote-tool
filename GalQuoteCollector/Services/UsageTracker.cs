using System.Diagnostics;
using System.IO;
using System.Text.Json;
using GalQuoteCollector.Models;

namespace GalQuoteCollector.Services;

public class UsageTracker : IDisposable
{
    private readonly string _filePath;
    private readonly GameDetectService _gameDetect;
    private UsageData _data = new();
    private Timer? _timer;
    private bool _running;
    private readonly object _lock = new();
    private const string ToolKey = UsageData.ToolKey;
    private const string LockedKey = UsageData.LockedKey;
    private bool _locked;

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

        // 先做一次性整理：合并大小写重复 key、把历史的锁屏进程（LockApp.exe 等）并入「锁屏」
        try
        {
            AppLog.Write($"usage rules: lockProcesses=[{string.Join(",", UsageRules.LockProcesses)}] " +
                         $"nameMap={UsageRules.NameMap.Count} keysNormalized={_data.KeysNormalized}");
            lock (_lock)
            {
                if (_data.Normalize(UsageRules.IsLockProcess, out var merged, out var lockMoved))
                {
                    AppLog.Write($"usage normalize: mergedKeys={merged} lockMoved={lockMoved}");
                    Save();
                }
                else
                {
                    AppLog.Write("usage normalize: already done, skipped");
                }
            }
        }
        catch (Exception ex) { AppLog.Write($"usage tracker: normalize failed: {ex.Message}"); }

        // Record tool runtime immediately on start (so it's never 0 across sessions)
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        lock (_lock) { _data.AddSeconds(today, ToolKey, "工具运行", 0); }
        Save();

        // 锁屏事件：锁屏期间的时间单独记到 __locked__，不算到任何应用头上。
        // 注意：只认 SessionLock / SessionUnlock —— ConsoleDisconnect / RemoteDisconnect
        // 在"远程串流 / 远程工具接管控制台"时也会触发，本地屏幕变成锁屏画面（输入桌面
        // 变成 Winlogon），但那并不是用户离开，实测会把远程玩游戏的几小时全记成锁屏。
        try { Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch; }
        catch (Exception ex) { AppLog.Write($"usage tracker: SessionSwitch subscribe failed: {ex.Message}"); }

        _timer = new Timer(Tick, null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
    }

    public void Stop()
    {
        _running = false;
        try { Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch; }
        catch { }
        _timer?.Dispose();
        _timer = null;
        Save();
    }

    private void OnSessionSwitch(object sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionLock) SetLocked(true);
        else if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionUnlock) SetLocked(false);
        else AppLog.Write($"usage tracker: 忽略会话事件 {e.Reason}（不算锁屏）");
    }

    private void SetLocked(bool locked)
    {
        if (_locked == locked) return;
        _locked = locked;
        AppLog.Write($"usage tracker: session {(locked ? "locked" : "unlocked")}");
        if (_running) Tick(null); // 立刻把当前这一分钟归到正确的桶
    }

    /// <summary>输入桌面是不是普通的 Default 桌面（锁屏 / 屏保 / UAC 安全桌面时不是）。</summary>
    private static bool IsInputDesktopDefault()
    {
        IntPtr desktop = OpenInputDesktop(0, false, 0x0001 /* DESKTOP_READOBJECTS */);
        if (desktop == IntPtr.Zero) return false;
        try
        {
            uint need = 0;
            GetUserObjectInformation(desktop, 2 /* UOI_NAME */, IntPtr.Zero, 0, ref need);
            if (need == 0) return false;
            var buf = System.Runtime.InteropServices.Marshal.AllocHGlobal((int)need);
            try
            {
                if (!GetUserObjectInformation(desktop, 2, buf, need, ref need)) return false;
                var name = System.Runtime.InteropServices.Marshal.PtrToStringUni(buf) ?? "";
                return name.Equals("Default", StringComparison.OrdinalIgnoreCase);
            }
            finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(buf); }
        }
        catch { return false; }
        finally { CloseDesktop(desktop); }
    }

    public UsageData GetData() { lock (_lock) { return _data; } }

    /// <summary>记一分钟锁屏（并写清原因，方便排查误判）。</summary>
    private void RecordLocked(string date, int hour, string reason)
    {
        if (_lastLockReason != reason)
        {
            _lastLockReason = reason;
            AppLog.Write($"usage tracker: 锁屏（{reason}）");
        }
        lock (_lock)
        {
            _data.AddSeconds(date, LockedKey, "锁屏", 60, hour);
            _data.AddSeconds(date, ToolKey, "工具运行", 60, hour);
        }
        Save();
    }

    private string? _lastLockReason;

    /// <summary>距最后一次用户输入过了多少秒（用 GetLastInputInfo，跨会话有效）。</summary>
    private static uint IdleSeconds()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return 0;
        uint now = (uint)Environment.TickCount;
        uint last = info.dwTime;
        return now >= last ? (now - last) / 1000 : 0;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    private void Tick(object? state)
    {
        try
        {
            var now = DateTime.Now;
            var date = now.ToString("yyyy-MM-dd");
            var hour = now.Hour;

            // ① 会话锁定事件期间（最可靠）：这一分钟记到「锁屏」桶。
            // 如果输入桌面已经回到 Default，说明解锁事件丢了，顺手恢复。
            if (_locked)
            {
                if (IsInputDesktopDefault())
                {
                    _locked = false;
                    AppLog.Write("usage tracker: 输入桌面已回到 Default，解除锁屏状态（解锁事件可能丢失）");
                }
                else
                {
                    RecordLocked(date, hour, "会话锁定事件");
                    return;
                }
            }

            var hwnd = CaptureService.GetForegroundWindowHandle();
            if (hwnd == IntPtr.Zero) return;

            // Get process name from the foreground window
            GetWindowThreadProcessId(hwnd, out uint pid);
            string? processName = null;
            string? processBaseName = null;
            string? exePath = null;
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                processBaseName = proc.ProcessName;
                processName = processBaseName + ".exe";
                try { exePath = proc.MainModule?.FileName; } catch { /* 权限不足（如管理员进程）→ 只留名字 */ }
            }
            catch { return; }

            if (processName == null) return;

            // ② 前台就是锁屏程序（LockApp.exe / LogonUI.exe / 用户自定）→ 记到「锁屏」桶。
            // 这是很可靠的信号：锁屏界面自己就是前台窗口。
            if (UsageRules.IsLockProcess(processName))
            {
                RecordLocked(date, hour, $"前台是锁屏程序 {processName}");
                return;
            }

            // ③ 输入桌面不是 Default（锁屏/屏保/UAC 安全桌面）——**必须同时长时间没有任何输入**才算锁屏。
            // 只用桌面名太不可靠：实测有整晚被误判成锁屏（日志里并没有真正的 SessionLock 事件），
            // 把正在玩的游戏时间全吃掉了；加上"无输入"约束后，边玩边看也不会被误判。
            if (!IsInputDesktopDefault() && IdleSeconds() >= 120)
            {
                RecordLocked(date, hour, $"输入桌面非 Default 且已 {IdleSeconds() / 60} 分钟无输入");
                return;
            }

            // Check blacklist
            lock (_lock)
            {
                if (_data.Blacklist.Contains(processName, StringComparer.OrdinalIgnoreCase))
                    return;
            }

            // Skip if process name is mostly non-alphabetic (system processes, special chars)
            if (!IsValidRecordName(processName)) return;

            // Display name: games (custom rule match / engine suffix in the title) get the
            // detected game name; everything else is recorded by its process name — e.g.
            // browsers keep "msedge" instead of the current tab title.
            var title = CaptureService.GetWindowTitle(hwnd);
            var detected = string.IsNullOrWhiteSpace(title) ? null : _gameDetect.DetectUsageName(title);
            var displayName = detected ?? processBaseName!;
            if (!IsValidRecordName(displayName))
                displayName = processBaseName!;

            lock (_lock)
            {
                _data.AddSeconds(date, processName, displayName, 60, hour, exePath);
                _data.AddSeconds(date, ToolKey, "工具运行", 60, hour);
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

    // 锁屏检测（输入桌面是否为 Default）
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool GetUserObjectInformation(IntPtr hObj, int index, IntPtr info, uint length, ref uint lengthNeeded);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
}
