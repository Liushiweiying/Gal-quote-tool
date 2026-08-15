using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace GalQuoteCollector.Services;

/// <summary>
/// GitHub Releases based update check &amp; download. Prefers the Inno Setup
/// installer asset (_Setup.exe); downloads are verified against the asset's
/// sha256 digest when GitHub provides one.
/// </summary>
public class UpdateService
{
    public class UpdateInfo
    {
        public string Tag { get; set; } = "";
        public string Body { get; set; } = "";
        public string AssetUrl { get; set; } = "";
        public string AssetName { get; set; } = "";
        public string Digest { get; set; } = ""; // "sha256:…" or empty
    }

    private const string LatestApi =
        "https://api.github.com/repos/Liushiweiying/Gal-quote-tool/releases/latest";

    /// <summary>Returns update info when a newer version exists, otherwise null.</summary>
    public async Task<UpdateInfo?> CheckAsync(string currentVersion, int timeoutSec = 10)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSec) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Gal-quote-tool/{currentVersion}");
        var jsonStr = await http.GetStringAsync(LatestApi);

        using var doc = JsonDocument.Parse(jsonStr);
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString();
        if (string.IsNullOrWhiteSpace(tag)) return null;
        if (!IsNewer(tag, currentVersion)) return null;

        var info = new UpdateInfo { Tag = tag };
        info.Body = root.TryGetProperty("body", out var bodyEl) ? bodyEl.GetString() ?? "" : "";

        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.GetProperty("name").GetString() ?? "";
                if (name.EndsWith("_Setup.exe", StringComparison.OrdinalIgnoreCase))
                {
                    info.AssetUrl = a.GetProperty("browser_download_url").GetString() ?? "";
                    info.AssetName = name;
                    info.Digest = a.TryGetProperty("digest", out var d) ? d.GetString() ?? "" : "";
                    break;
                }
            }
        }
        return info;
    }

    public static bool IsNewer(string latestTag, string currentVersion)
    {
        var l = TryParseVersion(latestTag);
        var c = TryParseVersion(currentVersion);
        return l != null && c != null && l > c;
    }

    private static Version? TryParseVersion(string v)
    {
        v = v.Trim().TrimStart('v', 'V');
        return Version.TryParse(v, out var ver) ? ver : null;
    }

    /// <summary>
    /// Download the asset to <paramref name="destDir"/> with progress reporting,
    /// verifying the sha256 digest when the release provides one.
    /// </summary>
    public async Task<string> DownloadAsync(UpdateInfo info, string destDir, IProgress<double>? progress = null)
    {
        Directory.CreateDirectory(destDir);
        var dest = Path.Combine(destDir, info.AssetName);

        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Gal-quote-tool/update");
        using var resp = await http.GetAsync(info.AssetUrl, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();

        long total = resp.Content.Headers.ContentLength ?? 0;
        long readTotal = 0;
        using (var fs = File.Create(dest))
        using (var stream = await resp.Content.ReadAsStreamAsync())
        {
            var buffer = new byte[81920];
            int n;
            while ((n = await stream.ReadAsync(buffer)) > 0)
            {
                await fs.WriteAsync(buffer, 0, n);
                readTotal += n;
                if (total > 0) progress?.Report((double)readTotal / total);
            }
        }
        progress?.Report(1.0);

        // Verify the sha256 digest when GitHub provides one
        if (info.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            var expected = info.Digest["sha256:".Length..].Trim().ToLowerInvariant();
            var actual = ComputeSha256(dest);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(dest); } catch { }
                throw new InvalidDataException("下载文件校验失败（SHA256 不匹配），已删除");
            }
        }
        return dest;
    }

    private static string ComputeSha256(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }
}
