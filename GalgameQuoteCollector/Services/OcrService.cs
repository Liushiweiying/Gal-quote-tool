using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace GalgameQuoteCollector.Services;

public class OcrService
{
    private OcrEngine? _ocrEngine;
    private bool _initialized;
    private readonly object _lock = new();

    // Simple in-memory OCR cache: file path → (text, cached_at)
    private static readonly Dictionary<string, (string text, DateTime time)> _cache = new();
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(3);

    private const double ScaleFactor = 4.0;

    // Minimum text region height (after upscale)
    private const int MinTextRegionHeight = 60;

    public bool IsAvailable
    {
        get
        {
            EnsureInitialized();
            return _ocrEngine != null;
        }
    }

    private void EnsureInitialized()
    {
        if (_initialized) return;
        lock (_lock)
        {
            if (_initialized) return;
            try
            {
                _ocrEngine = OcrEngine.TryCreateFromUserProfileLanguages();
            }
            catch
            {
                _ocrEngine = null;
            }
            _initialized = true;
        }
    }

    public async Task<string> RecognizeTextAsync(string imagePath)
    {
        EnsureInitialized();
        if (_ocrEngine == null) return string.Empty;

        // Check cache
        lock (_cache)
        {
            if (_cache.TryGetValue(imagePath, out var cached)
                && DateTime.Now - cached.time < CacheDuration)
                return cached.text;
        }

        try
        {
            string bestText = "";
            double bestScore = 0;

            foreach (var highContrast in new[] { false, true })
            {
                var processedPath = PreprocessForOcr(imagePath, highContrast);
                if (processedPath == null) continue;

                var softwareBitmap = await LoadSoftwareBitmapAsync(processedPath);
                try { File.Delete(processedPath); } catch { }

                if (softwareBitmap == null) continue;

                var result = await _ocrEngine.RecognizeAsync(softwareBitmap);

                var lines = result.Lines
                    .Select(l => l.Text.Trim())
                    .Where(t => t.Length >= 2)
                    .ToList();

                var text = string.Join(Environment.NewLine, lines);
                if (string.IsNullOrEmpty(text)) continue;

                int valid = 0, total = 0;
                foreach (char c in text) { total++; if (IsValidOcrChar(c)) valid++; }
                double score = (double)valid / total * text.Length;

                if (score > bestScore) { bestScore = score; bestText = text; }
            }

            lock (_cache) _cache[imagePath] = (bestText, DateTime.Now);
            return bestText;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Heuristic: keep CJK, Latin, digits, common punctuation — reject noise.
    /// </summary>
    private static bool IsValidOcrChar(char c)
    {
        if (c >= 0x4E00 && c <= 0x9FFF) return true;  // CJK
        if (c >= 0x3040 && c <= 0x30FF) return true;  // Hiragana / Katakana
        if (c >= 0x31F0 && c <= 0x31FF) return true;  // Katakana extended
        if (c >= 0xFF00 && c <= 0xFFEF) return true;  // Fullwidth forms
        if (c >= 'A' && c <= 'Z') return true;
        if (c >= 'a' && c <= 'z') return true;
        if (c >= '0' && c <= '9') return true;
        if ("、。？！，．：；「」『』【】《》…—・　".Contains(c)) return true;
        return false;
    }

    private static string? PreprocessForOcr(string imagePath, bool highContrast = false)
    {
        try
        {
            using var original = new Bitmap(imagePath);
            int width = original.Width;
            int height = original.Height;

            // Detect text region by scanning rows for high variance
            int textTop = 0, textBottom = 0;
            DetectTextRowRange(original, out textTop, out textBottom);

            // Clamp to valid range
            int cropY = Math.Max(0, textTop);
            int cropHeight = Math.Min(height - cropY, textBottom - textTop + 20);
            if (cropHeight < 30) // fallback to bottom 35%
            {
                cropHeight = (int)(height * 0.35);
                cropY = height - cropHeight;
            }

            int newWidth = (int)(width * ScaleFactor);
            int newHeight = (int)(cropHeight * ScaleFactor);

            using var cropped = original.Clone(new Rectangle(0, cropY, width, cropHeight), PixelFormat.Format24bppRgb);
            using var scaled = new Bitmap(newWidth, newHeight);
            using (var g = Graphics.FromImage(scaled))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(cropped, 0, 0, newWidth, newHeight);
            }

            using var gray = ConvertToGrayscale(scaled);
            EnhanceContrast(gray);
            Sharpen(gray);
            if (highContrast) ApplyHighContrast(gray);

            var tempPath = Path.Combine(Path.GetTempPath(), $"galocr_{Guid.NewGuid():N}.png");
            gray.Save(tempPath, ImageFormat.Png);
            return tempPath;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Scans the image for rows with high pixel variance (likely text).
    /// Outputs the top and bottom y-coordinates of the best text region.
    /// </summary>
    private static unsafe void DetectTextRowRange(Bitmap bmp, out int top, out int bottom)
    {
        int w = bmp.Width;
        int h = bmp.Height;
        var rect = new Rectangle(0, 0, w, h);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        int stride = data.Stride;
        byte* ptr = (byte*)data.Scan0;

        // Compute per-row variance (grayscale approximation)
        var rowScores = new float[h];
        for (int y = 0; y < h; y++)
        {
            byte* row = ptr + y * stride;
            int sum = 0, sumSq = 0;
            for (int x = 0; x < w * 3; x += 3)
            {
                byte gray = (byte)((row[x] * 29 + row[x + 1] * 150 + row[x + 2] * 77) >> 8);
                sum += gray;
                sumSq += gray * gray;
            }
            int n = w;
            float mean = (float)sum / n;
            rowScores[y] = (float)sumSq / n - mean * mean;
        }
        bmp.UnlockBits(data);

        // Smooth scores with a 3-row window
        var smoothed = new float[h];
        for (int y = 0; y < h; y++)
        {
            float s = 0; int c = 0;
            for (int dy = -1; dy <= 1; dy++)
            {
                int ny = y + dy;
                if (ny >= 0 && ny < h) { s += rowScores[ny]; c++; }
            }
            smoothed[y] = s / c;
        }

        // Find threshold: 3x the median (robust to outliers)
        var sorted = (float[])smoothed.Clone();
        Array.Sort(sorted);
        float threshold = sorted[h / 2] * 3;

        // Find bottom-most contiguous region above threshold (at least 5 rows)
        int bestStart = 0, bestEnd = 0, bestLen = 0;
        int curStart = -1;
        for (int y = 0; y < h; y++)
        {
            if (smoothed[y] > threshold)
            {
                if (curStart < 0) curStart = y;
                if (y - curStart > bestLen)
                {
                    bestLen = y - curStart;
                    bestStart = curStart;
                    bestEnd = y;
                }
            }
            else
            {
                curStart = -1;
            }
        }

        // If no text found, use bottom 35%
        if (bestLen < 5)
        {
            top = h - (int)(h * 0.35);
            bottom = h;
            return;
        }

        // Prefer bottom-most region if multiple
        // (already bottom-most since we scan from top and update on each longer region)

        top = bestStart;
        bottom = bestEnd;
    }

    /// <summary>
    /// Convert 24bpp to 8bpp grayscale using luminance weights.
    /// </summary>
    private static unsafe Bitmap ConvertToGrayscale(Bitmap bmp)
    {
        var gray = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format8bppIndexed);

        // Set grayscale palette
        var palette = gray.Palette;
        for (int i = 0; i < 256; i++)
            palette.Entries[i] = Color.FromArgb(i, i, i);
        gray.Palette = palette;

        var srcRect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var srcData = bmp.LockBits(srcRect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        var dstData = gray.LockBits(srcRect, ImageLockMode.WriteOnly, PixelFormat.Format8bppIndexed);

        int srcStride = srcData.Stride;
        int dstStride = dstData.Stride;
        int h = bmp.Height;
        int w = bmp.Width;

        byte* srcPtr = (byte*)srcData.Scan0;
        byte* dstPtr = (byte*)dstData.Scan0;

        for (int y = 0; y < h; y++)
        {
            byte* srcRow = srcPtr + y * srcStride;
            byte* dstRow = dstPtr + y * dstStride;
            for (int x = 0; x < w; x++)
            {
                int idx = x * 3;
                byte b = srcRow[idx];
                byte g = srcRow[idx + 1];
                byte r = srcRow[idx + 2];
                // Luminance: 0.299 R + 0.587 G + 0.114 B
                dstRow[x] = (byte)((r * 77 + g * 150 + b * 29) >> 8);
            }
        }

        bmp.UnlockBits(srcData);
        gray.UnlockBits(dstData);
        return gray;
    }

    /// <summary>
    /// Auto-contrast: histogram stretch to use full 0-255 range.
    /// </summary>
    private static unsafe void EnhanceContrast(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadWrite, bmp.PixelFormat);

        int stride = data.Stride;
        int h = bmp.Height;
        int w = bmp.Width;
        int bytesPerPixel = bmp.PixelFormat == PixelFormat.Format8bppIndexed ? 1 : 3;

        byte* ptr = (byte*)data.Scan0;

        // Find min/max
        int min = 255, max = 0;
        for (int y = 0; y < h; y++)
        {
            byte* row = ptr + y * stride;
            for (int x = 0; x < w * bytesPerPixel; x++)
            {
                byte val = row[x];
                if (val < min) min = val;
                if (val > max) max = val;
            }
        }

        int range = max - min;
        if (range < 10) range = 10;

        for (int y = 0; y < h; y++)
        {
            byte* row = ptr + y * stride;
            for (int x = 0; x < w * bytesPerPixel; x++)
            {
                int val = row[x];
                row[x] = (byte)Math.Clamp((val - min) * 255 / range, 0, 255);
            }
        }

        bmp.UnlockBits(data);
    }

    /// <summary>
    /// 3x3 unsharp mask sharpening kernel.
    /// </summary>
    private static unsafe void Sharpen(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format8bppIndexed);
        int stride = data.Stride;
        int h = bmp.Height;
        int w = bmp.Width;
        byte* ptr = (byte*)data.Scan0;

        // Copy source to work on
        var src = new byte[h * stride];
        Marshal.Copy(data.Scan0, src, 0, src.Length);

        int[] kernel = [0, -1, 0, -1, 5, -1, 0, -1, 0];

        for (int y = 1; y < h - 1; y++)
        {
            byte* row = ptr + y * stride;
            for (int x = 1; x < w - 1; x++)
            {
                int sum = 0;
                for (int ky = -1; ky <= 1; ky++)
                    for (int kx = -1; kx <= 1; kx++)
                        sum += src[(y + ky) * stride + (x + kx)] * kernel[(ky + 1) * 3 + (kx + 1)];

                row[x] = (byte)Math.Clamp(sum, 0, 255);
            }
        }

        bmp.UnlockBits(data);
    }

    /// <summary>
    /// Push pixel values away from 128 — dark gets darker, light gets lighter.
    /// Helps when text &amp; background have similar luminance.
    /// </summary>
    private static unsafe void ApplyHighContrast(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format8bppIndexed);
        int stride = data.Stride;
        int h = bmp.Height;
        int w = bmp.Width;
        byte* ptr = (byte*)data.Scan0;

        for (int y = 0; y < h; y++)
        {
            byte* row = ptr + y * stride;
            for (int x = 0; x < w; x++)
            {
                int v = row[x];
                // Sigmoid-like curve: steepens around 128
                v = (v - 128) * 3 + 128;
                row[x] = (byte)Math.Clamp(v, 0, 255);
            }
        }

        bmp.UnlockBits(data);
    }

    private static async Task<SoftwareBitmap?> LoadSoftwareBitmapAsync(string imagePath)
    {
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(imagePath);
            using var stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            return await decoder.GetSoftwareBitmapAsync();
        }
        catch
        {
            return null;
        }
    }
}
