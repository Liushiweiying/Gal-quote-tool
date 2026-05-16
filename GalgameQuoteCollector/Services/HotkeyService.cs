using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace GalgameQuoteCollector.Services;

public class HotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int HOTKEY_ID = 9000;

    private readonly Window _window;
    private HwndSource? _source;
    private bool _registered;
    private bool _hookAdded;

    private uint _currentModifiers;
    private uint _currentVirtualKey;

    public event EventHandler? HotkeyPressed;

    /// <summary>
    /// Human-readable name of the current hotkey, e.g. "Ctrl+Win+Z".
    /// </summary>
    public string CurrentHotkeyDisplay { get; private set; } = "";

    public HotkeyService(Window window, uint modifiers, uint virtualKey)
    {
        _window = window;
        _currentModifiers = modifiers;
        _currentVirtualKey = virtualKey;
        CurrentHotkeyDisplay = FormatKeys(modifiers, virtualKey);

        TryRegister();
        if (!_registered && !window.IsLoaded)
        {
            window.SourceInitialized += OnWindowReady;
        }
    }

    private void OnWindowReady(object? sender, EventArgs e)
    {
        TryRegister();
    }

    private void TryRegister()
    {
        _source = PresentationSource.FromVisual(_window) as HwndSource;
        if (_source == null) return;

        if (!_hookAdded)
        {
            _source.AddHook(WndProc);
            _hookAdded = true;
        }

        RegisterHotkey(_currentModifiers, _currentVirtualKey);
    }

    /// <summary>
    /// Change the hotkey at runtime. Unregisters the old one first.
    /// Returns true if successful.
    /// </summary>
    public bool UpdateHotkey(uint modifiers, uint virtualKey)
    {
        // Unregister old
        if (_registered && _source != null)
        {
            UnregisterHotKey(_source.Handle, HOTKEY_ID);
            _registered = false;
        }

        _currentModifiers = modifiers;
        _currentVirtualKey = virtualKey;
        CurrentHotkeyDisplay = FormatKeys(modifiers, virtualKey);

        // Register new (if source is ready)
        if (_source != null)
        {
            var result = RegisterHotKey(_source.Handle, HOTKEY_ID, modifiers, virtualKey);
            _registered = result;
            return result;
        }

        return false;
    }

    private void RegisterHotkey(uint modifiers, uint virtualKey)
    {
        if (_registered || _source == null) return;

        var result = RegisterHotKey(_source.Handle, HOTKEY_ID, modifiers, virtualKey);
        _registered = result;
        if (!result)
            throw new InvalidOperationException("热键注册失败，可能被其他程序占用");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
        {
            HotkeyPressed?.Invoke(this, EventArgs.Empty);
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_registered && _source != null)
        {
            UnregisterHotKey(_source.Handle, HOTKEY_ID);
            _registered = false;
        }
        if (_hookAdded && _source != null)
        {
            _source.RemoveHook(WndProc);
            _hookAdded = false;
        }
        _window.SourceInitialized -= OnWindowReady;
    }

    private static string FormatKeys(uint modifiers, uint virtualKey)
    {
        var parts = new System.Collections.Generic.List<string>();
        if ((modifiers & 0x0002) != 0) parts.Add("Ctrl");
        if ((modifiers & 0x0001) != 0) parts.Add("Alt");
        if ((modifiers & 0x0004) != 0) parts.Add("Shift");
        if ((modifiers & 0x0008) != 0) parts.Add("Win");

        var keyChar = virtualKey >= 0x41 && virtualKey <= 0x5A
            ? ((char)virtualKey).ToString()
            : $"0x{virtualKey:X2}";
        parts.Add(keyChar);

        return string.Join("+", parts);
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
