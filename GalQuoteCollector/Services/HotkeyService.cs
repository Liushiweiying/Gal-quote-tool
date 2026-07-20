using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GalQuoteCollector.Services;

public class HotkeyService : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    private IntPtr _hookId = IntPtr.Zero;
    private HookProc? _hookProc;

    // Primary hotkey
    private uint _mod1, _vk1;
    private bool _winKeyDown1;

    // Secondary hotkey (add screenshot)
    private uint _mod2, _vk2;
    private bool _winKeyDown2;

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

    public bool UpdateHotkey(uint modifiers, uint virtualKey)
    {
        _mod1 = modifiers; _vk1 = virtualKey;
        CurrentHotkeyDisplay = FormatKeys(modifiers, virtualKey);
        return true;
    }

    public void UpdateAddHotkey(uint modifiers, uint virtualKey)
    {
        _mod2 = modifiers; _vk2 = virtualKey;
    }

    private bool WinKeyDown => (GetAsyncKeyState(0x5B) & 0x8000) != 0;

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

            if (keyDown)
            {
                if (vkCode == _vk1 && ModifiersMatch(_mod1, WinKeyDown))
                    HotkeyPressed?.Invoke(this, EventArgs.Empty);

                if (_vk2 > 0 && vkCode == _vk2 && ModifiersMatch(_mod2, WinKeyDown))
                    HotkeyPressedAdd?.Invoke(this, EventArgs.Empty);
            }
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
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
        parts.Add(((char)virtualKey).ToString());
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
}
