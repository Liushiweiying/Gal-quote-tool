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
        SlideshowLoopCheckBox.IsChecked = cfg.SlideshowLoop;

        // TranslucentTB fix option is only meaningful (and only shown) while TranslucentTB is running
        TranslucentTbFixCheckBox.IsChecked = cfg.EnableTranslucentTbFix;
        TranslucentTbFixCheckBox.Visibility = System.Diagnostics.Process.GetProcessesByName("TranslucentTB").Length == 0
            ? Visibility.Collapsed : Visibility.Visible;

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

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        _newConfig.AutoStart = AutoStartCheckBox.IsChecked == true;
        _newConfig.CaptureDelayMs = (int)DelaySlider.Value;
        _newConfig.SlideshowMode = SlideshowModeCombo.SelectedIndex;
        _newConfig.SlideshowLoop = SlideshowLoopCheckBox.IsChecked == true;
        _newConfig.FontFamily = FontCombo.SelectedItem is System.Windows.Media.FontFamily f ? f.Source : "Segoe UI";
        _newConfig.SlideshowChineseFont = SlideshowChineseFontCombo.SelectedItem is System.Windows.Media.FontFamily cf ? cf.Source : "Microsoft YaHei";
        _newConfig.SlideshowEnglishFont = SlideshowEnglishFontCombo.SelectedItem is System.Windows.Media.FontFamily ef ? ef.Source : "Segoe UI";
        _newConfig.EnableUsageTracking = EnableTrackingCheckBox.IsChecked == true;
        _newConfig.HideUnrecognized = HideUnrecognizedCheckBox.IsChecked == true;
        _newConfig.EnableTranslucentTbFix = TranslucentTbFixCheckBox.IsChecked == true;
        var dir = ScreenshotDirBox.Text.Trim();
        _newConfig.ScreenshotDirectory = string.IsNullOrWhiteSpace(dir) ? "" : dir;
        _newConfig.ScreenshotFormat = FormatCombo.SelectedIndex == 1 ? "jpg" : "png";
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
