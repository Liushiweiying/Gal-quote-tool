using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GalQuoteCollector.Models;

namespace GalQuoteCollector.Services;

public class ExportService
{
    public string ToJson(List<Quote> quotes, Dictionary<int, List<Tag>> tagsByQuote,
                         Dictionary<int, List<QuoteGroup>> groupsByQuote,
                         Dictionary<int, List<string>>? screenshotsByQuote = null)
    {
        var items = quotes.Select(q => new
        {
            text = q.Text,
            game = q.GameName,
            capturedAt = q.CapturedAt.ToString("O"),
            tags = tagsByQuote.GetValueOrDefault(q.Id, []).Select(t => t.Name).ToList(),
            groups = groupsByQuote.GetValueOrDefault(q.Id, []).Select(g => g.Name).ToList(),
            screenshot = q.ScreenshotPath,
            screenshots = screenshotsByQuote != null && screenshotsByQuote.TryGetValue(q.Id, out var sl)
                ? sl
                : null,
            notes = q.Notes
        });

        return JsonSerializer.Serialize(items, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

    public string ToMarkdown(Dictionary<string, List<(Quote quote, List<Tag> tags, List<QuoteGroup> groups)>> grouped)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Gal 语录导出");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        foreach (var (game, quotes) in grouped)
        {
            var gameName = string.IsNullOrWhiteSpace(game) ? "未分类" : game;
            sb.AppendLine($"## {gameName}");
            sb.AppendLine();

            foreach (var (quote, tags, groups) in quotes)
            {
                sb.AppendLine($"日期: {quote.CapturedAt:yyyy-MM-dd HH:mm:ss}");

                if (tags.Count > 0)
                {
                    var tagStr = string.Join(" ", tags.Select(t => $"`{t.Name}`"));
                    sb.AppendLine($"标签: {tagStr}");
                }

                if (groups.Count > 0)
                {
                    var groupStr = string.Join(" ", groups.Select(g => $"`{g.Name}`"));
                    sb.AppendLine($"分组: {groupStr}");
                }

                sb.AppendLine($"> {quote.Text}");
                sb.AppendLine();

                if (!string.IsNullOrWhiteSpace(quote.Notes))
                {
                    sb.AppendLine($"备注: {quote.Notes}");
                    sb.AppendLine();
                }

                sb.AppendLine("---");
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    // ── Import ──

    public List<ImportItem> ParseJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var items = new List<ImportItem>();

        foreach (var el in doc.RootElement.EnumerateArray())
        {
            items.Add(new ImportItem
            {
                Text = el.GetProperty("text").GetString() ?? "",
                GameName = el.TryGetProperty("game", out var g) ? g.GetString() ?? "" : "",
                CapturedAt = el.TryGetProperty("capturedAt", out var d) && DateTime.TryParse(d.GetString(), out var dt) ? dt : DateTime.Now,
                Tags = el.TryGetProperty("tags", out var t) ? t.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s != "").ToList() : [],
                Groups = el.TryGetProperty("groups", out var gr) ? gr.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s != "").ToList() : [],
                Notes = el.TryGetProperty("notes", out var n) ? n.GetString() ?? "" : "",
                Screenshot = el.TryGetProperty("screenshot", out var s) ? s.GetString() ?? "" : "",
                Screenshots = el.TryGetProperty("screenshots", out var ss) && ss.ValueKind == JsonValueKind.Array
                    ? ss.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x != "").ToList()
                    : []
            });
        }

        return items;
    }

    public List<ImportItem> ParseMarkdown(string md)
    {
        var items = new List<ImportItem>();
        var lines = md.Split('\n').Select(l => l.Trim()).ToList();

        string? currentGame = null;
        ImportItem? current = null;
        var textLines = new List<string>();

        void Flush()
        {
            if (current != null)
            {
                current.Text = string.Join("\n", textLines).Trim();
                if (!string.IsNullOrWhiteSpace(current.Text))
                    items.Add(current);
            }
            textLines.Clear();
        }

        foreach (var line in lines)
        {
            if (line.StartsWith("## "))
            {
                Flush();
                currentGame = line[3..].Trim();
                continue;
            }

            if (line.StartsWith("日期: "))
            {
                Flush();
                current = new ImportItem { GameName = currentGame ?? "" };
                if (DateTime.TryParse(line[4..].Trim(), out var dt))
                    current.CapturedAt = dt;
                continue;
            }

            if (line.StartsWith("标签: "))
            {
                if (current != null)
                {
                    var tagMatches = Regex.Matches(line, @"`([^`]+)`");
                    current.Tags = tagMatches.Select(m => m.Groups[1].Value).ToList();
                }
                continue;
            }

            if (line.StartsWith("分组: "))
            {
                if (current != null)
                {
                    var groupMatches = Regex.Matches(line, @"`([^`]+)`");
                    current.Groups = groupMatches.Select(m => m.Groups[1].Value).ToList();
                }
                continue;
            }

            if (line.StartsWith("> "))
            {
                if (current != null)
                    textLines.Add(line[2..].Trim());
                continue;
            }

            if (line.StartsWith("备注: "))
            {
                if (current != null)
                    current.Notes = line[4..].Trim();
                continue;
            }

            if (line == "---" || line.StartsWith("# "))
            {
                Flush();
                continue;
            }

            if (current != null && textLines.Count > 0 && !string.IsNullOrWhiteSpace(line) && !line.StartsWith("!"))
                textLines.Add(line);
        }

        Flush();
        return items;
    }
}

public class ImportItem
{
    public string Text { get; set; } = "";
    public string GameName { get; set; } = "";
    public DateTime CapturedAt { get; set; } = DateTime.Now;
    public List<string> Tags { get; set; } = [];
    public List<string> Groups { get; set; } = [];
    public string Notes { get; set; } = "";
    public string Screenshot { get; set; } = "";
    public List<string> Screenshots { get; set; } = [];
}
