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
    /// Capture the specified window and return the screenshot file path.
    /// Pass a saved handle to capture the game even after our window minimizes.
    /// </summary>
    public string CaptureWindow(IntPtr hwnd, string format = "png")
    {
        GetWindowRect(hwnd, out var rect);

        var width = rect.right - rect.left;
        var height = rect.bottom - rect.top;

        if (width <= 0 || height <= 0)
            throw new InvalidOperationException("Invalid window dimensions");

        var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss_fff");
        var isJpg = format.Equals("jpg", StringComparison.OrdinalIgnoreCase);
        var ext = isJpg ? ".jpg" : ".png";
        var filePath = Path.Combine(ScreenshotDir, $"{timestamp}{ext}");

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
