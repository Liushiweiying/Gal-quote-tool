using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace GalQuoteCollector.Views;

public enum InfoDialogButtons { OK, OKCancel, YesNo }
public enum InfoDialogResult { OK, Yes, No, Cancel }
public enum InfoDialogIcon { Information, Question, Warning, Error }

/// <summary>
/// Unified modern replacement for MessageBox: borderless rounded card, theme-colored
/// icon, primary/danger buttons, draggable, Enter/Esc keyboard support.
/// </summary>
public partial class InfoDialog : Window
{
    private readonly InfoDialogButtons _buttons;
    private InfoDialogResult _result;

    public InfoDialog(string title, string message,
        InfoDialogButtons buttons = InfoDialogButtons.OK,
        InfoDialogIcon icon = InfoDialogIcon.Information,
        bool dangerConfirm = false)
    {
        InitializeComponent();
        _buttons = buttons;
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;

        switch (icon)
        {
            case InfoDialogIcon.Information:
                IconText.Text = "\uE946"; // MDL2 Info
                IconText.Foreground = new SolidColorBrush(Color.FromRgb(0x5B, 0x6A, 0xBF));
                break;
            case InfoDialogIcon.Question:
                IconText.Text = "\uE9CE"; // MDL2 Unknown
                IconText.Foreground = new SolidColorBrush(Color.FromRgb(0x5B, 0x6A, 0xBF));
                break;
            case InfoDialogIcon.Warning:
                IconText.Text = "\uE7BA"; // MDL2 Warning
                IconText.Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0xA6, 0x23));
                break;
            case InfoDialogIcon.Error:
                IconText.Text = "\uEA39"; // MDL2 ErrorBadge
                IconText.Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35));
                break;
        }

        switch (buttons)
        {
            case InfoDialogButtons.OK:
                OkBtn.Content = "确定";
                break;
            case InfoDialogButtons.OKCancel:
                OkBtn.Content = "确定";
                CancelBtn.Visibility = Visibility.Visible;
                break;
            case InfoDialogButtons.YesNo:
                OkBtn.Content = "是";
                NoBtn.Visibility = Visibility.Visible;
                break;
        }

        if (dangerConfirm)
            OkBtn.Background = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35));
    }

    public static InfoDialogResult Show(Window? owner, string title, string message,
        InfoDialogButtons buttons = InfoDialogButtons.OK,
        InfoDialogIcon icon = InfoDialogIcon.Information,
        bool dangerConfirm = false)
    {
        var dlg = new InfoDialog(title, message, buttons, icon, dangerConfirm);
        if (owner != null && owner.IsLoaded)
            dlg.Owner = owner;
        dlg.ShowDialog();
        return dlg._result;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        _result = _buttons == InfoDialogButtons.YesNo ? InfoDialogResult.Yes : InfoDialogResult.OK;
        DialogResult = true;
        Close();
    }

    private void OnNo(object sender, RoutedEventArgs e)
    {
        _result = InfoDialogResult.No;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _result = InfoDialogResult.Cancel;
        DialogResult = true;
        Close();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            OnCancel(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            OnOk(sender, e);
            e.Handled = true;
        }
    }

    private void OnDragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }
}
