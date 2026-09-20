using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using GalQuoteCollector.Models;
using GalQuoteCollector.Services;
using GalQuoteCollector.Views.Controls;

namespace GalQuoteCollector.Views;

/// <summary>
/// 单个应用的使用时长详情（参考 iOS「屏幕使用时间」里点进某个 App 的那一页）：
/// 平均每天 + 区间合计 + 该应用自己的柱状图 + 统计范围。
/// </summary>
public partial class AppUsageWindow : Window
{
    private readonly UsageData _data;
    private readonly List<string> _keys;
    private readonly string _displayName;
    private readonly string _iconPath;

    private UsagePeriod _period;
    private DateTime _anchor;
    private DateTime _customFrom;
    private DateTime _customTo;

    private static readonly Color Accent = Color.FromRgb(0x5B, 0x6A, 0xBF);
    private static readonly Color Ink = Color.FromRgb(0x1D, 0x1D, 0x1F);
    private static readonly Color Ink2 = Color.FromRgb(0x3C, 0x3C, 0x43);
    private static readonly Color Muted = Color.FromRgb(0x8E, 0x8E, 0x93);
    private static readonly Color Faint = Color.FromRgb(0xC7, 0xC7, 0xCC);
    private static readonly Color CardBorder = Color.FromRgb(0xE5, 0xE5, 0xEA);

    public AppUsageWindow(Window owner, UsageData data, UsageAppStat app, UsagePeriod period,
        DateTime anchor, DateTime customFrom, DateTime customTo)
    {
        InitializeComponent();
        Owner = owner;
        _data = data;
        _keys = app.Keys.Count > 0 ? new List<string>(app.Keys) : new List<string> { app.Key };
        _displayName = app.Name;
        _iconPath = app.Path;
        _period = period;
        _anchor = anchor;
        _customFrom = customFrom;
        _customTo = customTo;

        AppNameText.Text = _displayName;
        BuildIcon();
        RefreshView();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape) { Close(); e.Handled = true; }
        };
    }

    private void BuildIcon()
    {
        var cached = AppIconService.GetCachedIcon(_keys[0]);
        var bmp = cached ?? AppIconService.LoadIcon(_iconPath);
        if (bmp != null)
        {
            IconHost.Background = Brushes.Transparent;
            IconHost.Child = new System.Windows.Controls.Image { Source = bmp, Stretch = Stretch.Uniform };
        }
        else
        {
            IconHost.Child = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(_displayName) ? "?" : _displayName.Trim()[..1].ToUpperInvariant(),
                Foreground = Brushes.White,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }
    }

    private void OnBack(object s, RoutedEventArgs e) => Close();

    // ── 时间段 ──

    private void SetPeriod(UsagePeriod p)
    {
        _period = p;
        DayBtn.IsChecked = p == UsagePeriod.Day;
        WeekBtn.IsChecked = p == UsagePeriod.Week;
        MonthBtn.IsChecked = p == UsagePeriod.Month;
        YearBtn.IsChecked = p == UsagePeriod.Year;
        CustomBtn.IsChecked = p == UsagePeriod.Custom;
        RefreshView();
    }

    private void OnDay(object s, RoutedEventArgs e) => SetPeriod(UsagePeriod.Day);
    private void OnWeek(object s, RoutedEventArgs e) => SetPeriod(UsagePeriod.Week);
    private void OnMonth(object s, RoutedEventArgs e) => SetPeriod(UsagePeriod.Month);
    private void OnYear(object s, RoutedEventArgs e) => SetPeriod(UsagePeriod.Year);
    private void OnCustom(object s, RoutedEventArgs e) => SetPeriod(UsagePeriod.Custom);

    private (DateTime From, DateTime To) CurrentRange() => _period switch
    {
        UsagePeriod.Day => (_anchor.Date, _anchor.Date),
        UsagePeriod.Week => (_anchor.Date.AddDays(-6), _anchor.Date),
        UsagePeriod.Month => (new DateTime(_anchor.Year, _anchor.Month, 1), _anchor.Date),
        UsagePeriod.Year => (new DateTime(_anchor.Year, 1, 1), _anchor.Date),
        _ => (_customFrom.Date, _customTo.Date > DateTime.Today ? DateTime.Today : _customTo.Date),
    };

    private void RefreshView()
    {
        Panel.Children.Clear();

        var (from, to) = CurrentRange();
        var report = UsageAggregator.Build(_data, from, to, null, 64, _keys);
        int days = (to - from).Days + 1;

        // 使用天数 / 最多的一天 / 最近一次 —— 「平均每天」的分母只算**真的有记录的天数**，
        // 否则今年视图会拿 259 天去平均，数字被稀释得毫无意义。
        int usedDays = 0, bestSec = 0;
        string bestDay = "—", lastDay = "—";
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            var key = d.ToString("yyyy-MM-dd");
            var day = _data.GetDay(key);
            if (day == null) continue;
            int sec = 0;
            foreach (var k in _keys)
                if (day.TryGetValue(k, out var rec)) sec += rec.Seconds;
            if (sec > 0)
            {
                usedDays++;
                lastDay = d.Date == DateTime.Today ? "今天" : d.ToString("MM-dd");
                if (sec > bestSec) { bestSec = sec; bestDay = d.ToString("MM-dd"); }
            }
        }

        string avgText = usedDays > 0
            ? $"{UsageBarChart.FormatDuration(report.ActiveSeconds / usedDays)}/天"
            : "—";

        // ① 头部卡片：平均每天 + 区间合计 + 柱状图
        var card = CreateCard();
        Add(card, new TextBlock
        {
            Text = usedDays > 0 ? $"平均 {avgText}" : "这段时间没有记录",
            FontSize = 24,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Ink),
            Margin = new Thickness(0, 2, 0, 2)
        });
        Add(card, new TextBlock
        {
            Text = (usedDays > 0
                    ? $"有记录的 {usedDays} 天合计 {UsageBarChart.FormatDuration(report.ActiveSeconds)}"
                    : $"（区间共 {days} 天）")
                   + DescribeCompare(report),
            FontSize = 13,
            Foreground = new SolidColorBrush(Muted),
            Margin = new Thickness(0, 0, 0, 10)
        });

        if (report.Buckets.Any(b => b.ActiveSeconds > 0))
        {
            Add(card, new UsageBarChart
            {
                Values = report.Buckets.Select(b => (b.ActiveSeconds, 0)).ToList(),
                Labels = report.Buckets.Select(b => b.Label).ToList(),
                FullLabels = report.Buckets.Select(b => b.FullLabel).ToList(),
                IsHourlyAxis = report.Granularity == UsageGranularity.Hourly,
                ShowLocked = false,
                AxisFormatter = ShortAxis,
            });
        }
        else
        {
            Add(card, new Border
            {
                Height = 90,
                Background = new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFC)),
                CornerRadius = new CornerRadius(8),
                Child = new TextBlock
                {
                    Text = "这段时间没有这个应用的记录",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Faint),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            });
        }
        Panel.Children.Add(card);

        // ② 统计范围
        var scopePanel = new StackPanel();
        scopePanel.Children.Add(SectionLabel("统计范围"));
        var scope = CreateCard();

        // 该应用占同期总时长的比例
        var all = UsageAggregator.Build(_data, from, to);
        int share = all.ActiveSeconds > 0 ? (int)Math.Round(report.ActiveSeconds * 100.0 / all.ActiveSeconds) : 0;

        // 使用天数 / 最多的一天 / 最近一次（上面已经算过，这里直接用）
        var rows = new List<(string, string)>
        {
            ("区间", $"{from:yyyy-MM-dd} ~ {to:yyyy-MM-dd}"),
            ("合计", UsageBarChart.FormatDuration(report.ActiveSeconds)),
            ("有记录的天数", $"{usedDays} / {days} 天"),
            ("平均每天", usedDays > 0
                ? $"{UsageBarChart.FormatDuration(report.ActiveSeconds / usedDays)}（按有记录的 {usedDays} 天）"
                : "—"),
            ("最多的一天", bestSec > 0 ? $"{bestDay}（{UsageBarChart.FormatDuration(bestSec)}）" : "—"),
            ("最近使用", lastDay),
            ("占同期总时长", $"{share}%"),
            ("进程名", string.Join(" / ", _keys)),
        };
        for (int i = 0; i < rows.Count; i++)
            Add(scope, SmallRow(rows[i].Item1, rows[i].Item2, i == rows.Count - 1));
        scopePanel.Children.Add(scope);
        Panel.Children.Add(scopePanel);

        Add(Panel, new TextBlock
        {
            Text = "显示名字/图标不合适？在使用时间页点「应用名 / 锁屏进程」可以改，右键列表行可以换图标。",
            FontSize = 11,
            Foreground = new SolidColorBrush(Faint),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 6, 0, 0)
        });
    }

    private static string DescribeCompare(UsageReport report)
    {
        if (report.PreviousActiveSeconds <= 0) return "";
        int diff = report.ActiveSeconds - report.PreviousActiveSeconds;
        if (Math.Abs(diff) < 60) return "，与上一区间基本持平";
        return diff > 0
            ? $"，比上一区间多 {UsageBarChart.FormatDuration(diff)}"
            : $"，比上一区间少 {UsageBarChart.FormatDuration(-diff)}";
    }

    // ── 小部件 ──

    private static TextBlock SectionLabel(string text) => new()
    {
        Text = text,
        FontSize = 12,
        FontWeight = FontWeights.SemiBold,
        Foreground = new SolidColorBrush(Muted),
        Margin = new Thickness(2, 6, 0, 6)
    };

    private static Border CreateCard() => new()
    {
        Background = Brushes.White,
        BorderBrush = new SolidColorBrush(CardBorder),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(12),
        Padding = new Thickness(14, 12, 14, 12),
        Margin = new Thickness(0, 0, 0, 6),
        Child = new StackPanel()
    };

    private static void Add(Border card, UIElement child) => ((StackPanel)card.Child).Children.Add(child);

    private static void Add(StackPanel panel, UIElement child) => panel.Children.Add(child);

    private static Grid SmallRow(string label, string value, bool isLast = true)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, isLast ? 2 : 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var l = new TextBlock { Text = label, FontSize = 13, Foreground = new SolidColorBrush(Ink2), VerticalAlignment = VerticalAlignment.Center };
        var v = new TextBlock { Text = value, FontSize = 13, Foreground = new SolidColorBrush(Muted), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(v, 1);
        grid.Children.Add(l);
        grid.Children.Add(v);
        return grid;
    }

    private static string ShortAxis(int seconds)
    {
        if (seconds >= 3600) return $"{seconds / 3600.0:0.#} h";
        if (seconds >= 60) return $"{seconds / 60} min";
        return seconds > 0 ? $"{seconds} s" : "0";
    }
}
