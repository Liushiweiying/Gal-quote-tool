using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace GalQuoteCollector.Services;

public class OcrService
{
    private OcrEngine? _ocrEngine;
    private bool _initialized;
    private readonly object _lock = new();

    // Simple in-memory OCR cache: file path → (text, cached_at)
    private static readonly Dictionary<string, (string text, DateTime time)> _cache = new();
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(3);

    private const double ScaleFactor = 2.0;

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

        lock (_cache)
        {
            if (_cache.TryGetValue(imagePath, out var cached)
                && DateTime.Now - cached.time < CacheDuration)
                return cached.text;
        }

        try
        {
            string bestText = "";

            // Primary: full image (no crop), then try cropped as fallback
            foreach (var tryFull in new[] { true, false })
            {
                var processedPath = tryFull
                    ? PreprocessFallback(imagePath)
                    : PreprocessForOcr(imagePath);
                if (processedPath == null) continue;

                var sb = await LoadSoftwareBitmapAsync(processedPath);
                try { File.Delete(processedPath); } catch { }
                if (sb == null) continue;

                var r = await _ocrEngine.RecognizeAsync(sb);
                var lines = r.Lines.Select(l => l.Text.Trim()).Where(t => t.Length >= 2).ToList();
                var text = string.Join(Environment.NewLine, lines);
                if (text.Length > bestText.Length) bestText = text;
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
    /// Recognize text via a local vision model served by Ollama (/api/generate).
    /// Sends the raw image as base64 and returns the model's plain-text answer.
    /// </summary>
    public async Task<string> RecognizeLocalTextAsync(string imagePath, string baseUrl, string model)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(imagePath);
            var b64 = Convert.ToBase64String(bytes);

            var payload = new
            {
                model = model,
                prompt = "识别图片中的所有文字，严格按照原文逐行输出，不要添加任何解释或格式。",
                images = new[] { b64 },
                stream = false
            };

            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromMinutes(2);
            var content = new StringContent(
                JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            var resp = await client.PostAsync(baseUrl.TrimEnd('/') + "/api/generate", content);
            if (!resp.IsSuccessStatusCode) return string.Empty;

            var respStr = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(respStr);
            return doc.RootElement.TryGetProperty("response", out var r)
                ? (r.GetString()?.Trim() ?? string.Empty)
                : string.Empty;
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

    /// <summary>Downscale full uncropped image to ~1200px wide.</summary>
    private static string? PreprocessFallback(string imagePath)
    {
        try
        {
            using var original = new Bitmap(imagePath);
            int w = original.Width, h = original.Height;
            double scale = Math.Min(1.0, 1200.0 / w);
            int nw = (int)(w * scale), nh = (int)(h * scale);
            using var resized = new Bitmap(nw, nh);
            using (var g = Graphics.FromImage(resized))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(original, 0, 0, nw, nh);
            }
            using var gray = ConvertToGrayscale(resized);
            EnhanceContrast(gray);
            var path = Path.Combine(Path.GetTempPath(), $"galocr_fb_{Guid.NewGuid():N}.png");
            gray.Save(path, ImageFormat.Png);
            return path;
        }
        catch { return null; }
    }

    /// <summary>
    /// Simple bottom-30% crop + 2x upscale + grayscale + contrast.
    /// No adaptive crop, no sharpen, no binarize — most compatible.
    /// </summary>
    private static string? PreprocessForOcr(string imagePath)
    {
        try
        {
            using var original = new Bitmap(imagePath);
            int width = original.Width;
            int height = original.Height;

            // Fixed bottom 30% crop (original reliable approach)
            int cropY = (int)(height * 0.65);
            int cropHeight = height - cropY;

            int newWidth = (int)(width * 2.0);
            int newHeight = (int)(cropHeight * 2.0);

            using var cropped = original.Clone(new Rectangle(0, cropY, width, cropHeight), PixelFormat.Format24bppRgb);
            using var scaled = new Bitmap(newWidth, newHeight);
            using (var g = Graphics.FromImage(scaled))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(cropped, 0, 0, newWidth, newHeight);
            }

            using var gray = ConvertToGrayscale(scaled);
            EnhanceContrast(gray);

            var tempPath = Path.Combine(Path.GetTempPath(), $"galocr_{Guid.NewGuid():N}.png");
            gray.Save(tempPath, ImageFormat.Png);
            return tempPath;
        }
        catch { return null; }
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

    /// <summary>Hard threshold: below 128 → black, else white.</summary>
    private static unsafe void ApplyBinarize(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format8bppIndexed);
        int stride = data.Stride, h = bmp.Height, w = bmp.Width;
        byte* ptr = (byte*)data.Scan0;
        for (int y = 0; y < h; y++)
        {
            byte* row = ptr + y * stride;
            for (int x = 0; x < w; x++)
                row[x] = row[x] < 128 ? (byte)0 : (byte)255;
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
