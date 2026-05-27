using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GalgameQuoteCollector.Services;

public class HotkeyService : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    private IntPtr _hookId = IntPtr.Zero;
    private HookProc? _hookProc; // keep alive

    private uint _modifiers;  // cached modifier state
    private uint _virtualKey;
    private bool _winKeyDown;

    public event EventHandler? HotkeyPressed;
    public string CurrentHotkeyDisplay { get; private set; } = "";

    public HotkeyService(uint modifiers, uint virtualKey)
    {
        _modifiers = modifiers;
        _virtualKey = virtualKey;
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
        _modifiers = modifiers;
        _virtualKey = virtualKey;
        CurrentHotkeyDisplay = FormatKeys(modifiers, virtualKey);
        return true;
    }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int vkCode = Marshal.ReadInt32(lParam);
            bool keyDown = wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN;

            // Track modifier keys for state
            switch (vkCode)
            {
                case 0xA0: case 0xA1: /* L/R Shift */ break;
                case 0xA2: case 0xA3: /* L/R Ctrl */ break;
                case 0xA4: case 0xA5: /* L/R Alt */ break;
                case 0x5B: case 0x5C: /* L/R Win */ _winKeyDown = keyDown; break;
            }

            if (keyDown && vkCode == _virtualKey && ModifiersDown())
            {
                HotkeyPressed?.Invoke(this, EventArgs.Empty);
                return (IntPtr)1; // block the key from reaching other apps
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private bool ModifiersDown()
    {
        bool ctrl = (GetAsyncKeyState(0x11) & 0x8000) != 0;
        bool alt = (GetAsyncKeyState(0x12) & 0x8000) != 0;
        bool shift = (GetAsyncKeyState(0x10) & 0x8000) != 0;

        bool wantCtrl = (_modifiers & 0x0002) != 0;
        bool wantAlt = (_modifiers & 0x0001) != 0;
        bool wantShift = (_modifiers & 0x0004) != 0;
        bool wantWin = (_modifiers & 0x0008) != 0;

        return ctrl == wantCtrl && alt == wantAlt && shift == wantShift && _winKeyDown == wantWin;
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
