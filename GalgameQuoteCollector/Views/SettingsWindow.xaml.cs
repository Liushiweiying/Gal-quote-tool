using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using GalgameQuoteCollector.Models;

namespace GalgameQuoteCollector.Views;

public partial class SettingsWindow : Window
{
    private readonly HotkeyConfig _newConfig;

    public HotkeyConfig? Result { get; private set; }

    public SettingsWindow(Window owner, HotkeyConfig currentConfig, string currentDisplay)
    {
        InitializeComponent();
        Owner = owner;

        _newConfig = currentConfig.Clone();
        CurrentHotkeyText.Text = $"当前: {currentDisplay}";
        AutoStartCheckBox.IsChecked = currentConfig.AutoStart;
        DelaySlider.Value = currentConfig.CaptureDelayMs;
        UpdateDelayLabel(currentConfig.CaptureDelayMs);

        RulesList.ItemsSource = currentConfig.GameNameRules;

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
        HotkeyDisplay.Text = "按快捷键...";
        HotkeyDisplay.Foreground = System.Windows.Media.Brushes.Black;
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
            DialogResult = false;
            Close();
            return;
        }

        // If focus is in a text input box, let it handle typing normally
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

        _newConfig.Control = (modifiers & ModifierKeys.Control) != 0;
        _newConfig.Alt = (modifiers & ModifierKeys.Alt) != 0;
        _newConfig.Shift = (modifiers & ModifierKeys.Shift) != 0;
        _newConfig.Win = (modifiers & ModifierKeys.Windows) != 0;
        _newConfig.VirtualKey = virtualKey;

        HotkeyDisplay.Text = _newConfig.ToDisplayString();
        HotkeyDisplay.Foreground = System.Windows.Media.Brushes.Black;
        SaveButton.IsEnabled = _newConfig.IsValid();
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        _newConfig.AutoStart = AutoStartCheckBox.IsChecked == true;
        _newConfig.CaptureDelayMs = (int)DelaySlider.Value;
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
