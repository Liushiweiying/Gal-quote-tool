using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;

namespace GalgameQuoteCollector.Views;

public partial class ToastWindow : Window
{
    private readonly Action? _onClick;
    private readonly System.Timers.Timer _timer;

    public ToastWindow(string title, string body, int durationMs = 3000, Action? onClick = null)
    {
        InitializeComponent();
        TitleText.Text = title;
        BodyText.Text = body;
        _onClick = onClick;

        // Position at top-right
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 12;
        Top = workArea.Top + 12;

        // Auto-close timer
        _timer = new System.Timers.Timer(durationMs);
        _timer.Elapsed += (_, _) => Dispatcher.Invoke(Close);
        _timer.AutoReset = false;
        _timer.Start();
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
        base.OnClosed(e);
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
