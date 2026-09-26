using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace GalQuoteCollector.Views;

/// <summary>显示二维码的小窗口（由二维码插件生成 PNG，主程序只负责显示）。</summary>
public partial class QrWindow : Window
{
    private readonly string _uri;

    public QrWindow(string otpauthUri, byte[] png, string hint)
    {
        InitializeComponent();
        _uri = otpauthUri;
        UriText.Text = otpauthUri;
        HintText.Text = hint;
        try
        {
            using var ms = new MemoryStream(png);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            QrImage.Source = bmp;
        }
        catch (Exception ex)
        {
            HintText.Text = "二维码显示失败：" + ex.Message + "\n可以改用「手动输入密钥」。";
        }
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_uri);
            HintText.Text = "otpauth 链接已复制到剪贴板。";
        }
        catch (Exception ex)
        {
            HintText.Text = "复制失败：" + ex.Message;
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
