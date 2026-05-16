using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace GalgameQuoteCollector.Services;

public class OcrService
{
    private OcrEngine? _ocrEngine;
    private bool _initialized;
    private readonly object _lock = new();

    private const double ScaleFactor = 2.5;

    // Multiple crop ratios to try
    private static readonly double[] CropRatios = [0.30, 0.40];

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

        try
        {
            // Try multiple crop ratios, keep the best result (longest text)
            string bestText = "";
            foreach (var ratio in CropRatios)
            {
                var processedPath = PreprocessForOcr(imagePath, ratio);
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
                if (text.Length > bestText.Length)
                    bestText = text;
            }

            return bestText;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string? PreprocessForOcr(string imagePath, double cropRatio)
    {
        try
        {
            using var original = new Bitmap(imagePath);
            int width = original.Width;
            int height = original.Height;

            // Crop to bottom portion (dialogue area)
            int cropHeight = (int)(height * cropRatio);
            int cropY = height - cropHeight;

            // Scale up significantly for better OCR
            int newWidth = (int)(width * ScaleFactor);
            int newHeight = (int)(cropHeight * ScaleFactor);

            using var cropped = original.Clone(new Rectangle(0, cropY, width, cropHeight), PixelFormat.Format24bppRgb);
            using var scaled = new Bitmap(newWidth, newHeight);
            using (var g = Graphics.FromImage(scaled))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(cropped, 0, 0, newWidth, newHeight);
            }

            // Convert to grayscale + enhance contrast
            using var gray = ConvertToGrayscale(scaled);
            EnhanceContrast(gray);

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
