using System.Diagnostics;
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

            lock (_cache)
            {
                // Bound the cache: evict stale entries once it grows past a sane size,
                // so a long-running session doesn't accumulate paths forever.
                if (_cache.Count > 100)
                {
                    var cutoff = DateTime.Now - CacheDuration;
                    foreach (var k in _cache.Where(kv => kv.Value.time < cutoff).Select(kv => kv.Key).ToList())
                        _cache.Remove(k);
                }
                _cache[imagePath] = (bestText, DateTime.Now);
            }
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

    // ===================== RapidOCR (本地离线, Python) =====================
    // 通过装有 rapidocr-onnxruntime 的 python 运行内嵌助手脚本，逐行输出识别文字。

    private static readonly string[] RapidCandidatePythons =
    {
        // 本机已知装有 RapidOCR 的环境（按概率排序）
        @"E:\sd-webui-aki\sd-webui-aki-v4.10-cu128\python\python.exe",
        @"D:\Miniconda3\python.exe",
        @"C:\Users\未时\AppData\Local\Programs\Python\Python310\python.exe",
        @"C:\Users\未时\AppData\Local\Programs\Python\Python313\python.exe",
    };

    // null = 尚未探测；空串 = 已探测但都没装 RapidOCR
    private static string? _rapidPython;

    private const string RapidHelperScript = """
        # -*- coding: utf-8 -*-
        import os
        import sys

        def main():
            args = [a for a in sys.argv[1:] if not a.startswith("--")]
            if not args:
                sys.exit(2)
            sys.stdout.reconfigure(encoding="utf-8")
            sys.stderr.reconfigure(encoding="utf-8")
            from rapidocr_onnxruntime import RapidOCR
            ocr = RapidOCR()
            for img in args:
                if not os.path.exists(img):
                    continue
                result, _elapse = ocr(img)
                for box, text, score in (result or []):
                    if text:
                        print(text)
            sys.exit(0)

        if __name__ == "__main__":
            main()
        """;

    /// <summary>确保助手脚本已写入磁盘，返回其路径。</summary>
    private static string EnsureRapidHelperScript()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                               "GalQuoteCollector");
        Directory.CreateDirectory(dir);
        var script = Path.Combine(dir, "rapid_ocr_helper.py");
        if (!File.Exists(script))
            File.WriteAllText(script, RapidHelperScript, Encoding.UTF8);
        return script;
    }

    /// <summary>探测指定 python 是否装有 rapidocr-onnxruntime，返回版本号（未装返回 null）。</summary>
    private static async Task<string?> ProbeRapidVersionAsync(string pythonPath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = pythonPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("import importlib.metadata; print(importlib.metadata.version('rapidocr-onnxruntime'))");
            using var proc = Process.Start(psi);
            if (proc == null) return null;
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            return proc.ExitCode == 0 ? stdout.Trim() : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// 找到装有 RapidOCR 的 python.exe 并缓存。优先用设置里填的路径，其次探测本机已知环境，最后试 PATH 里的 python。
    /// </summary>
    private static async Task<(string? path, string? version)> FindRapidPythonAsync(string configured)
    {
        if (_rapidPython != null)
            return _rapidPython == "" ? (null, null) : (_rapidPython, _rapidPythonVersion);

        async Task<(string?, string?)> TryAsync(string path)
        {
            var ver = await ProbeRapidVersionAsync(path);
            return ver != null ? (path, ver) : (null, null);
        }

        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            var r = await TryAsync(configured);
            if (r.Item1 != null) { _rapidPython = r.Item1; _rapidPythonVersion = r.Item2; return r; }
        }
        foreach (var cand in RapidCandidatePythons)
        {
            if (!File.Exists(cand)) continue;
            var r = await TryAsync(cand);
            if (r.Item1 != null) { _rapidPython = r.Item1; _rapidPythonVersion = r.Item2; return r; }
        }
        var pathPy = await TryAsync("python");
        if (pathPy.Item1 != null) { _rapidPython = pathPy.Item1; _rapidPythonVersion = pathPy.Item2; return pathPy; }

        _rapidPython = "";   // 已探测，全都没装
        _rapidPythonVersion = null;
        return (null, null);
    }

    private static string? _rapidPythonVersion;

    /// <summary>检查当前机器能否用 RapidOCR，返回 (可用?, 说明)。</summary>
    public async Task<(bool ok, string detail)> CheckRapidAsync(string configuredPython)
    {
        var (path, ver) = await FindRapidPythonAsync(configuredPython);
        if (path == null)
            return (false, "未找到装有 RapidOCR 的 Python（可尝试在设置中指定 python.exe 路径）");
        return (true, $"RapidOCR {ver} · {path}");
    }

    /// <summary>
    /// 用 RapidOCR 识别截图文字。先做与 Win OCR 相同的底部裁剪+增强，再交给 python 助手脚本。
    /// </summary>
    public async Task<string> RecognizeRapidTextAsync(string imagePath, string configuredPython)
    {
        try
        {
            var (py, _) = await FindRapidPythonAsync(configuredPython);
            if (py == null) return string.Empty;

            var script = EnsureRapidHelperScript();
            var processed = PreprocessForOcr(imagePath);
            if (processed == null) return string.Empty;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = py,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                };
                psi.ArgumentList.Add(script);
                psi.ArgumentList.Add(processed);
                using var proc = Process.Start(psi);
                if (proc == null) return string.Empty;
                var stdout = await proc.StandardOutput.ReadToEndAsync();
                await proc.WaitForExitAsync();
                var lines = stdout.Trim().Split('\n')
                    .Select(l => l.Trim()).Where(l => l.Length >= 1);
                return string.Join(Environment.NewLine, lines);
            }
            finally { try { File.Delete(processed); } catch { } }
        }
        catch { return string.Empty; }
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
