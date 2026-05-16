using System.Windows;
using System.Windows.Input;
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

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _timer.Dispose();
        base.OnClosed(e);
    }
}
