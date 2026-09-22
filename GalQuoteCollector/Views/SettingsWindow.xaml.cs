using System.IO;
using System.Windows;
using System.Windows.Input;
using GalQuoteCollector.Models;
using GalQuoteCollector.Services;
using Microsoft.Win32;

namespace GalQuoteCollector.Views;

public partial class SettingsWindow : Window
{
    private HotkeyConfig _newConfig = new();
    private bool _capturingAddShot;

    public HotkeyConfig? Result { get; private set; }

    /// <summary>数据目录（备份功能要用）。</summary>
    private static readonly string _dataDir = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GalQuoteCollector");

    public SettingsWindow(Window owner, HotkeyConfig currentConfig, string currentDisplay)
    {
        InitializeComponent();
        Owner = owner;

        ApplyControls(currentConfig);
        CurrentHotkeyText.Text = $"当前: {currentDisplay}";
        CurrentAddShotText.Text = $"当前: {currentConfig.ToAddShotDisplay()}";
    }

    /// <summary>Apply a config to all controls (used at open and for "恢复默认").</summary>
    private void ApplyControls(HotkeyConfig cfg)
    {
        _newConfig = cfg.Clone();
        AutoStartCheckBox.IsChecked = cfg.AutoStart;
        SwallowHotkeyCheckBox.IsChecked = cfg.SwallowCaptureHotkey;
        CropBlackBarsCheckBox.IsChecked = cfg.CropBlackBars;
        BackupCheckBox.IsChecked = cfg.BackupEnabled;
        BackupKeepBox.Text = (cfg.BackupKeepCount <= 0 ? 3 : cfg.BackupKeepCount).ToString();
        UpdateBackupHint(cfg);
        WebCheckBox.IsChecked = cfg.WebEnabled;
        WebHttpsCheckBox.IsChecked = cfg.WebUseHttps;
        WebPortBox.Text = (cfg.WebPort >= 1024 && cfg.WebPort <= 65535 ? cfg.WebPort : 8088).ToString();
        WebCodeBox.Text = cfg.WebAccessCode ?? "";
        UpdateWebUrls();
        DelaySlider.Value = cfg.CaptureDelayMs;
        UpdateDelayLabel(cfg.CaptureDelayMs);
        SlideshowModeCombo.SelectedIndex = cfg.SlideshowMode;

        var fonts = System.Windows.Media.Fonts.SystemFontFamilies.OrderBy(f => f.Source).ToList();
        FontCombo.ItemsSource = fonts;
        for (int i = 0; i < fonts.Count; i++)
        {
            if (fonts[i].Source == cfg.FontFamily)
            { FontCombo.SelectedIndex = i; break; }
        }

        // Slideshow fonts
        SlideshowChineseFontCombo.ItemsSource = fonts;
        for (int i = 0; i < fonts.Count; i++)
        {
            if (fonts[i].Source == cfg.SlideshowChineseFont)
            { SlideshowChineseFontCombo.SelectedIndex = i; break; }
        }
        SlideshowEnglishFontCombo.ItemsSource = fonts;
        for (int i = 0; i < fonts.Count; i++)
        {
            if (fonts[i].Source == cfg.SlideshowEnglishFont)
            { SlideshowEnglishFontCombo.SelectedIndex = i; break; }
        }

        RulesList.ItemsSource = cfg.GameNameRules;
        EnableTrackingCheckBox.IsChecked = cfg.EnableUsageTracking;
        HideUnrecognizedCheckBox.IsChecked = cfg.HideUnrecognized;
        ScreenshotDirBox.Text = cfg.ScreenshotDirectory ?? "";
        FormatCombo.SelectedIndex = cfg.ScreenshotFormat == "jpg" ? 1 : 0;
        JpegQualitySlider.Value = cfg.JpegQuality;
        JpegQualityLabel.Text = cfg.JpegQuality.ToString();
        CaptureModeCombo.SelectedIndex = cfg.CaptureMode switch
        {
            "window" => 1,
            "region" => 2,
            "auto" => 3,
            "screen" => 4,
            _ => 0 // monitor（默认）
        };
        SlideshowLoopCheckBox.IsChecked = cfg.SlideshowLoop;
        MagpieSlideshowCheckBox.IsChecked = cfg.MagpieUpscaleSlideshow;
        MagpieHotkeyBox.Text = cfg.MagpieScaleHotkey ?? "";
        MagpiePathBox.Text = cfg.MagpiePath ?? "";
        UpdateMagpieStatus(cfg);

        // TranslucentTB fix: always visible so it can be enabled before TTB runs at boot
        TranslucentTbFixCheckBox.IsChecked = cfg.EnableTranslucentTbFix;
        MagpieNativeCheckBox.IsChecked = cfg.PreferNativeCaptureWhenMagpie;

        OcrEngineCombo.SelectedIndex = cfg.OcrEngine switch { "local" => 1, "rapid" => 2, _ => 0 };
        LocalOcrUrlBox.Text = cfg.LocalOcrUrl ?? "";
        LocalOcrModelBox.Text = cfg.LocalOcrModel ?? "";
        RapidOcrPythonBox.Text = cfg.RapidOcrPython ?? "";
        UpdateOcrPanelsVisibility();

        HotkeyDisplay.Text = _newConfig.ToDisplayString();
        AddShotDisplay.Text = _newConfig.ToAddShotDisplay();
        SaveButton.IsEnabled = _newConfig.IsValid();
    }

    private void OnResetDefaults(object sender, RoutedEventArgs e)
    {
        ApplyControls(new HotkeyConfig());
        CurrentHotkeyText.Text = $"当前: {_newConfig.ToDisplayString()}";
        CurrentAddShotText.Text = $"当前: {_newConfig.ToAddShotDisplay()}";
    }

    private void OnViewLog(object sender, RoutedEventArgs e)
    {
        try
        {
            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GalQuoteCollector", "startup.log");
            if (!File.Exists(logPath)) File.WriteAllText(logPath, "(暂无日志)");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = logPath,
                UseShellExecute = true
            });
        }
        catch { }
    }

    private void OcrEngineCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateOcrPanelsVisibility();
    }

    private void UpdateOcrPanelsVisibility()
    {
        LocalOcrPanel.Visibility = OcrEngineCombo.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        RapidOcrPanel.Visibility = OcrEngineCombo.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnBrowseRapidPython(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择装有 RapidOCR 的 python.exe",
            Filter = "Python|python.exe|所有文件|*.*",
            CheckFileExists = true
        };
        if (dlg.ShowDialog(this) == true)
            RapidOcrPythonBox.Text = dlg.FileName;
    }

    private async void OnCheckRapid(object sender, RoutedEventArgs e)
    {
        var btn = (System.Windows.Controls.Button)sender;
        btn.IsEnabled = false;
        RapidOcrStatus.Text = "检测中…";
        try
        {
            var (ok, detail) = await new OcrService().CheckRapidAsync(RapidOcrPythonBox.Text.Trim());
            RapidOcrStatus.Text = ok ? ("✓ " + detail) : ("✗ " + detail);
            RapidOcrStatus.Foreground = ok
                ? System.Windows.Media.Brushes.SeaGreen
                : System.Windows.Media.Brushes.Firebrick;
        }
        finally
        {
            btn.IsEnabled = true;
        }
    }

    private static string FormatDelay(int ms)
    {
        if (ms == 0) return "0ms（无延迟）";
        if (ms < 1000) return $"{ms}ms";
        return $"{ms / 1000.0:F1}秒";
    }

    private void UpdateDelayLabel(int ms) => DelayLabel.Text = FormatDelay(ms);

    private void HotkeyBox_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _capturingAddShot = false;
        HotkeyDisplay.Text = "按快捷键...";
        HotkeyDisplay.Foreground = System.Windows.Media.Brushes.Black;
    }

    private void AddShotBox_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _capturingAddShot = true;
        AddShotDisplay.Text = "按快捷键...";
        AddShotDisplay.Foreground = System.Windows.Media.Brushes.Black;
    }

    private void DelaySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Guard: the same Minimum-coercion order issue as the JPG slider
        if (DelayLabel == null) return;
        UpdateDelayLabel((int)e.NewValue);
    }

    private void OnAddRule(object sender, RoutedEventArgs e)
    {
        var match = RuleMatchBox.Text.Trim();
        var name = RuleNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(match) || string.IsNullOrWhiteSpace(name)) return;

        _newConfig.GameNameRules.Add(new GameNameRule { Match = match, Name = name });
        RulesList.ItemsSource = null;
        RulesList.ItemsSource = _newConfig.GameNameRules;
        RuleMatchBox.Clear();
        RuleNameBox.Clear();
        RuleMatchBox.Focus();
    }

    private void OnRemoveRule(object sender, RoutedEventArgs e)
    {
        if (_newConfig.GameNameRules.Count == 0) return;
        _newConfig.GameNameRules.RemoveAt(_newConfig.GameNameRules.Count - 1);
        RulesList.ItemsSource = null;
        RulesList.ItemsSource = _newConfig.GameNameRules;
    }

    private void OnRuleMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        if (sender is not System.Windows.Controls.Border border) return;
        if (border.DataContext is not GameNameRule rule) return;

        _newConfig.GameNameRules.Remove(rule);
        RulesList.ItemsSource = null;
        RulesList.ItemsSource = _newConfig.GameNameRules;
        e.Handled = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false; Close();
            return;
        }

        if (Keyboard.FocusedElement is System.Windows.Controls.TextBox)
            return;

        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None)
            return;

        var modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.None) return;

        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);

        if (_capturingAddShot)
        {
            _newConfig.AddShotControl = (modifiers & ModifierKeys.Control) != 0;
            _newConfig.AddShotAlt = (modifiers & ModifierKeys.Alt) != 0;
            _newConfig.AddShotShift = (modifiers & ModifierKeys.Shift) != 0;
            _newConfig.AddShotWin = (modifiers & ModifierKeys.Windows) != 0;
            _newConfig.AddShotVirtualKey = virtualKey;
            AddShotDisplay.Text = _newConfig.ToAddShotDisplay();
            AddShotDisplay.Foreground = System.Windows.Media.Brushes.Black;
            _capturingAddShot = false;
            SaveButton.IsEnabled = true;
        }
        else
        {
            _newConfig.Control = (modifiers & ModifierKeys.Control) != 0;
            _newConfig.Alt = (modifiers & ModifierKeys.Alt) != 0;
            _newConfig.Shift = (modifiers & ModifierKeys.Shift) != 0;
            _newConfig.Win = (modifiers & ModifierKeys.Windows) != 0;
            _newConfig.VirtualKey = virtualKey;
            HotkeyDisplay.Text = _newConfig.ToDisplayString();
            HotkeyDisplay.Foreground = System.Windows.Media.Brushes.Black;
        }
        SaveButton.IsEnabled = _newConfig.IsValid();
    }

    private void JpegQualitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Fires during InitializeComponent (Minimum coercion) before the label exists
        if (JpegQualityLabel == null) return;
        JpegQualityLabel.Text = ((int)e.NewValue).ToString();
    }

    private void OnOpenDataDir(object sender, RoutedEventArgs e)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GalQuoteCollector");
        try
        {
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{dir}\""
            });
        }
        catch { }
    }

    private void OnOpenScreenshotDir(object sender, RoutedEventArgs e)
    {
        var dir = ScreenshotDirBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(dir))
            dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                "GalQuoteCollector");
        try
        {
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{dir}\""
            });
        }
        catch { }
    }

    /// <summary>Run the TranslucentTB repair immediately (no reboot needed) and report the result.</summary>
    private async void OnFixTranslucentTbNow(object sender, RoutedEventArgs e)
    {
        var btn = sender as System.Windows.Controls.Button;
        if (btn != null) btn.IsEnabled = false;
        try
        {
            TranslucentTbStatus.Text = "正在修复…";
            var (ok, detail) = await TranslucentTbService.RestartAsync();
            TranslucentTbStatus.Text = ok ? $"✓ {detail}" : $"✗ {detail}";
            InfoDialog.Show(this, "TranslucentTB 修复", ok ? $"修复完成：{detail}" : $"修复失败：{detail}",
                icon: ok ? InfoDialogIcon.Information : InfoDialogIcon.Warning);
        }
        finally
        {
            if (btn != null) btn.IsEnabled = true;
        }
    }

    /// <summary>设置窗口打开时把 Magpie 状态（是否运行 / 生效热键）显示出来。</summary>
    private void UpdateMagpieStatus(HotkeyConfig cfg)
    {
        try
        {
            var running = MagpieService.IsRunning();
            var hotkey = MagpieService.ResolveScaleHotkey(cfg.MagpieScaleHotkey, out var source);
            var exe = !string.IsNullOrWhiteSpace(cfg.MagpiePath) && File.Exists(cfg.MagpiePath)
                ? cfg.MagpiePath
                : MagpieService.TryFindExePath();
            var log = MagpieService.TryFindLogPath(exe);
            var scaling = log != null && MagpieService.IsScalingActive(log);

            if (!running)
            {
                MagpieStatusText.Foreground = System.Windows.Media.Brushes.Gray;
                MagpieStatusText.Text = $"未检测到 Magpie 正在运行（exe：{exe ?? "未找到"}）· 生效热键 {hotkey}（{source}）";
            }
            else
            {
                MagpieStatusText.Foreground = System.Windows.Media.Brushes.SeaGreen;
                MagpieStatusText.Text =
                    $"Magpie 正在运行 · 生效热键 {hotkey}（来源：{source}）" +
                    (scaling ? "· 当前正在缩放某个窗口（全屏回想时会先把超分切过去）" : "") +
                    "\n全屏回想（F11）时才会超分「全屏的那个窗口」，退出全屏自动解除";
            }

            AppLog.Write($"magpie status: running={running} scaling={scaling} hotkey={hotkey} ({source}) exe={exe ?? "<none>"}");
        }
        catch (Exception ex)
        {
            AppLog.Write($"magpie status failed: {ex.Message}");
        }
    }

    /// <summary>读取 Magpie 配置里的「缩放窗口」热键。</summary>
    private void OnReadMagpieHotkey(object sender, RoutedEventArgs e)
    {
        if (MagpieService.TryReadScaleHotkey(out var hotkey, out var detail))
        {
            MagpieHotkeyBox.Text = hotkey;
            MagpieStatusText.Foreground = System.Windows.Media.Brushes.SeaGreen;
            MagpieStatusText.Text = "✓ " + detail;
        }
        else
        {
            MagpieStatusText.Foreground = System.Windows.Media.Brushes.Firebrick;
            MagpieStatusText.Text = "✗ " + detail;
        }
    }

    private void OnBrowseMagpiePath(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择 Magpie.exe",
            Filter = "Magpie|Magpie.exe|所有文件|*.*",
            CheckFileExists = true
        };
        if (dlg.ShowDialog(this) == true)
            MagpiePathBox.Text = dlg.FileName;
    }

    /// <summary>
    /// 验证热键是否真的能触发 Magpie：发一次热键（会超分当前的前台窗口），
    /// 2.5 秒后查 Magpie 日志里是否新增「热键 Scale 激活」记录，再发一次解除超分。
    /// </summary>
    private async void OnTestMagpieHotkey(object sender, RoutedEventArgs e)
    {
        var btn = sender as System.Windows.Controls.Button;
        var hotkey = MagpieService.ResolveScaleHotkey(MagpieHotkeyBox.Text, out var source);
        if (!MagpieService.IsValidHotkey(hotkey))
        {
            InfoDialog.Show(this, "Magpie 超分", $"热键格式无法识别：{hotkey}\n\n示例：Alt+Shift+A、Ctrl+Alt+W",
                icon: InfoDialogIcon.Warning);
            return;
        }

        if (!MagpieService.IsRunning())
        {
            MagpieStatusText.Text = "Magpie 未运行，正在尝试启动…";
            var (started, detail) = await Task.Run(() => MagpieService.TryStart(MagpiePathBox.Text.Trim(), out var d) ? (true, d) : (false, d));
            if (!started)
            {
                MagpieStatusText.Foreground = System.Windows.Media.Brushes.Firebrick;
                MagpieStatusText.Text = "✗ " + detail;
                InfoDialog.Show(this, "Magpie 超分", detail, icon: InfoDialogIcon.Warning);
                return;
            }
        }

        var ok = InfoDialog.Show(this, "测试 Magpie 超分",
            $"将向 Magpie 发送热键 {hotkey}（来源：{source}）。\n\n" +
            "本设置窗口会被 Magpie 超分约 3 秒，然后自动解除。\n" +
            "如果 Magpie 此刻正在超分别的窗口（比如游戏），这个测试会先把它停掉。\n\n" +
            "请确认 Magpie 正在运行。",
            InfoDialogButtons.OKCancel, InfoDialogIcon.Question);
        if (ok != InfoDialogResult.OK) return;

        if (btn != null) btn.IsEnabled = false;
        try
        {
            var logPath = MagpieService.TryFindLogPath(MagpiePathBox.Text.Trim());
            var size = MagpieService.LogSize(logPath);

            Activate();
            await Task.Delay(200);
            if (!await MagpieService.SendHotkeyAsync(hotkey))
            {
                MagpieStatusText.Foreground = System.Windows.Media.Brushes.Firebrick;
                MagpieStatusText.Text = "✗ 热键发送失败";
                return;
            }

            MagpieStatusText.Foreground = System.Windows.Media.Brushes.Gray;
            MagpieStatusText.Text = "已发送热键，等待 Magpie 响应…";
            await Task.Delay(2500);

            bool fired = MagpieService.LogHasScaleActivation(logPath, size, out var line);
            await MagpieService.SendHotkeyAsync(hotkey); // 解除超分

            if (fired)
            {
                MagpieStatusText.Foreground = System.Windows.Media.Brushes.SeaGreen;
                MagpieStatusText.Text = $"✓ Magpie 已响应热键 {hotkey}";
                AppLog.Write($"magpie test: {hotkey} fired — {line}");
            }
            else
            {
                MagpieStatusText.Foreground = System.Windows.Media.Brushes.Firebrick;
                MagpieStatusText.Text = logPath == null
                    ? "？找不到 Magpie 日志，无法确认；若本窗口刚才被超分则说明热键有效"
                    : $"✗ Magpie 日志里没有新增「缩放」记录：热键 {hotkey} 可能和 Magpie 内的设置不一致" +
                      (MagpieService.IsProbablyElevated() ? "（Magpie 为管理员权限，注入也有可能被系统拦下）" : "");
                AppLog.Write($"magpie test: {hotkey} not confirmed (log={logPath ?? "<none>"})");
            }
        }
        finally
        {
            if (btn != null) btn.IsEnabled = true;
        }
    }

    private void OnBrowseScreenshotDir(object sender, RoutedEventArgs e)
    {
        try
        {
            var psScript = "$f=New-Object -ComObject Shell.Application; " +
                           "$b=$f.BrowseForFolder(0,'选择截图保存目录',0,0); " +
                           "if($b){$b.Self.Path}else{Write-Host 'CANCEL'}";
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NoLogo -Command \"{psScript}\"",
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return;
            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(5000);
            if (output != "CANCEL" && !string.IsNullOrWhiteSpace(output))
                ScreenshotDirBox.Text = output;
        }
        catch { }
    }

    private void OnBrowseBackupDir(object sender, RoutedEventArgs e)
    {
        var picked = BrowseForFolder("选择备份保存目录");
        if (!string.IsNullOrWhiteSpace(picked)) BackupDirBox.Text = picked;
    }

    /// <summary>把备份目录和最近一次备份情况写在界面下方。</summary>
    private void UpdateBackupHint(HotkeyConfig cfg)
    {
        try
        {
            var dir = Services.BackupService.DefaultDirectory(_dataDir);
            int count = 0;
            try { if (Directory.Exists(dir)) count = Directory.GetDirectories(dir, "backup-*").Length; } catch { }
            BackupHintText.Text = $"备份位置：{dir}（现有 {count} 份）\n" +
                                  "每天首次启动时，如果数据较上次备份有变动才备份；截图也会一起备份。";
        }
        catch (Exception ex) { BackupHintText.Text = "备份目录不可用：" + ex.Message; }
    }

    // ── 左侧分类跳转 ──

    private void OnJumpHotkey(object sender, RoutedEventArgs e) => Jump(SecHeaderHotkey);
    private void OnJumpGeneral(object sender, RoutedEventArgs e) => Jump(SecHeaderGeneral);
    private void OnJumpSlideshow(object sender, RoutedEventArgs e) => Jump(SecHeaderSlideshow);
    private void OnJumpCapture(object sender, RoutedEventArgs e) => Jump(SecHeaderCapture);
    private void OnJumpBackup(object sender, RoutedEventArgs e) => Jump(SecHeaderBackup);
    private void OnJumpWeb(object sender, RoutedEventArgs e) => Jump(SecHeaderWeb);
    private void OnJumpOcr(object sender, RoutedEventArgs e) => Jump(SecHeaderOcr);
    private void OnJumpRules(object sender, RoutedEventArgs e) => Jump(SecHeaderRules);

    /// <summary>把某个分组标题滚到可视区域（顺带闪一下，知道跳哪了）。</summary>
    private void Jump(System.Windows.FrameworkElement target)
    {
        try
        {
            // 精确计算：目标相对 ScrollViewer 视口的位置 + 当前偏移 = 内容里的纵坐标
            var pos = target.TransformToAncestor(SettingsScroll).Transform(new System.Windows.Point(0, 0));
            double wanted = SettingsScroll.VerticalOffset + pos.Y - 8;
            SettingsScroll.ScrollToVerticalOffset(Math.Max(0, wanted));
        }
        catch
        {
            try { target.BringIntoView(); } catch { }
        }
    }
    private void OnOpenBackupDir(object sender, RoutedEventArgs e)
    {
        var dir = Services.BackupService.DefaultDirectory(_dataDir);
        try
        {
            System.IO.Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Views.InfoDialog.Show(this, "打开失败", ex.Message, icon: Views.InfoDialogIcon.Warning);
        }
    }

    private void OnBackupNow(object sender, RoutedEventArgs e)
    {
        _newConfig.CropBlackBars = CropBlackBarsCheckBox.IsChecked == true;
        _newConfig.BackupEnabled = BackupCheckBox.IsChecked == true;
        _newConfig.BackupKeepCount = int.TryParse(BackupKeepBox.Text.Trim(), out var k2) ? Math.Clamp(k2, 1, 30) : 3;
        _newConfig.BackupDirectory = ""; // 固定用"安装目录上一级\Gal Quote Tool Backup"
        _newConfig.BackupKeepCount = int.TryParse(BackupKeepBox.Text.Trim(), out var keep)
            ? Math.Clamp(keep, 1, 30) : 3;
        _newConfig.WebEnabled = WebCheckBox.IsChecked == true;
        _newConfig.WebPort = int.TryParse(WebPortBox.Text.Trim(), out var wp) && wp >= 1024 && wp <= 65535 ? wp : 8088;
        _newConfig.WebAccessCode = WebCodeBox.Text.Trim();
        _newConfig.WebUseHttps = WebHttpsCheckBox.IsChecked == true;
        var (ok, path, message) = Services.BackupService.BackupNow(_newConfig, _dataDir);
        BackupHintText.Text = message + (path.Length > 0 ? $"\n{path}" : "");
        if (!ok)
            Views.InfoDialog.Show(this, "备份", message, icon: Views.InfoDialogIcon.Warning);
    }

    /// <summary>系统文件夹选择对话框（和截图目录用的是同一套）。</summary>
    private static string BrowseForFolder(string title)
    {
        try
        {
            var psScript = "$f=New-Object -ComObject Shell.Application; " +
                           $"$b=$f.BrowseForFolder(0,'{title}',0,0); " +
                           "if($b){$b.Self.Path}else{Write-Host 'CANCEL'}";
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NoLogo -Command \"{psScript}\"",
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return "";
            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(5000);
            return output == "CANCEL" ? "" : output;
        }
        catch { return ""; }
    }

    private void OnGenWebCode(object sender, RoutedEventArgs e)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var rnd = new Random();
        WebCodeBox.Text = new string(Enumerable.Range(0, 6).Select(_ => alphabet[rnd.Next(alphabet.Length)]).ToArray());
        WebHintText.Text = "已生成访问码：手机第一次打开用 http://IP:端口/?k=这个码（之后浏览器会记住）";
        UpdateWebUrls();
    }

    private void OnTrustCert(object sender, RoutedEventArgs e)
    {
        Services.WebServerService? tmp = null;
        try
        {
            var storage = new Services.StorageService(System.IO.Path.Combine(_dataDir, "quotes.db"));
            tmp = new Services.WebServerService(storage, _dataDir);
            var port = int.TryParse(WebPortBox.Text.Trim(), out var pp) && pp >= 1024 && pp <= 65535 ? pp : 8088;
            var (started, startMsg) = tmp.Start(port, WebCodeBox.Text.Trim(), true);
            if (!started) { Views.InfoDialog.Show(this, "信任证书", startMsg, icon: Views.InfoDialogIcon.Warning); return; }
            var (ok, msg) = tmp.TrustOnThisMachine();
            Views.InfoDialog.Show(this, ok ? "已信任" : "信任失败", msg,
                icon: ok ? Views.InfoDialogIcon.Information : Views.InfoDialogIcon.Warning);
        }
        catch (Exception ex)
        {
            Views.InfoDialog.Show(this, "信任失败", ex.Message, icon: Views.InfoDialogIcon.Warning);
        }
        finally { tmp?.Stop(); tmp?.Dispose(); }
    }

    private void OnExportCert(object sender, RoutedEventArgs e)
    {
        try
        {
            // 证书要先生成/取出：起一次服务再导出（不实际占用端口就生成不了证书）
            Services.WebServerService? tmp = null;
            try
            {
                var storage = new Services.StorageService(System.IO.Path.Combine(_dataDir, "quotes.db"));
                tmp = new Services.WebServerService(storage, _dataDir);
                var (ok, msg) = tmp.Start(int.TryParse(WebPortBox.Text.Trim(), out var pp) && pp >= 1024 && pp <= 65535 ? pp : 8088,
                    WebCodeBox.Text.Trim(), true);
                if (!ok) { Views.InfoDialog.Show(this, "导出证书", msg, icon: Views.InfoDialogIcon.Warning); return; }
                var path = tmp.ExportCertificate();
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{path}\"",
                });
                Views.InfoDialog.Show(this, "证书已导出",
                    $"文件：{path}\n\n" +
                    "手机安装方法：\n" +
                    "· Android：设置 → 安全 → 加密与凭据 → 安装证书 → CA 证书 → 选择这个 .cer\n" +
                    "· iPhone：用 AirDrop/邮件把这个 .cer 发过去 → 安装描述文件 → 设置 → 通用 → 关于本机 → 证书信任设置 → 打开开关\n\n" +
                    "装好之后再访问 https 就不会提示不安全了。");
            }
            finally
            {
                tmp?.Stop();
                tmp?.Dispose();
            }
        }
        catch (Exception ex)
        {
            Views.InfoDialog.Show(this, "导出证书失败", ex.Message, icon: Views.InfoDialogIcon.Warning);
        }
    }

    private void OnOpenWebPage(object sender, RoutedEventArgs e)
    {
        int port = int.TryParse(WebPortBox.Text.Trim(), out var p) && p >= 1024 && p <= 65535 ? p : 8088;
        var code = WebCodeBox.Text.Trim();
        var scheme = WebHttpsCheckBox.IsChecked == true ? "https" : "http";
        var url = $"{scheme}://127.0.0.1:{port}/" + (code.Length > 0 ? $"?k={Uri.EscapeDataString(code)}" : "");
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Views.InfoDialog.Show(this, "打开失败",
                ex.Message + "\n（网页服务需要在点「保存」后才会生效）", icon: Views.InfoDialogIcon.Warning);
        }
    }

    private void UpdateWebUrls()
    {
        int port = int.TryParse(WebPortBox.Text.Trim(), out var p) && p >= 1024 && p <= 65535 ? p : 8088;
        var code = WebCodeBox.Text.Trim();
        var suffix = code.Length > 0 ? $"?k={code}" : "";
        var urls = Services.WebServerService.LocalUrls(port, WebHttpsCheckBox.IsChecked == true).Take(3).Select(u => u + suffix);
        WebUrlText.Text = "局域网地址：" + string.Join("    ", urls);
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        _newConfig.AutoStart = AutoStartCheckBox.IsChecked == true;
        _newConfig.SwallowCaptureHotkey = SwallowHotkeyCheckBox.IsChecked == true;
        _newConfig.CropBlackBars = CropBlackBarsCheckBox.IsChecked == true;
        _newConfig.BackupEnabled = BackupCheckBox.IsChecked == true;
        _newConfig.BackupKeepCount = int.TryParse(BackupKeepBox.Text.Trim(), out var k2) ? Math.Clamp(k2, 1, 30) : 3;
        _newConfig.BackupDirectory = ""; // 固定用"安装目录上一级\Gal Quote Tool Backup"
        _newConfig.BackupKeepCount = int.TryParse(BackupKeepBox.Text.Trim(), out var keep)
            ? Math.Clamp(keep, 1, 30) : 3;
        _newConfig.WebEnabled = WebCheckBox.IsChecked == true;
        _newConfig.WebPort = int.TryParse(WebPortBox.Text.Trim(), out var wp) && wp >= 1024 && wp <= 65535 ? wp : 8088;
        _newConfig.WebAccessCode = WebCodeBox.Text.Trim();
        _newConfig.WebUseHttps = WebHttpsCheckBox.IsChecked == true;
        _newConfig.CaptureDelayMs = (int)DelaySlider.Value;
        _newConfig.SlideshowMode = SlideshowModeCombo.SelectedIndex;
        _newConfig.SlideshowLoop = SlideshowLoopCheckBox.IsChecked == true;
        _newConfig.FontFamily = FontCombo.SelectedItem is System.Windows.Media.FontFamily f ? f.Source : "Segoe UI";
        _newConfig.SlideshowChineseFont = SlideshowChineseFontCombo.SelectedItem is System.Windows.Media.FontFamily cf ? cf.Source : "Microsoft YaHei";
        _newConfig.SlideshowEnglishFont = SlideshowEnglishFontCombo.SelectedItem is System.Windows.Media.FontFamily ef ? ef.Source : "Segoe UI";
        _newConfig.EnableUsageTracking = EnableTrackingCheckBox.IsChecked == true;
        _newConfig.HideUnrecognized = HideUnrecognizedCheckBox.IsChecked == true;
        _newConfig.EnableTranslucentTbFix = TranslucentTbFixCheckBox.IsChecked == true;
        _newConfig.PreferNativeCaptureWhenMagpie = MagpieNativeCheckBox.IsChecked == true;
        var dir = ScreenshotDirBox.Text.Trim();
        _newConfig.ScreenshotDirectory = string.IsNullOrWhiteSpace(dir) ? "" : dir;
        _newConfig.ScreenshotFormat = FormatCombo.SelectedIndex == 1 ? "jpg" : "png";
        _newConfig.JpegQuality = (int)JpegQualitySlider.Value;
        _newConfig.CaptureMode = CaptureModeCombo.SelectedIndex switch
        {
            1 => "window",
            2 => "region",
            3 => "auto",
            4 => "screen",
            _ => "monitor"
        };
        _newConfig.MagpieUpscaleSlideshow = MagpieSlideshowCheckBox.IsChecked == true;
        _newConfig.MagpieScaleHotkey = MagpieHotkeyBox.Text.Trim();
        _newConfig.MagpiePath = MagpiePathBox.Text.Trim();
        _newConfig.OcrEngine = OcrEngineCombo.SelectedIndex switch { 1 => "local", 2 => "rapid", _ => "win" };
        _newConfig.LocalOcrUrl = LocalOcrUrlBox.Text.Trim();
        _newConfig.LocalOcrModel = LocalOcrModelBox.Text.Trim();
        _newConfig.RapidOcrPython = RapidOcrPythonBox.Text.Trim();
        Result = _newConfig.Clone();
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
