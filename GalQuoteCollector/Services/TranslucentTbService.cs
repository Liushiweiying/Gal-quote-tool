using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GalQuoteCollector.Services;

/// <summary>
/// TranslucentTB helper. Taskbar transparency occasionally fails to apply at boot;
/// restarting TranslucentTB makes it re-apply its configuration. Restarting is done
/// properly: the Store build lives under WindowsApps and can only be started through
/// its Application User Model ID (AUMID), not by launching its exe path.
/// </summary>
public static class TranslucentTbService
{
    private const string ProcessName = "TranslucentTB";

    public static bool IsRunning() => Process.GetProcessesByName(ProcessName).Length > 0;

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

        // Stop every instance, then wait for the processes to真的 exit
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

        // Start it again — AUMID first (Store build), exe path as fallback
        bool launched = false;
        if (!string.IsNullOrWhiteSpace(aumid))
            launched = TryLaunch($"explorer.exe", $"shell:appsFolder\\{aumid}");
        if (!launched && !string.IsNullOrWhiteSpace(exePath))
            launched = TryLaunch(exePath!, null);

        if (!launched)
        {
            AppLog.Write("TTB: launch failed");
            return (false, "无法启动 TranslucentTB（AUMID 与 exe 路径都失败）");
        }

        // Verify it came back (TTB applies its config on start)
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

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetApplicationUserModelId(IntPtr hProcess, ref uint applicationUserModelIdLength,
        [Out] char[] applicationUserModelId);
}
