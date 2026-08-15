using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GalQuoteCollector.Models;

namespace GalQuoteCollector.Views;

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

        // Tool runtime from this date's data only — never from today's fallback
        int toolSec = day != null && day.TryGetValue(ToolKey, out var toolRec)
            ? toolRec.Seconds : 0;

        // ── Tool runtime card (red accent, full width) ──
        var toolCard = CreateCard();
        AddToCard(toolCard, CreateLabelRow("工具运行", FormatTime(toolSec), Red));
        AddToCard(toolCard, CreateBar(1.0, Red, 8));
        StatsPanel.Children.Add(toolCard);

        // ── App records (exclude tool key) ──
        if (apps == null || apps.Count == 0)
        {
            var emptyCard = CreateCard();
            AddToCard(emptyCard, new TextBlock
            {
                Text = "当天无应用记录",
                Foreground = Brushes.Gray,
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            StatsPanel.Children.Add(emptyCard);
            return;
        }

        foreach (var (key, record) in apps.OrderByDescending(x => x.Value.Seconds))
        {
            var pct = toolSec > 0 ? (double)record.Seconds / toolSec : 0;
            var card = CreateCard();
            AddToCard(card, CreateLabelRow(record.Name, $"{FormatTime(record.Seconds)}  ({(int)(pct * 100)}%)", Green));
            AddToCard(card, CreateBar(pct, Green, 8));
            StatsPanel.Children.Add(card);
        }

        // ── Total card ──
        int totalSec = apps.Sum(kv => kv.Value.Seconds);
        var totalCard = CreateCard();
        AddToCard(totalCard, CreateLabelRow("应用合计", FormatTime(totalSec), GrayText));
        StatsPanel.Children.Add(totalCard);
    }

    private static readonly Color Red = Color.FromRgb(0xE5, 0x39, 0x35);
    private static readonly Color Green = Color.FromRgb(0x4C, 0xAF, 0x50);
    private static readonly Color GrayText = Color.FromRgb(0x88, 0x88, 0x88);

    private static Border CreateCard()
    {
        return new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(0, 0, 0, 8),
            Child = new StackPanel()
        };
    }

    private static void AddToCard(Border card, UIElement el)
        => ((StackPanel)card.Child).Children.Add(el);

    private static Grid CreateLabelRow(string name, string detail, Color accent)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            VerticalAlignment = VerticalAlignment.Center
        });
        grid.Children.Add(new TextBlock
        {
            Text = detail,
            FontSize = 12,
            Foreground = new SolidColorBrush(accent),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        });
        Grid.SetColumn(grid.Children[1], 1);
        return grid;
    }

    private static Border CreateBar(double fraction, Color color, double height)
    {
        var track = new Border
        {
            Height = height,
            Background = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0)),
            CornerRadius = new CornerRadius(height / 2),
            Margin = new Thickness(0, 8, 0, 0)
        };
        track.Child = new Border
        {
            Height = height,
            Width = Math.Max(6, 300 * Math.Min(1.0, fraction)),
            Background = new SolidColorBrush(color),
            CornerRadius = new CornerRadius(height / 2),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        return track;
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
            FontSize = 13,
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
            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF7)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16),
                Child = picker
            },
            Width = 300, Height = 230,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ShowInTaskbar = false,
            ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF7)),
            FontFamily = new FontFamily("Segoe UI")
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
        InfoDialog.Show(this, "提示", "已导出");
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
            if (imported == null) { InfoDialog.Show(this, "提示", "文件格式错误"); return; }

            foreach (var (date, apps) in imported.Records)
                foreach (var (key, rec) in apps)
                    _data.AddSeconds(date, key, rec.Name, rec.Seconds);

            RefreshView();
            InfoDialog.Show(this, "提示", "导入完成");
        }
        catch (Exception ex)
        {
            InfoDialog.Show(this, "错误", $"导入失败: {ex.Message}", icon: InfoDialogIcon.Error);
        }
    }
}
