using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace GalQuoteCollector.Views.Controls;

/// <summary>
/// 使用时长柱状图：下段蓝色 = 使用，上段绿色 = 锁屏；
/// 鼠标划过整列（不用对准细柱子）就高亮该列并在顶部预留区弹出数值气泡，点击可钉住。
/// 刻度标签按控件宽度自适应，窄窗口不会把 "00:00" 截断。
/// </summary>
public class UsageBarChart : FrameworkElement
{
    /// <summary>每根柱子：使用秒数 + 锁屏秒数。</summary>
    public IReadOnlyList<(int Active, int Locked)> Values { get; set; } = Array.Empty<(int, int)>();

    /// <summary>柱子下方短标签（可为空字符串）。</summary>
    public IReadOnlyList<string> Labels { get; set; } = Array.Empty<string>();

    /// <summary>气泡副标题（完整区间）。</summary>
    public IReadOnlyList<string> FullLabels { get; set; } = Array.Empty<string>();

    /// <summary>y 轴刻度文案（传入秒数）。</summary>
    public Func<int, string> AxisFormatter { get; set; } = s => $"{s / 60} min";

    /// <summary>气泡里的时长文案。</summary>
    public Func<int, string> DurationFormatter { get; set; } = FormatDuration;

    /// <summary>柱子含义（气泡第二行用，如 "今天采集" -> 用条数格式化）。</summary>
    public Func<(int Active, int Locked), string>? BubbleFormatter { get; set; }

    /// <summary>是否画绿色锁屏分段（采集图里没有）。</summary>
    public bool ShowLocked { get; set; } = true;

    public bool IsHourlyAxis { get; set; }

    private int _hoverIndex = -1;
    private int _pinnedIndex = -1;

    /// <summary>是否有被钉住的气泡（窗口用来决定 Esc/点空白是否要拦下来）。</summary>
    public bool IsPinned => _pinnedIndex >= 0;

    private static readonly Color ActiveColor = Color.FromRgb(0x5B, 0x6A, 0xBF);
    private static readonly Color ActiveDark = Color.FromRgb(0x2F, 0x3B, 0x8F);
    private static readonly Color ActiveDim = Color.FromRgb(0xC9, 0xCF, 0xEE);
    private static readonly Color LockedColor = Color.FromRgb(0x34, 0xA8, 0x53);
    private static readonly Color LockedDim = Color.FromRgb(0xC3, 0xE4, 0xCB);
    private static readonly Color Faint = Color.FromRgb(0xC7, 0xC7, 0xCC);
    private static readonly Color TrackColor = Color.FromRgb(0xEC, 0xEC, 0xF0);
    private static readonly Color BubbleBg = Color.FromRgb(0x22, 0x2A, 0x4A);

    private const double BubbleArea = 48;   // 顶部给气泡预留的高度
    private const double AxisHeight = 16;   // 底部标签
    private const double YAxisWidth = 44;   // 左侧刻度

    public UsageBarChart()
    {
        ClipToBounds = false;
        SnapsToDevicePixels = true;
        MouseMove += OnMouseMoveInternal;
        MouseLeave += (_, _) => { _hoverIndex = -1; InvalidateVisual(); };
        MouseLeftButtonDown += OnMouseDownInternal;
        SizeChanged += (_, _) => InvalidateVisual();
    }

    /// <summary>高度随宽度自适应（横屏更高，竖屏压扁一点）。</summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 520 : availableSize.Width;
        double height = Math.Clamp(width * 0.22, 112, 200) + BubbleArea + AxisHeight;
        return new Size(width, height);
    }

    private void OnMouseMoveInternal(object sender, MouseEventArgs e)
    {
        int idx = IndexAt(e.GetPosition(this));
        if (idx != _hoverIndex)
        {
            _hoverIndex = idx;
            InvalidateVisual();
        }
    }

    /// <summary>
    /// 让整块控件都可命中：FrameworkElement 默认只在"画出来的内容"上命中，
    /// 空白处（柱子之间、绘图区上方）会漏给下面的卡片，导致点击/悬停时有时无。
    /// </summary>
    protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters)
        => new PointHitTestResult(this, hitTestParameters.HitPoint);

    private void OnMouseDownInternal(object sender, MouseButtonEventArgs e)
    {
        // 点击用更严的命中：只有真的点在柱子上才算（柱子之间的空白 = 取消钉住）
        int idx = HitBarIndex(e.GetPosition(this));
        int before = _pinnedIndex;
        _pinnedIndex = idx < 0 ? -1 : (_pinnedIndex == idx ? -1 : idx);
        Services.AppLog.Write($"chart click: idx={idx} pinned {before}→{_pinnedIndex}");
        InvalidateVisual();
    }

    /// <summary>严格的柱子命中（点用）：x 要在柱宽内、y 要在柱高范围内，否则返回 -1。</summary>
    private int HitBarIndex(Point p)
    {
        int n = Values.Count;
        if (n == 0) return -1;

        double plotLeft = YAxisWidth;
        double plotWidth = ActualWidth - plotLeft;
        if (plotWidth <= 0) return -1;

        double plotTop = BubbleArea;
        double plotHeight = Math.Max(10, ActualHeight - BubbleArea - AxisHeight);
        if (p.Y < plotTop || p.Y > plotTop + plotHeight) return -1;

        double x = p.X - plotLeft;
        if (x < 0 || x > plotWidth) return -1;
        double slot = plotWidth / n;
        int idx = (int)(x / slot);
        if (idx < 0 || idx >= n) return -1;

        int max = 1;
        foreach (var v in Values) max = Math.Max(max, v.Active + v.Locked);
        var (active, locked) = Values[idx];
        int total = active + locked;
        if (total <= 0) return -1; // 这一列本来就没柱子

        double barWidth = Math.Max(2, Math.Min(26, slot * 0.56));
        double cx = plotLeft + slot * (idx + 0.5);
        if (Math.Abs(p.X - cx) > barWidth / 2 + 2) return -1; // 落在柱子左右两侧的空白

        double totalH = plotHeight * ((double)total / max);
        double barTop = plotTop + plotHeight - totalH;
        if (p.Y < barTop - 2) return -1; // 落在柱子顶部以上

        return idx;
    }

    /// <summary>点空白处取消钉住（窗口里调用）。</summary>
    public void Unpin()
    {
        if (_pinnedIndex < 0) return;
        _pinnedIndex = -1;
        InvalidateVisual();
    }

    private int IndexAt(Point p)
    {
        int n = Values.Count;
        if (n == 0) return -1;
        double plotWidth = ActualWidth - YAxisWidth;
        if (plotWidth <= 0) return -1;

        // 只有鼠标在「绘图区」里才弹气泡：纵向避开顶部气泡预留区和底部时间刻度条
        // （悬在时间文字上不该弹东西），横向要在绘图区内。
        double plotTop = BubbleArea;
        double plotHeight = Math.Max(10, ActualHeight - BubbleArea - AxisHeight);
        if (p.Y < plotTop || p.Y > plotTop + plotHeight) return -1;

        double x = p.X - YAxisWidth;
        if (x < 0 || x > plotWidth) return -1;
        double slot = plotWidth / n;
        int idx = (int)(x / slot);
        return idx >= 0 && idx < n ? idx : -1;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        int n = Values.Count;
        if (n == 0) return;

        int max = 1;
        foreach (var v in Values) max = Math.Max(max, v.Active + v.Locked);

        double plotLeft = YAxisWidth;
        double plotWidth = Math.Max(1, w - plotLeft);
        double plotTop = BubbleArea;
        double plotHeight = Math.Max(10, h - BubbleArea - AxisHeight);
        double slot = plotWidth / n;
        double barWidth = Math.Max(2, Math.Min(26, slot * 0.56));

        int highlight = _pinnedIndex >= 0 ? _pinnedIndex : _hoverIndex;

        // y 轴刻度线 + 文案
        var axisBrush = new SolidColorBrush(Faint);
        var trackBrush = new SolidColorBrush(TrackColor);
        var typeface = new Typeface("Segoe UI");
        for (int i = 0; i <= 2; i++)
        {
            double frac = i / 2.0;
            double y = plotTop + plotHeight * frac;
            dc.DrawLine(new Pen(trackBrush, 1), new Point(plotLeft, y), new Point(w, y));
            var label = FormatAxis(max - (int)Math.Round(max * frac));
            var ft = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                typeface, 10, axisBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(ft, new Point(plotLeft - 6 - ft.Width, y - ft.Height / 2));
        }

        // 柱子
        for (int i = 0; i < n; i++)
        {
            var (active, locked) = Values[i];
            int total = active + locked;
            if (total <= 0) continue;

            double cx = plotLeft + slot * (i + 0.5);
            double x = cx - barWidth / 2;
            double totalH = plotHeight * ((double)total / max);

            bool dim = highlight >= 0 && highlight != i;
            var activeColor = highlight == i ? ActiveDark : (dim ? ActiveDim : ActiveColor);
            var lockedColor = dim ? LockedDim : LockedColor;

            double activeH = total > 0 ? totalH * ((double)active / total) : 0;
            double lockedH = totalH - activeH;

            // 下段：使用（圆角在底部）
            if (activeH > 0.5)
            {
                dc.DrawRoundedRectangle(new SolidColorBrush(activeColor), null,
                    new Rect(x, plotTop + plotHeight - activeH, barWidth, activeH), 2, 2);
            }
            // 上段：锁屏（绿色，压在蓝色上面）
            if (ShowLocked && lockedH > 0.5)
            {
                dc.DrawRoundedRectangle(new SolidColorBrush(lockedColor), null,
                    new Rect(x, plotTop + plotHeight - totalH, barWidth, lockedH), 2, 2);
            }
        }

        // x 轴标签（按宽度选密度，首尾夹在绘图区内）
        var labelIndices = PickLabelIndices(n, plotWidth);
        foreach (var i in labelIndices)
        {
            if (i < 0 || i >= Labels.Count) continue;
            var text = Labels[i];
            if (string.IsNullOrEmpty(text)) continue;
            var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                typeface, 10, axisBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            double cx = plotLeft + slot * (i + 0.5);
            double tx = Math.Clamp(cx - ft.Width / 2, plotLeft, w - ft.Width);
            dc.DrawText(ft, new Point(tx, h - AxisHeight + 2));
        }

        // 气泡
        if (highlight >= 0 && highlight < n)
            DrawBubble(dc, highlight, typeface, plotLeft, slot, plotTop, plotHeight, max);
    }

    private void DrawBubble(DrawingContext dc, int index, Typeface typeface,
        double plotLeft, double slot, double plotTop, double plotHeight, int max)
    {
        var (active, locked) = Values[index];
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // 气泡第一行：优先用外部格式化（采集条数那种），否则给「使用 xx」
        string title = BubbleFormatter?.Invoke((active, locked))
            ?? (ShowLocked ? $"使用 {FormatDuration(active)}" : FormatDuration(active));
        string? sub1 = ShowLocked && locked > 0 ? $"锁屏 {FormatDuration(locked)}" : null;
        string? sub2 = index < FullLabels.Count ? FullLabels[index] : null;

        var titleFt = new FormattedText(title, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            typeface, 12.5, Brushes.White, dpi);
        FormattedText? sub1Ft = null, sub2Ft = null;
        var subBrush = new SolidColorBrush(Color.FromRgb(0xB9, 0xC6, 0xF0));
        if (sub1 != null)
            sub1Ft = new FormattedText(sub1, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                typeface, 10.5, new SolidColorBrush(Color.FromRgb(0x9B, 0xE0, 0xB0)), dpi);
        if (sub2 != null)
            sub2Ft = new FormattedText(sub2, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                typeface, 10.5, subBrush, dpi);

        double contentW = Math.Max(titleFt.Width, Math.Max(sub1Ft?.Width ?? 0, sub2Ft?.Width ?? 0));
        double lineH = 14.5;
        double contentH = titleFt.Height + (sub1Ft != null ? lineH : 0) + (sub2Ft != null ? lineH : 0);
        const double padX = 10, padY = 6;
        double bw = contentW + padX * 2;
        double bh = contentH + padY * 2;

        // 贴在选中柱子顶端上方（和 iOS 一样跟着柱高走），并保证不越界
        int total = active + locked;
        double barTop = plotTop + plotHeight - (total > 0 ? plotHeight * ((double)total / max) : 0);
        double by = Math.Clamp(barTop - bh - 9, 0, Math.Max(0, ActualHeight - bh - 1));

        double cx = plotLeft + slot * (index + 0.5);
        double bx = Math.Clamp(cx - bw / 2, 0, Math.Max(0, ActualWidth - bw));

        var bg = new SolidColorBrush(BubbleBg);
        dc.DrawRoundedRectangle(bg, null, new Rect(bx, by, bw, bh), 9, 9);

        // 小尖角（指向柱子）
        double tailX = Math.Clamp(cx, bx + 12, bx + bw - 12);
        var tail = new StreamGeometry();
        using (var ctx = tail.Open())
        {
            ctx.BeginFigure(new Point(tailX - 5, by + bh - 1), true, true);
            ctx.LineTo(new Point(tailX + 5, by + bh - 1), true, false);
            ctx.LineTo(new Point(tailX, by + bh + 5), true, false);
        }
        tail.Freeze();
        dc.DrawGeometry(bg, null, tail);

        double ty = by + padY;
        dc.DrawText(titleFt, new Point(bx + padX, ty));
        ty += titleFt.Height;
        if (sub1Ft != null) { dc.DrawText(sub1Ft, new Point(bx + padX, ty)); ty += lineH; }
        if (sub2Ft != null) { dc.DrawText(sub2Ft, new Point(bx + padX, ty)); }
    }

    /// <summary>按可用宽度决定显示哪些 x 标签：窄了只留 0/12/24，宽了每 3 小时一个。</summary>
    private List<int> PickLabelIndices(int n, double plotWidth)
    {
        var result = new List<int>();
        if (n <= 0) return result;

        if (IsHourlyAxis && n == 24)
        {
            int step = plotWidth >= 620 ? 3 : plotWidth >= 380 ? 6 : 12;
            for (int i = 0; i < 24; i += step) result.Add(i);
            result.Add(23);
            return result;
        }

        // 非小时轴：按每 ~54px 放一个标签估算步长
        int maxLabels = Math.Max(2, (int)(plotWidth / 54));
        int stride = Math.Max(1, (int)Math.Ceiling(n / (double)maxLabels));
        for (int i = 0; i < n; i += stride) result.Add(i);
        if (result.Count == 0 || result[^1] != n - 1) result.Add(n - 1);
        return result;
    }

    private string FormatAxis(int seconds) => AxisFormatter(seconds);

    public static string FormatDuration(int seconds)
    {
        if (seconds <= 0) return "0 分钟";
        int mins = (int)Math.Round(seconds / 60.0);
        if (mins < 60) return $"{mins} 分钟";
        int h = mins / 60, m = mins % 60;
        return m == 0 ? $"{h} 小时" : $"{h} 小时 {m} 分";
    }
}
