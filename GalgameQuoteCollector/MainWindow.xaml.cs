using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }
}
