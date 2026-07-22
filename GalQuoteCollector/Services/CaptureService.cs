using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace GalQuoteCollector.Services;

public class CaptureService
{
    public string ScreenshotDir { get; }

    public CaptureService(string screenshotDir)
    {
        ScreenshotDir = screenshotDir;
        Directory.CreateDirectory(screenshotDir);
    }

    /// <summary>
    /// Capture the specified window and return the screenshot file path.
    /// Pass a saved handle to capture the game even after our window minimizes.
    /// </summary>
    public string CaptureWindow(IntPtr hwnd, string format = "png", int sequence = 0, bool forceFullscreen = false)
    {
        GetWindowRect(hwnd, out var rect);

        var width = rect.right - rect.left;
        var height = rect.bottom - rect.top;

        if (width <= 0 || height <= 0)
            throw new InvalidOperationException("Invalid window dimensions");

        // Game windows (especially Magpie-upscaled) may report their internal resolution
        // via GetWindowRect while the actual visible content fills the entire screen.
        // forceFullscreen is set by the caller when the window is recognized as a game.
        // Also auto-detect borderless fullscreen windows (WS_POPUP, no caption).
        int screenW = GetSystemMetrics(SM_CXSCREEN);
        int screenH = GetSystemMetrics(SM_CYSCREEN);
        bool shouldCaptureFullscreen = forceFullscreen;
        if (!shouldCaptureFullscreen)
        {
            int style = GetWindowLong(hwnd, GWL_STYLE);
            shouldCaptureFullscreen = (style & WS_POPUP) != 0 && (style & WS_CAPTION) != WS_CAPTION;
        }
        if (shouldCaptureFullscreen)
        {
            rect.left = 0;
            rect.top = 0;
            width = screenW;
            height = screenH;
        }

        var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss_fff");
        var suffix = sequence > 1 ? $"_{sequence}" : "";
        var isJpg = format.Equals("jpg", StringComparison.OrdinalIgnoreCase);
        var ext = isJpg ? ".jpg" : ".png";
        var filePath = Path.Combine(ScreenshotDir, $"{timestamp}{suffix}{ext}");

        using var bitmap = new Bitmap(width, height);

        using (var g = Graphics.FromImage(bitmap))
        {
            g.CopyFromScreen(rect.left, rect.top, 0, 0, new Size(width, height));
        }

        if (isJpg)
        {
            var encoder = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using var ep = new EncoderParameters(1);
            ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 90L);
            bitmap.Save(filePath, encoder, ep);
        }
        else
        {
            bitmap.Save(filePath, ImageFormat.Png);
        }
        return filePath;
    }

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
    private const int GWL_STYLE = -16;
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_CAPTION = unchecked((int)0x00C00000);

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

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }
}
