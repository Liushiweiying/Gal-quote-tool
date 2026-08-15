using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;

namespace GalQuoteCollector.Views;

public partial class ToastWindow : Window
{
    private readonly Action? _onClick;
    private readonly System.Timers.Timer _timer;
    private bool _closing;

    // Stacking: multiple toasts stack downward from the top-right corner
    private static readonly List<ToastWindow> _openToasts = new();
    private const double ToastHeight = 80;
    private const double ToastGap = 10;

    public ToastWindow(string title, string body, int durationMs = 3000, Action? onClick = null)
    {
        InitializeComponent();
        TitleText.Text = title;
        BodyText.Text = body;
        _onClick = onClick;

        // Auto-close timer (started once the window is visible)
        _timer = new System.Timers.Timer(durationMs);
        _timer.Elapsed += (_, _) => Dispatcher.Invoke(BeginClose);
        _timer.AutoReset = false;

        Loaded += (_, _) =>
        {
            _openToasts.Add(this);
            Reposition();
            FadeIn();
            _timer.Start();
        };
    }

    private void FadeIn()
    {
        Opacity = 0;
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
    }

    private void BeginClose()
    {
        if (_closing) return;
        _closing = true;
        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(300));
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }

    private void Reposition()
    {
        var workArea = SystemParameters.WorkArea;
        for (int i = 0; i < _openToasts.Count; i++)
        {
            var t = _openToasts[i];
            t.Left = workArea.Right - t.Width - 12;
            t.Top = workArea.Top + 12 + i * (ToastHeight + ToastGap);
        }
    }

    private void OnToastClick(object sender, MouseButtonEventArgs e)
    {
        _timer.Stop();
        _onClick?.Invoke();
        Close();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Remove from Alt+Tab by adding WS_EX_TOOLWINDOW style
        var hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _timer.Dispose();
        _openToasts.Remove(this);
        Reposition();
        base.OnClosed(e);
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
