using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;

namespace GalQuoteCollector.Services;

/// <summary>
/// TranslucentTB helper. Taskbar transparency occasionally fails to apply at boot.
///
/// Root cause (confirmed by logs): restarting TranslucentTB immediately after logon does
/// not help because the shell/taskbar is still initialising — TTB applies its appearance
/// on start, so starting it while the shell is not ready fails the same way. A restart
/// minutes later (e.g. the "repair now" button) works. The boot repair therefore:
///   1. waits for the taskbar (Shell_TrayWnd) to exist,
///   2. waits for TranslucentTB to be running,
///   3. waits a settle period so TTB's own start-up attempt has finished,
///   4. restarts TTB, then samples the taskbar pixels,
///   5. retries once after a longer delay if the appearance did not change.
///
/// A Store build lives under WindowsApps and can only be started through its AUMID.
/// </summary>
public static class TranslucentTbService
{
    private const string ProcessName = "TranslucentTB";

    public static bool IsRunning() => Process.GetProcessesByName(ProcessName).Length > 0;

    private static bool IsTaskbarReady()
    {
        try { return FindWindow("Shell_TrayWnd", null) != IntPtr.Zero; }
        catch { return false; }
    }

    /// <summary>
    /// 任务栏通知区（TrayNotifyWnd）出现才算 shell 基本就绪——比只看 Shell_TrayWnd 更靠谱，
    /// 过早重启 TranslucentTB 无法让它把透明效果应用上去。
    /// </summary>
    private static bool IsShellSettled()
    {
        try
        {
            var tray = FindWindow("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) return false;
            return FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null) != IntPtr.Zero;
        }
        catch { return false; }
    }

    /// <summary>Boot-time repair: shell-aware timing + verification retry.</summary>
    public static async Task<(bool ok, string detail)> RepairAtBootAsync(int settleSeconds = 20)
    {
        // 1. shell / taskbar ready
        int waited = 0;
        while (!IsTaskbarReady() && waited < 180)
        {
            await Task.Delay(1000);
            waited++;
        }
        AppLog.Write($"TTB boot: taskbar ready after {waited}s");

        // 2. shell settled (tray area exists) — restarting TTB before this does not stick
        waited = 0;
        while (!IsShellSettled() && waited < 180)
        {
            await Task.Delay(1000);
            waited++;
        }
        AppLog.Write($"TTB boot: shell settled after {waited}s");

        // 3. TranslucentTB running
        waited = 0;
        while (!IsRunning() && waited < 120)
        {
            await Task.Delay(1000);
            waited++;
        }
        if (!IsRunning())
        {
            AppLog.Write("TTB boot: not running, skipped");
            return (false, "TranslucentTB 未在运行");
        }
        AppLog.Write($"TTB boot: process present after {waited}s");

        // 4. let TTB's own first attempt (and the shell) settle
        await Task.Delay(Math.Max(0, settleSeconds) * 1000);

        // 5. restart once, then verify the taskbar actually changed
        var before = TaskbarSample.Sample();
        var first = await RestartAsync();
        if (!first.ok) return first;

        await Task.Delay(4000);
        var after = TaskbarSample.Sample();
        AppLog.Write($"TTB boot: sample before={before} after={after}");

        if (!TaskbarSample.CanTell(before) || !TaskbarSample.CanTell(after))
        {
            // 任务栏被全屏窗口遮住（采样全黑）时无法判断，不做多余的重启
            AppLog.Write("TTB boot: taskbar sample unusable, skipping verification retry");
            return (true, "已重启 TranslucentTB");
        }

        if (TaskbarSample.Changed(before, after))
            return (true, "已重启 TranslucentTB（任务栏外观已变化）");

        // 6. not effective yet — the shell may still have been settling; retry once
        AppLog.Write("TTB boot: appearance unchanged, retrying after 30s");
        await Task.Delay(30_000);
        var second = await RestartAsync();
        if (!second.ok) return second;

        await Task.Delay(4000);
        var after2 = TaskbarSample.Sample();
        AppLog.Write($"TTB boot: retry sample={after2} (changed={TaskbarSample.Changed(after, after2)})");
        return (true, "已重启 TranslucentTB（含一次重试）");
    }

    /// <summary>Restart TranslucentTB once so it re-applies the taskbar appearance.</summary>
    public static async Task<(bool ok, string detail)> RestartAsync()
    {
        var procs = Process.GetProcessesByName(ProcessName);
        if (procs.Length == 0)
        {
            AppLog.Write("TTB: not running, nothing to restart");
            return (false, "TranslucentTB 未在运行");
        }

        string? aumid = null;
        string? exePath = null;
        try
        {
            aumid = TryGetAumid(procs[0]);
            exePath = TryGetExePath(procs[0]);
        }
        catch (Exception ex)
        {
            AppLog.Write($"TTB: launch info failed: {ex.Message}");
        }
        AppLog.Write($"TTB: aumid={(aumid ?? "<none>")} exe={(exePath ?? "<none>")}");

        foreach (var p in procs)
        {
            try { p.Kill(); }
            catch (Exception ex) { AppLog.Write($"TTB: kill failed ({p.Id}): {ex.Message}"); }
        }
        for (int i = 0; i < 25 && IsRunning(); i++)
            await Task.Delay(200);
        if (IsRunning())
        {
            AppLog.Write("TTB: still running after kill, aborting restart");
            return (false, "无法结束 TranslucentTB 进程");
        }
        AppLog.Write("TTB: stopped");

        bool launched = false;
        if (!string.IsNullOrWhiteSpace(aumid))
            launched = TryLaunch("explorer.exe", $"shell:appsFolder\\{aumid}");
        if (!launched && !string.IsNullOrWhiteSpace(exePath))
            launched = TryLaunch(exePath!, null);

        if (!launched)
        {
            AppLog.Write("TTB: launch failed");
            return (false, "无法启动 TranslucentTB（AUMID 与 exe 路径都失败）");
        }

        for (int i = 0; i < 30; i++)
        {
            if (IsRunning())
            {
                AppLog.Write($"TTB: restarted OK (waited {i * 200}ms)");
                return (true, "已重启 TranslucentTB");
            }
            await Task.Delay(200);
        }

        AppLog.Write("TTB: process did not come back within 6s");
        return (false, "TranslucentTB 重启后未检测到进程");
    }

    private static bool TryLaunch(string fileName, string? arguments)
    {
        try
        {
            var psi = new ProcessStartInfo { FileName = fileName, UseShellExecute = true };
            if (arguments != null) psi.Arguments = arguments;
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Write($"TTB: launch '{fileName} {arguments}' failed: {ex.Message}");
            return false;
        }
    }

    private static string? TryGetExePath(Process p)
    {
        try { return p.MainModule?.FileName; }
        catch { return null; }
    }

    /// <summary>AUMID of a packaged (Store) app — required to start it via shell:appsFolder.</summary>
    private static string? TryGetAumid(Process p)
    {
        IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, p.Id);
        if (handle == IntPtr.Zero) return null;
        try
        {
            uint len = 512;
            var buffer = new char[len];
            int hr = GetApplicationUserModelId(handle, ref len, buffer);
            if (hr != 0) return null;
            var aumid = new string(buffer, 0, (int)Math.Max(0, len - 1)).TrimEnd('\0');
            return string.IsNullOrWhiteSpace(aumid) ? null : aumid;
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>
    /// Samples a few taskbar pixels to judge whether TranslucentTB's appearance is applied.
    /// Heuristic: used only to decide whether a retry is worth doing (a false "unchanged"
    /// costs one extra restart, never data).
    /// </summary>
    private static class TaskbarSample
    {
        public static string Sample()
        {
            try
            {
                var hwnd = FindWindow("Shell_TrayWnd", null);
                if (hwnd == IntPtr.Zero) return "no-taskbar";
                if (!GetWindowRect(hwnd, out var r)) return "no-rect";

                int w = r.right - r.left;
                int h = r.bottom - r.top;
                if (w <= 0 || h <= 0) return "bad-rect";

                int y = r.top + h / 2;
                int[] xs = { r.left + w / 6, r.left + w / 3, r.left + w / 2, r.left + (w * 2) / 3, r.left + (w * 5) / 6 };

                using var bmp = new Bitmap(1, 1);
                using var g = Graphics.FromImage(bmp);
                long sr = 0, sg = 0, sb = 0;
                int n = 0;
                foreach (var x in xs)
                {
                    g.CopyFromScreen(x, y, 0, 0, new Size(1, 1));
                    var c = bmp.GetPixel(0, 0);
                    sr += c.R; sg += c.G; sb += c.B; n++;
                }
                if (n == 0) return "no-samples";
                return $"rgb({sr / n},{sg / n},{sb / n})";
            }
            catch (Exception ex)
            {
                return $"err:{ex.GetType().Name}";
            }
        }

        public static bool Changed(string a, string b)
        {
            if (a == b) return false;
            if (a.StartsWith("rgb") && b.StartsWith("rgb")) return true; // different average colour
            return false; // unknown samples → treat as unchanged so we retry
        }

        /// <summary>
        /// 采样是否可信：全黑说明任务栏被全屏窗口（如 Magpie 缩放窗口的黑边）遮住，
        /// 这时无从判断透明是否生效，就不再补一次重启。
        /// </summary>
        public static bool CanTell(string sample) =>
            sample.StartsWith("rgb") && sample != "rgb(0,0,0)";
    }

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetApplicationUserModelId(IntPtr hProcess, ref uint applicationUserModelIdLength,
        [Out] char[] applicationUserModelId);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter,
        string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }
}
