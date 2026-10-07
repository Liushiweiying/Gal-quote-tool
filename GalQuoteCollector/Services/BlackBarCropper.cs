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

/// <summary>一次黑边处理的结果（给界面分类显示用）。</summary>
public enum BarOutcome
{
    /// <summary>改了文件（裁掉 / 涂黑 / 涂白）。</summary>
    Changed,
    /// <summary>没检测到黑边，原样不动。</summary>
    NoBars,
    /// <summary>黑边占比过大，判定为暗色画面，跳过。</summary>
    TooDark,
    /// <summary>文件不存在。</summary>
    Missing,
    /// <summary>处理时出错。</summary>
    Error,
    /// <summary>模式为「不操作」。</summary>
    Disabled,
}

/// <summary>
/// 截图四周黑边的检测与处理（**直接改文件**，第一次改动前会把原图备份到 <c>_originals</c> 子目录，可还原）。
/// 支持四边微调：正数 = 把黑边往里多算 N 像素（多裁/多涂），负数 = 少算 N 像素（留一点黑边）。
///
/// 两段式判定（2026-10-07 修「没裁干净」+ 防误裁）：
///   ① **纯黑边**：≥98.5% 像素 RGB 三通道都 &lt; 34 —— 无上限，信箱边/黑边靠它；
///   ② **边缘残留**：平均亮度 &lt; 45 且 ≥98% 像素 &lt; 64 的行/列 —— 每边**最多再去 4 像素**，
///      用来清掉截图边界那条抗锯齿过渡线（实测：残留行极差 ~31、标准差 ~6.5，
///      而暗色画作的行/列极差 54+、标准差 9~18 —— 所以②必须**限量**，否则会把
///      星空/头发这类暗色画面当成黑边吃掉 26 像素）。
/// 引擎用**深灰**（而不是纯黑）画边时，靠「四边微调」手工补几像素。
/// </summary>
public static class BlackBarCropper
{
    public const int DarkThreshold = 34;
    public const double DarkRatio = 0.985;

    /// <summary>边缘残留清理：每边最多再去掉几像素。</summary>
    public const int ResidueMaxPx = 4;
    /// <summary>边缘残留清理：这一行/列的平均亮度上限。</summary>
    public const double ResidueMeanLum = 45;
    /// <summary>边缘残留清理：亮度 &lt; 64 的像素占比下限。</summary>
    public const double ResidueNearRatio = 0.98;

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
        return DetectCore(buf, stride, w, h, darkThreshold, darkRatio);
    }

    /// <summary>
    /// 检测核心（GDI+ Bitmap 与 WPF BitmapSource 共用同一份实现）。
    /// 用两级判据判断"这一行/列算不算黑边"：见类注释。
    /// </summary>
    private static Rect DetectCore(byte[] buf, int stride, int w, int h, int darkThreshold, double darkRatio)
    {
        int step = Math.Max(1, Math.Min(w, h) / 400);

        // 一行/一列：纯黑占比（RGB 三通道都暗）+ 平均亮度 + <64 占比
        (double dark, double mean, double near) Stats(int fixedCoord, int from, int to, bool row)
        {
            int total = 0, dark = 0, near = 0;
            double sum = 0;
            for (int v = from; v <= to; v += step)
            {
                int x = row ? v : fixedCoord;
                int y = row ? fixedCoord : v;
                int i = y * stride + x * 4;
                double b = buf[i], g = buf[i + 1], r = buf[i + 2];
                double lum = 0.114 * b + 0.587 * g + 0.299 * r;
                sum += lum;
                if (r < darkThreshold && g < darkThreshold && b < darkThreshold) dark++;
                if (lum < 64) near++;
                total++;
            }
            if (total == 0) return (1, 0, 1);
            return ((double)dark / total, sum / total, (double)near / total);
        }

        bool IsPureBlackRow(int y, int x0, int x1) => Stats(y, x0, x1, row: true).dark >= darkRatio;
        bool IsPureBlackCol(int x) => Stats(x, 0, h - 1, row: false).dark >= darkRatio;

        bool IsResidueRow(int y, int x0, int x1)
        {
            var s = Stats(y, x0, x1, row: true);
            return s.mean < ResidueMeanLum && s.near >= ResidueNearRatio;
        }
        bool IsResidueCol(int x)
        {
            var s = Stats(x, 0, h - 1, row: false);
            return s.mean < ResidueMeanLum && s.near >= ResidueNearRatio;
        }

        int left = 0, right = w - 1, top = 0, bottom = h - 1;

        // ① 纯黑边：不限量
        while (left < right && IsPureBlackCol(left)) left++;
        while (right > left && IsPureBlackCol(right)) right--;
        while (top < bottom && IsPureBlackRow(top, left, right)) top++;
        while (bottom > top && IsPureBlackRow(bottom, left, right)) bottom--;

        // ② 边缘残留：每边最多 ResidueMaxPx 像素（清掉抗锯齿过渡线，但不深挖暗色画面）
        int n = 0;
        while (left < right && n < ResidueMaxPx && IsResidueCol(left)) { left++; n++; }
        n = 0;
        while (right > left && n < ResidueMaxPx && IsResidueCol(right)) { right--; n++; }
        n = 0;
        while (top < bottom && n < ResidueMaxPx && IsResidueRow(top, left, right)) { top++; n++; }
        n = 0;
        while (bottom > top && n < ResidueMaxPx && IsResidueRow(bottom, left, right)) { bottom--; n++; }

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
        var (outcome, detail, _, _) = ApplyEx(path, mode, adjLeft, adjRight, adjTop, adjBottom, makeBackup);
        return (outcome == BarOutcome.Changed, detail);
    }

    /// <summary>
    /// 处理一个文件（就地覆盖），并返回精确结果分类（界面按它分别统计「已处理/无黑边/偏暗/失败」）。
    /// 返回的宽高是**处理后**图片的尺寸（没改动时是原尺寸）。
    /// </summary>
    public static (BarOutcome outcome, string detail, int width, int height) ApplyEx(string path, BarMode mode,
        int adjLeft = 0, int adjRight = 0, int adjTop = 0, int adjBottom = 0, bool makeBackup = true)
    {
        var name = Path.GetFileName(path);
        if (mode == BarMode.None) return (BarOutcome.Disabled, $"{name}: 不操作", 0, 0);
        try
        {
            if (!File.Exists(path)) return (BarOutcome.Missing, $"{name}: 文件不存在", 0, 0);

            using var bmp = new Bitmap(path);
            int srcW = bmp.Width, srcH = bmp.Height;
            var detected = Detect(bmp);
            if (detected.IsFull(bmp))
                return (BarOutcome.NoBars, $"{name}: 没有检测到黑边（{srcW}x{srcH}）", srcW, srcH);

            var rect = Adjust(detected, bmp.Width, bmp.Height, adjLeft, adjRight, adjTop, adjBottom);
            double removed = 1.0 - (double)(rect.Width * rect.Height) / (bmp.Width * bmp.Height);
            if (mode == BarMode.Crop && (removed > MaxRemovable || rect.Width < bmp.Width * 0.25 || rect.Height < bmp.Height * 0.25))
                return (BarOutcome.TooDark, $"{name}: 黑边占比过大（{removed * 100:0.#}%），判定为暗色画面，跳过", srcW, srcH);

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
            int outW, outH;
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
                outW = result.Width;
                outH = result.Height;

                var tmp = path + ".bars.tmp";
                result.Save(tmp, ImageFormat.Png);
                bmp.Dispose();
                File.Delete(path);
                File.Move(tmp, path);
            }

            var trimText = mode == BarMode.Crop && (outW != srcW || outH != srcH)
                ? $"{srcW}x{srcH} → {sizeText}"
                : sizeText;
            return (BarOutcome.Changed, $"{name}: {modeName} → {trimText}", outW, outH);
        }
        catch (Exception ex)
        {
            return (BarOutcome.Error, $"{name}: 失败 {ex.Message}", 0, 0);
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
        return DetectCore(buf, stride, w, h, darkThreshold, darkRatio);
    }
}
