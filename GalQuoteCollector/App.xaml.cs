using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Hardcodet.Wpf.TaskbarNotification;

namespace GalQuoteCollector;

public partial class App : Application
{
    private static readonly Mutex _mutex = new(true, "GalQuoteCollector-SingleInstance");
    private TaskbarIcon? _trayIcon;
    private bool _startMinimized;
    private static void Log(string msg) => Services.AppLog.Write(msg);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);
    private const int SW_RESTORE = 9;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Log("=== Startup begin ===");

        // Log (and survive) UI-thread exceptions instead of dying silently
        DispatcherUnhandledException += (_, args) =>
        {
            Log($"UNHANDLED: {args.Exception}");
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log($"DOMAIN UNHANDLED: {args.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log($"TASK UNHANDLED: {args.Exception}");
            args.SetObserved();
        };

        // Single-instance check
        if (!_mutex.WaitOne(TimeSpan.Zero, true))
        {
            Log("Another instance already running — activating it and exiting.");
            // Find existing window and bring to front
            foreach (var p in Process.GetProcessesByName("Gal-quote-tool"))
            {
                if (p.MainWindowHandle != IntPtr.Zero)
                {
                    ShowWindowAsync(p.MainWindowHandle, SW_RESTORE);
                    SetForegroundWindow(p.MainWindowHandle);
                    break;
                }
            }
            // Also try FindWindow as fallback
            var hwnd = FindWindow(null, "Gal 语录收藏");
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

            // Diagnostic: --open-settings opens the settings dialog right after startup
            if (e.Args.Contains("--open-settings"))
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    Log("--open-settings: opening settings dialog");
                    try
                    {
                        if (MainWindow?.DataContext is ViewModels.MainViewModel vm)
                            vm.OpenSettingsCommand.Execute(null);
                    }
                    catch (Exception ex)
                    {
                        Log($"--open-settings failed: {ex}");
                    }
                }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }

            // Diagnostic: --open-usage opens the usage stats window right after startup
            if (e.Args.Contains("--open-usage"))
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    Log("--open-usage: opening usage stats window");
                    try
                    {
                        if (MainWindow?.DataContext is ViewModels.MainViewModel vm)
                            vm.OpenUsageStatsCommand.Execute(null);
                    }
                    catch (Exception ex)
                    {
                        Log($"--open-usage failed: {ex}");
                    }
                }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }

            // Diagnostic: --open-slideshow opens the slideshow (回想) with Magpie upscaling
            // forced on, so the F11 → Magpie hotkey path can be checked without touching settings.
            if (e.Args.Contains("--open-slideshow"))
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    Log("--open-slideshow: opening slideshow (Magpie upscale forced on)");
                    try
                    {
                        if (MainWindow?.DataContext is ViewModels.MainViewModel vm)
                            vm.OpenSlideshowForDiagnostics();
                    }
                    catch (Exception ex)
                    {
                        Log($"--open-slideshow failed: {ex}");
                    }
                }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }

            // Diagnostic: --fix-ttb runs the full boot repair path (shell wait → settle →
            // restart → pixel verification → one retry) and logs the result. Takes ~10-20s.
            if (e.Args.Contains("--fix-ttb"))
            {
                _ = Task.Run(async () =>
                {
                    Log("--fix-ttb: running TranslucentTB boot repair");
                    var (ok, detail) = await Services.TranslucentTbService.RepairAtBootAsync(2);
                    Log($"--fix-ttb: ok={ok} detail={detail}");
                });
            }

            // Diagnostic: --update-info logs the detected install form and the asset it would use
            if (e.Args.Contains("--update-info"))
            {
                var form = Services.UpdateService.DetectInstallForm();
                var asset = Services.UpdateService.PreferredAssetName(form);
                Log($"--update-info: form={form} preferredAsset={asset} " +
                    $"exe={Environment.ProcessPath} dir={AppContext.BaseDirectory}");
            }

            Log("=== Startup complete ===");
        }
        catch (Exception ex)
        {
            Log($"FATAL: {ex.GetType().Name}: {ex.Message}");
            if (!_startMinimized)
                Views.InfoDialog.Show(null, "错误", $"启动失败: {ex.Message}", icon: Views.InfoDialogIcon.Error);
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
                ToolTipText = "Gal 语录收藏",
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
