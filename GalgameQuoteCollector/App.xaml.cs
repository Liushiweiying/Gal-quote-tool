using System.IO;
using System.Windows;
using Hardcodet.Wpf.TaskbarNotification;

namespace GalgameQuoteCollector;

public partial class App : Application
{
    private TaskbarIcon? _trayIcon;
    private bool _startMinimized;
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GalgameQuoteCollector", "startup.log");

    private static void Log(string msg)
    {
        try
        {
            File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {msg}\n");
        }
        catch { }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Log("=== Startup begin ===");
        Log($"Args: {string.Join(", ", e.Args)}");

        try
        {
            _startMinimized = e.Args.Contains("--minimized");
            Log($"Minimized mode: {_startMinimized}");

            CreateTrayIcon();
            Log("Tray icon created");

            ShowMainWindow();
            Log("Main window initialized");

            Log("=== Startup complete ===");
        }
        catch (Exception ex)
        {
            Log($"FATAL: {ex.GetType().Name}: {ex.Message}");
            Log(ex.StackTrace ?? "");
            // Cannot show MessageBox during auto-start (no UI),
            // but during normal launch it's useful
            if (!_startMinimized)
            {
                MessageBox.Show($"启动失败: {ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
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
        catch (Exception ex)
        {
            Log($"Tray icon failed (non-critical): {ex.Message}");
        }
    }

    private void ShowMainWindow()
    {
        if (MainWindow == null || MainWindow.IsVisible == false)
        {
            if (MainWindow == null)
            {
                Log("Creating MainWindow...");
                MainWindow = new MainWindow();
                MainWindow.Closed += (_, _) => MainWindow = null;
                Log("MainWindow created");
            }

            if (_startMinimized)
            {
                Log("Auto-start: creating handle silently...");
                var helper = new System.Windows.Interop.WindowInteropHelper(MainWindow);
                _ = helper.EnsureHandle();
                _startMinimized = false;
                Log("Auto-start: handle created, window hidden");
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
            vm.OpenSettingsCommand.Execute(null);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        base.OnExit(e);
    }
}
