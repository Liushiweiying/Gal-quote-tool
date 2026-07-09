using System.Windows;
using System.Windows.Input;
using GalgameQuoteCollector.Models;

namespace GalgameQuoteCollector.Views;

public partial class SettingsWindow : Window
{
    private readonly HotkeyConfig _newConfig;
    private bool _capturingAddShot;

    public HotkeyConfig? Result { get; private set; }

    public SettingsWindow(Window owner, HotkeyConfig currentConfig, string currentDisplay)
    {
        InitializeComponent();
        Owner = owner;

        _newConfig = currentConfig.Clone();
        CurrentHotkeyText.Text = $"当前: {currentDisplay}";
        CurrentAddShotText.Text = $"当前: {currentConfig.ToAddShotDisplay()}";
        AutoStartCheckBox.IsChecked = currentConfig.AutoStart;
        DelaySlider.Value = currentConfig.CaptureDelayMs;
        UpdateDelayLabel(currentConfig.CaptureDelayMs);
        SlideshowModeCombo.SelectedIndex = currentConfig.SlideshowMode;

        var fonts = System.Windows.Media.Fonts.SystemFontFamilies.OrderBy(f => f.Source).ToList();
        FontCombo.ItemsSource = fonts;
        for (int i = 0; i < fonts.Count; i++)
        {
            if (fonts[i].Source == currentConfig.FontFamily)
            { FontCombo.SelectedIndex = i; break; }
        }

        RulesList.ItemsSource = currentConfig.GameNameRules;
        EnableTrackingCheckBox.IsChecked = currentConfig.EnableUsageTracking;
        HideUnrecognizedCheckBox.IsChecked = currentConfig.HideUnrecognized;
        ScreenshotDirBox.Text = currentConfig.ScreenshotDirectory ?? "";
        FormatCombo.SelectedIndex = currentConfig.ScreenshotFormat == "jpg" ? 1 : 0;
        SlideshowLoopCheckBox.IsChecked = currentConfig.SlideshowLoop;

        SaveButton.IsEnabled = _newConfig.IsValid();
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
        _newConfig.EnableUsageTracking = EnableTrackingCheckBox.IsChecked == true;
        _newConfig.HideUnrecognized = HideUnrecognizedCheckBox.IsChecked == true;
        var dir = ScreenshotDirBox.Text.Trim();
        _newConfig.ScreenshotDirectory = string.IsNullOrWhiteSpace(dir) ? "" : dir;
        _newConfig.ScreenshotFormat = FormatCombo.SelectedIndex == 1 ? "jpg" : "png";
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
