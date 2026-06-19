using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GalgameQuoteCollector.Models;
using GalgameQuoteCollector.ViewModels;

namespace GalgameQuoteCollector;

public partial class MainWindow : Window
{
    /// <summary>
    /// Set to true when the app is actually shutting down (tray exit).
    /// </summary>
    public bool ForceClose { get; set; }

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel(this);

        // Hide instead of close when clicking ×
        Closing += OnClosing;
    }

    private void OnFilterMenu(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        var menu = FilterMenu;
        menu.Items.Clear();

        // ── Sort ──
        var sortMenu = new MenuItem { Header = "排序方式" };
        sortMenu.Items.Add(MakeRadioItem("按时间", vm.SortMode == 0, () => vm.SortMode = 0));
        sortMenu.Items.Add(MakeRadioItem("按游戏名", vm.SortMode == 1, () => vm.SortMode = 1));
        sortMenu.Items.Add(MakeRadioItem("按相关度", vm.SortMode == 2, () => vm.SortMode = 2));
        menu.Items.Add(sortMenu);

        // ── Game ──
        var gameMenu = new MenuItem { Header = "游戏" };
        foreach (var g in vm.AvailableGamesForFilter)
        {
            var isSelected = g.Id == vm.SelectedGameFilter;
            var item = MakeRadioItem(g.Name, isSelected, () => vm.SelectedGameFilter = g.Id);
            gameMenu.Items.Add(item);
        }
        menu.Items.Add(gameMenu);

        // ── Group ──
        var groupMenu = new MenuItem { Header = "分组" };
        foreach (var g in vm.AvailableGroups)
        {
            var isSelected = g.Id == vm.SelectedGroupFilter;
            var item = MakeRadioItem(g.Name, isSelected, () => vm.SelectedGroupFilter = g.Id);
            groupMenu.Items.Add(item);
        }
        menu.Items.Add(groupMenu);

        // ── Tag ──
        var tagMenu = new MenuItem { Header = "标签" };
        foreach (var t in vm.AvailableTagsForFilter)
        {
            var isSelected = t.Id == vm.SelectedTagFilter;
            var item = MakeRadioItem(t.Name, isSelected, () => vm.SelectedTagFilter = t.Id);
            tagMenu.Items.Add(item);
        }
        menu.Items.Add(tagMenu);

        // Show
        menu.PlacementTarget = sender as System.Windows.UIElement;
        menu.IsOpen = true;
    }

    private static MenuItem MakeRadioItem(string text, bool isChecked, Action onClick)
    {
        var item = new MenuItem
        {
            Header = text,
            IsCheckable = true,
            IsChecked = isChecked,
            StaysOpenOnClick = false
        };
        item.Click += (_, _) => onClick();
        return item;
    }

    private void OnMoreMenu(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement el && el.ContextMenu != null)
        {
            el.ContextMenu.PlacementTarget = el;
            el.ContextMenu.IsOpen = true;
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!ForceClose)
        {
            e.Cancel = true;
            Hide();
        }
    }

    private void OnBatchDelete(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var listBox = FindVisualChild<ListBox>(this);
        if (listBox == null) return;

        var selected = listBox.SelectedItems.Cast<Quote>().ToList();
        if (selected.Count < 2)
        {
            MessageBox.Show("按住 Ctrl 或 Shift 多选后再删除", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show($"确定删除选中的 {selected.Count} 条语录？", "批量删除",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        foreach (var q in selected.ToList())
        {
            vm.DeleteQuoteDirect(q);
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) return found;
            var foundChild = FindVisualChild<T>(child);
            if (foundChild != null) return foundChild;
        }
        return null;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Delete && DataContext is MainViewModel vm && vm.SelectedQuote != null)
        {
            var result = MessageBox.Show("确定要删除这条语录吗？", "确认删除",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
                vm.DeleteQuoteDirect(vm.SelectedQuote);
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }
}
