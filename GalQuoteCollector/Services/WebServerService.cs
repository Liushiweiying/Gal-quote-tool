using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GalQuoteCollector.Models;

namespace GalQuoteCollector.Services;

/// <summary>
/// 内网网页服务：在程序进程内起一个极简 HTTP 服务（TcpListener，不需要管理员/URL ACL），
/// 手机或电脑打开 http://本机IP:端口 就能查看、搜索、修改、导出语录。
/// 直接复用程序自己的 StorageService（它内部有锁），所以不会和桌面端抢数据库。
/// </summary>
public sealed class WebServerService : IDisposable
{
    private readonly StorageService _storage;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private int _port;
    private string _code = "";

    public bool IsRunning => _listener != null;
    public int Port => _port;
    public bool RequiresCode => _code.Length > 0;

    public WebServerService(StorageService storage)
    {
        _storage = storage;
    }

    /// <summary>启动（重复调用会先停掉旧的）。accessCode 留空 = 局域网免密。</summary>
    public (bool ok, string message) Start(int port, string accessCode)
    {
        Stop();
        if (port < 1024 || port > 65535) return (false, "端口要在 1024-65535 之间");
        try
        {
            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            _listener = listener;
            _port = port;
            _code = (accessCode ?? "").Trim();
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
            AppLog.Write($"web: 已启动 http://0.0.0.0:{port}/ 访问码={(RequiresCode ? "需要" : "无")}");
            return (true, $"已启动，端口 {port}");
        }
        catch (Exception ex)
        {
            Stop();
            AppLog.Write($"web: 启动失败 {ex.Message}");
            return (false, "启动失败: " + ex.Message);
        }
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _listener = null;
        _cts = null;
        if (_port != 0) AppLog.Write("web: 已停止");
        _port = 0;
    }

    /// <summary>本机可用于访问的地址（局域网 IPv4）。</summary>
    public static List<string> LocalUrls(int port)
    {
        var list = new List<string>();
        try
        {
            foreach (var ip in Dns.GetHostAddresses(Dns.GetHostName()))
            {
                if (ip.AddressFamily != AddressFamily.InterNetwork) continue;
                var s = ip.ToString();
                if (s.StartsWith("127.") || s.StartsWith("169.254.")) continue;
                list.Add($"http://{s}:{port}/");
            }
        }
        catch { }
        if (list.Count == 0) list.Add($"http://127.0.0.1:{port}/");
        list.Add($"http://127.0.0.1:{port}/");
        return list;
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct); }
            catch { break; }
            _ = Task.Run(() => HandleClientAsync(client, ct), ct);
        }
    }

    // ── HTTP 解析 ──

    private sealed record Request(string Method, string Path, Dictionary<string, string> Query,
        Dictionary<string, string> Headers, byte[] Body);

    private static async Task<Request?> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int headerEnd = -1;
        while (headerEnd < 0)
        {
            int n = await stream.ReadAsync(chunk, ct);
            if (n <= 0) return null;
            buffer.Write(chunk, 0, n);
            var arr = buffer.GetBuffer();
            headerEnd = IndexOfDoubleCrlf(arr, (int)buffer.Length);
            if (buffer.Length > 64 * 1024) return null; // 头部太大，拒绝
        }

        var all = buffer.ToArray();
        var headerText = Encoding.UTF8.GetString(all, 0, headerEnd);
        var lines = headerText.Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length < 2) return null;
        var method = first[0].ToUpperInvariant();
        var target = first[1];
        var qIdx = target.IndexOf('?');
        var path = qIdx < 0 ? target : target[..qIdx];
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (qIdx >= 0)
        {
            foreach (var pair in target[(qIdx + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = pair.Split('=', 2);
                query[Uri.UnescapeDataString(kv[0])] = kv.Length > 1 ? Uri.UnescapeDataString(kv[1].Replace('+', ' ')) : "";
            }
        }
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            var kv = lines[i].Split(':', 2);
            if (kv.Length == 2) headers[kv[0].Trim()] = kv[1].Trim();
        }

        int contentLength = 0;
        if (headers.TryGetValue("Content-Length", out var cl)) int.TryParse(cl, out contentLength);
        var body = new byte[Math.Max(0, contentLength)];
        int bodyStart = headerEnd + 4;
        int already = Math.Max(0, all.Length - bodyStart);
        if (already > 0) Array.Copy(all, bodyStart, body, 0, Math.Min(already, body.Length));
        int read = Math.Min(already, body.Length);
        while (read < body.Length)
        {
            int n = await stream.ReadAsync(body.AsMemory(read), ct);
            if (n <= 0) break;
            read += n;
        }
        return new Request(method, path, query, headers, body);
    }

    private static int IndexOfDoubleCrlf(byte[] data, int length)
    {
        for (int i = 0; i + 3 < length; i++)
            if (data[i] == 13 && data[i + 1] == 10 && data[i + 2] == 13 && data[i + 3] == 10) return i;
        return -1;
    }

    private static async Task WriteAsync(NetworkStream stream, int status, string statusText,
        string contentType, byte[] body, string? extraHeaders = null, CancellationToken ct = default)
    {
        var head = new StringBuilder();
        head.Append($"HTTP/1.1 {status} {statusText}\r\n");
        head.Append($"Content-Type: {contentType}\r\n");
        head.Append($"Content-Length: {body.Length}\r\n");
        head.Append("Cache-Control: no-store\r\n");
        if (!string.IsNullOrEmpty(extraHeaders)) head.Append(extraHeaders);
        head.Append("Connection: close\r\n\r\n");
        var headBytes = Encoding.UTF8.GetBytes(head.ToString());
        await stream.WriteAsync(headBytes, ct);
        if (body.Length > 0) await stream.WriteAsync(body, ct);
        await stream.FlushAsync(ct);
    }

    private static Task JsonAsync(NetworkStream s, object payload, string? extra = null, CancellationToken ct = default)
        => WriteAsync(s, 200, "OK", "application/json; charset=utf-8",
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)), extra, ct);

    private static Task ErrorAsync(NetworkStream s, int status, string message, CancellationToken ct = default)
        => WriteAsync(s, status, status == 401 ? "Unauthorized" : "Bad Request", "text/plain; charset=utf-8",
            Encoding.UTF8.GetBytes(message), null, ct);

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            using (client)
            {
                client.ReceiveTimeout = 15000;
                client.SendTimeout = 30000;
                var stream = client.GetStream();
                var req = await ReadRequestAsync(stream, ct);
                if (req == null) return;

                // 授权：设了访问码就要求 ?k= 或 Cookie k=
                bool authorized = !RequiresCode;
                string? setCookie = null;
                if (RequiresCode)
                {
                    var given = req.Query.TryGetValue("k", out var k) ? k : "";
                    if (given.Length == 0 && req.Headers.TryGetValue("Cookie", out var cookie))
                    {
                        foreach (var part in cookie.Split(';'))
                        {
                            var kv = part.Trim().Split('=', 2);
                            if (kv.Length == 2 && kv[0] == "k") { given = Uri.UnescapeDataString(kv[1]); break; }
                        }
                    }
                    authorized = string.Equals(given, _code, StringComparison.Ordinal);
                    if (authorized) setCookie = $"Set-Cookie: k={Uri.EscapeDataString(_code)}; Path=/; Max-Age=31536000\r\n";
                }
                if (!authorized)
                {
                    await WriteAsync(stream, 401, "Unauthorized", "text/plain; charset=utf-8",
                        Encoding.UTF8.GetBytes("需要访问码：在网址后加 ?k=你的访问码"), null, ct);
                    return;
                }

                await RouteAsync(stream, req, setCookie, ct);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"web: 请求处理失败 {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task RouteAsync(NetworkStream stream, Request req, string? setCookie, CancellationToken ct)
    {
        var path = req.Path;

        if (path == "/" || path == "/index.html")
        {
            await WriteAsync(stream, 200, "OK", "text/html; charset=utf-8",
                Encoding.UTF8.GetBytes(WebPage.Html), setCookie, ct);
            return;
        }

        if (path == "/api/meta" && req.Method == "GET")
        {
            var quotes = _storage.GetAllQuotes();
            var tags = _storage.GetAllTags().Select(t => new { id = t.Id, name = t.Name }).ToList();
            var groups = _storage.GetAllGroups().Select(g => new { id = g.Id, name = g.Name }).ToList();
            await JsonAsync(stream, new
            {
                total = quotes.Count,
                games = quotes.Select(q => q.GameName).Where(g => !string.IsNullOrWhiteSpace(g))
                              .Distinct().OrderBy(g => g, StringComparer.CurrentCulture).ToList(),
                tags,
                groups,
            }, setCookie, ct);
            return;
        }

        if (path == "/api/quotes" && req.Method == "GET")
        {
            string q = Get(req, "q").Trim();
            string game = Get(req, "game").Trim();
            string group = Get(req, "group").Trim();
            string tag = Get(req, "tag").Trim();
            int offset = Math.Max(0, ParseInt(Get(req, "offset"), 0));
            int limit = Math.Clamp(ParseInt(Get(req, "limit"), 20), 1, 200);

            var all = _storage.GetAllQuotes();
            var tagMap = _storage.GetTagIdsByQuote();
            var groupMap = _storage.GetGroupIdsByQuote();
            var tagName = _storage.GetAllTags().ToDictionary(t => t.Id, t => t.Name);
            var groupName = _storage.GetAllGroups().ToDictionary(g => g.Id, g => g.Name);

            IEnumerable<Quote> filtered = all.OrderByDescending(x => x.CapturedAt);
            if (q.Length > 0)
                filtered = filtered.Where(x =>
                    x.Text.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    x.GameName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    x.Notes.Contains(q, StringComparison.OrdinalIgnoreCase));
            if (game.Length > 0) filtered = filtered.Where(x => x.GameName == game);
            if (group.Length > 0 && int.TryParse(group, out var gid))
                filtered = filtered.Where(x => groupMap.GetValueOrDefault(x.Id, new List<int>()).Contains(gid));
            if (tag.Length > 0 && int.TryParse(tag, out var tid))
                filtered = filtered.Where(x => tagMap.GetValueOrDefault(x.Id, new List<int>()).Contains(tid));

            var list = filtered.ToList();
            var page = list.Skip(offset).Take(limit).Select(x => new
            {
                id = x.Id,
                text = x.Text,
                gameName = x.GameName,
                notes = x.Notes,
                capturedAt = x.CapturedAt.ToString("yyyy-MM-ddTHH:mm:ss"),
                hasShot = !string.IsNullOrWhiteSpace(x.ScreenshotPath) && File.Exists(x.ScreenshotPath),
                groups = groupMap.GetValueOrDefault(x.Id, new List<int>())
                                 .Select(id => groupName.GetValueOrDefault(id, "")).Where(s => s.Length > 0).ToList(),
                tags = tagMap.GetValueOrDefault(x.Id, new List<int>())
                             .Select(id => tagName.GetValueOrDefault(id, "")).Where(s => s.Length > 0).ToList(),
            }).ToList();

            await JsonAsync(stream, new { total = list.Count, offset, limit, items = page }, setCookie, ct);
            return;
        }

        if (path.StartsWith("/api/quotes/") && req.Method == "GET")
        {
            int id = ParseInt(path[12..], -1);
            var quote = _storage.GetAllQuotes().FirstOrDefault(x => x.Id == id);
            if (quote == null) { await ErrorAsync(stream, 404, "没有这条语录", ct); return; }
            await JsonAsync(stream, new
            {
                id = quote.Id,
                text = quote.Text,
                gameName = quote.GameName,
                notes = quote.Notes,
                windowTitle = quote.WindowTitle,
                capturedAt = quote.CapturedAt.ToString("yyyy-MM-ddTHH:mm:ss"),
                hasShot = !string.IsNullOrWhiteSpace(quote.ScreenshotPath) && File.Exists(quote.ScreenshotPath),
                groups = _storage.GetGroupsForQuote(id).Select(g => g.Name).ToList(),
                tags = _storage.GetTagsForQuote(id).Select(t => t.Name).ToList(),
            }, setCookie, ct);
            return;
        }

        if (path.StartsWith("/api/quotes/") && req.Method == "PUT")
        {
            int id = ParseInt(path[12..], -1);
            var quote = _storage.GetAllQuotes().FirstOrDefault(x => x.Id == id);
            if (quote == null) { await ErrorAsync(stream, 404, "没有这条语录", ct); return; }

            var node = JsonNode.Parse(Encoding.UTF8.GetString(req.Body)) as JsonObject;
            if (node == null) { await ErrorAsync(stream, 400, "JSON 解析失败", ct); return; }

            if (node["text"] is JsonNode t) quote.Text = t.GetValue<string>();
            if (node["gameName"] is JsonNode g) quote.GameName = g.GetValue<string>();
            if (node["notes"] is JsonNode n) quote.Notes = n.GetValue<string>();
            if (node["capturedAt"] is JsonNode c && DateTime.TryParse(c.GetValue<string>(), out var dt))
                quote.CapturedAt = dt;
            _storage.UpdateQuote(quote);

            // 分组 / 标签：按名字同步（不存在就新建）
            var wantedGroups = ReadNames(node, "groups").Concat(ReadNames(node, "newNames")).Distinct().ToList();
            var wantedTags = ReadNames(node, "tags").Concat(ReadNames(node, "newNames")).Distinct().ToList();

            foreach (var existing in _storage.GetGroupsForQuote(id))
                if (!wantedGroups.Contains(existing.Name)) _storage.RemoveQuoteFromGroup(id, existing.Id);
            foreach (var name in wantedGroups)
            {
                var grp = _storage.GetAllGroups().FirstOrDefault(x => x.Name == name) ?? _storage.AddGroup(name);
                if (!_storage.GetGroupsForQuote(id).Any(x => x.Id == grp.Id)) _storage.AddQuoteToGroup(id, grp.Id);
            }

            foreach (var existing in _storage.GetTagsForQuote(id))
                if (!wantedTags.Contains(existing.Name)) _storage.RemoveTagFromQuote(id, existing.Id);
            foreach (var name in wantedTags)
            {
                var tag = _storage.GetAllTags().FirstOrDefault(x => x.Name == name) ?? _storage.AddTag(name);
                if (!_storage.GetTagsForQuote(id).Any(x => x.Id == tag.Id)) _storage.AddTagToQuote(id, tag.Id);
            }

            AppLog.Write($"web: 已修改语录 #{id}");
            await JsonAsync(stream, new { ok = true, id }, setCookie, ct);
            return;
        }

        if (path.StartsWith("/api/quotes/") && req.Method == "DELETE")
        {
            int id = ParseInt(path[12..], -1);
            var quote = _storage.GetAllQuotes().FirstOrDefault(x => x.Id == id);
            if (quote == null) { await ErrorAsync(stream, 404, "没有这条语录", ct); return; }
            _storage.DeleteQuote(id); // 截图/关联由上层界面处理，这里只删数据行
            AppLog.Write($"web: 已删除语录 #{id}");
            await JsonAsync(stream, new { ok = true }, setCookie, ct);
            return;
        }

        if (path.StartsWith("/api/shot/") && req.Method == "GET")
        {
            int id = ParseInt(path[10..], -1);
            var quote = _storage.GetAllQuotes().FirstOrDefault(x => x.Id == id);
            var file = quote?.ScreenshotPath;
            if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
            {
                await ErrorAsync(stream, 404, "没有截图", ct);
                return;
            }
            var bytes = await File.ReadAllBytesAsync(file, ct);
            var ext = Path.GetExtension(file).ToLowerInvariant();
            var type = ext switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".webp" => "image/webp",
                ".bmp" => "image/bmp",
                _ => "image/png",
            };
            await WriteAsync(stream, 200, "OK", type, bytes, setCookie, ct);
            return;
        }

        if (path == "/api/export" && req.Method == "GET")
        {
            var format = Get(req, "format").ToLowerInvariant();
            var quotes = _storage.GetAllQuotes();
            var tagMap = _storage.GetTagIdsByQuote();
            var groupMap = _storage.GetGroupIdsByQuote();
            var tags = _storage.GetAllTags().ToDictionary(t => t.Id, t => t);
            var groups = _storage.GetAllGroups().ToDictionary(g => g.Id, g => g);

            var tagsByQuote = quotes.ToDictionary(q => q.Id, q =>
                tagMap.GetValueOrDefault(q.Id, new List<int>()).Where(tags.ContainsKey).Select(i => tags[i]).ToList());
            var groupsByQuote = quotes.ToDictionary(q => q.Id, q =>
                groupMap.GetValueOrDefault(q.Id, new List<int>()).Where(groups.ContainsKey).Select(i => groups[i]).ToList());

            var export = new ExportService();
            string content;
            string fileName;
            string contentType;
            if (format == "md" || format == "markdown")
            {
                var grouped = quotes.GroupBy(q => string.IsNullOrWhiteSpace(q.GameName) ? "未标注" : q.GameName)
                    .ToDictionary(g => g.Key, g => g.Select(q =>
                        (q, tagsByQuote.GetValueOrDefault(q.Id, new List<Tag>()),
                            groupsByQuote.GetValueOrDefault(q.Id, new List<QuoteGroup>()))).ToList());
                content = export.ToMarkdown(grouped);
                fileName = $"quotes-{DateTime.Now:yyyyMMdd-HHmm}.md";
                contentType = "text/markdown; charset=utf-8";
            }
            else
            {
                content = export.ToJson(quotes, tagsByQuote, groupsByQuote);
                fileName = $"quotes-{DateTime.Now:yyyyMMdd-HHmm}.json";
                contentType = "application/json; charset=utf-8";
            }

            var header = $"Content-Disposition: attachment; filename=\"{fileName}\"\r\n" + setCookie;
            await WriteAsync(stream, 200, "OK", contentType, Encoding.UTF8.GetBytes(content), header, ct);
            return;
        }

        await ErrorAsync(stream, 404, "没有这个地址: " + path, ct);
    }

    private static string Get(Request req, string key) => req.Query.TryGetValue(key, out var v) ? v : "";

    private static int ParseInt(string s, int fallback) => int.TryParse(s, out var v) ? v : fallback;

    private static List<string> ReadNames(JsonObject node, string key)
    {
        var list = new List<string>();
        if (node[key] is JsonArray arr)
            foreach (var item in arr)
            {
                var s = item?.GetValue<string>()?.Trim();
                if (!string.IsNullOrEmpty(s)) list.Add(s);
            }
        return list;
    }

    public void Dispose() => Stop();
}
