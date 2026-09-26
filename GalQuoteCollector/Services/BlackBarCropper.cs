using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace GalQuoteCollector.Services;

/// <summary>黑边处理方式（对文件操作）。</summary>
public enum BarMode
{
    /// <summary>不操作。</summary>
    None = 0,
    /// <summary>把黑边涂成黑色（统一成纯黑，保留原尺寸）。</summary>
    FillBlack = 1,
    /// <summary>把黑边涂成白色（保留原尺寸）。</summary>
    FillWhite = 2,
    /// <summary>裁掉黑边（只留画面）。</summary>
    Crop = 3,
}

/// <summary>
/// 截图四周黑边的检测与处理（**直接改文件**，第一次改动前会把原图备份到 <c>_originals</c> 子目录，可还原）。
/// 支持四边微调：正数 = 把黑边往里多算 N 像素（多裁/多涂），负数 = 少算 N 像素（留一点黑边）。
/// </summary>
public static class BlackBarCropper
{
    public const int DarkThreshold = 34;
    public const double DarkRatio = 0.985;
    public const double MaxRemovable = 0.45;
    public const string OriginalFolder = "_originals";

    public readonly record struct Rect(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left + 1;
        public int Height => Bottom - Top + 1;
        public bool IsFull(Bitmap bmp) => Left == 0 && Top == 0 && Right == bmp.Width - 1 && Bottom == bmp.Height - 1;
    }

    /// <summary>检测内容区（不含微调）。</summary>
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

        int step = Math.Max(1, Math.Min(w, h) / 400);
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

    /// <summary>按微调修正内容区（adj 正数 = 多算黑边）。</summary>
    public static Rect Adjust(Rect rect, int width, int height, int adjLeft, int adjRight, int adjTop, int adjBottom)
    {
        int left = Math.Clamp(rect.Left + adjLeft, 0, width - 1);
        int right = Math.Clamp(rect.Right - adjRight, 0, width - 1);
        int top = Math.Clamp(rect.Top + adjTop, 0, height - 1);
        int bottom = Math.Clamp(rect.Bottom - adjBottom, 0, height - 1);
        if (right <= left) right = Math.Min(width - 1, left + 1);
        if (bottom <= top) bottom = Math.Min(height - 1, top + 1);
        return new Rect(left, top, right, bottom);
    }

    /// <summary>原图备份路径（第一次改动前保存）。</summary>
    public static string OriginalPath(string path)
        => Path.Combine(Path.GetDirectoryName(path) ?? ".", OriginalFolder, Path.GetFileName(path));

    public static bool HasOriginal(string path) => File.Exists(OriginalPath(path));

    /// <summary>处理一个文件（就地覆盖）。返回 (是否改动, 说明)。</summary>
    public static (bool changed, string detail) Apply(string path, BarMode mode,
        int adjLeft = 0, int adjRight = 0, int adjTop = 0, int adjBottom = 0, bool makeBackup = true)
    {
        var name = Path.GetFileName(path);
        try
        {
            if (mode == BarMode.None) return (false, $"{name}: 不操作");
            if (!File.Exists(path)) return (false, $"{name}: 文件不存在");

            using var bmp = new Bitmap(path);
            var detected = Detect(bmp);
            if (detected.IsFull(bmp))
                return (false, $"{name}: 没有检测到黑边");

            var rect = Adjust(detected, bmp.Width, bmp.Height, adjLeft, adjRight, adjTop, adjBottom);
            double removed = 1.0 - (double)(rect.Width * rect.Height) / (bmp.Width * bmp.Height);
            if (mode == BarMode.Crop && (removed > MaxRemovable || rect.Width < bmp.Width * 0.25 || rect.Height < bmp.Height * 0.25))
                return (false, $"{name}: 黑边占比过大（{removed * 100:0.#}%），判定为暗色画面，跳过");

            if (makeBackup && !HasOriginal(path))
            {
                var orig = OriginalPath(path);
                Directory.CreateDirectory(Path.GetDirectoryName(orig)!);
                File.Copy(path, orig, true);
            }

            var modeName = mode switch
            {
                BarMode.Crop => "裁掉黑边",
                BarMode.FillBlack => "黑边涂黑",
                BarMode.FillWhite => "黑边涂白",
                _ => "不操作",
            };

            string sizeText;
            using (var result = mode == BarMode.Crop
                ? new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb)
                : new Bitmap(bmp))
            {
                using (var g = Graphics.FromImage(result))
                {
                    if (mode == BarMode.Crop)
                    {
                        g.DrawImage(bmp, new Rectangle(0, 0, rect.Width, rect.Height),
                            new Rectangle(rect.Left, rect.Top, rect.Width, rect.Height), GraphicsUnit.Pixel);
                    }
                    else
                    {
                        using var brush = new SolidBrush(mode == BarMode.FillBlack ? Color.Black : Color.White);
                        if (rect.Left > 0) g.FillRectangle(brush, 0, 0, rect.Left, result.Height);
                        if (rect.Right < result.Width - 1) g.FillRectangle(brush, rect.Right + 1, 0, result.Width - rect.Right - 1, result.Height);
                        if (rect.Top > 0) g.FillRectangle(brush, 0, 0, result.Width, rect.Top);
                        if (rect.Bottom < result.Height - 1) g.FillRectangle(brush, 0, rect.Bottom + 1, result.Width, result.Height - rect.Bottom - 1);
                    }
                }
                sizeText = $"{result.Width}x{result.Height}";

                var tmp = path + ".bars.tmp";
                result.Save(tmp, ImageFormat.Png);
                bmp.Dispose();
                File.Delete(path);
                File.Move(tmp, path);
            }

            return (true, $"{name}: {modeName} → {sizeText}");
        }
        catch (Exception ex)
        {
            return (false, $"{name}: 失败 {ex.Message}");
        }
    }

    /// <summary>从备份还原原图。</summary>
    public static (bool ok, string detail) Restore(string path)
    {
        var name = Path.GetFileName(path);
        try
        {
            var orig = OriginalPath(path);
            if (!File.Exists(orig)) return (false, $"{name}: 没有备份原图，无法还原");
            File.Copy(orig, path, true);
            return (true, $"{name}: 已还原原图");
        }
        catch (Exception ex) { return (false, $"{name}: 还原失败 {ex.Message}"); }
    }

    // ── 兼容旧接口 ──

    public static (bool cropped, string detail) Crop(string sourcePath, string? targetPath = null)
    {
        if (targetPath != null && !string.Equals(targetPath, sourcePath, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var bmp = new Bitmap(sourcePath);
                var rect = Detect(bmp);
                using var outBmp = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(outBmp))
                    g.DrawImage(bmp, new Rectangle(0, 0, rect.Width, rect.Height),
                        new Rectangle(rect.Left, rect.Top, rect.Width, rect.Height), GraphicsUnit.Pixel);
                outBmp.Save(targetPath, ImageFormat.Png);
                return (true, $"{Path.GetFileName(sourcePath)} → {Path.GetFileName(targetPath)}");
            }
            catch (Exception ex) { return (false, $"{Path.GetFileName(sourcePath)}: 失败 {ex.Message}"); }
        }
        return Apply(sourcePath, BarMode.Crop);
    }

    public static (bool ok, string detail) WhiteFill(string sourcePath) => Apply(sourcePath, BarMode.FillWhite);

    /// <summary>WPF 用：从 BitmapSource 检测内容区（显示级处理）。</summary>
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
