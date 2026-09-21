using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GalQuoteCollector.Services;

namespace GalQuoteCollector.Views.Controls;

/// <summary>
/// 回想窗口的「白底」显示处理：**不改文件**，只在显示时把截图四周的黑边
/// 裁掉（CroppedBitmap）或涂成白色（写一份内存位图）。
/// 0 = 原样；1 = 裁掉黑边；2 = 黑边涂白。
/// </summary>
public static class SlideshowImageBars
{
    public const int ModeNone = 0;
    public const int ModeCrop = 1;
    public const int ModeWhiteFill = 2;

    public static string ModeName(int mode) => mode switch
    {
        ModeCrop => "白底·裁掉黑边",
        ModeWhiteFill => "白底·黑边涂白",
        _ => "原样",
    };

    /// <summary>按模式处理位图（无黑边或原样模式时原样返回）。</summary>
    public static BitmapSource Apply(BitmapSource source, int mode)
    {
        if (mode == ModeNone || source == null) return source!;
        try
        {
            var rect = BlackBarCropper.Detect(source);
            if (rect.Left == 0 && rect.Top == 0 && rect.Right == source.PixelWidth - 1 && rect.Bottom == source.PixelHeight - 1)
                return source; // 没有黑边

            if (mode == ModeCrop)
                return new CroppedBitmap(source, new Int32Rect(rect.Left, rect.Top, rect.Width, rect.Height));

            // 涂白：复制一份像素，把黑边区域的像素写成白色
            var converted = source.Format == PixelFormats.Bgra32
                ? source
                : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            int w = converted.PixelWidth, h = converted.PixelHeight;
            int stride = w * 4;
            var buf = new byte[stride * h];
            converted.CopyPixels(buf, stride, 0);

            void Paint(int x0, int y0, int x1, int y1)
            {
                for (int y = Math.Max(0, y0); y <= Math.Min(h - 1, y1); y++)
                {
                    int row = y * stride;
                    for (int x = Math.Max(0, x0); x <= Math.Min(w - 1, x1); x++)
                    {
                        int i = row + x * 4;
                        buf[i] = 255; buf[i + 1] = 255; buf[i + 2] = 255; buf[i + 3] = 255;
                    }
                }
            }
            if (rect.Left > 0) Paint(0, 0, rect.Left - 1, h - 1);
            if (rect.Right < w - 1) Paint(rect.Right + 1, 0, w - 1, h - 1);
            if (rect.Top > 0) Paint(0, 0, w - 1, rect.Top - 1);
            if (rect.Bottom < h - 1) Paint(0, rect.Bottom + 1, w - 1, h - 1);

            var wb = new WriteableBitmap(w, h, source.DpiX > 0 ? source.DpiX : 96, source.DpiY > 0 ? source.DpiY : 96,
                PixelFormats.Bgra32, null);
            wb.WritePixels(new Int32Rect(0, 0, w, h), buf, stride, 0);
            wb.Freeze();
            return wb;
        }
        catch
        {
            return source; // 出错就用原图，绝不影响显示
        }
    }
}
