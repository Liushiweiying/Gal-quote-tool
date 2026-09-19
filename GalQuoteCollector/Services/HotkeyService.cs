using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GalQuoteCollector.Services;

public class HotkeyService : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    private IntPtr _hookId = IntPtr.Zero;
    private HookProc? _hookProc;

    // Primary hotkey
    private uint _mod1, _vk1;

    // Secondary hotkey (add screenshot)
    private uint _mod2, _vk2;

    /// <summary>
    /// 触发截图热键时是否吞掉这次按键（不让前台程序收到）。
    /// 很多游戏的引擎也用 Alt+E 之类热键，不吞的话会同时弹游戏的窗口。
    /// 默认开；在自己的窗口里（设置改键等）永远不吞。
    /// </summary>
    public bool SwallowHotkeys { get; set; } = true;

    // 挂起计数：设置窗口改键期间不要触发截图
    private int _suspended;

    // 已经吞掉了按下事件的键，对应的抬起也要吞掉，避免前台程序收到"悬空"按键
    private uint _swallowUpVk;

    private static int _ownPid = Process.GetCurrentProcess().Id;

    public void Suspend() => _suspended++;
    public void Resume() { if (_suspended > 0) _suspended--; }

    public event EventHandler? HotkeyPressed;
    public event EventHandler? HotkeyPressedAdd;

    public string CurrentHotkeyDisplay { get; private set; } = "";

    public HotkeyService(uint modifiers, uint virtualKey,
                         uint addModifiers = 0, uint addVirtualKey = 0)
    {
        _mod1 = modifiers; _vk1 = virtualKey;
        _mod2 = addModifiers; _vk2 = addVirtualKey;
        CurrentHotkeyDisplay = FormatKeys(modifiers, virtualKey);
        InstallHook();
    }

    private void InstallHook()
    {
        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule!;
        _hookProc = HookCallback;
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc,
            GetModuleHandle(curModule.ModuleName), 0);
    }

    /// <summary>
    /// Update the primary hotkey. Returns false when the new combo collides with the
    /// add-screenshot hotkey (the two must stay distinct so a single press can't fire
    /// both actions).
    /// </summary>
    public bool UpdateHotkey(uint modifiers, uint virtualKey)
    {
        if (_vk2 > 0 && modifiers == _mod2 && virtualKey == _vk2)
            return false; // 与补拍热键冲突
        _mod1 = modifiers; _vk1 = virtualKey;
        CurrentHotkeyDisplay = FormatKeys(modifiers, virtualKey);
        return true;
    }

    public void UpdateAddHotkey(uint modifiers, uint virtualKey)
    {
        _mod2 = modifiers; _vk2 = virtualKey;
    }

    // Both Windows keys count (left 0x5B / right 0x5C)
    private bool WinKeyDown =>
        (GetAsyncKeyState(0x5B) & 0x8000) != 0 || (GetAsyncKeyState(0x5C) & 0x8000) != 0;

    private bool ModifiersMatch(uint mods, bool winKey)
    {
        bool ctrl = (GetAsyncKeyState(0x11) & 0x8000) != 0;
        bool alt = (GetAsyncKeyState(0x12) & 0x8000) != 0;
        bool shift = (GetAsyncKeyState(0x10) & 0x8000) != 0;
        return ctrl == ((mods & 0x0002) != 0) &&
               alt == ((mods & 0x0001) != 0) &&
               shift == ((mods & 0x0004) != 0) &&
               winKey == ((mods & 0x0008) != 0);
    }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int vkCode = Marshal.ReadInt32(lParam);
            bool keyDown = wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN;
            bool keyUp = wParam == WM_KEYUP || wParam == WM_SYSKEYUP;

            if (keyDown && _suspended == 0)
            {
                bool primary = vkCode == _vk1 && ModifiersMatch(_mod1, WinKeyDown);
                bool add = _vk2 > 0 && vkCode == _vk2 && ModifiersMatch(_mod2, WinKeyDown);

                if (primary) HotkeyPressed?.Invoke(this, EventArgs.Empty);
                if (add) HotkeyPressedAdd?.Invoke(this, EventArgs.Empty);

                // 吞掉这次按键，免得游戏引擎的同名热键也被触发
                // （在自己窗口里不吞：设置界面改键、或者我们在自己的 UI 上按了同样的键）
                if ((primary || add) && SwallowHotkeys && !IsOwnProcessForeground())
                {
                    _swallowUpVk = (uint)vkCode;
                    AppLog.Write($"hotkey: 已吞掉 {FormatKeys(primary ? _mod1 : _mod2, (uint)vkCode)}（不再传给前台程序）");
                    return 1;
                }
            }
            else if (keyUp && _swallowUpVk != 0 && _swallowUpVk == (uint)vkCode)
            {
                _swallowUpVk = 0;
                return 1;
            }
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    /// <summary>前台窗口是不是本程序自己（自己窗口里不改键、不吞键）。</summary>
    private static bool IsOwnProcessForeground()
    {
        try
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return false;
            GetWindowThreadProcessId(hwnd, out uint pid);
            return pid == (uint)_ownPid;
        }
        catch { return false; }
    }

    public void Dispose()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
    }

    private static string FormatKeys(uint modifiers, uint virtualKey)
    {
        var parts = new System.Collections.Generic.List<string>();
        if ((modifiers & 0x0002) != 0) parts.Add("Ctrl");
        if ((modifiers & 0x0001) != 0) parts.Add("Alt");
        if ((modifiers & 0x0004) != 0) parts.Add("Shift");
        if ((modifiers & 0x0008) != 0) parts.Add("Win");
        parts.Add(Models.HotkeyConfig.KeyName(virtualKey));
        return string.Join("+", parts);
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
