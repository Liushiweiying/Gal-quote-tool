using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace GalQuoteCollector.Services;

/// <summary>
/// 滚轮/触摸板滚动手感：WPF 默认一个滚轮格就滚 3 行，触摸板两指滑动一下容易"飞太远"。
/// 这里统一把滚动量减半，并且按像素平滑滚动（不再按行跳）。注册一次即可覆盖所有窗口。
/// </summary>
public static class SmoothScroll
{
    /// <summary>滚动系数：0.5 = 半速（用户觉得太快，默认取一半）。</summary>
    private const double Factor = 0.5;

    private static bool _registered;

    public static void Register()
    {
        if (_registered) return;
        _registered = true;

        // 类级别处理器：任何一个 Window 上的滚轮事件都会先走这里
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(OnPreviewMouseWheel), true);
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || e.Delta == 0) return;

        var sv = FindScrollViewer(e.OriginalSource as DependencyObject);
        if (sv == null) return;

        // 有横向滚动条且按住 Shift 时滚横向
        bool horizontal = (Keyboard.Modifiers & ModifierKeys.Shift) != 0
                          && sv.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled
                          && sv.ScrollableWidth > 0;

        double delta = e.Delta * Factor;
        if (horizontal)
        {
            if (sv.ScrollableWidth <= 0) return;
            sv.ScrollToHorizontalOffset(sv.HorizontalOffset - delta);
        }
        else
        {
            if (sv.ScrollableHeight <= 0) return;
            sv.ScrollToVerticalOffset(sv.VerticalOffset - delta);
        }
        e.Handled = true;
    }

    /// <summary>找鼠标下面最近的可滚动 ScrollViewer（找到就归它滚）。</summary>
    private static ScrollViewer? FindScrollViewer(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is ScrollViewer sv && sv.ScrollableHeight > 0) return sv;
            // 遇到嵌套滚动：优先内层，但内层滚不动时交给外层
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : null;
        }
        return null;
    }
}
