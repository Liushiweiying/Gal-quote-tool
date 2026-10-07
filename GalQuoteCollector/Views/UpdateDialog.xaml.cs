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
        BodyBox.Text = string.IsNullOrWhiteSpace(info.Body) ? "（无更新说明）" : info.Body;

        // 让用户看得见「检测到什么部署形态 / 会下哪个包」，也能手动换成别的包
        // （以前只写"将下载 publish-folder.zip"，用户会以为下错了）
        _loadingForms = true;
        foreach (var f in new[] { InstallForm.Installer, InstallForm.Folder, InstallForm.SingleFile })
        {
            if (!info.CanUse(f)) continue;
            FormCombo.Items.Add(new System.Windows.Controls.ComboBoxItem
            {
                Content = UpdateService.FormLabel(f),
                Tag = f
            });
        }
        _loadingForms = false;

        var selected = -1;
        for (int i = 0; i < FormCombo.Items.Count; i++)
            if (FormCombo.Items[i] is System.Windows.Controls.ComboBoxItem it && it.Tag is InstallForm f && f == info.Form)
                selected = i;
        if (selected < 0 && FormCombo.Items.Count > 0) selected = 0;
        if (selected >= 0) FormCombo.SelectedIndex = selected;
        else FormCombo.IsEnabled = false;

        VersionText.Text = $"当前版本: {currentVersion}";
        RefreshPlanText();
    }

    private bool _loadingForms;

    /// <summary>把「检测到的形态 → 将下载的包」写清楚。</summary>
    private void RefreshPlanText()
    {
        var detected = UpdateService.FormLabel(_info.DetectedForm);
        if (string.IsNullOrWhiteSpace(_info.AssetUrl))
        {
            FormCombo.IsEnabled = false;
            PlanText.Text = "（这个版本没有可下载的包）";
            DownloadBtn.IsEnabled = false;
            StatusText.Text = "未找到可下载的更新包";
            return;
        }

        var chosen = UpdateService.FormLabel(_info.Form);
        PlanText.Text = chosen == detected
            ? $"将下载 {_info.AssetName}"
            : $"将下载 {_info.AssetName}（检测到的是「{detected}」，已手动改为「{chosen}」）";
        if (_info.FellBackToSetup)
            PlanText.Text += "　·　当前形态没有对应的包，已改用安装包";
        DownloadBtn.Content = _info.Form == InstallForm.Installer ? "下载并安装" : "下载并更新";
        DownloadBtn.IsEnabled = true;
    }

    /// <summary>用户手动换了升级方式 → 切到那个形态的资产。</summary>
    private void OnFormChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loadingForms) return;
        if (FormCombo.SelectedItem is not System.Windows.Controls.ComboBoxItem item) return;
        if (item.Tag is not InstallForm form) return;
        if (form == _info.Form) { RefreshPlanText(); return; }

        // 已经下载完了才换方式 → 之前的下载作废，要求重新下载
        if (_downloadedPath != null)
        {
            _downloadedPath = null;
            ProgressBar.Visibility = Visibility.Collapsed;
            ProgressBar.Value = 0;
            DownloadBtn.Content = "下载并更新";
            CancelBtn.Content = "取消";
        }

        StatusText.Text = _info.UseForm(form)
            ? ""
            : "这个版本里没有该形态的包";
        RefreshPlanText();
    }

    private async void OnDownload(object sender, RoutedEventArgs e)
    {
        // Second click = apply the already-downloaded package
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
            // 下到独立临时目录（绝不覆盖正在运行的 exe —— 单文件版就跑在 Downloads 里，同名会撞锁）
            _downloadedPath = await Task.Run(() => _updateService.DownloadAsync(_info, progress));

            ProgressBar.Value = 100;
            var how = _info.Form switch
            {
                InstallForm.Installer => "将退出本程序并启动安装器（原地升级，沿用原目录）",
                InstallForm.SingleFile => "将退出本程序、替换自身并重新启动",
                _ => "将退出本程序、覆盖当前目录并重新启动"
            };
            StatusText.Text = $"下载完成（已通过 SHA256 校验）。点击「立即更新」后{how}。";
            DownloadBtn.Content = "立即更新";
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
