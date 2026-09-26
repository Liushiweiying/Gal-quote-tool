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
    private readonly object _certLock = new();

    public bool IsRunning => _listener != null;
    public int Port => _port;
    public bool RequiresCode => _code.Length > 0;
    /// <summary>0 = 自动（HTTP / HTTPS 都接受）、1 = 仅 HTTP、2 = 仅 HTTPS。</summary>
    public int TlsMode { get; private set; }
    public bool UseHttps => TlsMode == 2;
    public bool AllowLan { get; private set; }
    /// <summary>是否允许经 tunnel / 反向代理进来的公网访问（false = 外部请求一律 403）。</summary>
    public bool AllowExternal { get; private set; }
    /// <summary>公网访问是否要求 TOTP 动态码。</summary>
    public bool TotpEnabled => _totpSecret.Length > 0;
    /// <summary>是否强制整个网页只读（局域网也不许改）——盒子/镜像模式用。</summary>
    public bool ForceReadOnly { get; private set; }
    private string _totpSecret = "";
    private int _totpSessionHours = 12;
    public string CertPath => Path.Combine(_dataDir, "web-cert.pfx");

    public WebServerService(StorageService storage, string dataDir)
    {
        _storage = storage;
        _dataDir = dataDir;
    }

    /// <summary>启动参数（设置界面里的「网页与手机」一页）。</summary>
    public sealed record Options(
        int Port,
        string AccessCode,
        int TlsMode,
        bool AllowLan,
        bool AllowExternal,
        string TotpSecret,
        int TotpSessionHours,
        bool ForceReadOnly = false);

    private static string ModeText(int mode) => mode switch
    {
        1 => "仅 HTTP",
        2 => "仅 HTTPS",
        _ => "HTTP + HTTPS 自动兼容",
    };

    /// <summary>启动（重复调用会先停掉旧的）。accessCode 留空 = 局域网免密；tlsMode 0=自动 1=仅HTTP 2=仅HTTPS。</summary>
    public (bool ok, string message) Start(Options o)
    {
        Stop();
        if (o.Port < 1024 || o.Port > 65535) return (false, "端口要在 1024-65535 之间");
        try
        {
            TlsMode = Math.Clamp(o.TlsMode, 0, 2);
            AllowLan = o.AllowLan;
            AllowExternal = o.AllowExternal;
            ForceReadOnly = o.ForceReadOnly;
            _totpSecret = (o.TotpSecret ?? "").Trim().ToUpperInvariant();
            _totpSessionHours = Math.Clamp(o.TotpSessionHours <= 0 ? 12 : o.TotpSessionHours, 1, 24 * 30);
            // 仅 HTTPS：启动时就要证书（生成失败就直接报错）；自动模式等到真有 TLS 连接再生成
            if (TlsMode == 2) GetCert();
            // allowLan=false 时只监听本机回环（tunnel / 反向代理也只需本机可达）
            var bindAddress = AllowLan ? IPAddress.Any : IPAddress.Loopback;
            var listener = new TcpListener(bindAddress, o.Port);
            listener.Start();
            _listener = listener;
            _port = o.Port;
            _code = (o.AccessCode ?? "").Trim();
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
            AppLog.Write($"web: 已启动 {bindAddress}:{o.Port}/ 协议={ModeText(TlsMode)} 访问码={(RequiresCode ? "需要" : "无")} " +
                         $"局域网={AllowLan} 公网={AllowExternal}{(TotpEnabled ? " 公网两步验证=开" : "")}");
            var scope = (AllowLan, AllowExternal) switch
            {
                (true, true) => "局域网 + 公网可访问",
                (true, false) => "仅局域网可访问",
                (false, true) => "仅公网（tunnel / 反代）可访问",
                _ => "仅本机可访问",
            };
            return (true, $"已启动（{ModeText(TlsMode)}，{scope}）{(TotpEnabled ? "，公网需动态码" : "")}，端口 {o.Port}");
        }
        catch (Exception ex)
        {
            Stop();
            AppLog.Write($"web: 启动失败 {ex.Message}");
            return (false, "启动失败: " + ex.Message);
        }
    }

    /// <summary>取证书（必要时生成），并发安全。</summary>
    private X509Certificate2 GetCert()
    {
        lock (_certLock) { return _cert ??= GetOrCreateCertificate(); }
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
        var req = new CertificateRequest("CN=Gal Quote Tool Web", rsa,
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
            var cert = GetCert();
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

    /// <summary>确保自签证书已生成（不启动监听、不占端口），返回 .pfx 路径。</summary>
    public string EnsureCertificate()
    {
        GetCert();
        return CertPath;
    }

    /// <summary>把证书导出成 .cer（给手机安装用），返回文件路径。</summary>
    public string ExportCertificate()
    {
        var cert = GetCert();
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
        TlsMode = 0;
        AllowLan = false;
        AllowExternal = false;
        _totpSecret = "";
        _totpSessionHours = 12;
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

    /// <summary>请求是否来自"外网"（局域网以外的直连，或经过 tunnel/反向代理）。</summary>
    private static bool IsExternalRequest(Request req, System.Net.IPAddress? remote)
    {
        // 经过 tunnel / 反向代理时一定带这些头（cloudflared 会给 CF-Connecting-IP）
        foreach (var h in new[] { "CF-Connecting-IP", "X-Forwarded-For", "X-Real-IP", "CF-Ray" })
            if (req.Headers.ContainsKey(h)) return true;

        if (remote == null) return true;
        if (System.Net.IPAddress.IsLoopback(remote)) return false;
        var bytes = remote.GetAddressBytes();
        if (remote.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && bytes.Length == 4)
        {
            // 10/8, 172.16/12, 192.168/16, 169.254/16 → 局域网
            if (bytes[0] == 10) return false;
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return false;
            if (bytes[0] == 192 && bytes[1] == 168) return false;
            if (bytes[0] == 169 && bytes[1] == 254) return false;
            return true;
        }
        // IPv6：fc00::/7（ULA）与 loopback 视为内网
        if (remote.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            return !(remote.IsIPv6LinkLocal || remote.IsIPv6SiteLocal || (bytes[0] & 0xFE) == 0xFC);
        return true;
    }

    /// <summary>暴力破解防护：同一个来源 5 分钟内错 8 次，封 10 分钟。</summary>
    private readonly Dictionary<string, (int fails, DateTime firstFail, DateTime blockedUntil)> _attempts = new();
    private static readonly object _attemptLock = new();

    private string ClientKey(Request req, System.Net.IPAddress? remote)
    {
        foreach (var h in new[] { "CF-Connecting-IP", "X-Forwarded-For", "X-Real-IP" })
            if (req.Headers.TryGetValue(h, out var v) && !string.IsNullOrWhiteSpace(v))
                return v.Split(',')[0].Trim();
        return remote?.ToString() ?? "unknown";
    }

    private bool IsBlocked(string key)
    {
        lock (_attemptLock)
        {
            if (!_attempts.TryGetValue(key, out var a)) return false;
            if (a.blockedUntil > DateTime.Now) return true;
            if ((DateTime.Now - a.firstFail).TotalMinutes > 5) { _attempts.Remove(key); return false; }
            return false;
        }
    }

    private void NoteFailure(string key)
    {
        lock (_attemptLock)
        {
            var now = DateTime.Now;
            if (!_attempts.TryGetValue(key, out var a) || (now - a.firstFail).TotalMinutes > 5)
                a = (0, now, DateTime.MinValue);
            a.fails++;
            if (a.fails >= 8) a.blockedUntil = now.AddMinutes(10);
            _attempts[key] = a;
        }
    }

    private void NoteSuccess(string key)
    {
        lock (_attemptLock) { _attempts.Remove(key); }
    }

    // ── 登录 / 两步验证（TOTP）──

    /// <summary>从 Cookie 里取一个值（没有就返回 null）。</summary>
    private static string? GetCookie(Request req, string name)
    {
        if (!req.Headers.TryGetValue("Cookie", out var cookie)) return null;
        foreach (var part in cookie.Split(';'))
        {
            var kv = part.Trim().Split('=', 2);
            if (kv.Length == 2 && kv[0] == name) return Uri.UnescapeDataString(kv[1]);
        }
        return null;
    }

    /// <summary>请求里带的访问码（?k= 或 Cookie k=）对不对。定长比较，避免时序侧信道。</summary>
    private bool CodeMatches(Request req)
    {
        var given = req.Query.TryGetValue("k", out var q) ? q : (GetCookie(req, "k") ?? "");
        return FixedTimeEquals(given, _code);
    }

    private bool TotpSessionValid(Request req) =>
        TotpService.ValidateSessionToken(_totpSecret, GetCookie(req, "t"));

    /// <summary>表单字段：优先 application/x-www-form-urlencoded 的 body，其次 URL query。</summary>
    private static Dictionary<string, string> FormFields(Request req)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var text = req.Body.Length > 0 ? Encoding.UTF8.GetString(req.Body) : "";
        bool form = req.Headers.TryGetValue("Content-Type", out var ct) && ct.Contains("form-urlencoded");
        if (form || (text.Length > 0 && text.Contains('=') && !text.Contains('\n')))
        {
            foreach (var pair in text.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = pair.Split('=', 2);
                if (kv.Length == 2) d[Uri.UnescapeDataString(kv[0])] = Uri.UnescapeDataString(kv[1].Replace('+', ' '));
            }
        }
        foreach (var kv in req.Query) if (!d.ContainsKey(kv.Key)) d[kv.Key] = kv.Value;
        return d;
    }

    /// <summary>浏览器导航（要 HTML 登录页）还是页面里 fetch 的 API 请求（要纯文本 401）。</summary>
    private static bool WantsHtml(Request req)
        => req.Path is "/" or "/index.html"
           || (!req.Path.StartsWith("/api/") && req.Headers.TryGetValue("Accept", out var a) && a.Contains("text/html"));

    /// <summary>
    /// 认证失败的统一出口：POST /login 校验访问码 + 动态码并下发 Cookie；
    /// 浏览器导航回登录页；页面里的 fetch 回纯文本 401。总是已经应答过。
    /// </summary>
    private async Task TryHandleLoginAsync(Stream stream, Request req, string clientKey, bool external,
        bool needCode, bool needTotp, CancellationToken ct)
    {
        if (req.Path == "/login" && req.Method == "POST")
        {
            var f = FormFields(req);
            var givenCode = f.TryGetValue("k", out var k) ? k : "";
            var givenTotp = (f.TryGetValue("code", out var c) ? c : "").Replace(" ", "").Replace("-", "");
            bool okCode = !needCode || FixedTimeEquals(givenCode, _code);
            bool okTotp = !needTotp || TotpService.Verify(_totpSecret, givenTotp);
            if (okCode && okTotp)
            {
                NoteSuccess(clientKey);
                var hours = _totpSessionHours;
                var cookies = new StringBuilder();
                if (needCode) cookies.Append($"Set-Cookie: k={Uri.EscapeDataString(_code)}; Path=/; Max-Age=31536000\r\n");
                if (needTotp)
                    cookies.Append($"Set-Cookie: t={Uri.EscapeDataString(TotpService.SessionToken(_totpSecret, TimeSpan.FromHours(hours)))}; " +
                                   $"Path=/; Max-Age={hours * 3600}\r\n");
                AppLog.Write($"web: 登录成功（来源={clientKey} 外网={external} 动态码={(needTotp ? "已验证" : "不要求")}，{hours} 小时内免验证）");
                await WriteAsync(stream, 303, "See Other", "text/plain; charset=utf-8", Array.Empty<byte>(),
                    cookies + "Location: /\r\n", ct);
                return;
            }
            NoteFailure(clientKey);
            AppLog.Write($"web: 登录失败（来源={clientKey} 外网={external} 访问码={(okCode ? "对" : "错")} " +
                         $"动态码={(needTotp ? (okTotp ? "对" : "错") : "不要求")}）");
            await WriteAsync(stream, 401, "Unauthorized", "text/html; charset=utf-8",
                Encoding.UTF8.GetBytes(WebPage.Login(needCode, needTotp, failed: true)), null, ct);
            return;
        }

        // 普通请求：只有真的提交过访问码才算一次失败，免得浏览器刷新几次就被封
        if (req.Query.ContainsKey("k") || GetCookie(req, "k") != null) NoteFailure(clientKey);
        AppLog.Write($"web: 需要登录（来源={clientKey} 外网={external} 访问码={needCode} 动态码={needTotp}）");
        if (WantsHtml(req))
        {
            await WriteAsync(stream, 401, "Unauthorized", "text/html; charset=utf-8",
                Encoding.UTF8.GetBytes(WebPage.Login(needCode, needTotp, failed: false)), null, ct);
            return;
        }
        await ErrorAsync(stream, 401, needTotp
            ? "需要访问码 + 动态码：请用浏览器打开首页登录（登录后 12 小时内免验证）"
            : "需要访问码：在网址后加 ?k=你的访问码", ct);
    }

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

    /// <summary>
    /// 用 Peek 看连接的第一个字节是不是 TLS 的 ClientHello（0x16）——**不消耗数据**，
    /// 所以 HTTP 明文解析和 SslStream 之后都能照常读到完整请求。
    /// 自动模式下靠它让同一个端口同时接受 http:// 与 https://（tunnel / 反向代理的 origin 填哪个都行）。
    /// </summary>
    private static bool LooksLikeTls(TcpClient client, CancellationToken ct)
    {
        try
        {
            var sock = client.Client;
            bool readable = false;
            for (int i = 0; i < 30; i++) // 最多等 3 秒，等不到就当不是 TLS
            {
                if (sock.Poll(100_000, SelectMode.SelectRead)) { readable = true; break; }
                if (ct.IsCancellationRequested) return false;
            }
            if (!readable) return false; // 连上却不发数据：直接当明文处理（下面的解析会立刻结束）
            var one = new byte[1];
            int n = sock.Receive(one, 0, 1, SocketFlags.Peek);
            return n == 1 && one[0] == 0x16;
        }
        catch { return false; }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            using (client)
            {
                client.ReceiveTimeout = 15000;
                client.SendTimeout = 30000;
                Stream stream = client.GetStream();
                bool isTls = LooksLikeTls(client, ct);
                if (isTls && TlsMode == 1)
                {
                    AppLog.Write("web: 收到 TLS 握手请求，但当前是「仅 HTTP」模式（tunnel / 反向代理的 origin 若填 https:// 就会失败）→ 设置里改成「自动」或「仅 HTTPS」");
                    return;
                }
                if (isTls)
                {
                    var ssl = new SslStream(stream, false);
                    try
                    {
                        await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                        {
                            ServerCertificate = GetCert(),
                            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                            ClientCertificateRequired = false,
                        }, ct);
                        stream = ssl;
                    }
                    catch (Exception ex)
                    {
                        // 客户端不信任自签证书就会在这里断掉（云端的 tunnel 默认会校验证书）；
                        // 手机浏览器第一次点「继续访问」是正常的，这里记一行日志就够
                        AppLog.Write($"web: TLS 握手失败（自签证书未通过对方的证书校验）{ex.Message}");
                        ssl.Dispose();
                        return;
                    }
                }
                var req = await ReadRequestAsync(stream, ct);
                if (req == null) return;

                if (!isTls && TlsMode == 2)
                {
                    // 仅 HTTPS 模式收到明文请求：回一条能被人看懂的提示（否则浏览器只会显示「连接被重置」）
                    AppLog.Write($"web: 收到明文 HTTP 请求，但当前是「仅 HTTPS」模式（{req.Method} {req.Path}）");
                    await WriteAsync(stream, 400, "Bad Request", "text/plain; charset=utf-8",
                        Encoding.UTF8.GetBytes("这个端口只接受 HTTPS：请把网址改成 https:// 开头（自签证书第一次会提示「不安全」，选择继续访问即可）；" +
                                               "或者到「设置 → 网页与手机 → 访问协议」里改成「自动」。"), null, ct);
                    return;
                }

                var remoteIp = (client.Client.RemoteEndPoint as System.Net.IPEndPoint)?.Address;
                bool external = IsExternalRequest(req, remoteIp);
                // 外网（含 tunnel）只读；ForceReadOnly 时局域网也只读（盒子镜像模式）
                bool readOnly = external || ForceReadOnly;
                var clientKey = ClientKey(req, remoteIp);
                if (IsBlocked(clientKey))
                {
                    await ErrorAsync(stream, 429, "尝试次数过多，请稍后再试", ct);
                    return;
                }

                // ① 公网开关：没开放公网时，外部请求一律拒绝（和「局域网访问」是两把独立的锁）
                if (external && !AllowExternal)
                {
                    AppLog.Write($"web: 拒绝公网访问（未开启「允许公网访问」）来源={clientKey} {req.Method} {req.Path}");
                    await WriteAsync(stream, 403, "Forbidden", "text/html; charset=utf-8",
                        Encoding.UTF8.GetBytes(WebPage.Notice("未开放公网访问",
                            "这台电脑没有开启「允许公网访问」。请让管理员在「设置 → 网页与手机」里打开它。")), null, ct);
                    return;
                }

                // ② 认证：访问码（设了就要）+ 动态码（只对公网要求）
                bool needCode = RequiresCode;
                bool needTotp = external && TotpEnabled;
                bool codeOk = !needCode || CodeMatches(req);
                bool totpOk = !needTotp || TotpSessionValid(req);
                if ((needCode || needTotp) && (!codeOk || !totpOk))
                {
                    await TryHandleLoginAsync(stream, req, clientKey, external, needCode, needTotp, ct);
                    return; // 登录页 / 401 都已经应答
                }
                NoteSuccess(clientKey);
                // 用 ?k= 进来的一次性地址：顺手把访问码记住，页面里的 fetch 就不用再带
                string? setCookie = needCode && GetCookie(req, "k") is null
                    ? $"Set-Cookie: k={Uri.EscapeDataString(_code)}; Path=/; Max-Age=31536000\r\n"
                    : null;

                // 外网只读：写操作一律拒绝（登录 POST 已经在上面处理掉了）
                if (readOnly && req.Method is "PUT" or "DELETE" or "POST")
                {
                    AppLog.Write($"web: 拒绝写操作 {req.Method} {req.Path}（来源={clientKey} 外网={external} 强制只读={ForceReadOnly}）");
                    await ErrorAsync(stream, 403, ForceReadOnly
                        ? "这台设备是只读模式（只能查看和导出，不能修改）"
                        : "非局域网访问：只能查看和导出，不能修改", ct);
                    return;
                }

                await RouteAsync(stream, req, setCookie, ct, readOnly, external);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"web: 请求处理失败 {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task RouteAsync(Stream stream, Request req, string? setCookie, CancellationToken ct,
        bool readOnly = false, bool external = false)
    {
        var path = req.Path;

        // 需要登录时 /login 已经在认证阶段处理掉了；能走到这里说明不用登录
        if (path == "/login")
        {
            await WriteAsync(stream, 303, "See Other", "text/plain; charset=utf-8", Array.Empty<byte>(),
                "Location: /\r\n", ct);
            return;
        }

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
                canEdit = !readOnly,
                isLan = !external,
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

        // 整库打包导出（含截图文件）→ 与桌面端「打包导出（含截图）」同格式，可直接被它的「打包导入」读回
        if (path == "/api/export-zip" && req.Method == "GET")
        {
            try
            {
                var all = _storage.GetAllQuotes();
                var tagMap = _storage.GetTagIdsByQuote();
                var groupMap = _storage.GetGroupIdsByQuote();
                var tagDict = _storage.GetAllTags().ToDictionary(x => x.Id, x => x);
                var groupDict = _storage.GetAllGroups().ToDictionary(x => x.Id, x => x);

                var items = all.Select(q =>
                {
                    var shots = new List<string>();
                    if (!string.IsNullOrWhiteSpace(q.ScreenshotPath) && File.Exists(q.ScreenshotPath)) shots.Add(q.ScreenshotPath);
                    try { foreach (var s in _storage.GetScreenshots(q.Id)) if (File.Exists(s.FilePath)) shots.Add(s.FilePath); } catch { }
                    return (q,
                        tagMap.GetValueOrDefault(q.Id, new List<int>()).Where(tagDict.ContainsKey).Select(i => tagDict[i]).ToList(),
                        groupMap.GetValueOrDefault(q.Id, new List<int>()).Where(groupDict.ContainsKey).Select(i => groupDict[i]).ToList(),
                        shots);
                });

                var zipPath = QuoteZipExporter.BuildLibraryZip(items);
                var zipName = $"gal-quotes_{DateTime.Now:yyyy-MM-dd_HHmm}.zip";
                var zipHeader = $"Content-Disposition: attachment; filename=\"{zipName}\"\r\n" + setCookie;
                try
                {
                    var bytes = await File.ReadAllBytesAsync(zipPath, ct);
                    await WriteAsync(stream, 200, "OK", "application/zip", bytes, zipHeader, ct);
                }
                finally { try { File.Delete(zipPath); } catch { } }
            }
            catch (Exception ex)
            {
                AppLog.Write($"web: 打包导出失败 {ex.Message}");
                await ErrorAsync(stream, 500, "打包导出失败: " + ex.Message, ct);
            }
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

    /// <summary>固定时间字符串比较（避免按字符比较泄露长度/前缀）。</summary>
    private static bool FixedTimeEquals(string a, string b)
    {
        var ba = Encoding.UTF8.GetBytes(a ?? "");
        var bb = Encoding.UTF8.GetBytes(b ?? "");
        int diff = ba.Length ^ bb.Length;
        for (int i = 0; i < ba.Length && i < bb.Length; i++) diff |= ba[i] ^ bb[i];
        return diff == 0;
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
