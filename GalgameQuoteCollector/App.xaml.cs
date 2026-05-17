using System.Windows;
using Hardcodet.Wpf.TaskbarNotification;

namespace GalgameQuoteCollector;

public partial class App : Application
{
    private TaskbarIcon? _trayIcon;
    private bool _startMinimized;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Check for --minimized flag (auto-start)
        _startMinimized = e.Args.Contains("--minimized");

        try
        {
            // Create tray icon first
            CreateTrayIcon();

            // Create and show main window (minimized if auto-start)
            ShowMainWindow();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"启动失败: {ex.Message}\n\n{ex.GetType().Name}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    private void CreateTrayIcon()
    {
        try
        {
            _trayIcon = new TaskbarIcon
            {
                ToolTipText = "Galgame 语录收藏",
                Visibility = Visibility.Visible
            };

            _trayIcon.ContextMenu = new System.Windows.Controls.ContextMenu();

            var openItem = new System.Windows.Controls.MenuItem { Header = "打开主窗口" };
            openItem.Click += (_, _) => ShowMainWindow();
            _trayIcon.ContextMenu.Items.Add(openItem);

            var settingsItem = new System.Windows.Controls.MenuItem { Header = "设置" };
            settingsItem.Click += (_, _) => ShowMainWindowAndOpenSettings();
            _trayIcon.ContextMenu.Items.Add(settingsItem);

            _trayIcon.ContextMenu.Items.Add(new System.Windows.Controls.Separator());

            var exitItem = new System.Windows.Controls.MenuItem { Header = "退出" };
            exitItem.Click += (_, _) =>
            {
                if (MainWindow is MainWindow w) w.ForceClose = true;
                Shutdown();
            };
            _trayIcon.ContextMenu.Items.Add(exitItem);

            _trayIcon.TrayMouseDoubleClick += (_, _) => ShowMainWindow();
        }
        catch
        {
            // Tray icon not critical
        }
    }

    private void ShowMainWindow()
    {
        if (MainWindow == null || MainWindow.IsVisible == false)
        {
            if (MainWindow == null)
            {
                MainWindow = new MainWindow();
                MainWindow.Closed += (_, _) => MainWindow = null;
            }

            if (_startMinimized)
            {
                // Don't show window at all - tray icon only
                MainWindow.ShowInTaskbar = false;
                MainWindow.WindowState = WindowState.Minimized;
                MainWindow.Show();
                MainWindow.Hide();
                _startMinimized = false;
            }
            else
            {
                MainWindow.Show();
                MainWindow.WindowState = WindowState.Normal;
                MainWindow.Activate();
            }
        }
        else
        {
            MainWindow.WindowState = WindowState.Normal;
            MainWindow.Activate();
        }
    }

    private void ShowMainWindowAndOpenSettings()
    {
        ShowMainWindow();
        if (MainWindow?.DataContext is ViewModels.MainViewModel vm)
        {
            vm.OpenSettingsCommand.Execute(null);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        base.OnExit(e);
    }
}
