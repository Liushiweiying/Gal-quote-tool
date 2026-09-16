using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GalQuoteCollector.Models;
using GalQuoteCollector.Services;

namespace GalQuoteCollector.Views;

/// <summary>
/// 「应用名 / 锁屏进程」管理：
/// - 上面：所有记录过的进程 → 可自定义显示名（如 msedge.exe → Edge），历史数据也会跟着改名；
/// - 下面：锁屏相关进程（默认 LockApp.exe / LogonUI.exe，可加壁纸软件等），这些进程的时间计入「锁屏」。
/// </summary>
public partial class ProcessMapWindow : Window
{
    private readonly UsageData _data;
    private readonly HotkeyConfig _config;
    private readonly SettingsService _settings;
    private readonly Dictionary<string, TextBox> _nameBoxes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _lockProcesses = new();

    public ProcessMapWindow(Window owner, UsageData data, HotkeyConfig config, SettingsService settings)
    {
        InitializeComponent();
        Owner = owner;
        _data = data;
        _config = config;
        _settings = settings;

        foreach (var p in UsageRules.LockProcesses) _lockProcesses.Add(p);
        BuildAppList();
        BuildLockList();
    }

    /// <summary>是否保存了（调用方据此刷新）</summary>
    public bool Saved { get; private set; }

    /// <summary>列表排序：true = 按时长从长到短，false = 按名称（首字母）</summary>
    private bool _sortByTime = true;

    private void OnSortByTime(object s, RoutedEventArgs e)
    {
        _sortByTime = true;
        SortByTimeBtn.IsChecked = true;
        SortByNameBtn.IsChecked = false;
        BuildAppList();
    }

    private void OnSortByName(object s, RoutedEventArgs e)
    {
        _sortByTime = false;
        SortByTimeBtn.IsChecked = false;
        SortByNameBtn.IsChecked = true;
        BuildAppList();
    }

    private void BuildAppList()
    {
        var typed = new Dictionary<string, TextBox>(StringComparer.OrdinalIgnoreCase);
        // 记住已经输入但还没保存的内容，重排时不丢
        foreach (var (key, box) in _nameBoxes) typed[key] = box;

        AppsPanel.Children.Clear();

        // 汇总所有记录过的进程（跳过工具自身/锁屏）
        var agg = new Dictionary<string, (string Name, int Seconds)>(StringComparer.OrdinalIgnoreCase);
        foreach (var day in _data.Records.Values)
        {
            foreach (var kv in day)
            {
                if (kv.Key is UsageData.ToolKey or UsageData.LockedKey) continue;
                if (UsageRules.IsLockProcess(kv.Key)) continue;
                if (agg.TryGetValue(kv.Key, out var cur))
                    agg[kv.Key] = (string.IsNullOrWhiteSpace(cur.Name) ? kv.Value.Name : cur.Name, cur.Seconds + kv.Value.Seconds);
                else
                    agg[kv.Key] = (kv.Value.Name, kv.Value.Seconds);
            }
        }

        if (agg.Count == 0)
        {
            AppsPanel.Children.Add(new TextBlock
            {
                Text = "还没有记录到应用",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0xC7, 0xC7, 0xCC)),
                Margin = new Thickness(8, 10, 8, 10)
            });
            return;
        }

        // 两种排序：按时间（从长到短）/ 按名称（首字母，用系统区域设置，中文按拼音）
        var ordered = _sortByTime
            ? agg.OrderByDescending(x => x.Value.Seconds).ThenBy(x => x.Key, StringComparer.CurrentCultureIgnoreCase)
            : agg.OrderBy(x => x.Key, StringComparer.CurrentCultureIgnoreCase);

        foreach (var kv in ordered)
        {
            var key = kv.Key;
            var row = new Grid { Margin = new Thickness(6, 5, 6, 5) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel();
            left.Children.Add(new TextBlock
            {
                Text = key,
                FontSize = 12.5,
                Foreground = new SolidColorBrush(Color.FromRgb(0x1D, 0x1D, 0x1F)),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            left.Children.Add(new TextBlock
            {
                Text = $"自动识别：{kv.Value.Name} · {FormatDuration(kv.Value.Seconds)}",
                FontSize = 10.5,
                Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93)),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            Grid.SetColumn(left, 0);
            row.Children.Add(left);

            var box = new TextBox
            {
                Text = typed.TryGetValue(key, out var oldBox) ? oldBox.Text
                     : (UsageRules.HasCustomName(key) ? UsageRules.NameMap[key] : ""),
                FontSize = 12.5,
                Padding = new Thickness(6, 3, 6, 3),
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF7, 0xFA)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
                ToolTip = "填你想显示的名字，留空 = 用自动识别的名字",
                Margin = new Thickness(0, 0, 6, 0)
            };
            Grid.SetColumn(box, 1);
            row.Children.Add(box);
            _nameBoxes[key] = box;

            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            var saveBtn = new Button
            {
                Content = "保存",
                FontSize = 11,
                Padding = new Thickness(10, 3, 10, 3),
                Background = new SolidColorBrush(Color.FromRgb(0x5B, 0x6A, 0xBF)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = "保存这一行的显示名（立刻生效）"
            };
            var revertBtn = new Button
            {
                Content = "复原",
                FontSize = 11,
                Padding = new Thickness(10, 3, 10, 3),
                Margin = new Thickness(6, 0, 0, 0),
                Background = new SolidColorBrush(Color.FromRgb(0xE4, 0xE4, 0xEA)),
                Foreground = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x43)),
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = "放弃这一行的修改，恢复成已保存的值"
            };
            var k = key;
            saveBtn.Click += (_, _) =>
            {
                if (!_nameBoxes.TryGetValue(k, out var b)) return;
                SaveName(k, b.Text);
                saveBtn.Content = "已保存";
                Dispatcher.BeginInvoke(new Action(() => saveBtn.Content = "保存"),
                    System.Windows.Threading.DispatcherPriority.Background);
            };
            revertBtn.Click += (_, _) =>
            {
                if (!_nameBoxes.TryGetValue(k, out var b)) return;
                // 恢复成"已经保存的值"（没设过就清空 = 用自动识别的名字）
                b.Text = UsageRules.HasCustomName(k) ? UsageRules.NameMap[k] : "";
            };
            buttons.Children.Add(saveBtn);
            buttons.Children.Add(revertBtn);
            Grid.SetColumn(buttons, 2);
            row.Children.Add(buttons);

            AppsPanel.Children.Add(row);
        }
    }

    /// <summary>保存单个进程的显示名并立刻写入 settings.json。</summary>
    private void SaveName(string processKey, string? displayName)
    {
        UsageRules.SetName(processKey, displayName);
        UsageRules.SaveTo(_config);
        try { _settings.SaveHotkeyConfig(_config); }
        catch (Exception ex) { AppLog.Write($"process map: save name failed: {ex.Message}"); }
        Saved = true;
    }

    private void BuildLockList()
    {
        LockList.Items.Clear();
        foreach (var p in _lockProcesses)
        {
            var isDefault = UsageRules.DefaultLockProcesses.Contains(p, StringComparer.OrdinalIgnoreCase);
            var btn = new Button
            {
                Content = isDefault ? $"{p}（默认）" : $"✕  {p}",
                FontSize = 12,
                Padding = new Thickness(6, 2, 6, 2),
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x43)),
                BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Cursor = isDefault ? System.Windows.Input.Cursors.Arrow : System.Windows.Input.Cursors.Hand,
                IsEnabled = !isDefault
            };
            if (!isDefault)
            {
                var name = p;
                btn.Click += (_, _) =>
                {
                    _lockProcesses.RemoveAll(x => x.Equals(name, StringComparison.OrdinalIgnoreCase));
                    BuildLockList();
                };
            }
            LockList.Items.Add(btn);
        }
    }

    private void OnAddLockProcess(object s, RoutedEventArgs e)
    {
        var name = UsageRules.Normalize(LockInput.Text);
        if (name.Length == 0) return;
        if (!_lockProcesses.Contains(name, StringComparer.OrdinalIgnoreCase)) _lockProcesses.Add(name);
        LockInput.Clear();
        BuildLockList();
    }

    private void OnSave(object s, RoutedEventArgs e)
    {
        foreach (var (key, box) in _nameBoxes)
            UsageRules.SetName(key, box.Text);
        UsageRules.SetLockProcesses(_lockProcesses);
        UsageRules.SaveTo(_config);

        try { _settings.SaveHotkeyConfig(_config); }
        catch (Exception ex) { AppLog.Write($"process map: save failed: {ex.Message}"); }

        Saved = true;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object s, RoutedEventArgs e)
    {
        DialogResult = Saved; // 行内保存过就算有改动，让调用方刷新
        Close();
    }

    private static string FormatDuration(int seconds)
    {
        if (seconds <= 0) return "0 分钟";
        int mins = (int)Math.Round(seconds / 60.0);
        if (mins < 60) return $"{mins} 分钟";
        int h = mins / 60, m = mins % 60;
        return m == 0 ? $"{h} 小时" : $"{h} 小时 {m} 分";
    }
}
