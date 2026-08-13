using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GalQuoteCollector.Models;
using GalQuoteCollector.ViewModels;

namespace GalQuoteCollector;

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

        // ── Game (multi-select, click to toggle, right-click to exclude) ──
        var gameMenu = new MenuItem { Header = "游戏" };
        var gameExcludeItem = new MenuItem
        {
            Header = vm.GameFilterExclude ? "✓ 排除模式" : "  排除模式",
            IsCheckable = true, IsChecked = vm.GameFilterExclude,
            StaysOpenOnClick = true
        };
        gameExcludeItem.Click += (_, _) => { vm.GameFilterExclude = !vm.GameFilterExclude; };
        gameMenu.Items.Add(gameExcludeItem);
        gameMenu.Items.Add(new Separator());
        foreach (var g in vm.AvailableGamesForFilter)
        {
            var id = g.Id;
            var isSelected = vm.SelectedGameFilters.Contains(id);
            var item = new MenuItem { Header = g.Name, IsCheckable = true, IsChecked = isSelected, StaysOpenOnClick = true };
            item.Click += (_, _) =>
            {
                if (vm.SelectedGameFilters.Contains(id))
                    vm.SelectedGameFilters.Remove(id);
                else
                    vm.SelectedGameFilters.Add(id);
                vm.RefreshQuotes();
            };
            gameMenu.Items.Add(item);
        }
        menu.Items.Add(gameMenu);

        // ── Group (multi-select, click to toggle, right-click to exclude) ──
        var groupMenu = new MenuItem { Header = "分组" };
        var groupExcludeItem = new MenuItem
        {
            Header = vm.GroupFilterExclude ? "✓ 排除模式" : "  排除模式",
            IsCheckable = true, IsChecked = vm.GroupFilterExclude,
            StaysOpenOnClick = true
        };
        groupExcludeItem.Click += (_, _) => { vm.GroupFilterExclude = !vm.GroupFilterExclude; };
        groupMenu.Items.Add(groupExcludeItem);
        groupMenu.Items.Add(new Separator());
        foreach (var g in vm.AvailableGroups)
        {
            var id = g.Id;
            var isSelected = vm.SelectedGroupFilters.Contains(id);
            var item = new MenuItem { Header = g.Name, IsCheckable = true, IsChecked = isSelected, StaysOpenOnClick = true };
            item.Click += (_, _) =>
            {
                if (vm.SelectedGroupFilters.Contains(id))
                    vm.SelectedGroupFilters.Remove(id);
                else
                    vm.SelectedGroupFilters.Add(id);
                vm.RefreshQuotes();
            };
            groupMenu.Items.Add(item);
        }
        menu.Items.Add(groupMenu);

        // ── Tag (multi-select, click to toggle, right-click to exclude) ──
        var tagMenu = new MenuItem { Header = "标签" };
        var tagExcludeItem = new MenuItem
        {
            Header = vm.TagFilterExclude ? "✓ 排除模式" : "  排除模式",
            IsCheckable = true, IsChecked = vm.TagFilterExclude,
            StaysOpenOnClick = true
        };
        tagExcludeItem.Click += (_, _) => { vm.TagFilterExclude = !vm.TagFilterExclude; };
        tagMenu.Items.Add(tagExcludeItem);
        tagMenu.Items.Add(new Separator());
        foreach (var t in vm.AvailableTagsForFilter)
        {
            var id = t.Id;
            var isSelected = vm.SelectedTagFilters.Contains(id);
            var item = new MenuItem { Header = t.Name, IsCheckable = true, IsChecked = isSelected, StaysOpenOnClick = true };
            item.Click += (_, _) =>
            {
                if (vm.SelectedTagFilters.Contains(id))
                    vm.SelectedTagFilters.Remove(id);
                else
                    vm.SelectedTagFilters.Add(id);
                vm.RefreshQuotes();
            };
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

    private void OnScreenshotPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Image image) return;
        if (image.DataContext is not Screenshot screenshot) return;
        if (DataContext is not MainViewModel vm) return;
        vm.OpenScreenshotFileCommand.Execute(screenshot);
    }

    private void OnGridListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        // Switch back to list view and auto-select the double-clicked item
        vm.IsGridView = false;
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

        // Consistent with single-quote deletion: ask whether screenshot files go too
        var delSs = MessageBox.Show("是否同时删除这些语录的截图文件？（移入回收站）\n「否」= 保留截图文件", "删除截图？",
            MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

        vm.DeleteQuotes(selected, delSs);
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
