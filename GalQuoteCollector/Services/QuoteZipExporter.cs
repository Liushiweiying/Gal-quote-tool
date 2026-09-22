using System.IO;
using System.IO.Compression;
using System.Text;
using GalQuoteCollector.Models;

namespace GalQuoteCollector.Services;

/// <summary>
/// 单条语录的 ZIP 导出（含截图文件本身，不是 base64 内嵌）。
/// 结构与「打包导出」完全一致：zip 里是 <c>quotes.json</c> + <c>screenshots/&lt;图片&gt;</c>，
/// 所以导出后可以直接用「打包导入」或网页的「导入」读回来。
/// </summary>
public static class QuoteZipExporter
{
    public static byte[] BuildZip(Quote quote, IEnumerable<Tag> tags, IEnumerable<QuoteGroup> groups,
        IEnumerable<string> screenshotPaths)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            var relative = new List<string>();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in screenshotPaths)
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
                var name = Path.GetFileName(path);
                if (!used.Add(name)) continue; // 重名只放一份
                var entry = zip.CreateEntry("screenshots/" + name, CompressionLevel.Optimal);
                using (var es = entry.Open())
                using (var fs = File.OpenRead(path)) fs.CopyTo(es);
                relative.Add("screenshots/" + name);
            }

            var export = new ExportService();
            var json = export.ToJson(
                new List<Quote> { quote },
                new Dictionary<int, List<Tag>> { [quote.Id] = tags.ToList() },
                new Dictionary<int, List<QuoteGroup>> { [quote.Id] = groups.ToList() },
                new Dictionary<int, List<string>> { [quote.Id] = relative });

            var jsonEntry = zip.CreateEntry("quotes.json", CompressionLevel.Optimal);
            using var js = jsonEntry.Open();
            var bytes = Encoding.UTF8.GetBytes(json);
            js.Write(bytes, 0, bytes.Length);
        }
        return ms.ToArray();
    }

    /// <summary>建议的文件名（游戏名 + 时间）。</summary>
    public static string SuggestFileName(Quote quote)
    {
        var game = string.IsNullOrWhiteSpace(quote.GameName) ? "语录" : quote.GameName;
        foreach (var c in Path.GetInvalidFileNameChars()) game = game.Replace(c, '_');
        if (game.Length > 40) game = game[..40];
        return $"{game}_{quote.CapturedAt:yyyyMMdd-HHmm}";
    }

    /// <summary>把 ZIP 字节解到临时目录（供导入用），返回目录路径；调用方负责删除。</summary>
    public static string ExtractToTemp(byte[] zipBytes)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"galquote_one_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        using var ms = new MemoryStream(zipBytes);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;              // 目录项
            var rel = entry.FullName.Replace('\\', '/');
            if (rel.Contains("..")) continue;                             // 防目录穿越
            var target = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, true);
        }
        return dir;
    }
}
