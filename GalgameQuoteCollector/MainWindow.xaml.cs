using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
