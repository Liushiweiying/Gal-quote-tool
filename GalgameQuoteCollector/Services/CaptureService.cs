using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace GalgameQuoteCollector.Services;

public class CaptureService
{
    public string ScreenshotDir { get; }

    public CaptureService(string screenshotDir)
    {
        ScreenshotDir = screenshotDir;
        Directory.CreateDirectory(screenshotDir);
    }

    /// <summary>
    /// Capture the foreground window and return the screenshot file path.
    /// Uses PrintWindow for DirectX-aware capture, falls back to CopyFromScreen.
    /// </summary>
    public string CaptureForegroundWindow()
    {
        var hwnd = GetForegroundWindow();
        GetWindowRect(hwnd, out var rect);

        var width = rect.right - rect.left;
        var height = rect.bottom - rect.top;

        if (width <= 0 || height <= 0)
            throw new InvalidOperationException("Invalid window dimensions");

        var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss_fff");
        var filePath = Path.Combine(ScreenshotDir, $"{timestamp}.png");

        using var bitmap = new Bitmap(width, height);

        // Try PrintWindow first (handles DirectX-rendered content)
        using (var g = Graphics.FromImage(bitmap))
        {
            var hdc = g.GetHdc();
            bool printed = PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT);
            g.ReleaseHdc(hdc);

            if (!printed)
            {
                // Fallback: classic GDI screen capture
                using var g2 = Graphics.FromImage(bitmap);
                g2.CopyFromScreen(rect.left, rect.top, 0, 0, new Size(width, height));
            }
        }

        bitmap.Save(filePath, ImageFormat.Png);
        return filePath;
    }

    /// <summary>
    /// Get the foreground window's title text.
    /// </summary>
    public static string GetForegroundWindowTitle()
    {
        var hwnd = GetForegroundWindow();
        var length = GetWindowTextLength(hwnd);
        if (length == 0) return string.Empty;

        var sb = new System.Text.StringBuilder(length + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private const int PW_RENDERFULLCONTENT = 0x00000002;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, int nFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }
}
