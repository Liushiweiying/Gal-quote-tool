using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace GalQuoteCollector.Services;

/// <summary>
/// 裁掉截图四周的纯黑边（游戏比例和显示器不一致时，整屏截图左右/上下会有黑框）。
/// 只裁"整行/整列几乎全黑"的边缘，画面本身偏暗时不会误裁（裁掉比例过大就直接放弃）。
/// </summary>
public static class BlackBarCropper
{
    /// <summary>判定为"黑"的阈值（0-255，RGB 都低于它才算黑）。</summary>
    public const int DarkThreshold = 34;

    /// <summary>一行/一列里有多少比例是黑的才算黑边。</summary>
    public const double DarkRatio = 0.985;

    /// <summary>最多允许裁掉多少（超过就认为画面本身很暗，放弃裁剪）。</summary>
    public const double MaxRemovable = 0.45;

    public readonly record struct Rect(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left + 1;
        public int Height => Bottom - Top + 1;
        public bool IsFull(Bitmap bmp) => Left == 0 && Top == 0 && Right == bmp.Width - 1 && Bottom == bmp.Height - 1;
    }

    /// <summary>检测内容区（含黑边时返回内框）。</summary>
    public static Rect Detect(Bitmap bmp, int darkThreshold = DarkThreshold, double darkRatio = DarkRatio)
    {
        int w = bmp.Width, h = bmp.Height;
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        byte[] buf;
        int stride;
        try
        {
            stride = data.Stride;
            buf = new byte[stride * h];
            Marshal.Copy(data.Scan0, buf, 0, buf.Length);
        }
        finally { bmp.UnlockBits(data); }

        bool Dark(int x, int y)
        {
            int i = y * stride + x * 4;
            return buf[i] < darkThreshold && buf[i + 1] < darkThreshold && buf[i + 2] < darkThreshold;
        }

        int step = Math.Max(1, Math.Min(w, h) / 400); // 采样步长：够快也够准

        bool ColDark(int x)
        {
            int total = 0, dark = 0;
            for (int y = 0; y < h; y += step) { total++; if (Dark(x, y)) dark++; }
            return total > 0 && (double)dark / total >= darkRatio;
        }

        int left = 0, right = w - 1, top = 0, bottom = h - 1;
        while (left < right && ColDark(left)) left++;
        while (right > left && ColDark(right)) right--;

        bool RowDark(int y)
        {
            int total = 0, dark = 0;
            for (int x = left; x <= right; x += step) { total++; if (Dark(x, y)) dark++; }
            return total > 0 && (double)dark / total >= darkRatio;
        }

        while (top < bottom && RowDark(top)) top++;
        while (bottom > top && RowDark(bottom)) bottom--;
        return new Rect(left, top, right, bottom);
    }

    /// <summary>
    /// 检测并裁剪 <paramref name="sourcePath"/>，写到 <paramref name="targetPath"/>（null = 覆盖原文件）。
    /// 返回是否真的裁了 + 说明文字。
    /// </summary>
    public static (bool cropped, string detail) Crop(string sourcePath, string? targetPath = null)
    {
        try
        {
            using var bmp = new Bitmap(sourcePath);
            var rect = Detect(bmp);
            if (rect.IsFull(bmp))
                return (false, $"{Path.GetFileName(sourcePath)}: 没有黑边");

            double removed = 1.0 - (double)(rect.Width * rect.Height) / (bmp.Width * bmp.Height);
            if (removed > MaxRemovable || rect.Width < bmp.Width * 0.25 || rect.Height < bmp.Height * 0.25)
                return (false, $"{Path.GetFileName(sourcePath)}: 黑边占比过大（{removed * 100:0.#}%），判定为暗色画面，保持原图");

            using var outBmp = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(outBmp))
                g.DrawImage(bmp, new Rectangle(0, 0, rect.Width, rect.Height),
                    new Rectangle(rect.Left, rect.Top, rect.Width, rect.Height), GraphicsUnit.Pixel);

            var target = targetPath ?? sourcePath;
            if (string.Equals(target, sourcePath, StringComparison.OrdinalIgnoreCase))
            {
                // 覆盖原文件：先写临时文件再替换，避免 GDI+ 锁着原图
                var tmp = sourcePath + ".crop.tmp";
                outBmp.Save(tmp, ImageFormat.Png);
                bmp.Dispose();
                File.Delete(sourcePath);
                File.Move(tmp, sourcePath);
            }
            else
            {
                outBmp.Save(target, ImageFormat.Png);
            }

            return (true, $"{Path.GetFileName(sourcePath)}: {bmp.Width}x{bmp.Height} → {rect.Width}x{rect.Height}" +
                          $"（裁掉 {removed * 100:0.#}%，比例 {(double)rect.Width / rect.Height:0.###}）");
        }
        catch (Exception ex)
        {
            return (false, $"{Path.GetFileName(sourcePath)}: 失败 {ex.Message}");
        }
    }

    /// <summary>
    /// 把四周的纯黑边**涂成白色**（尺寸不变），就地覆盖原文件（先写临时文件再替换）。
    /// </summary>
    public static (bool ok, string detail) WhiteFill(string sourcePath)
    {
        try
        {
            using var bmp = new Bitmap(sourcePath);
            var rect = Detect(bmp);
            if (rect.IsFull(bmp)) return (false, $"{Path.GetFileName(sourcePath)}: 没有黑边");

            using (var g = Graphics.FromImage(bmp))
            {
                using var white = new SolidBrush(Color.White);
                if (rect.Left > 0) g.FillRectangle(white, 0, 0, rect.Left, bmp.Height);
                if (rect.Right < bmp.Width - 1) g.FillRectangle(white, rect.Right + 1, 0, bmp.Width - rect.Right - 1, bmp.Height);
                if (rect.Top > 0) g.FillRectangle(white, 0, 0, bmp.Width, rect.Top);
                if (rect.Bottom < bmp.Height - 1) g.FillRectangle(white, 0, rect.Bottom + 1, bmp.Width, bmp.Height - rect.Bottom - 1);
            }

            var tmp = sourcePath + ".white.tmp";
            bmp.Save(tmp, ImageFormat.Png);
            bmp.Dispose();
            File.Delete(sourcePath);
            File.Move(tmp, sourcePath);
            return (true, $"{Path.GetFileName(sourcePath)}: 已把黑边涂成白色（{bmp.Width}x{bmp.Height} 不变）");
        }
        catch (Exception ex)
        {
            return (false, $"{Path.GetFileName(sourcePath)}: 涂白失败 {ex.Message}");
        }
    }

    /// <summary>WPF 用：从 BitmapSource 检测内容区（供回想窗口在做显示时处理时用）。</summary>
    public static Rect Detect(System.Windows.Media.Imaging.BitmapSource source,
        int darkThreshold = DarkThreshold, double darkRatio = DarkRatio)
    {
        int w = source.PixelWidth, h = source.PixelHeight;
        var converted = source.Format == System.Windows.Media.PixelFormats.Bgra32
            ? source
            : new System.Windows.Media.Imaging.FormatConvertedBitmap(
                source, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        int stride = w * 4;
        var buf = new byte[stride * h];
        converted.CopyPixels(buf, stride, 0);

        bool Dark(int x, int y)
        {
            int i = y * stride + x * 4;
            return buf[i] < darkThreshold && buf[i + 1] < darkThreshold && buf[i + 2] < darkThreshold;
        }

        int step = Math.Max(1, Math.Min(w, h) / 400);
        int left = 0, right = w - 1, top = 0, bottom = h - 1;

        bool ColDark(int x)
        {
            int total = 0, dark = 0;
            for (int y = 0; y < h; y += step) { total++; if (Dark(x, y)) dark++; }
            return total > 0 && (double)dark / total >= darkRatio;
        }

        bool RowDark(int y)
        {
            int total = 0, dark = 0;
            for (int x = left; x <= right; x += step) { total++; if (Dark(x, y)) dark++; }
            return total > 0 && (double)dark / total >= darkRatio;
        }

        while (left < right && ColDark(left)) left++;
        while (right > left && ColDark(right)) right--;
        while (top < bottom && RowDark(top)) top++;
        while (bottom > top && RowDark(bottom)) bottom--;
        return new Rect(left, top, right, bottom);
    }
}
