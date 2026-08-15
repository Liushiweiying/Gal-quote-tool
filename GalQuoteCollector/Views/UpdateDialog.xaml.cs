using System.IO;
using System.Windows;
using System.Windows.Input;
using GalQuoteCollector.Services;

namespace GalQuoteCollector.Views;

/// <summary>
/// Update dialog: shows the release notes, downloads the installer with a progress
/// bar and sha256 verification, then offers one-click install (app exits and the
/// installer starts).
/// </summary>
public partial class UpdateDialog : Window
{
    private readonly UpdateService.UpdateInfo _info;
    private readonly UpdateService _updateService;
    private readonly Action<string>? _onSkipped;
    private string? _downloadedPath;
    private bool _downloading;

    public bool InstallRequested { get; private set; }
    public string? DownloadedPath => _downloadedPath;

    public UpdateDialog(UpdateService.UpdateInfo info, string currentVersion,
        UpdateService updateService, Action<string>? onSkipped = null)
    {
        InitializeComponent();
        _info = info;
        _updateService = updateService;
        _onSkipped = onSkipped;

        TitleText.Text = $"发现新版本 {info.Tag}";
        VersionText.Text = $"当前版本: {currentVersion}";
        BodyBox.Text = string.IsNullOrWhiteSpace(info.Body) ? "（无更新说明）" : info.Body;

        if (string.IsNullOrWhiteSpace(info.AssetUrl))
        {
            DownloadBtn.IsEnabled = false;
            StatusText.Text = "未找到可下载的安装包";
        }
    }

    private async void OnDownload(object sender, RoutedEventArgs e)
    {
        // Second click = install the already-downloaded installer
        if (_downloadedPath != null)
        {
            InstallRequested = true;
            DialogResult = true;
            Close();
            return;
        }

        if (_downloading) return;
        _downloading = true;
        DownloadBtn.IsEnabled = false;
        SkipBtn.IsEnabled = false;
        ProgressBar.Visibility = Visibility.Visible;
        StatusText.Text = "正在下载...";

        var progress = new Progress<double>(p =>
        {
            ProgressBar.Value = p * 100;
            StatusText.Text = $"正在下载... {p:P0}";
        });

        try
        {
            var destDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            _downloadedPath = await Task.Run(() => _updateService.DownloadAsync(_info, destDir, progress));

            ProgressBar.Value = 100;
            StatusText.Text = "下载完成（已通过 SHA256 校验）。点击「立即安装」将退出本程序并启动安装器。";
            DownloadBtn.Content = "立即安装";
            DownloadBtn.IsEnabled = true;
            CancelBtn.Content = "稍后";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"下载失败: {ex.Message}";
            DownloadBtn.IsEnabled = true;
            SkipBtn.IsEnabled = true;
        }
        finally
        {
            _downloading = false;
        }
    }

    private void OnSkip(object sender, RoutedEventArgs e)
    {
        _onSkipped?.Invoke(_info.Tag);
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            OnCancel(sender, e);
            e.Handled = true;
        }
    }
}
