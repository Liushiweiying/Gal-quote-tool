using System.Windows;
using GalQuoteCollector.Models;

namespace GalQuoteCollector.Views;

/// <summary>回想窗口里那个 ⚙ 按钮打开的专属设置（只包含跟回想有关的项）。</summary>
public partial class SlideshowSettingsDialog : Window
{
    public HotkeyConfig? Result { get; private set; }

    public SlideshowSettingsDialog(Window owner, HotkeyConfig cfg)
    {
        InitializeComponent();
        Owner = owner;
        _cfg = cfg.Clone();
        BarsCombo.SelectedIndex = Math.Clamp(cfg.SlideshowBarsMode, 0, 2);
        TextStyleCombo.SelectedIndex = Math.Clamp(cfg.SlideshowTextStyle, 0, 1);
        OpacitySlider.Value = Math.Clamp(cfg.SlideshowTextOpacity * 100, 0, 100);
        LoopCheck.IsChecked = cfg.SlideshowLoop;
        ChineseFontBox.Text = cfg.SlideshowChineseFont;
        EnglishFontBox.Text = cfg.SlideshowEnglishFont;
        MagpieCheck.IsChecked = cfg.MagpieUpscaleSlideshow;
        MagpieHotkeyBox.Text = cfg.MagpieScaleHotkey ?? "";
    }

    private readonly HotkeyConfig _cfg;

    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OpacityLabel != null) OpacityLabel.Text = $"{(int)Math.Round(e.NewValue)}%";
    }

    /// <summary>恢复默认：黑底白字 + 50% 透明度（其它项也回到默认）。</summary>
    private void OnRestoreDefaults(object sender, RoutedEventArgs e)
    {
        BarsCombo.SelectedIndex = 0;
        TextStyleCombo.SelectedIndex = 0;
        OpacitySlider.Value = 50;
        LoopCheck.IsChecked = false;
        ChineseFontBox.Text = "Microsoft YaHei";
        EnglishFontBox.Text = "Segoe UI";
        MagpieHotkeyBox.Text = "";
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnOk(object sender, RoutedEventArgs e)
    {
        _cfg.SlideshowBarsMode = Math.Max(0, BarsCombo.SelectedIndex);
        _cfg.SlideshowLoop = LoopCheck.IsChecked == true;
        _cfg.SlideshowTextStyle = Math.Max(0, TextStyleCombo.SelectedIndex);
        _cfg.SlideshowTextOpacity = Math.Clamp(OpacitySlider.Value / 100.0, 0, 1);
        _cfg.SlideshowChineseFont = string.IsNullOrWhiteSpace(ChineseFontBox.Text) ? "Microsoft YaHei" : ChineseFontBox.Text.Trim();
        _cfg.SlideshowEnglishFont = string.IsNullOrWhiteSpace(EnglishFontBox.Text) ? "Segoe UI" : EnglishFontBox.Text.Trim();
        _cfg.MagpieUpscaleSlideshow = MagpieCheck.IsChecked == true;
        _cfg.MagpieScaleHotkey = MagpieHotkeyBox.Text.Trim();
        Result = _cfg;
        DialogResult = true;
    }
}
