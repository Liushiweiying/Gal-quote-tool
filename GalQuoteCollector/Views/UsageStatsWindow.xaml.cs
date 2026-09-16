using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GalQuoteCollector.Models;
using GalQuoteCollector.Services;
using GalQuoteCollector.Views.Controls;

namespace GalQuoteCollector.Views;

public enum UsagePeriod { Day, Week, Month, Year, Custom }

/// <summary>
/// 使用时间页（参考 iOS「屏幕使用时间」）：
/// 总时长大卡片（蓝=使用 / 绿=锁屏）+ 响应式柱状图 → 游戏/应用排行 → 采集 → 统计范围。
/// 时间段：今天 / 最近 7 天 / 本月 / 今年 / 自选（日历）。
/// </summary>
public partial class UsageStatsWindow : Window
{
    private const int TopAppCount = 6;

    private readonly UsageData _data;
    private readonly List<Quote> _quotes;
    private readonly SettingsService? _settings;
    private HotkeyConfig? _config;
    private readonly bool _readOnly;

    private UsagePeriod _period = UsagePeriod.Day;
    private DateTime _anchor = DateTime.Today;
    private DateTime _customFrom = DateTime.Today.AddDays(-6);
    private DateTime _customTo = DateTime.Today;

    private bool _showAllApps;
    private bool _isWide;
    private bool _lightDismissArmed;
    private int _childDialogs;
    private readonly List<UsageBarChart> _charts = new(); // 当前视图里的图表（用于 Esc/点空白取消钉住）

    public UsageStatsWindow(Window owner, UsageData data, List<Quote>? quotes = null,
        SettingsService? settings = null, bool readOnly = false)
    {
        InitializeComponent();
        Owner = owner;
        _data = data;
        _quotes = quotes ?? new List<Quote>();
        _settings = settings;
        _readOnly = readOnly;
        _config = settings?.LoadHotkeyConfig();

        RestorePreferences();
        RefreshView();

        Loaded += OnLoaded;
        Closing += OnClosingWindow;
        Deactivated += OnWindowDeactivated;
        SizeChanged += OnSizeChanged;
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseLeftButtonDown += OnPreviewMouseDown;
    }

    // ── 取消「钉住」的气泡：Esc / 点图表以外的地方 ──

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Escape) return;
        if (!UnpinAllCharts()) return; // 没有钉住的气泡时不吞掉 Esc
        e.Handled = true;
    }

    private void OnPreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_charts.Count == 0) return;
        // 点在图表内部时交给图表自己处理（点柱子=钉住，点空白=取消）
        if (IsInsideAnyChart(e.OriginalSource as DependencyObject)) return;
        UnpinAllCharts();
    }

    private bool UnpinAllCharts()
    {
        bool any = false;
        foreach (var chart in _charts)
        {
            if (chart.IsPinned) { chart.Unpin(); any = true; }
        }
        return any;
    }

    private bool IsInsideAnyChart(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is UsageBarChart chart && _charts.Contains(chart)) return true;
            source = source is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(source)
                : null;
        }
        return false;
    }

    /// <summary>登记图表，方便统一取消钉住。</summary>
    private UsageBarChart RegisterChart(UsageBarChart chart)
    {
        _charts.Add(chart);
        return chart;
    }

    // ── 窗口尺寸 / 偏好 ──

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _lightDismissArmed = true;

        var work = SystemParameters.WorkArea;
        double w = _config?.UsageWindowWidth > 200 ? _config.UsageWindowWidth : Math.Clamp(work.Width * 0.5, 520, 900);
        double h = _config?.UsageWindowHeight > 200 ? _config.UsageWindowHeight : Math.Min(work.Height - 80, 900);
        Width = Math.Min(w, work.Width);
        Height = Math.Min(h, work.Height);

        _isWide = ActualWidth >= 960;
        RefreshView();
    }

    private void OnClosingWindow(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_readOnly || _settings == null) return;
        try
        {
            _config ??= _settings.LoadHotkeyConfig();
            _config.UsageWindowWidth = Width;
            _config.UsageWindowHeight = Height;
            _config.UsagePeriodMode = _period.ToString().ToLowerInvariant();
            _config.UsageRangeFrom = _customFrom;
            _config.UsageRangeTo = _customTo;
            _settings.SaveHotkeyConfig(_config);
        }
        catch (Exception ex) { AppLog.Write($"usage window: save prefs failed: {ex.Message}"); }
    }

    private void RestorePreferences()
    {
        if (_config == null) return;
        _period = _config.UsagePeriodMode switch
        {
            "week" => UsagePeriod.Week,
            "month" => UsagePeriod.Month,
            "year" => UsagePeriod.Year,
            "custom" => UsagePeriod.Custom,
            _ => UsagePeriod.Day,
        };
        if (_config.UsageRangeFrom.HasValue) _customFrom = _config.UsageRangeFrom.Value;
        if (_config.UsageRangeTo.HasValue) _customTo = _config.UsageRangeTo.Value;
        DayModeBtn.IsChecked = _period == UsagePeriod.Day;
        WeekModeBtn.IsChecked = _period == UsagePeriod.Week;
        MonthModeBtn.IsChecked = _period == UsagePeriod.Month;
        YearModeBtn.IsChecked = _period == UsagePeriod.Year;
        CustomModeBtn.IsChecked = _period == UsagePeriod.Custom;
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        bool wide = ActualWidth >= 960;
        if (wide == _isWide) return;
        _isWide = wide;
        RefreshView();
    }

    // ── 点窗口外关闭 ──

    private async void OnWindowDeactivated(object? sender, EventArgs e)
    {
        if (!_lightDismissArmed || _childDialogs > 0) return;

        // 失活可能是瞬时的（子窗口切换、托盘菜单、我们自己弹提示）。
        // 等一小会儿再看：仍然失活、没有别的本程序窗口在前台、也没有菜单/拖动在抓鼠标，才真的关。
        await Task.Delay(650);
        if (!_lightDismissArmed || _childDialogs > 0 || IsActive) return;
        if (IsAnotherOwnWindowActive()) return;
        if (System.Windows.Input.Mouse.Captured != null) return; // 右键菜单/拖动中

        AppLog.Write("usage window: 点窗口外 → 自动关闭");
        Close();
    }

    /// <summary>本程序是不是还有别的窗口在前台（例如主窗口、别的对话框）。</summary>
    private static bool IsAnotherOwnWindowActive()
    {
        try
        {
            foreach (Window w in Application.Current.Windows)
            {
                if (w != null && w.IsActive) return true;
            }
        }
        catch { }
        return false;
    }

    private bool? ShowChildDialog(Window win)
    {
        _childDialogs++;
        try { return win.ShowDialog(); }
        finally { _childDialogs--; }
    }

    private bool? ShowChildDialog(Microsoft.Win32.CommonDialog dialog)
    {
        _childDialogs++;
        try { return dialog.ShowDialog(this); }
        finally { _childDialogs--; }
    }

    private InfoDialogResult ShowInfo(string title, string message,
        InfoDialogButtons buttons = InfoDialogButtons.OK, InfoDialogIcon icon = InfoDialogIcon.Information)
    {
        _childDialogs++;
        try { return InfoDialog.Show(this, title, message, buttons, icon); }
        finally { _childDialogs--; }
    }

    // ── 时间段 ──

    private void SetPeriod(UsagePeriod p)
    {
        _period = p;
        _showAllApps = false;
        DayModeBtn.IsChecked = p == UsagePeriod.Day;
        WeekModeBtn.IsChecked = p == UsagePeriod.Week;
        MonthModeBtn.IsChecked = p == UsagePeriod.Month;
        YearModeBtn.IsChecked = p == UsagePeriod.Year;
        CustomModeBtn.IsChecked = p == UsagePeriod.Custom;
        RefreshView();
    }

    private void OnPeriodDay(object s, RoutedEventArgs e) => SetPeriod(UsagePeriod.Day);
    private void OnPeriodWeek(object s, RoutedEventArgs e) => SetPeriod(UsagePeriod.Week);
    private void OnPeriodMonth(object s, RoutedEventArgs e) => SetPeriod(UsagePeriod.Month);
    private void OnPeriodYear(object s, RoutedEventArgs e) => SetPeriod(UsagePeriod.Year);

    private void OnPeriodCustom(object s, RoutedEventArgs e)
    {
        if (!PickCustomRange()) { CustomModeBtn.IsChecked = _period == UsagePeriod.Custom; return; }
        SetPeriod(UsagePeriod.Custom);
    }

    private void OnPrevPeriod(object s, RoutedEventArgs e) => Shift(-1);
    private void OnNextPeriod(object s, RoutedEventArgs e) => Shift(1);

    private void Shift(int direction)
    {
        switch (_period)
        {
            case UsagePeriod.Day: _anchor = _anchor.AddDays(direction); break;
            case UsagePeriod.Week: _anchor = _anchor.AddDays(7 * direction); break;
            case UsagePeriod.Month: _anchor = _anchor.AddMonths(direction); break;
            case UsagePeriod.Year: _anchor = _anchor.AddYears(direction); break;
            case UsagePeriod.Custom:
                int span = (_customTo.Date - _customFrom.Date).Days + 1;
                _customFrom = _customFrom.AddDays(span * direction);
                _customTo = _customTo.AddDays(span * direction);
                break;
        }
        if (_anchor.Date > DateTime.Today) _anchor = DateTime.Today;
        _showAllApps = false;
        RefreshView();
    }

    private (DateTime From, DateTime To) CurrentRange()
    {
        return _period switch
        {
            UsagePeriod.Day => (_anchor.Date, _anchor.Date),
            UsagePeriod.Week => (_anchor.Date.AddDays(-6), _anchor.Date),
            UsagePeriod.Month => (new DateTime(_anchor.Year, _anchor.Month, 1), _anchor.Date),
            UsagePeriod.Year => (new DateTime(_anchor.Year, 1, 1), _anchor.Date),
            _ => (_customFrom.Date, _customTo.Date > DateTime.Today ? DateTime.Today : _customTo.Date),
        };
    }

    private string CompareWhat() => _period switch
    {
        UsagePeriod.Day => "昨天",
        UsagePeriod.Week => "前 7 天",
        UsagePeriod.Month => "上月同期",
        UsagePeriod.Year => "去年同期",
        _ => "上一个区间",
    };

    private void RefreshView()
    {
        StatsPanel.Children.Clear();
        _pendingIcons.Clear();
        _charts.Clear();

        var (from, to) = CurrentRange();
        var report = UsageAggregator.Build(_data, from, to);
        report.CompareLabel = CompareWhat();

        RangeText.Content = DescribeRange(from, to);

        StatsPanel.Children.Add(BuildTotalCard(report));
        StatsPanel.Children.Add(BuildAppSection(report));
        StatsPanel.Children.Add(BuildCaptureSection(report));
        StatsPanel.Children.Add(BuildScopeSection(report));

        StartIconResolution();
    }

    private string DescribeRange(DateTime from, DateTime to)
    {
        if (_period == UsagePeriod.Day)
            return from == DateTime.Today ? $"今天 {from:MM-dd}" : from.ToString("yyyy-MM-dd");
        if (from.Year == to.Year && from.Month == to.Month)
            return $"{from:M月d日} - {to:M月d日}";
        return $"{from:yyyy-MM-dd} - {to:MM-dd}";
    }

    // ── 卡片 ──

    private UIElement BuildTotalCard(UsageReport report)
    {
        var card = CreateCard();

        Add(card, new TextBlock
        {
            Text = UsageBarChart.FormatDuration(report.ActiveSeconds),
            FontSize = 34,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Ink),
            Margin = new Thickness(0, 2, 0, 2)
        });

        Add(card, new TextBlock
        {
            Text = UsageAggregator.CompareText(report.ActiveSeconds, report.PreviousActiveSeconds, report.CompareLabel),
            FontSize = 13,
            Foreground = new SolidColorBrush(Muted),
            Margin = new Thickness(0, 0, 0, report.LockedSeconds > 0 ? 2 : 10)
        });

        if (report.LockedSeconds > 0)
        {
            Add(card, new TextBlock
            {
                Text = $"锁屏 {UsageBarChart.FormatDuration(report.LockedSeconds)}",
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(LockedColor),
                Margin = new Thickness(0, 0, 0, 10)
            });
        }

        bool hasData = report.Buckets.Any(b => b.ActiveSeconds + b.LockedSeconds > 0);
        if (hasData)
        {
            Add(card, RegisterChart(new UsageBarChart
            {
                Values = report.Buckets.Select(b => (b.ActiveSeconds, b.LockedSeconds)).ToList(),
                Labels = report.Buckets.Select(b => b.Label).ToList(),
                FullLabels = report.Buckets.Select(b => b.FullLabel).ToList(),
                IsHourlyAxis = report.Granularity == UsageGranularity.Hourly,
                AxisFormatter = ShortAxis,
            }));
            Add(card, BuildLegend());
        }
        else
        {
            Add(card, new Border
            {
                Height = 96,
                Background = new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFC)),
                CornerRadius = new CornerRadius(8),
                Child = new TextBlock
                {
                    Text = report.Granularity == UsageGranularity.Hourly
                        ? "还没有每小时明细\n（每小时数据从本版开始记录，今天会随使用慢慢长出来）"
                        : "这段时间没有记录",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Faint),
                    TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            });
        }

        Add(card, new TextBlock
        {
            Text = "柱子下段蓝色 = 使用时长，上段绿色 = 锁屏时长；鼠标划到柱子上可看具体数值（点击钉住）",
            FontSize = 11,
            Foreground = new SolidColorBrush(Faint),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        });
        return card;
    }

    private static UIElement BuildLegend()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(LegendDot(Accent, "使用"));
        panel.Children.Add(LegendDot(LockedColor, "锁屏"));
        return panel;
    }

    private static UIElement LegendDot(Color color, string text)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 0) };
        panel.Children.Add(new Border
        {
            Width = 8, Height = 8,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(color),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0)
        });
        panel.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 11,
            Foreground = new SolidColorBrush(Muted),
            VerticalAlignment = VerticalAlignment.Center
        });
        return panel;
    }

    private UIElement BuildAppSection(UsageReport report)
    {
        var panel = new StackPanel();
        panel.Children.Add(SectionLabel("游戏 / 应用"));

        if (report.Apps.Count == 0)
        {
            panel.Children.Add(HintCard("这段时间没有应用记录"));
            return panel;
        }

        int maxSec = Math.Max(1, report.Apps.Max(a => a.Seconds));
        int shown = _showAllApps ? report.Apps.Count : Math.Min(TopAppCount, report.Apps.Count);
        var card = CreateCard();

        if (_isWide)
        {
            var grid = new UniformGrid { Columns = 2 };
            for (int i = 0; i < shown; i++)
                grid.Children.Add(new Border { Padding = new Thickness(0, 0, 14, 0), Child = BuildAppRow(report.Apps[i], maxSec) });
            Add(card, grid);
        }
        else
        {
            for (int i = 0; i < shown; i++)
                Add(card, BuildAppRow(report.Apps[i], maxSec));
        }

        if (report.Apps.Count > TopAppCount)
        {
            var moreBtn = new Button
            {
                Content = _showAllApps ? "收起" : $"显示更多（还有 {report.Apps.Count - TopAppCount} 个）",
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = new SolidColorBrush(Accent),
                FontSize = 13,
                Cursor = System.Windows.Input.Cursors.Hand,
                Padding = new Thickness(0, 10, 0, 2),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            moreBtn.Click += (_, _) => { _showAllApps = !_showAllApps; RefreshView(); };
            Add(card, moreBtn);
        }

        panel.Children.Add(card);
        return panel;
    }

    private UIElement BuildAppRow(UsageAppStat app, int maxSec)
    {
        var outer = new StackPanel { Margin = new Thickness(0, 6, 0, 8) };

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = BuildIcon(app.Name);
        Grid.SetColumn(icon, 0);
        row.Children.Add(icon);

        var nameText = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(app.Name) ? app.Key : app.Name,
            FontSize = 14,
            Foreground = new SolidColorBrush(Ink),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(10, 0, 8, 0)
        };
        Grid.SetColumn(nameText, 1);
        row.Children.Add(nameText);

        var timeText = new TextBlock
        {
            Text = UsageBarChart.FormatDuration(app.Seconds),
            FontSize = 13,
            Foreground = new SolidColorBrush(Muted),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(timeText, 2);
        row.Children.Add(timeText);
        outer.Children.Add(row);

        if (app.AllBlacklisted)
        {
            outer.Children.Add(new TextBlock
            {
                Text = "已在黑名单，不再记录",
                FontSize = 10,
                Foreground = new SolidColorBrush(Faint),
                Margin = new Thickness(40, 2, 0, 0)
            });
        }

        outer.Children.Add(BuildProgressBar((double)app.Seconds / maxSec));

        var menu = new ContextMenu();
        var miPickImage = new MenuItem { Header = "选择图标图片…" };
        miPickImage.Click += (_, _) => PickIconFile(app.Key, imagesOnly: true);
        var miPickExe = new MenuItem { Header = "选择 exe 并提取图标…" };
        miPickExe.Click += (_, _) => PickIconFile(app.Key, imagesOnly: false);
        var miSearch = new MenuItem { Header = "自动搜索 exe（较慢）…" };
        miSearch.Click += (_, _) => SearchExeFor(app.Key, app.Name);
        var miAuto = new MenuItem { Header = "恢复自动识别" };
        miAuto.Click += (_, _) => SetIconOverride(app.Key, null);
        menu.Items.Add(miPickImage);
        menu.Items.Add(miPickExe);
        menu.Items.Add(miSearch);
        menu.Items.Add(new Separator());
        menu.Items.Add(miAuto);
        outer.ContextMenu = menu;
        outer.ToolTip = app.Keys.Count > 1
            ? $"进程：{string.Join(" / ", app.Keys)}\n右键可自定义图标"
            : "右键可自定义图标（游戏图标抓不到时用这个）";

        // 图标：已缓存过就立刻套上（重建列表不会先闪成首字母），否则排队异步解析
        var cachedIcon = AppIconService.GetCachedIcon(app.Key);
        if (AppIconService.IsIconCached(app.Key))
            ApplyIconTo(icon, cachedIcon);
        else
            _pendingIcons.Add((app.Key, app.Name, app.Path, icon));

        return outer;
    }

    private static UIElement BuildProgressBar(double fraction)
    {
        fraction = Math.Max(0, Math.Min(1, fraction));
        var grid = new Grid { Height = 3, Margin = new Thickness(0, 7, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(fraction, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.0001, 1 - fraction), GridUnitType.Star) });
        var fill = new Border { Background = new SolidColorBrush(Accent), CornerRadius = new CornerRadius(1.5) };
        Grid.SetColumn(fill, 0);
        grid.Children.Add(fill);
        return grid;
    }

    private static Border BuildIcon(string name)
    {
        var border = new Border
        {
            Width = 30, Height = 30,
            CornerRadius = new CornerRadius(7),
            Background = new SolidColorBrush(IconColorFor(name)),
            VerticalAlignment = VerticalAlignment.Center
        };
        border.Child = new TextBlock
        {
            Text = InitialOf(name),
            Foreground = Brushes.White,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        return border;
    }

    private static void ApplyIconTo(Border host, BitmapSource? bmp)
    {
        if (bmp == null) return;
        host.Background = Brushes.Transparent;
        host.Child = new Image { Source = bmp, Stretch = Stretch.Uniform };
    }

    private static Color IconColorFor(string name)
    {
        int hash = 0;
        foreach (var c in name) hash = (hash * 31 + c) & 0x7FFFFFFF;
        return IconPalette[hash % IconPalette.Length];
    }

    private static string InitialOf(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";
        var ch = name.Trim()[0];
        return char.IsLetter(ch) ? char.ToUpperInvariant(ch).ToString() : "·";
    }

    private UIElement BuildCaptureSection(UsageReport report)
    {
        var panel = new StackPanel();
        panel.Children.Add(SectionLabel("采集"));

        var caps = _quotes.Where(q => q.CapturedAt >= report.From && q.CapturedAt <= report.To.AddDays(1).AddTicks(-1)).ToList();

        var card = CreateCard();
        Add(card, new TextBlock
        {
            Text = caps.Count == 0
                ? (report.Granularity == UsageGranularity.Hourly ? "今天没有采集" : "这段时间没有采集")
                : $"采集 {caps.Count} 条",
            FontSize = 26,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Ink),
            Margin = new Thickness(0, 2, 0, 2)
        });

        var counts = report.Buckets
            .Select(b => caps.Count(q => q.CapturedAt >= b.Start && q.CapturedAt <= b.End))
            .ToArray();

        if (counts.Any(c => c > 0))
        {
            Add(card, RegisterChart(new UsageBarChart
            {
                Values = counts.Select(c => (c, 0)).ToArray(),
                Labels = report.Buckets.Select(b => b.Label).ToList(),
                FullLabels = report.Buckets.Select(b => b.FullLabel).ToList(),
                IsHourlyAxis = report.Granularity == UsageGranularity.Hourly,
                ShowLocked = false,
                AxisFormatter = s => $"{s} 条",
                BubbleFormatter = v => $"{v.Active} 条",
            }));
        }
        else
        {
            Add(card, new Border
            {
                Height = 72,
                Background = new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFC)),
                CornerRadius = new CornerRadius(8),
                Child = new TextBlock
                {
                    Text = "这段时间没有采集",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Faint),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            });
        }

        int days = (report.To.Date - report.From.Date).Days + 1;
        string busiest = "—";
        if (caps.Count > 0)
        {
            var top = caps.GroupBy(q => q.CapturedAt.Date).OrderByDescending(g => g.Count()).First();
            busiest = $"{top.Key:MM-dd}（{top.Count()} 条）";
        }
        Add(card, SmallRow("最多的一天", busiest));
        Add(card, SmallRow(days <= 1 ? "平均每小时" : "平均每天",
            caps.Count == 0 ? "—" : $"{caps.Count / (days <= 1 ? 24.0 : days):0.#} 条"));

        panel.Children.Add(card);
        return panel;
    }

    private UIElement BuildScopeSection(UsageReport report)
    {
        var panel = new StackPanel();
        panel.Children.Add(SectionLabel("统计范围"));
        var card = CreateCard();
        var rows = new List<(string, string)>
        {
            ("区间", $"{report.From:yyyy-MM-dd} ~ {report.To:yyyy-MM-dd}"),
            ("使用时长", UsageBarChart.FormatDuration(report.ActiveSeconds)),
            ("锁屏时长", UsageBarChart.FormatDuration(report.LockedSeconds)),
            ("游戏 / 应用", $"{report.Apps.Count} 个"),
            ("工具运行", UsageBarChart.FormatDuration(report.ToolSeconds)),
            ("黑名单", $"{_data.Blacklist.Count} 个"),
        };
        for (int i = 0; i < rows.Count; i++)
            Add(card, SmallRow(rows[i].Item1, rows[i].Item2, i == rows.Count - 1));
        panel.Children.Add(card);
        return panel;
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

    private static Border HintCard(string text)
    {
        var card = CreateCard();
        Add(card, new TextBlock
        {
            Text = text,
            FontSize = 13,
            Foreground = new SolidColorBrush(Muted),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 6)
        });
        return card;
    }

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

    private static string ShortAxis(int seconds)
    {
        if (seconds >= 3600) return $"{seconds / 3600.0:0.#} h";
        if (seconds >= 60) return $"{seconds / 60} min";
        return seconds > 0 ? $"{seconds} s" : "0";
    }

    // ── 配色 ──
    private static readonly Color Accent = Color.FromRgb(0x5B, 0x6A, 0xBF);
    private static readonly Color LockedColor = Color.FromRgb(0x34, 0xA8, 0x53);
    private static readonly Color Ink = Color.FromRgb(0x1D, 0x1D, 0x1F);
    private static readonly Color Ink2 = Color.FromRgb(0x3C, 0x3C, 0x43);
    private static readonly Color Muted = Color.FromRgb(0x8E, 0x8E, 0x93);
    private static readonly Color Faint = Color.FromRgb(0xC7, 0xC7, 0xCC);
    private static readonly Color CardBorder = Color.FromRgb(0xE5, 0xE5, 0xEA);

    private static readonly Color[] IconPalette =
    {
        Color.FromRgb(0x5B, 0x6A, 0xBF), Color.FromRgb(0x34, 0xA8, 0x53),
        Color.FromRgb(0xE0, 0x8A, 0x2E), Color.FromRgb(0xD1, 0x4B, 0x6E),
        Color.FromRgb(0x2E, 0x9E, 0xA8), Color.FromRgb(0x7A, 0x5A, 0xC8),
    };

    // ── 图标解析 ──

    private readonly List<(string Key, string Name, string Path, Border Host)> _pendingIcons = new();

    private async void StartIconResolution()
    {
        if (_pendingIcons.Count == 0) return;
        var pending = _pendingIcons.ToList();
        _pendingIcons.Clear();

        var overrides = _config?.UsageIconOverrides ?? new Dictionary<string, string>();
        try
        {
            var resolved = await Task.Run(() => pending
                .Select(p => (p, exe: AppIconService.ResolveExePath(p.Key, p.Path, overrides)))
                .Select(x => (x.p, icon: AppIconService.LoadIcon(x.exe), x.exe))
                .ToList());

            foreach (var (p, icon, exe) in resolved)
            {
                // 缓存（含"确认没有图标"），下次重建列表立刻可用
                AppIconService.CacheIcon(p.Key, icon);
                if (icon != null) ApplyIconTo(p.Host, icon);
                AppLog.Write($"usage icon: {p.Key} → {exe ?? "<未找到>"} (icon={(icon != null)})");
                if (!_readOnly && !string.IsNullOrWhiteSpace(exe))
                {
                    foreach (var day in _data.Records.Values)
                    {
                        if (day.TryGetValue(p.Key, out var rec) && string.IsNullOrWhiteSpace(rec.Path))
                        {
                            rec.Path = exe;
                            break;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"usage icons: resolve failed: {ex.Message}");
        }
    }

    private void PickIconFile(string processKey, bool imagesOnly)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = imagesOnly ? "选择图标图片" : "选择 exe / 图标文件",
            Filter = imagesOnly
                ? "图片 (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.ico)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.ico|所有文件|*.*"
                : "程序 / 图标 (*.exe;*.dll;*.ico)|*.exe;*.dll;*.ico|所有文件|*.*",
            CheckFileExists = true
        };
        if (ShowChildDialog(dlg) != true) return;
        SetIconOverride(processKey, dlg.FileName);
    }

    private void SearchExeFor(string processKey, string displayName)
    {
        var exeName = processKey.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? processKey : processKey + ".exe";
        var win = new Window
        {
            Title = $"搜索 {exeName}",
            Width = 620, Height = 380,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF7)),
            FontFamily = new FontFamily("Segoe UI")
        };
        var root = new Grid { Margin = new Thickness(14) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var status = new TextBlock
        {
            Text = "在常见目录（Program Files / Steam / 各盘 Games、gal 等）里按文件名搜索…",
            FontSize = 12,
            Foreground = new SolidColorBrush(Muted),
            Margin = new Thickness(0, 0, 0, 8),
            TextWrapping = TextWrapping.Wrap
        };
        var list = new ListBox { FontSize = 12 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var ok = new Button { Content = "使用选中的 exe", Padding = new Thickness(12, 6, 12, 6), FontSize = 12, IsEnabled = false };
        var cancel = new Button { Content = "取消", Padding = new Thickness(12, 6, 12, 6), FontSize = 12, Margin = new Thickness(8, 0, 0, 0) };
        ok.Click += (_, _) => { if (list.SelectedItem is string sel) { SetIconOverride(processKey, sel); win.Close(); } };
        cancel.Click += (_, _) => win.Close();
        list.SelectionChanged += (_, _) => ok.IsEnabled = list.SelectedItem != null;
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        Grid.SetRow(status, 0); Grid.SetRow(list, 1); Grid.SetRow(buttons, 2);
        root.Children.Add(status); root.Children.Add(list); root.Children.Add(buttons);
        win.Content = root;

        win.Loaded += async (_, _) =>
        {
            var hits = await Task.Run(() => AppIconService.SearchExePaths(exeName));
            foreach (var h in hits) list.Items.Add(h);
            status.Text = hits.Count == 0
                ? $"没搜到 {exeName}。可以直接右键「选择 exe 并提取图标…」手动指定。"
                : $"找到 {hits.Count} 个结果，选一个作为 {displayName} 的图标：";
        };
        ShowChildDialog(win);
    }

    private void SetIconOverride(string processKey, string? path)
    {
        if (_settings != null && !_readOnly)
        {
            _config ??= _settings.LoadHotkeyConfig();
            if (string.IsNullOrWhiteSpace(path)) _config.UsageIconOverrides.Remove(processKey);
            else _config.UsageIconOverrides[processKey] = path;
            try { _settings.SaveHotkeyConfig(_config); }
            catch (Exception ex) { AppLog.Write($"usage icons: save failed: {ex.Message}"); }
        }
        if (string.IsNullOrWhiteSpace(path)) AppIconService.Forget(processKey);
        RefreshView();
    }

    // ── 日期 / 区间选择 ──

    private void OnPickRange(object s, RoutedEventArgs e)
    {
        if (_period == UsagePeriod.Custom)
        {
            if (PickCustomRange()) { _showAllApps = false; RefreshView(); }
            return;
        }
        ShowDatePicker();
    }

    private void ShowDatePicker()
    {
        var picker = new DatePicker
        {
            SelectedDate = _anchor,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 13,
            DisplayDateStart = _data.Records.Keys.Count > 0 && _data.Records.Keys.MinBy(k => k) is string min
                ? DateTime.Parse(min) : _anchor.AddMonths(-1),
            DisplayDateEnd = DateTime.Today
        };
        Converters.UsageDateHighlightConverter.SetDates(_data.Records.Keys);
        if (FindResource("CalendarWithData") is Style calStyle)
            picker.CalendarStyle = calStyle;
        picker.SelectedDateChanged += (_, _) =>
        {
            if (picker.SelectedDate.HasValue)
            {
                _anchor = picker.SelectedDate.Value > DateTime.Today ? DateTime.Today : picker.SelectedDate.Value;
                _showAllApps = false;
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
        ShowChildDialog(win);
    }

    /// <summary>自选区间：两个日期框（可开日历）。</summary>
    private bool PickCustomRange()
    {
        bool ok = false;
        var from = new DatePicker { SelectedDate = _customFrom, FontSize = 13, DisplayDateEnd = DateTime.Today };
        var to = new DatePicker { SelectedDate = _customTo, FontSize = 13, DisplayDateEnd = DateTime.Today };
        Converters.UsageDateHighlightConverter.SetDates(_data.Records.Keys);
        if (FindResource("CalendarWithData") is Style calStyle)
        {
            from.CalendarStyle = calStyle;
            to.CalendarStyle = calStyle;
        }

        var win = new Window
        {
            Title = "自选时间段",
            Width = 340, Height = 210,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF7)),
            FontFamily = new FontFamily("Segoe UI")
        };

        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var hint = new TextBlock
        {
            Text = "选一段时间（含首尾两天）",
            FontSize = 13,
            Foreground = new SolidColorBrush(Ink2),
            Margin = new Thickness(0, 0, 0, 10)
        };
        Grid.SetRow(hint, 0);
        root.Children.Add(hint);

        var fromRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        fromRow.Children.Add(new TextBlock { Text = "开始", Width = 42, VerticalAlignment = VerticalAlignment.Center, FontSize = 13 });
        fromRow.Children.Add(from);
        Grid.SetRow(fromRow, 1);
        root.Children.Add(fromRow);

        var toRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        toRow.Children.Add(new TextBlock { Text = "结束", Width = 42, VerticalAlignment = VerticalAlignment.Center, FontSize = 13 });
        toRow.Children.Add(to);
        Grid.SetRow(toRow, 2);
        root.Children.Add(toRow);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
        var okBtn = new Button { Content = "确定", Padding = new Thickness(16, 6, 16, 6), FontSize = 13, Background = new SolidColorBrush(Accent), Foreground = Brushes.White, BorderThickness = new Thickness(0) };
        var cancelBtn = new Button { Content = "取消", Padding = new Thickness(16, 6, 16, 6), FontSize = 13, Margin = new Thickness(8, 0, 0, 0), Background = new SolidColorBrush(Color.FromRgb(0xE4, 0xE4, 0xEA)), Foreground = new SolidColorBrush(Ink2), BorderThickness = new Thickness(0) };
        okBtn.Click += (_, _) =>
        {
            var f = from.SelectedDate ?? DateTime.Today.AddDays(-6);
            var t = to.SelectedDate ?? DateTime.Today;
            if (t > DateTime.Today) t = DateTime.Today;
            if (f > t) (f, t) = (t, f);
            _customFrom = f;
            _customTo = t;
            ok = true;
            win.Close();
        };
        cancelBtn.Click += (_, _) => win.Close();
        buttons.Children.Add(okBtn);
        buttons.Children.Add(cancelBtn);
        Grid.SetRow(buttons, 3);
        root.Children.Add(buttons);

        win.Content = root;
        ShowChildDialog(win);
        return ok;
    }

    // ── 底部按钮 ──

    /// <summary>保存 usage.json 的回调（由主窗口注入；只读演示模式下为空）。</summary>
    public Action? UsageSave { get; set; }

    private void OnManageBlacklist(object s, RoutedEventArgs e)
    {
        var win = new BlacklistWindow(this, _data);
        ShowChildDialog(win);
        RefreshView();
    }

    /// <summary>应用显示名 + 锁屏进程管理（改完立刻生效，历史数据也会跟着改名/并桶）。</summary>
    private void OnManageProcessMap(object s, RoutedEventArgs e)
    {
        if (_settings == null)
        {
            ShowInfo("提示", "这个诊断窗口不能改设置");
            return;
        }
        _config ??= _settings.LoadHotkeyConfig();
        var win = new ProcessMapWindow(this, _data, _config, _settings);
        ShowChildDialog(win);
        if (win.Saved)
        {
            // 新加的锁屏进程：把已记录的数据也挪进「锁屏」桶并落盘
            if (!_readOnly && _data.Normalize(UsageRules.IsLockProcess, out _, out _)) UsageSave?.Invoke();
            RefreshView();
        }
    }

    private void OnExport(object s, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "JSON 文件 (*.json)|*.json",
            DefaultExt = ".json",
            FileName = $"usage_{DateTime.Now:yyyy-MM-dd}.json"
        };
        if (ShowChildDialog(dialog) != true) return;
        var json = JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(dialog.FileName, json);
        ShowInfo("提示", "已导出");
    }

    private void OnImport(object s, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "JSON 文件 (*.json)|*.json",
            Title = "导入使用记录"
        };
        if (ShowChildDialog(dialog) != true) return;
        try
        {
            var json = File.ReadAllText(dialog.FileName);
            var imported = JsonSerializer.Deserialize<UsageData>(json);
            if (imported == null) { ShowInfo("提示", "文件格式错误"); return; }

            foreach (var (date, apps) in imported.Records)
            {
                foreach (var (key, rec) in apps)
                {
                    var hourly = rec.HourlyOrEmpty();
                    for (int h = 0; h < 24; h++)
                        if (hourly[h] > 0) _data.AddSeconds(date, key, rec.Name, hourly[h], h, rec.Path);
                    int rest = rec.Seconds - hourly.Sum();
                    if (rest > 0) _data.AddSeconds(date, key, rec.Name, rest, -1, rec.Path);
                }
            }
            _showAllApps = false;
            RefreshView();
            ShowInfo("提示", "导入完成");
        }
        catch (Exception ex)
        {
            ShowInfo("错误", $"导入失败: {ex.Message}", icon: InfoDialogIcon.Error);
        }
    }
}
