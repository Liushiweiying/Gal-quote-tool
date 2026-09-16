using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GalQuoteCollector.Models;

namespace GalQuoteCollector.Views;

/// <summary>
/// 黑名单管理：列出「已记录的应用」（显示名 + 进程名 + 累计时长），点一下就能加入黑名单，
/// 不用手打进程名；加入时可选择是否把已经记录的数据一起删掉。
/// </summary>
public partial class BlacklistWindow : Window
{
    private readonly UsageData _data;
    private const string ToolKey = "__tool__";

    public BlacklistWindow(Window owner, UsageData data)
    {
        InitializeComponent();
        Owner = owner;
        _data = data;
        RefreshAll();
    }

    private void RefreshAll()
    {
        RefreshApps();
        RefreshBlacklist();
    }

    /// <summary>把历史上记录过的进程聚合出来（跳过工具自身和已在黑名单里的）。</summary>
    private void RefreshApps()
    {
        AppsPanel.Children.Clear();

        var agg = new Dictionary<string, (string Name, int Seconds)>(StringComparer.OrdinalIgnoreCase);
        foreach (var day in _data.Records.Values)
        {
            foreach (var kv in day)
            {
                if (kv.Key == ToolKey) continue;
                if (_data.Blacklist.Contains(kv.Key, StringComparer.OrdinalIgnoreCase)) continue;
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
                Text = "还没有记录到应用（或在设置里关闭了时长记录）",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0xC7, 0xC7, 0xCC)),
                Margin = new Thickness(8, 10, 8, 10)
            });
            return;
        }

        foreach (var kv in agg.OrderByDescending(x => x.Value.Seconds))
        {
            var key = kv.Key;
            var row = new Grid { Margin = new Thickness(6, 4, 6, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel();
            info.Children.Add(new TextBlock
            {
                Text = Services.UsageRules.DisplayName(key, kv.Value.Name),
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(0x1D, 0x1D, 0x1F)),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            info.Children.Add(new TextBlock
            {
                Text = $"{key} · {FormatDuration(kv.Value.Seconds)}",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93))
            });
            Grid.SetColumn(info, 0);
            row.Children.Add(info);

            var btn = new Button
            {
                Content = "加入黑名单",
                FontSize = 12,
                Padding = new Thickness(10, 4, 10, 4),
                Background = new SolidColorBrush(Color.FromRgb(0xE4, 0xE4, 0xEA)),
                Foreground = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x43)),
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            btn.Click += (_, _) => AddToBlacklist(key);
            Grid.SetColumn(btn, 1);
            row.Children.Add(btn);

            AppsPanel.Children.Add(row);
            AppsPanel.Children.Add(new Border
            {
                Height = 1,
                Background = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF4)),
                Margin = new Thickness(6, 0, 6, 0)
            });
        }
    }

    private void RefreshBlacklist()
    {
        BlacklistBox.Items.Clear();
        EmptyHint.Visibility = _data.Blacklist.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var item in _data.Blacklist.ToList())
        {
            var btn = new Button
            {
                Content = $"✕  {item}",
                FontSize = 12,
                Padding = new Thickness(6, 2, 6, 2),
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x43)),
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            btn.Click += (_, _) =>
            {
                _data.Blacklist.Remove(item);
                RefreshAll();
            };
            BlacklistBox.Items.Add(btn);
        }
    }

    /// <summary>加入黑名单：顺带问一下要不要删掉已经记录的数据。</summary>
    private void AddToBlacklist(string processKey)
    {
        var name = Normalize(processKey);
        if (name.Length == 0) return;

        if (!_data.Blacklist.Contains(name, StringComparer.OrdinalIgnoreCase))
            _data.Blacklist.Add(name);

        int recorded = RecordedSeconds(name);
        if (recorded > 60)
        {
            var answer = InfoDialog.Show(this, "加入黑名单",
                $"{name} 已加入黑名单，之后不再记录。\n\n" +
                $"已经记录的 {FormatDuration(recorded)} 要一起删掉吗？",
                InfoDialogButtons.YesNo, InfoDialogIcon.Question);
            if (answer == InfoDialogResult.Yes)
            {
                RemoveRecords(name);
            }
        }
        RefreshAll();
    }

    private void OnAdd(object s, RoutedEventArgs e)
    {
        var name = Normalize(ProcessInput.Text);
        if (name.Length == 0) return;
        ProcessInput.Clear();
        AddToBlacklist(name);
    }

    private static string Normalize(string raw)
    {
        var name = (raw ?? "").Trim();
        if (name.Length == 0) return "";
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name += ".exe";
        return name;
    }

    private int RecordedSeconds(string processKey)
    {
        int total = 0;
        foreach (var day in _data.Records.Values)
            if (day.TryGetValue(processKey, out var rec)) total += rec.Seconds;
        return total;
    }

    private void RemoveRecords(string processKey)
    {
        foreach (var day in _data.Records.Values)
            day.Remove(processKey);
    }

    private static string FormatDuration(int seconds)
    {
        if (seconds <= 0) return "0 分钟";
        int mins = (int)Math.Round(seconds / 60.0);
        if (mins < 60) return $"{mins} 分钟";
        int h = mins / 60, m = mins % 60;
        return m == 0 ? $"{h} 小时" : $"{h} 小时 {m} 分";
    }

    private void OnClose(object s, RoutedEventArgs e) => DialogResult = true;
}
