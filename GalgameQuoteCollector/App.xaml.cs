using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Hardcodet.Wpf.TaskbarNotification;

namespace GalgameQuoteCollector;

public partial class App : Application
{
    private static readonly Mutex _mutex = new(true, "GalgameQuoteCollector-SingleInstance");
    private TaskbarIcon? _trayIcon;
    private bool _startMinimized;
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GalgameQuoteCollector", "startup.log");

    private static void Log(string msg)
    {
        try { File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {msg}\n"); }
        catch { }
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);
    private const int SW_RESTORE = 9;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Log("=== Startup begin ===");

        // Single-instance check
        if (!_mutex.WaitOne(TimeSpan.Zero, true))
        {
            Log("Another instance already running — activating it and exiting.");
            // Find existing window and bring to front
            foreach (var p in Process.GetProcessesByName("GalgameQuoteCollector"))
            {
                if (p.MainWindowHandle != IntPtr.Zero)
                {
                    ShowWindowAsync(p.MainWindowHandle, SW_RESTORE);
                    SetForegroundWindow(p.MainWindowHandle);
                    break;
                }
            }
            // Also try FindWindow as fallback
            var hwnd = FindWindow(null, "Galgame 语录收藏");
            if (hwnd != IntPtr.Zero)
            {
                ShowWindowAsync(hwnd, SW_RESTORE);
                SetForegroundWindow(hwnd);
            }
            Shutdown();
            return;
        }

        // Save usage data on system shutdown / logoff
        SessionEnding += (_, _) => SaveUsageData();

        try
        {
            _startMinimized = e.Args.Contains("--minimized");
            Log($"Minimized mode: {_startMinimized}");

            // High priority for auto-start scenarios (faster capture hotkey response)
            if (_startMinimized)
            {
                try
                {
                    using var proc = System.Diagnostics.Process.GetCurrentProcess();
                    proc.PriorityClass = System.Diagnostics.ProcessPriorityClass.High;
                    Log("Priority set to High");
                }
                catch (Exception ex)
                {
                    Log($"Failed to set priority: {ex.Message}");
                }
            }

            CreateTrayIcon();
            Log("Tray icon created");

            ShowMainWindow();
            Log("Main window initialized");

            Log("=== Startup complete ===");
        }
        catch (Exception ex)
        {
            Log($"FATAL: {ex.GetType().Name}: {ex.Message}");
            if (!_startMinimized)
                MessageBox.Show($"启动失败: {ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);

    private void CreateTrayIcon()
    {
        try
        {
            System.Drawing.Icon? trayIcon = null;
            try
            {
                var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (exePath != null)
                    trayIcon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
            }
            catch { }

            _trayIcon = new TaskbarIcon
            {
                ToolTipText = "Galgame 语录收藏",
                Icon = trayIcon,
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

    private void SaveUsageData()
    {
        if (MainWindow?.DataContext is ViewModels.MainViewModel vm)
            vm.SaveUsageDataNow();
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
                var helper = new System.Windows.Interop.WindowInteropHelper(MainWindow);
                _ = helper.EnsureHandle();
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
            vm.OpenSettingsCommand.Execute(null);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        _mutex.Dispose();
        base.OnExit(e);
    }
}
