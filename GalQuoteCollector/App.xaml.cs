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

            // 触摸板/滚轮滚动手感：统一半速（对所有窗口生效）
            Services.SmoothScroll.Register();

            // 以前开机自启时会把整个进程设成 High 优先级（想让它响应热键更快）。
            // 这是坏做法：常驻进程占 High 会跟游戏抢 CPU，而 OCR/截图本身很吃 CPU，
            // 热键延迟也主要取决于低级钩子而不是优先级。现在保持默认（Normal）。
            Log($"Priority: {System.Diagnostics.Process.GetCurrentProcess().PriorityClass}（默认，不再提升）");

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

            // Diagnostic: --usage-demo 用「真数据 + 注入的锁屏样例」打开使用时间页（只读、不落盘），
            // 用来在没有真正锁屏的情况下检查绿色锁屏分段 / 多时间段渲染
            if (e.Args.Contains("--usage-demo"))
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    Log("--usage-demo: opening usage stats window with demo data (read-only)");
                    try
                    {
                        if (MainWindow?.DataContext is ViewModels.MainViewModel vm)
                            vm.OpenUsageStatsDemo();
                    }
                    catch (Exception ex)
                    {
                        Log($"--usage-demo failed: {ex}");
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

            // Diagnostic: --fix-lock <yyyy-MM-dd> <进程名> [起始小时 结束小时]
            // 把某天（可选小时范围）误记的锁屏时长改记到指定应用；结果写日志
            int fixIdx = Array.IndexOf(e.Args, "--fix-lock");
            if (fixIdx >= 0 && e.Args.Length > fixIdx + 2)
            {
                var date = e.Args[fixIdx + 1];
                var key = e.Args[fixIdx + 2];
                int fromHour = 0, toHour = 23;
                if (e.Args.Length > fixIdx + 4)
                {
                    int.TryParse(e.Args[fixIdx + 3], out fromHour);
                    int.TryParse(e.Args[fixIdx + 4], out toHour);
                }
                var dataDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "GalQuoteCollector");
                var (ok, detail) = Services.UsageRepair.MoveLockToApp(dataDir, date, key, fromHour, toHour);
                Log($"--fix-lock: ok={ok} {detail}");
            }

            // Diagnostic: --move-usage <yyyy-MM-dd> <源进程> <目标进程> [起始小时 结束小时]
            // 把某天（可选小时范围）某个应用的时长改记到另一个应用；结果写日志
            int moveIdx = Array.IndexOf(e.Args, "--move-usage");
            if (moveIdx >= 0 && e.Args.Length > moveIdx + 3)
            {
                var date = e.Args[moveIdx + 1];
                var fromKey = e.Args[moveIdx + 2];
                var toKey = e.Args[moveIdx + 3];
                int fromHour = 0, toHour = 23;
                if (e.Args.Length > moveIdx + 5)
                {
                    int.TryParse(e.Args[moveIdx + 4], out fromHour);
                    int.TryParse(e.Args[moveIdx + 5], out toHour);
                }
                var dataDir2 = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "GalQuoteCollector");
                var (ok2, detail2) = Services.UsageRepair.MoveUsage(dataDir2, date, fromKey, toKey, fromHour, toHour);
                Log($"--move-usage: ok={ok2} {detail2}");
            }

            // Diagnostic: --crop-bars <文件或目录> [输出目录] 测试裁黑边（不改原文件，输出 *-crop.png）
            int cropIdx = Array.IndexOf(e.Args, "--crop-bars");
            if (cropIdx >= 0 && e.Args.Length > cropIdx + 1)
            {
                var src = e.Args[cropIdx + 1];
                var outDir = e.Args.Length > cropIdx + 2 ? e.Args[cropIdx + 2] : null;
                var files = Directory.Exists(src)
                    ? Directory.GetFiles(src, "*.png").Concat(Directory.GetFiles(src, "*.jpg")).ToArray()
                    : new[] { src };
                foreach (var f in files)
                {
                    var target = outDir == null
                        ? Path.Combine(Path.GetDirectoryName(f) ?? ".", Path.GetFileNameWithoutExtension(f) + "-crop.png")
                        : Path.Combine(outDir, Path.GetFileNameWithoutExtension(f) + "-crop.png");
                    var (cropped, detail) = Services.BlackBarCropper.Crop(f, target);
                    Log($"--crop-bars: {detail}");
                }
            }

            // Diagnostic: --bars-test <文件> <模式0/1/2> [输出目录] 验证回想的白底显示处理（不改原文件）
            int barsIdx = Array.IndexOf(e.Args, "--bars-test");
            if (barsIdx >= 0 && e.Args.Length > barsIdx + 2)
            {
                try
                {
                    var file = e.Args[barsIdx + 1];
                    int mode = int.Parse(e.Args[barsIdx + 2]);
                    var outDir = e.Args.Length > barsIdx + 3 ? e.Args[barsIdx + 3] : Path.GetDirectoryName(file)!;
                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(file);
                    bmp.EndInit();
                    var result = Views.Controls.SlideshowImageBars.Apply(bmp, mode);
                    var name = $"{Path.GetFileNameWithoutExtension(file)}-bars{mode}.png";
                    var target = Path.Combine(outDir, name);
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(result));
                    using (var fs = File.Create(target)) encoder.Save(fs);
                    Log($"--bars-test: {Path.GetFileName(file)} mode={Views.Controls.SlideshowImageBars.ModeName(mode)} " +
                        $"{bmp.PixelWidth}x{bmp.PixelHeight} → {result.PixelWidth}x{result.PixelHeight} 保存 {name}");
                }
                catch (Exception ex) { Log($"--bars-test failed: {ex.Message}"); }
            }

            // Diagnostic: --magpie-log-test 打印程序读到的 Magpie 日志尾部与判定结果
            if (e.Args.Contains("--magpie-log-test"))
            {
                try
                {
                    var lp = Services.MagpieService.TryFindLogPath(null);
                    Log($"--magpie-log-test: log={lp ?? "<null>"} size={Services.MagpieService.LogSize(lp)} " +
                        $"active={Services.MagpieService.IsScalingActive(lp)}");
                    if (lp != null && File.Exists(lp))
                    {
                        using var fsx = new FileStream(lp, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        long start = Math.Max(0, fsx.Length - 256 * 1024);
                        fsx.Seek(start, SeekOrigin.Begin);
                        using var rdr = new StreamReader(fsx, System.Text.Encoding.UTF8);
                        var text = rdr.ReadToEnd();
                        Log($"   readLen={text.Length} lastStart={text.LastIndexOf("缩放开始", StringComparison.Ordinal)} " +
                            $"lastEnd={text.LastIndexOf("缩放结束", StringComparison.Ordinal)}");
                        var tail = text.Length > 260 ? text[^260..] : text;
                        Log("   tail=" + tail.Replace("\r", "").Replace("\n", " ⏎ "));
                    }
                }
                catch (Exception ex) { Log($"--magpie-log-test failed: {ex.Message}"); }
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
