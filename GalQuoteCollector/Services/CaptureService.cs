using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace GalQuoteCollector.Services;

/// <summary>
/// 截图方式：
///   auto   — 自动（无边框全屏 / 占据显示器 ≥90% 时抓该显示器，否则抓窗口可见区域）
///   window — 抓窗口自身渲染内容（PrintWindow + PW_RENDERFULLCONTENT）
///            —— 得到游戏原生分辨率的画面，串流窗口、minori 等特殊引擎更可靠
///   region — 抓窗口在屏幕上的可见区域（DWM 真实边框）
///   monitor— 抓窗口所在显示器的完整画面
///   screen — 抓整个虚拟屏幕（所有显示器）
/// </summary>
public enum CaptureMode { Auto, WindowContent, WindowRegion, Monitor, VirtualScreen }

/// <summary>Result of a capture: where it was saved and what was actually grabbed.</summary>
public class CaptureResult
{
    public string FilePath { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>True when the window's own render buffer was used (native resolution).</summary>
    public bool FromWindowContent { get; set; }
    public string ModeLabel { get; set; } = "";
}

public class CaptureService
{
    public string ScreenshotDir { get; }

    public CaptureService(string screenshotDir)
    {
        ScreenshotDir = screenshotDir;
        Directory.CreateDirectory(screenshotDir);
    }

    /// <summary>
    /// 截图方式解析。空串/未知值一律落到「当前显示器」——这是 v1.2.5 起的默认值，
    /// 也兼容更早版本 settings.json 里没写过 CaptureMode 的空值。
    /// </summary>
    public static CaptureMode ParseMode(string? mode) => (mode ?? "").Trim().ToLowerInvariant() switch
    {
        "window" => CaptureMode.WindowContent,
        "region" => CaptureMode.WindowRegion,
        "monitor" => CaptureMode.Monitor,
        "screen" => CaptureMode.VirtualScreen,
        "auto" => CaptureMode.Auto,
        _ => CaptureMode.Monitor
    };

    /// <summary>
    /// Capture the specified window. Pass a saved handle to capture the game even after
    /// our window minimizes. The result reports the actual pixel size and whether the
    /// window's own render buffer (native resolution) was used.
    /// </summary>
    public CaptureResult CaptureWindow(IntPtr hwnd, string format = "png", int sequence = 0,
        bool forceFullscreen = false, int jpegQuality = 90, string? captureMode = "auto")
    {
        if (hwnd == IntPtr.Zero)
            throw new InvalidOperationException("无效的窗口句柄");

        if (IsIconic(hwnd))
            throw new InvalidOperationException("目标窗口已最小化，无法截图");

        var mode = ParseMode(captureMode);
        RECT rect;
        bool usePrintWindow = false;

        switch (mode)
        {
            case CaptureMode.WindowContent:
                GetWindowRect(hwnd, out rect);
                usePrintWindow = true;
                break;

            case CaptureMode.WindowRegion:
                rect = GetVisibleBounds(hwnd);
                break;

            case CaptureMode.Monitor:
                rect = GetMonitorRect(hwnd);
                break;

            case CaptureMode.VirtualScreen:
                rect = GetVirtualScreenRect();
                break;

            default: // Auto
                var winRect = GetVisibleBounds(hwnd);
                var monRect = GetMonitorRect(hwnd);
                int winW = winRect.right - winRect.left;
                int winH = winRect.bottom - winRect.top;
                int monW = monRect.right - monRect.left;
                int monH = monRect.bottom - monRect.top;

                // A borderless window, or one that covers nearly the whole monitor
                // (fullscreen games, Magpie-upscaled windows) → grab that monitor.
                int style = GetWindowLong(hwnd, GWL_STYLE);
                bool borderless = (style & WS_POPUP) != 0 && (style & WS_CAPTION) != WS_CAPTION;
                double cover = monW > 0 && monH > 0 ? (double)(winW * winH) / (monW * monH) : 0;

                // A window that is far larger than the monitor (streaming clients reporting
                // an inflated rect, engines that lie about their size) is also grabbed from
                // the monitor so we never capture a partially off-screen region.
                bool oversized = winW > monW * 1.05 || winH > monH * 1.05;

                if (borderless || oversized || (forceFullscreen && cover >= 0.9))
                    rect = monRect;
                else
                    rect = winRect;
                break;
        }

        int width = rect.right - rect.left;
        int height = rect.bottom - rect.top;
        if (width <= 0 || height <= 0)
            throw new InvalidOperationException("窗口尺寸无效（可能已关闭或最小化）");

        var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss_fff");
        var suffix = sequence > 1 ? $"_{sequence}" : "";
        var isJpg = format.Equals("jpg", StringComparison.OrdinalIgnoreCase);
        var ext = isJpg ? ".jpg" : ".png";
        var filePath = Path.Combine(ScreenshotDir, $"{timestamp}{suffix}{ext}");

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);

        bool captured = false;
        if (usePrintWindow)
            captured = TryPrintWindow(hwnd, bitmap);

        if (!captured)
        {
            // Screen copy fallback (also the normal path for the other modes).
            // If PrintWindow produced nothing usable we may be off-rect: re-grab the
            // window's on-screen region so the user still gets a picture.
            if (usePrintWindow)
            {
                var visible = GetVisibleBounds(hwnd);
                int vw = visible.right - visible.left;
                int vh = visible.bottom - visible.top;
                if (vw > 0 && vh > 0 && (vw != width || vh != height))
                {
                    bitmap.Dispose();
                    width = vw;
                    height = vh;
                    using var resized = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                    using (var sg = Graphics.FromImage(resized))
                        sg.CopyFromScreen(visible.left, visible.top, 0, 0, new Size(width, height));
                    SaveBitmap(resized, filePath, isJpg, jpegQuality);
                    return new CaptureResult
                    {
                        FilePath = filePath,
                        Width = width,
                        Height = height,
                        FromWindowContent = false,
                        ModeLabel = "窗口区域"
                    };
                }
            }

            using var g = Graphics.FromImage(bitmap);
            g.CopyFromScreen(rect.left, rect.top, 0, 0, new Size(width, height));
        }

        SaveBitmap(bitmap, filePath, isJpg, jpegQuality);

        string label = captured ? "窗口内容（原生）"
            : mode switch
            {
                CaptureMode.WindowRegion => "窗口区域",
                CaptureMode.Monitor => "显示器",
                CaptureMode.VirtualScreen => "整屏",
                _ => "屏幕"
            };

        return new CaptureResult
        {
            FilePath = filePath,
            Width = width,
            Height = height,
            FromWindowContent = captured,
            ModeLabel = label
        };
    }

    private static void SaveBitmap(Bitmap bitmap, string filePath, bool isJpg, int jpegQuality)
    {
        if (isJpg)
        {
            var encoder = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using var ep = new EncoderParameters(1);
            ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)Math.Clamp(jpegQuality, 50, 100));
            bitmap.Save(filePath, encoder, ep);
        }
        else
        {
            bitmap.Save(filePath, ImageFormat.Png);
        }
    }

    /// <summary>
    /// PrintWindow with PW_RENDERFULLCONTENT renders the window's own content (DWM
    /// composition included) — native resolution even when the window is scaled,
    /// covered or upscaled by an external tool. Returns false when the result looks
    /// blank so the caller can fall back to a screen grab.
    /// </summary>
    private static bool TryPrintWindow(IntPtr hwnd, Bitmap bitmap)
    {
        try
        {
            using var g = Graphics.FromImage(bitmap);
            var hdc = g.GetHdc();
            bool ok;
            try
            {
                ok = PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT);
                if (!ok) ok = PrintWindow(hwnd, hdc, 0);
            }
            finally
            {
                g.ReleaseHdc(hdc);
            }
            if (!ok) return false;
            return !LooksBlank(bitmap);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Cheap blank check: sample a grid of pixels and see whether they are all equal.</summary>
    private static bool LooksBlank(Bitmap bmp)
    {
        try
        {
            int first = 0;
            bool firstSet = false;
            for (int y = 0; y < 5; y++)
            {
                for (int x = 0; x < 5; x++)
                {
                    int px = Math.Min(bmp.Width - 1, bmp.Width * x / 4);
                    int py = Math.Min(bmp.Height - 1, bmp.Height * y / 4);
                    int argb = bmp.GetPixel(px, py).ToArgb();
                    if (!firstSet) { first = argb; firstSet = true; }
                    else if (argb != first) return false;
                }
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Window bounds as actually drawn (DWM extended frame), falling back to GetWindowRect.</summary>
    private static RECT GetVisibleBounds(IntPtr hwnd)
    {
        try
        {
            int size = Marshal.SizeOf<RECT>();
            var ptr = Marshal.AllocHGlobal(size);
            try
            {
                int hr = DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, ptr, size);
                if (hr == 0)
                {
                    var r = Marshal.PtrToStructure<RECT>(ptr);
                    if (r.right > r.left && r.bottom > r.top) return r;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
        catch { }

        GetWindowRect(hwnd, out var fallback);
        return fallback;
    }

    private static RECT GetMonitorRect(IntPtr hwnd)
    {
        var mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (mon != IntPtr.Zero && GetMonitorInfo(mon, ref mi))
            return mi.rcMonitor;

        return new RECT
        {
            left = 0,
            top = 0,
            right = GetSystemMetrics(SM_CXSCREEN),
            bottom = GetSystemMetrics(SM_CYSCREEN)
        };
    }

    private static RECT GetVirtualScreenRect() => new()
    {
        left = GetSystemMetrics(SM_XVIRTUALSCREEN),
        top = GetSystemMetrics(SM_YVIRTUALSCREEN),
        right = GetSystemMetrics(SM_XVIRTUALSCREEN) + GetSystemMetrics(SM_CXVIRTUALSCREEN),
        bottom = GetSystemMetrics(SM_YVIRTUALSCREEN) + GetSystemMetrics(SM_CYVIRTUALSCREEN)
    };

    /// <summary>Get the foreground window handle. Save this before minimizing.</summary>
    public static IntPtr GetForegroundWindowHandle() => GetForegroundWindow();

    /// <summary>Get title of a specific window by handle.</summary>
    public static string GetWindowTitle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return string.Empty;
        var length = GetWindowTextLength(hwnd);
        if (length == 0) return string.Empty;
        var sb = new System.Text.StringBuilder(length + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>Get the foreground window's title text.</summary>
    public static string GetForegroundWindowTitle() => GetWindowTitle(GetForegroundWindow());

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;
    private const int GWL_STYLE = -16;
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_CAPTION = unchecked((int)0x00C00000);
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const int PW_RENDERFULLCONTENT = 0x00000002;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, int nFlags);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, IntPtr pvAttribute, int cbAttribute);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }
}
