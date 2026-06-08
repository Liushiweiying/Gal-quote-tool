using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GalgameQuoteCollector.Models;

namespace GalgameQuoteCollector.Views;

public partial class UsageStatsWindow : Window
{
    private readonly UsageData _data;
    private DateTime _currentDate = DateTime.Now;

    public UsageStatsWindow(Window owner, UsageData data)
    {
        InitializeComponent();
        Owner = owner;
        _data = data;
        RefreshView();
    }

    private const string ToolKey = "__tool__";

    private void RefreshView()
    {
        var dateStr = _currentDate.ToString("yyyy-MM-dd");
        DateText.Text = dateStr;

        StatsPanel.Children.Clear();
        var day = _data.GetDay(dateStr);
        var apps = day?.Where(kv => kv.Key != ToolKey).ToList();
        int appsMax = apps != null && apps.Count > 0 ? apps.Max(kv => kv.Value.Seconds) : 1;

        // Tool runtime from this date's data only — never from today's fallback
        int toolSec = day != null && day.TryGetValue(ToolKey, out var toolRec)
            ? toolRec.Seconds : 0;

        // ── Tool runtime bar (red, always 100%) ──
        StatsPanel.Children.Add(new TextBlock
        {
            Text = $"工具运行  {FormatTime(toolSec)}",
            FontSize = 14, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0x43, 0x36)),
            Margin = new Thickness(0, 0, 0, 2)
        });
        StatsPanel.Children.Add(new Border
        {
            Height = 20, Width = 300,
            Background = new SolidColorBrush(Color.FromRgb(0xF4, 0x43, 0x36)),
            CornerRadius = new CornerRadius(3),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 2, 0, 4)
        });

        // Separator
        StatsPanel.Children.Add(new Border
        {
            Height = 1, Background = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
            Margin = new Thickness(0, 4, 0, 8)
        });

        // ── App records (exclude tool key) ──
        if (apps == null || apps.Count == 0)
        {
            StatsPanel.Children.Add(new TextBlock
            {
                Text = "当天无应用记录",
                Foreground = Brushes.Gray,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 8, 0, 0)
            });
            return;
        }

        int barMaxWidth = 300;

        foreach (var (key, record) in apps.OrderByDescending(x => x.Value.Seconds))
        {
            var pct = toolSec > 0 ? (double)record.Seconds / toolSec : 0;
            var bar = new Border
            {
                Height = 20, Width = Math.Max(4, barMaxWidth * pct),
                Background = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)),
                CornerRadius = new CornerRadius(3),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 2, 0, 2)
            };

            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 2) };
            panel.Children.Add(new TextBlock
            {
                Text = $"{record.Name}   {FormatTime(record.Seconds)}   ({(int)(pct * 100)}%)",
                FontSize = 13, Margin = new Thickness(0, 4, 0, 0)
            });
            panel.Children.Add(bar);
            StatsPanel.Children.Add(panel);
        }

        // ── Total at bottom ──
        int totalSec = apps.Sum(kv => kv.Value.Seconds);
        StatsPanel.Children.Add(new Border
        {
            Height = 1, Background = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
            Margin = new Thickness(0, 8, 0, 8)
        });
        StatsPanel.Children.Add(new TextBlock
        {
            Text = $"应用合计  {FormatTime(totalSec)}",
            FontSize = 14, FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 8)
        });
    }

    private static string FormatTime(int seconds)
    {
        int mins = (int)Math.Ceiling(seconds / 60.0);
        if (mins < 60) return $"{mins}分";
        return $"{mins / 60}时{mins % 60}分";
    }

    private void OnPrevDay(object s, RoutedEventArgs e) { _currentDate = _currentDate.AddDays(-1); RefreshView(); }
    private void OnNextDay(object s, RoutedEventArgs e) { _currentDate = _currentDate.AddDays(1); RefreshView(); }
    private void OnDateClick(object s, RoutedEventArgs e) => ShowDatePicker();

    private void ShowDatePicker()
    {
        var picker = new DatePicker
        {
            SelectedDate = _currentDate,
            VerticalAlignment = VerticalAlignment.Center,
            DisplayDateStart = _data.Records.Keys.MinBy(k => k) is string min ? DateTime.Parse(min) : _currentDate.AddMonths(-1),
            DisplayDateEnd = DateTime.Today
        };
        picker.SelectedDateChanged += (_, _) =>
        {
            if (picker.SelectedDate.HasValue)
            {
                _currentDate = picker.SelectedDate.Value;
                RefreshView();
            }
        };

        var win = new Window
        {
            Title = "选择日期",
            Content = picker,
            Width = 280, Height = 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ShowInTaskbar = false
        };
        win.ShowDialog();
    }

    private void OnManageBlacklist(object s, RoutedEventArgs e)
    {
        var win = new BlacklistWindow(this, _data);
        win.ShowDialog();
    }

    private void OnExport(object s, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "JSON 文件 (*.json)|*.json",
            DefaultExt = ".json",
            FileName = $"usage_{DateTime.Now:yyyy-MM-dd}.json"
        };
        if (dialog.ShowDialog() != true) return;
        var json = JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(dialog.FileName, json);
        MessageBox.Show("已导出", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnImport(object s, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "JSON 文件 (*.json)|*.json",
            Title = "导入使用记录"
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var json = File.ReadAllText(dialog.FileName);
            var imported = JsonSerializer.Deserialize<UsageData>(json);
            if (imported == null) { MessageBox.Show("文件格式错误"); return; }

            foreach (var (date, apps) in imported.Records)
                foreach (var (key, rec) in apps)
                    _data.AddSeconds(date, key, rec.Name, rec.Seconds);

            RefreshView();
            MessageBox.Show("导入完成", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"导入失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
