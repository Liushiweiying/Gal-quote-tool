using System.ComponentModel;
using System.IO;
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

        // Use the exe's icon for the window title bar / taskbar
        SetWindowIcon();

        // Hide instead of close when clicking ×
        Closing += OnClosing;
    }

    private void SetWindowIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (string.IsNullOrEmpty(path)) return;
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon == null) return;
            Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle,
                System.Windows.Int32Rect.Empty,
                System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
        }
        catch { }
    }

    private void OnClearSearch(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.Focus();
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;

        foreach (var f in files)
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            if (ext is ".md" or ".json" or ".zip")
                vm.ImportFile(f);
        }
        e.Handled = true;
    }

    private void OnShowSlideshowHelp(object sender, RoutedEventArgs e)
    {
        Views.InfoDialog.Show(this, "回想教程",
            "回想窗口使用说明：\n\n" +
            "- 打开：点击工具栏「回想」，展示当前筛选结果\n" +
            "- 翻页：← / → 或 PageUp / PageDown 或 空格\n" +
            "- 随机跳转：按 Enter\n" +
            "- 全屏：按 F11；全屏下点左键下一页、点右键上一页\n" +
            "- 置顶：按 F2\n" +
            "- 循环：按 R，或点击顶部的「循环」开关\n" +
            "- 筛选：左下角选择分组 / 标签，「全部」为不过滤\n" +
            "- 多截图：一条语录有多张截图时，继续按 → 会依次切换\n" +
            "- 关闭：Esc 或点击右上角 ×");
    }

    private void OnShowBatchHelp(object sender, RoutedEventArgs e)
    {
        Views.InfoDialog.Show(this, "批量操作教程",
            "批量操作说明：\n\n" +
            "- 多选：在列表按住 Ctrl 或 Shift 点击多条语录\n" +
            "- 批量删除：多选后点「···」菜单 → 批量删除\n" +
            "  · 会询问是否同时删除截图文件，文件移入回收站可恢复\n" +
            "- 撤销删除：删除后点击工具栏「撤销」，可恢复最近 20 条（含标签、分组、截图关联）\n" +
            "- 网格视图：右下角按钮或「···」菜单切换，双击卡片返回列表");
    }

    private void OnShowImportExportHelp(object sender, RoutedEventArgs e)
    {
        Views.InfoDialog.Show(this, "导入导出教程",
            "导入导出说明：\n\n" +
            "- 导出全部：··· 菜单 → 导出全部（Markdown / JSON）\n" +
            "- 导出单条：选中语录后点详情区「导出」\n" +
            "- 导出分组：··· 菜单 → 导出分组（需先在筛选中选择分组）\n" +
            "- 打包导出：··· 菜单 → 打包导出，语录 + 截图 + 设置打包为 zip\n" +
            "- 打包导入：··· 菜单 → 打包导入，读取 zip 恢复语录和截图\n" +
            "- 直接导入：··· 菜单 → 导入（.md / .json）\n" +
            "- 拖拽导入：把 .md / .json / .zip 文件直接拖进主窗口\n" +
            "- 打包导入时若 zip 内含 settings.json / usage.json，会先询问是否恢复");
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
        // Only left-click opens the image; right-click shows the context menu instead
        if (e.ChangedButton != MouseButton.Left) return;
        if (sender is not Image image) return;
        if (image.DataContext is not Screenshot screenshot) return;
        if (DataContext is not MainViewModel vm) return;
        vm.OpenScreenshotFileCommand.Execute(screenshot);
    }

    // ContextMenu items can't resolve RelativeSource AncestorType bindings (they live in
    // a separate popup visual tree), so these use Click handlers + the MenuItem DataContext.
    private void OnScreenshotMenuOpen(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.DataContext is Screenshot ss && DataContext is MainViewModel vm)
            vm.OpenScreenshotFileCommand.Execute(ss);
    }
    private void OnScreenshotMenuOpenFolder(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.DataContext is Screenshot ss && DataContext is MainViewModel vm)
            vm.OpenScreenshotFolderCommand.Execute(ss);
    }
    private void OnScreenshotMenuCopyPath(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.DataContext is Screenshot ss && DataContext is MainViewModel vm)
            vm.CopyScreenshotPathCommand.Execute(ss);
    }
    private void OnScreenshotMenuCopyImage(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.DataContext is Screenshot ss && DataContext is MainViewModel vm)
            vm.CopyScreenshotImageCommand.Execute(ss);
    }
    private void OnScreenshotMenuDelete(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.DataContext is Screenshot ss && DataContext is MainViewModel vm)
            vm.DeleteScreenshotCommand.Execute(ss);
    }
    private void OnQuoteMenuCopy(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.CopyQuoteTextCommand.Execute(null);
    }
    private void OnQuoteMenuDelete(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.DataContext is Quote q && DataContext is MainViewModel vm)
            vm.DeleteQuoteDirectCommand.Execute(q);
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
            Views.InfoDialog.Show(this, "提示", "按住 Ctrl 或 Shift 多选后再删除");
            return;
        }

        var result = Views.InfoDialog.Show(this, "批量删除", $"确定删除选中的 {selected.Count} 条语录？",
            Views.InfoDialogButtons.YesNo, Views.InfoDialogIcon.Question, dangerConfirm: true);
        if (result != Views.InfoDialogResult.Yes) return;

        // Consistent with single-quote deletion: ask whether screenshot files go too
        var delSs = Views.InfoDialog.Show(this, "删除截图？",
            "是否同时删除这些语录的截图文件？（移入回收站）\n「否」= 保留截图文件",
            Views.InfoDialogButtons.YesNo, Views.InfoDialogIcon.Question) == Views.InfoDialogResult.Yes;

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
            var result = Views.InfoDialog.Show(this, "确认删除", "确定要删除这条语录吗？",
                Views.InfoDialogButtons.YesNo, Views.InfoDialogIcon.Question, dangerConfirm: true);
            if (result == Views.InfoDialogResult.Yes)
                vm.DeleteQuoteDirect(vm.SelectedQuote);
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }
}
