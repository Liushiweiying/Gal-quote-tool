using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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
    private readonly string _dataDir;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private int _port;
    private string _code = "";
    private X509Certificate2? _cert;

    public bool IsRunning => _listener != null;
    public int Port => _port;
    public bool RequiresCode => _code.Length > 0;
    public bool UseHttps { get; private set; }
    public string CertPath => Path.Combine(_dataDir, "web-cert.pfx");

    public WebServerService(StorageService storage, string dataDir)
    {
        _storage = storage;
        _dataDir = dataDir;
    }

    /// <summary>启动（重复调用会先停掉旧的）。accessCode 留空 = 局域网免密；useHttps 用自签证书。</summary>
    public (bool ok, string message) Start(int port, string accessCode, bool useHttps = false)
    {
        Stop();
        if (port < 1024 || port > 65535) return (false, "端口要在 1024-65535 之间");
        try
        {
            if (useHttps)
            {
                _cert = GetOrCreateCertificate();
                UseHttps = true;
            }
            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            _listener = listener;
            _port = port;
            _code = (accessCode ?? "").Trim();
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
            AppLog.Write($"web: 已启动 {(UseHttps ? "https" : "http")}://0.0.0.0:{port}/ 访问码={(RequiresCode ? "需要" : "无")}");
            return (true, $"已启动（{(UseHttps ? "HTTPS" : "HTTP")}），端口 {port}");
        }
        catch (Exception ex)
        {
            Stop();
            AppLog.Write($"web: 启动失败 {ex.Message}");
            return (false, "启动失败: " + ex.Message);
        }
    }

    /// <summary>取出（必要时生成）自签证书：SAN 含机器名 + 所有本机 IPv4。</summary>
    private X509Certificate2 GetOrCreateCertificate()
    {
        if (File.Exists(CertPath))
        {
            try
            {
                var loaded = new X509Certificate2(CertPath, "galquote", X509KeyStorageFlags.Exportable);
                // 旧版本生成的是 CA:FALSE 的证书，装进"受信任的根"后浏览器仍会警告 → 重新生成
                var bc = loaded.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault();
                if (bc != null && bc.CertificateAuthority) return loaded;
                AppLog.Write("web: 旧证书不是根证书，重新生成（消除浏览器「不安全」提示）");
            }
            catch (Exception ex) { AppLog.Write($"web: 读取证书失败，重新生成（{ex.Message}）"); }
        }

        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=Gal Quote Collector Web", rsa,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(Environment.MachineName);
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        foreach (var url in LocalUrls(_port == 0 ? 8088 : _port))
        {
            var host = url.Replace("http://", "").Replace("https://", "");
            var colon = host.LastIndexOf(':');
            if (colon > 0) host = host[..colon];
            if (IPAddress.TryParse(host, out var ip)) san.AddIpAddress(ip);
        }
        req.CertificateExtensions.Add(san.Build());
        // CA=TRUE：这样把它装进"受信任的根证书颁发机构"后，浏览器才会真的信任它
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.KeyCertSign, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false)); // serverAuth

        var cert = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(5));
        var bytes = cert.Export(X509ContentType.Pfx, "galquote");
        File.WriteAllBytes(CertPath, bytes);
        AppLog.Write($"web: 已生成自签证书 {CertPath}");
        return new X509Certificate2(bytes, "galquote", X509KeyStorageFlags.Exportable);
    }

    /// <summary>
    /// 把证书装进「当前用户 → 受信任的根证书颁发机构」，之后本机浏览器（Edge/Chrome）就不再提示不安全。
    /// 会弹一次 Windows 确认框，但**不需要管理员权限**。
    /// </summary>
    public (bool ok, string message) TrustOnThisMachine()
    {
        try
        {
            var cert = _cert ?? (_cert = GetOrCreateCertificate());
            using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);
            var found = store.Certificates.Find(X509FindType.FindByThumbprint, cert.Thumbprint, false);
            if (found.Count > 0) return (true, "本机已经信任过这个证书了");
            store.Add(cert);
            AppLog.Write($"web: 已把证书装进本机受信任根 {cert.Thumbprint}");
            return (true, "已装进「受信任的根证书颁发机构」：本机浏览器不会再提示不安全（手机仍需安装导出的 .cer）");
        }
        catch (Exception ex)
        {
            AppLog.Write($"web: 信任证书失败 {ex.Message}");
            return (false, "信任失败（可能是你在确认框里点了「否」）：" + ex.Message);
        }
    }

    /// <summary>把证书导出成 .cer（给手机安装用），返回文件路径。</summary>
    public string ExportCertificate()
    {
        var cert = _cert ?? (_cert = GetOrCreateCertificate());
        var path = Path.Combine(_dataDir, "GalQuoteCollectorWeb.cer");
        File.WriteAllBytes(path, cert.Export(X509ContentType.Cert));
        return path;
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _listener = null;
        _cts = null;
        _cert = null;
        UseHttps = false;
        if (_port != 0) AppLog.Write("web: 已停止");
        _port = 0;
    }

    /// <summary>本机可用于访问的地址（局域网 IPv4）。</summary>
    public static List<string> LocalUrls(int port, bool https = false)
    {
        var list = new List<string>();
        try
        {
            foreach (var ip in Dns.GetHostAddresses(Dns.GetHostName()))
            {
                if (ip.AddressFamily != AddressFamily.InterNetwork) continue;
                var s = ip.ToString();
                if (s.StartsWith("127.") || s.StartsWith("169.254.")) continue;
                list.Add($"{(https ? "https" : "http")}://{s}:{port}/");
            }
        }
        catch { }
        if (list.Count == 0) list.Add($"{(https ? "https" : "http")}://127.0.0.1:{port}/");
        list.Add($"{(https ? "https" : "http")}://127.0.0.1:{port}/");
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

    private static async Task<Request?> ReadRequestAsync(Stream stream, CancellationToken ct)
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

    private static async Task WriteAsync(Stream stream, int status, string statusText,
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

    private static Task JsonAsync(Stream s, object payload, string? extra = null, CancellationToken ct = default)
        => WriteAsync(s, 200, "OK", "application/json; charset=utf-8",
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)), extra, ct);

    private static Task ErrorAsync(Stream s, int status, string message, CancellationToken ct = default)
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
                Stream stream = client.GetStream();
                if (UseHttps && _cert != null)
                {
                    var ssl = new SslStream(stream, false);
                    try
                    {
                        await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                        {
                            ServerCertificate = _cert,
                            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                            ClientCertificateRequired = false,
                        }, ct);
                        stream = ssl;
                    }
                    catch (Exception ex)
                    {
                        // 手机第一次会因为自签证书报错/用户点"继续"；握手失败很正常，记一行就够
                        AppLog.Write($"web: TLS 握手失败 {ex.Message}");
                        ssl.Dispose();
                        return;
                    }
                }
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

    private async Task RouteAsync(Stream stream, Request req, string? setCookie, CancellationToken ct)
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
            // ex=game,group,tag → 这几项按"排除"处理（反选）
            var excluded = (Get(req, "ex") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim().ToLowerInvariant()).ToHashSet();

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

            if (game.Length > 0)
            {
                bool ex = excluded.Contains("game");
                filtered = filtered.Where(x => ex ? x.GameName != game : x.GameName == game);
            }
            if (group.Length > 0 && int.TryParse(group, out var gid))
            {
                bool ex = excluded.Contains("group");
                filtered = filtered.Where(x => ex
                    ? !groupMap.GetValueOrDefault(x.Id, new List<int>()).Contains(gid)
                    : groupMap.GetValueOrDefault(x.Id, new List<int>()).Contains(gid));
            }
            if (tag.Length > 0 && int.TryParse(tag, out var tid))
            {
                bool ex = excluded.Contains("tag");
                filtered = filtered.Where(x => ex
                    ? !tagMap.GetValueOrDefault(x.Id, new List<int>()).Contains(tid)
                    : tagMap.GetValueOrDefault(x.Id, new List<int>()).Contains(tid));
            }

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

        // 注意：/api/quotes/{id}/export 与 /export-zip 必须先于这条通用的单条查询匹配，否则会被它吃掉
        if (path.StartsWith("/api/quotes/") && req.Method == "GET"
            && !path.EndsWith("/export") && !path.EndsWith("/export-zip"))
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
            var wantedGroups = ReadNames(node, "groups").Concat(ReadNames(node, "newGroups"))
                .Concat(ReadNames(node, "newNames")).Distinct().ToList();
            var wantedTags = ReadNames(node, "tags").Concat(ReadNames(node, "newTags"))
                .Concat(ReadNames(node, "newNames")).Distinct().ToList();

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
            // bars=0 原样 / 1 裁掉黑边 / 2 黑边涂白（只在内存里处理，**不改文件**）
            int bars = ParseInt(Get(req, "bars"), 0);
            if (bars is 1 or 2)
            {
                try
                {
                    using var src = new System.Drawing.Bitmap(file);
                    var rect = BlackBarCropper.Detect(src);
                    bool hasBars = !(rect.Left == 0 && rect.Top == 0 &&
                                     rect.Right == src.Width - 1 && rect.Bottom == src.Height - 1);
                    if (hasBars)
                    {
                        using var dst = new System.Drawing.Bitmap(
                            bars == 1 ? rect.Width : src.Width,
                            bars == 1 ? rect.Height : src.Height,
                            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                        using (var g = System.Drawing.Graphics.FromImage(dst))
                        {
                            g.DrawImage(src, new System.Drawing.Rectangle(0, 0, dst.Width, dst.Height),
                                new System.Drawing.Rectangle(rect.Left, rect.Top, rect.Width, rect.Height),
                                System.Drawing.GraphicsUnit.Pixel);
                            if (bars == 2)
                            {
                                using var white = new System.Drawing.SolidBrush(System.Drawing.Color.White);
                                if (rect.Left > 0) g.FillRectangle(white, 0, 0, rect.Left, dst.Height);
                                if (rect.Right < dst.Width - 1) g.FillRectangle(white, rect.Right + 1, 0, dst.Width - rect.Right - 1, dst.Height);
                                if (rect.Top > 0) g.FillRectangle(white, 0, 0, dst.Width, rect.Top);
                                if (rect.Bottom < dst.Height - 1) g.FillRectangle(white, 0, rect.Bottom + 1, dst.Width, dst.Height - rect.Bottom - 1);
                            }
                        }
                        using var ms = new MemoryStream();
                        dst.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                        await WriteAsync(stream, 200, "OK", "image/png", ms.ToArray(), setCookie, ct);
                        return;
                    }
                }
                catch (Exception ex) { AppLog.Write($"web: 处理黑边失败，返回原图（{ex.Message}）"); }
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

        // 单条导出（含图片，base64 内嵌，方便手机/电脑之间搬运）
        if (path.StartsWith("/api/quotes/") && path.EndsWith("/export") && req.Method == "GET")
        {
            var idText = path[12..^7];
            int id = ParseInt(idText, -1);
            var quote = _storage.GetAllQuotes().FirstOrDefault(x => x.Id == id);
            if (quote == null) { await ErrorAsync(stream, 404, "没有这条语录", ct); return; }

            string? imageName = null;
            string? imageBase64 = null;
            if (!string.IsNullOrWhiteSpace(quote.ScreenshotPath) && File.Exists(quote.ScreenshotPath))
            {
                imageName = Path.GetFileName(quote.ScreenshotPath);
                imageBase64 = Convert.ToBase64String(await File.ReadAllBytesAsync(quote.ScreenshotPath, ct));
            }

            var bundle = new
            {
                type = "galquote.single",
                version = 1,
                text = quote.Text,
                gameName = quote.GameName,
                notes = quote.Notes,
                windowTitle = quote.WindowTitle,
                capturedAt = quote.CapturedAt.ToString("yyyy-MM-ddTHH:mm:ss"),
                groups = _storage.GetGroupsForQuote(id).Select(g => g.Name).ToList(),
                tags = _storage.GetTagsForQuote(id).Select(t => t.Name).ToList(),
                imageName,
                imageBase64,
            };
            var json = JsonSerializer.Serialize(bundle, new JsonSerializerOptions { WriteIndented = true });
            var fileName = $"quote-{id}-{quote.CapturedAt:yyyyMMdd-HHmm}.json";
            var header = $"Content-Disposition: attachment; filename=\"{fileName}\"\r\n" + setCookie;
            await WriteAsync(stream, 200, "OK", "application/json; charset=utf-8",
                Encoding.UTF8.GetBytes(json), header, ct);
            return;
        }

        // 单条导出成 ZIP（含截图文件本身），结构与「打包导出」一致，可被桌面端「打包导入」读回
        if (path.StartsWith("/api/quotes/") && path.EndsWith("/export-zip") && req.Method == "GET")
        {
            int id = ParseInt(path[12..^11], -1);
            var quote = _storage.GetAllQuotes().FirstOrDefault(x => x.Id == id);
            if (quote == null) { await ErrorAsync(stream, 404, "没有这条语录", ct); return; }

            var shots = new List<string>();
            if (!string.IsNullOrWhiteSpace(quote.ScreenshotPath)) shots.Add(quote.ScreenshotPath);
            try { foreach (var s in _storage.GetScreenshots(id)) shots.Add(s.FilePath); } catch { }

            var zipBytes = QuoteZipExporter.BuildZip(quote, _storage.GetTagsForQuote(id),
                _storage.GetGroupsForQuote(id), shots);
            var zipName = QuoteZipExporter.SuggestFileName(quote) + ".zip";
            var zipHeader = $"Content-Disposition: attachment; filename*=UTF-8''{Uri.EscapeDataString(zipName)}\r\n" + setCookie;
            await WriteAsync(stream, 200, "OK", "application/zip", zipBytes, zipHeader, ct);
            return;
        }

        // 单条导入（含图片）：接受 JSON（base64 内嵌）或 ZIP（含截图文件，与「打包导出」同格式）
        if (path == "/api/import" && req.Method == "POST")
        {
            // ZIP：PK 开头 → 解包后读 quotes.json，再把 screenshots/ 里的图片落到截图目录
            if (req.Body.Length > 4 && req.Body[0] == 0x50 && req.Body[1] == 0x4B)
            {
                try
                {
                    var dir = QuoteZipExporter.ExtractToTemp(req.Body);
                    try
                    {
                        var jsonPath = Path.Combine(dir, "quotes.json");
                        if (!File.Exists(jsonPath)) { await ErrorAsync(stream, 400, "压缩包里没有 quotes.json", ct); return; }
                        var items = new ExportService().ParseJson(await File.ReadAllTextAsync(jsonPath, ct));
                        int imported = 0;
                        var shotsDir = ResolveScreenshotDir();
                        Directory.CreateDirectory(shotsDir);
                        foreach (var item in items)
                        {
                            var saved = "";
                            foreach (var rel in item.Screenshots)
                            {
                                var candidate = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
                                if (!File.Exists(candidate)) continue;
                                saved = Path.Combine(shotsDir, $"import-{DateTime.Now:yyyyMMdd-HHmmssfff}{Path.GetExtension(candidate)}");
                                File.Copy(candidate, saved, true);
                                break;
                            }
                            var q = new Quote
                            {
                                Text = item.Text,
                                GameName = item.GameName,
                                Notes = item.Notes,
                                ScreenshotPath = saved,
                                CapturedAt = item.CapturedAt,
                            };
                            _storage.InsertQuote(q);
                            if (saved.Length > 0) _storage.AddScreenshot(q.Id, saved, 0);
                            foreach (var name in item.Groups)
                                _storage.AddQuoteToGroup(q.Id, (_storage.GetAllGroups().FirstOrDefault(x => x.Name == name) ?? _storage.AddGroup(name)).Id);
                            foreach (var name in item.Tags)
                                _storage.AddTagToQuote(q.Id, (_storage.GetAllTags().FirstOrDefault(x => x.Name == name) ?? _storage.AddTag(name)).Id);
                            imported++;
                        }
                        AppLog.Write($"web: 已从 ZIP 导入 {imported} 条语录");
                        await JsonAsync(stream, new { ok = true, imported }, setCookie, ct);
                        return;
                    }
                    finally { try { Directory.Delete(dir, true); } catch { } }
                }
                catch (Exception ex)
                {
                    AppLog.Write($"web: ZIP 导入失败 {ex.Message}");
                    await ErrorAsync(stream, 400, "ZIP 导入失败: " + ex.Message, ct);
                    return;
                }
            }

            JsonNode? node;
            try { node = JsonNode.Parse(Encoding.UTF8.GetString(req.Body)); }
            catch { await ErrorAsync(stream, 400, "JSON 解析失败", ct); return; }
            if (node is not JsonObject obj) { await ErrorAsync(stream, 400, "内容格式不对", ct); return; }

            var text = obj["text"]?.GetValue<string>() ?? "";
            if (string.IsNullOrWhiteSpace(text)) { await ErrorAsync(stream, 400, "缺少 text 字段", ct); return; }

            var imageBase64 = obj["imageBase64"]?.GetValue<string>();
            string savedImage = "";
            if (!string.IsNullOrWhiteSpace(imageBase64))
            {
                try
                {
                    var bytes = Convert.FromBase64String(imageBase64);
                    var ext = Path.GetExtension(obj["imageName"]?.GetValue<string>() ?? "").ToLowerInvariant();
                    if (ext.Length is 0 or > 6) ext = ".png";
                    var shots = ResolveScreenshotDir();
                    Directory.CreateDirectory(shots);
                    savedImage = Path.Combine(shots, $"import-{DateTime.Now:yyyyMMdd-HHmmssfff}{ext}");
                    await File.WriteAllBytesAsync(savedImage, bytes, ct);
                }
                catch (Exception ex)
                {
                    AppLog.Write($"web: 导入图片失败 {ex.Message}");
                    savedImage = "";
                }
            }

            var quote = new Quote
            {
                Text = text,
                GameName = obj["gameName"]?.GetValue<string>() ?? "",
                Notes = obj["notes"]?.GetValue<string>() ?? "",
                WindowTitle = obj["windowTitle"]?.GetValue<string>() ?? "",
                ScreenshotPath = savedImage,
                CapturedAt = DateTime.TryParse(obj["capturedAt"]?.GetValue<string>(), out var when) ? when : DateTime.Now,
            };
            _storage.InsertQuote(quote);
            if (savedImage.Length > 0) _storage.AddScreenshot(quote.Id, savedImage, 0);

            foreach (var name in ReadNames(obj, "groups"))
            {
                var grp = _storage.GetAllGroups().FirstOrDefault(x => x.Name == name) ?? _storage.AddGroup(name);
                _storage.AddQuoteToGroup(quote.Id, grp.Id);
            }
            foreach (var name in ReadNames(obj, "tags"))
            {
                var tag = _storage.GetAllTags().FirstOrDefault(x => x.Name == name) ?? _storage.AddTag(name);
                _storage.AddTagToQuote(quote.Id, tag.Id);
            }

            AppLog.Write($"web: 已导入语录 #{quote.Id}（图片={(savedImage.Length > 0 ? "有" : "无")}）");
            await JsonAsync(stream, new { ok = true, id = quote.Id }, setCookie, ct);
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

    /// <summary>导入图片时用的截图目录（跟设置里的保持一致）。</summary>
    private string ResolveScreenshotDir()
    {
        try
        {
            var cfg = new SettingsService(_dataDir).LoadHotkeyConfig();
            if (!string.IsNullOrWhiteSpace(cfg.ScreenshotDirectory)) return cfg.ScreenshotDirectory.Trim();
        }
        catch { }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "GalQuoteCollector");
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
