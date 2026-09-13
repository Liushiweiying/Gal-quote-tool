using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using GalQuoteCollector.Models;

namespace GalQuoteCollector.Services;

/// <summary>
/// Magpie（超分工具）集成：回想窗口用 Magpie 超分。
///
/// 做法：合成 Magpie「缩放窗口」热键（默认 Alt+Shift+A，可自定义），Magpie 会把当前前台窗口
/// 接管并超分显示。热键只需要前台窗口是本程序的窗口即可，注入的按键通过 Magpie 的
/// 低级键盘钩子（ShortcutService）匹配——该钩子不过滤 injected 事件，实测有效。
///
/// 热键可以自动从 Magpie 的配置里读出来（%LOCALAPPDATA%\Magpie\config\v*\config.json）：
/// Magpie 把热键存成一个 uint32（低 8 位是虚拟键码，0x100/0x200/0x400/0x800 分别是 Win/Ctrl/Alt/Shift）。
/// </summary>
public static class MagpieService
{
    /// <summary>Magpie 默认「缩放窗口」热键（读不到配置时使用）。</summary>
    public const string DefaultScaleHotkey = "Alt+Shift+A";

    private static readonly string[] CandidateDirs =
    {
        @"Magpie",
        @"Program Files\Magpie",
        @"Program Files (x86)\Magpie",
    };

    public static bool IsRunning()
    {
        try { return Process.GetProcessesByName("Magpie").Length > 0; }
        catch { return false; }
    }

    /// <summary>
    /// 猜测 Magpie 是否以管理员身份运行：普通权限进程读不到更高完整性级别进程的
    /// MainModule（Access Denied），据此判断。Magpie 开了「总是以管理员身份运行」时为真。
    /// 这种情况下 Windows 会拦截低权限进程注入的热键，自动超分会失效。
    /// </summary>
    public static bool IsProbablyElevated()
    {
        try
        {
            var procs = Process.GetProcessesByName("Magpie");
            if (procs.Length == 0) return false;
            try
            {
                _ = procs[0].MainModule?.FileName;
                return false; // 能读到，说明级别相同（非管理员）
            }
            catch
            {
                return true; // Access Denied → 更高级别
            }
        }
        catch { return false; }
    }

    /// <summary>查找 Magpie.exe：运行中进程 → MuiCache 运行记录 → 常见目录。</summary>
    public static string? TryFindExePath()
    {
        // 1) 正在运行的进程（Magpie 以管理员运行时读不到，属正常）
        try
        {
            foreach (var p in Process.GetProcessesByName("Magpie"))
            {
                try
                {
                    var path = p.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) return path;
                }
                catch { }
            }
        }
        catch { }

        // 2) 资源管理器的 MuiCache 里记录过 exe 全路径（绿色版也适用）
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache");
            if (key != null)
            {
                foreach (var name in key.GetValueNames())
                {
                    if (!name.EndsWith("Magpie.exe.FriendlyAppName", StringComparison.OrdinalIgnoreCase)) continue;
                    var path = name[..^".FriendlyAppName".Length];
                    if (File.Exists(path)) return path;
                }
            }
        }
        catch { }

        // 3) 常见安装位置（含各盘符根目录，绿色版常见）
        var candidates = new List<string>();
        foreach (var dir in CandidateDirs)
        {
            candidates.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), dir, "Magpie.exe"));
            candidates.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), dir, "Magpie.exe"));
        }
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed) continue;
                candidates.Add(Path.Combine(drive.RootDirectory.FullName, "Magpie", "Magpie.exe"));
                candidates.Add(Path.Combine(drive.RootDirectory.FullName, "Program Files", "Magpie", "Magpie.exe"));
            }
            catch { }
        }
        foreach (var c in candidates)
        {
            try { if (File.Exists(c)) return c; }
            catch { }
        }

        return null;
    }

    /// <summary>Magpie 配置文件：%LOCALAPPDATA%\Magpie\config\v&lt;版本&gt;\config.json，取版本号最大的。</summary>
    public static string? TryFindConfigPath()
    {
        try
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Magpie", "config");
            if (!Directory.Exists(root)) return null;

            string? best = null;
            int bestVersion = -1;
            foreach (var dir in Directory.GetDirectories(root))
            {
                var name = Path.GetFileName(dir);
                if (name.Length < 2 || (name[0] != 'v' && name[0] != 'V')) continue;
                if (!int.TryParse(name.AsSpan(1), out var version)) continue;
                var file = Path.Combine(dir, "config.json");
                if (!File.Exists(file)) continue;
                if (version > bestVersion) { bestVersion = version; best = file; }
            }
            return best;
        }
        catch { return null; }
    }

    /// <summary>读 Magpie 配置里的「缩放窗口」热键。</summary>
    public static bool TryReadScaleHotkey(out string hotkey, out string detail)
    {
        hotkey = "";
        detail = "";
        var path = TryFindConfigPath();
        if (path == null)
        {
            detail = "未找到 Magpie 配置文件";
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("shortcuts", out var shortcuts) ||
                !shortcuts.TryGetProperty("scale", out var scale) ||
                scale.ValueKind != JsonValueKind.Number)
            {
                detail = "配置里没有 shortcuts.scale";
                return false;
            }

            var value = scale.GetUInt32();
            if (value > 0xFFF)
            {
                detail = "热键值无法解析";
                return false;
            }

            var parts = new List<string>();
            if ((value & 0x100) != 0) parts.Add("Win");
            if ((value & 0x200) != 0) parts.Add("Ctrl");
            if ((value & 0x400) != 0) parts.Add("Alt");
            if ((value & 0x800) != 0) parts.Add("Shift");
            var code = (uint)(value & 0xFF);
            if (code == 0)
            {
                detail = "Magpie 尚未设置「缩放窗口」热键";
                return false;
            }
            parts.Add(HotkeyConfig.KeyName(code));

            hotkey = string.Join("+", parts);
            detail = $"已从 Magpie 配置读取：{hotkey}";
            return true;
        }
        catch (Exception ex)
        {
            detail = "读取 Magpie 配置失败：" + ex.Message;
            return false;
        }
    }

    /// <summary>最终生效的热键：用户填写 → Magpie 配置 → Magpie 默认值。</summary>
    public static string ResolveScaleHotkey(string? configured, out string source)
    {
        if (!string.IsNullOrWhiteSpace(configured) && IsValidHotkey(configured))
        {
            source = "设置";
            return Display(configured);
        }
        if (TryReadScaleHotkey(out var hotkey, out _))
        {
            source = "Magpie 配置";
            return hotkey;
        }
        source = "Magpie 默认值";
        return DefaultScaleHotkey;
    }

    /// <summary>启动 Magpie（未运行时）。</summary>
    public static bool TryStart(string? configuredPath, out string detail)
    {
        var exe = !string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath)
            ? configuredPath
            : TryFindExePath();
        if (exe == null || !File.Exists(exe))
        {
            detail = "未找到 Magpie.exe，请在设置里指定路径";
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            detail = $"启动 Magpie 失败：{ex.Message}（Magpie 通常需要管理员权限，可手动启动）";
            AppLog.Write($"Magpie: start failed: {ex.Message}");
            return false;
        }

        for (int i = 0; i < 25; i++)
        {
            if (IsRunning())
            {
                detail = "已启动 Magpie";
                return true;
            }
            Thread.Sleep(200);
        }

        detail = "Magpie 启动后未检测到进程";
        return false;
    }

    /// <summary>规范化显示，例如 "alt+w" → "Alt+W"。</summary>
    public static string Display(string? text) =>
        TryParse(text, out var mods, out var vk) ? Build(mods, vk) : (text ?? "").Trim();

    public static bool IsValidHotkey(string? text) => TryParse(text, out _, out _);

    /// <summary>合成热键。修饰键与主键之间要留出间隔：Magpie 的钩子用 GetAsyncKeyState 判断修饰键状态。</summary>
    public static async Task<bool> SendHotkeyAsync(string hotkey)
    {
        if (!TryParse(hotkey, out var mods, out var vk)) return false;

        foreach (var m in mods) SendKey(m, true);
        if (mods.Count > 0) await Task.Delay(70);

        SendKey(vk, true);
        await Task.Delay(45);
        SendKey(vk, false);

        await Task.Delay(35);
        for (int i = mods.Count - 1; i >= 0; i--) SendKey(mods[i], false);
        return true;
    }

    private static string Build(List<ushort> mods, ushort vk)
    {
        var sb = new StringBuilder();
        foreach (var m in mods)
        {
            sb.Append(m switch
            {
                VK_LWIN => "Win+",
                VK_CONTROL => "Ctrl+",
                VK_MENU => "Alt+",
                _ => "Shift+"
            });
        }
        sb.Append(HotkeyConfig.KeyName(vk));
        return sb.ToString();
    }

    private static bool TryParse(string? text, out List<ushort> mods, out ushort vk)
    {
        mods = new List<ushort>();
        vk = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        bool win = false, ctrl = false, alt = false, shift = false;
        ushort key = 0;

        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.Trim();
            if (token.Length == 0) continue;

            switch (token.ToLowerInvariant())
            {
                case "win": case "windows": case "super": case "meta": case "cmd": win = true; continue;
                case "ctrl": case "control": ctrl = true; continue;
                case "alt": case "menu": alt = true; continue;
                case "shift": shift = true; continue;
            }

            if (key != 0) return false; // 两个主键 → 非法
            if (!TryParseKey(token, out key)) return false;
        }

        if (key == 0) return false;

        // 顺序与 Magpie 的显示一致：Win → Ctrl → Alt → Shift
        if (win) mods.Add(VK_LWIN);
        if (ctrl) mods.Add(VK_CONTROL);
        if (alt) mods.Add(VK_MENU);
        if (shift) mods.Add(VK_LSHIFT);
        vk = key;
        return true;
    }

    private static bool TryParseKey(string token, out ushort vk)
    {
        vk = 0;
        if (token.Length == 1)
        {
            var c = char.ToUpperInvariant(token[0]);
            if (c is >= 'A' and <= 'Z') { vk = c; return true; }
            if (c is >= '0' and <= '9') { vk = c; return true; }
        }

        if ((token[0] == 'F' || token[0] == 'f') &&
            int.TryParse(token.AsSpan(1), out var fn) && fn is >= 1 and <= 24)
        {
            vk = (ushort)(0x6F + fn);
            return true;
        }

        ushort? named = token.ToLowerInvariant() switch
        {
            "space" => 0x20,
            "pageup" or "pgup" => 0x21,
            "pagedown" or "pgdn" => 0x22,
            "end" => 0x23,
            "home" => 0x24,
            "insert" or "ins" => 0x2D,
            "delete" or "del" => 0x2E,
            "backspace" or "back" => 0x08,
            "tab" => 0x09,
            "enter" or "return" => 0x0D,
            "esc" or "escape" => 0x1B,
            "left" or "←" => 0x25,
            "up" or "↑" => 0x26,
            "right" or "→" => 0x27,
            "down" or "↓" => 0x28,
            ";" => 0xBA, "=" => 0xBB, "," => 0xBC, "-" => 0xBD, "." => 0xBE,
            "/" => 0xBF, "`" => 0xC0, "[" => 0xDB, "\\" => 0xDC, "]" => 0xDD, "'" => 0xDE,
            _ => null
        };
        if (named == null) return false;
        vk = named.Value;
        return true;
    }

    // ── 结果校验：Magpie 的日志会记录热键激活 ──

    /// <summary>Magpie 日志路径（Magpie.exe 同级的 logs\magpie.log）。</summary>
    public static string? TryFindLogPath(string? exePath = null)
    {
        var exe = !string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath) ? exePath : TryFindExePath();
        if (exe == null) return null;
        try
        {
            var log = Path.Combine(Path.GetDirectoryName(exe) ?? "", "logs", "magpie.log");
            return File.Exists(log) ? log : null;
        }
        catch { return null; }
    }

    /// <summary>日志当前长度（用于判断之后是否新增了热键激活记录）。</summary>
    public static long LogSize(string? logPath)
    {
        try { return logPath != null && File.Exists(logPath) ? new FileInfo(logPath).Length : -1; }
        catch { return -1; }
    }

    /// <summary>日志在 givenSize 之后是否出现「热键 Scale 激活」。</summary>
    public static bool LogHasScaleActivation(string? logPath, long sinceSize, out string line)
    {
        line = "";
        try
        {
            if (logPath == null || !File.Exists(logPath)) return false;
            using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (sinceSize > 0 && sinceSize <= fs.Length) fs.Seek(sinceSize, SeekOrigin.Begin);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            var text = reader.ReadToEnd();
            foreach (var l in text.Split('\n'))
            {
                if (l.Contains("Scale") && l.Contains("激活"))
                {
                    line = l.Trim();
                    return true;
                }
            }
            return false;
        }
        catch { return false; }
    }

    // ── SendInput ──

    private const ushort VK_SHIFT = 0x10;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_MENU = 0x12;
    private const ushort VK_LSHIFT = 0xA0;
    private const ushort VK_LWIN = 0x5B;

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;

    private static void SendKey(ushort vk, bool down)
    {
        var input = new INPUT { type = INPUT_KEYBOARD };
        input.U.ki.wVk = vk;
        input.U.ki.dwFlags = down ? 0 : KEYEVENTF_KEYUP;
        if (vk == VK_LWIN) input.U.ki.dwFlags |= KEYEVENTF_EXTENDEDKEY;
        var inputs = new[] { input };
        SendInput(1, inputs, Marshal.SizeOf<INPUT>());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
}
