using System.Text.Json;
using GalQuoteCollector.Models;
using GalQuoteCollector.Services;

namespace GalQuoteCollector.Server;

/// <summary>
/// 无界面版（Armbian 电视盒子 / NAS / 树莓派 / Linux 小主机）：只跑网页服务那套，
/// 复用电脑端同一份 WebServerService + WebPage + StorageService + TotpService 源码。
/// 电脑关机也能继续提供网页（浏览 / 搜索 / 回想 / 导出；要不要允许修改由设置决定）。
/// 典型用法：
///   galquote-server --data /var/lib/galquote --port 8088 --lan --external --totp ABCD...
///   galquote-server --gen-totp            生成一个两步验证密钥并打印
///   galquote-server --print-code ABCD...  打印当前 6 位动态码（核对手机用）
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var o = Options.Parse(args);
        if (o.Help) { PrintHelp(); return 0; }
        if (o.GenTotp)
        {
            var s = TotpService.NewSecret();
            Console.WriteLine("两步验证密钥（Base32）：" + s);
            Console.WriteLine("分组显示：" + TotpService.Group(s));
            Console.WriteLine("otpauth 链接：" + TotpService.OtpauthUri(s, "server"));
            Console.WriteLine("当前动态码：" + TotpService.Code(s));
            return 0;
        }
        if (o.PrintCode is { Length: > 0 } secret)
        {
            Console.WriteLine($"当前动态码：{TotpService.Code(secret)}（{30 - DateTimeOffset.UtcNow.ToUnixTimeSeconds() % 30} 秒后变）");
            return 0;
        }

        Directory.CreateDirectory(o.DataDir);
        var dbPath = Path.Combine(o.DataDir, "quotes.db");
        if (!File.Exists(dbPath))
            Console.WriteLine($"注意：{dbPath} 还不存在，会新建一个空库（把电脑上的 quotes.db 和截图目录同步过来即可）。");

        var cfg = LoadSettings(o, out var settingsPath);
        Console.WriteLine($"数据目录：{o.DataDir}");
        Console.WriteLine($"设置来源：{(settingsPath.Length > 0 ? settingsPath : "命令行参数 / 默认值")}");

        var storage = new StorageService(dbPath);
        using var server = new WebServerService(storage, o.DataDir);

        var options = new WebServerService.Options(
            Port: cfg.Port,
            AccessCode: cfg.Code,
            TlsMode: cfg.TlsMode,
            AllowLan: cfg.AllowLan,
            AllowExternal: cfg.AllowExternal,
            TotpSecret: cfg.TotpSecret,
            TotpSessionHours: cfg.TotpHours,
            ForceReadOnly: cfg.ReadOnly);

        var (ok, message) = server.Start(options);
        Console.WriteLine((ok ? "启动成功：" : "启动失败：") + message);
        if (!ok) return 1;

        foreach (var url in WebServerService.LocalUrls(cfg.Port, cfg.TlsMode == 2))
            Console.WriteLine("  监听：" + url + (cfg.Code.Length > 0 ? $"?k={cfg.Code}" : ""));
        Console.WriteLine("按 Ctrl+C 退出。");

        var stop = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.TrySetResult(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.TrySetResult();
        await stop.Task;
        server.Stop();
        Console.WriteLine("已停止。");
        return 0;
    }

    /// <summary>设置优先级：命令行 &gt; 数据目录里的 settings.json（可以直接把电脑上的那份拷过来）&gt; 默认值。</summary>
    private static EffectiveConfig LoadSettings(Options o, out string settingsPath)
    {
        settingsPath = o.SettingsFile;
        if (settingsPath.Length == 0)
        {
            var candidate = Path.Combine(o.DataDir, "settings.json");
            if (File.Exists(candidate)) settingsPath = candidate;
        }

        HotkeyConfig? file = null;
        if (settingsPath.Length > 0 && File.Exists(settingsPath))
        {
            try
            {
                file = JsonSerializer.Deserialize<HotkeyConfig>(File.ReadAllText(settingsPath),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (Exception ex) { Console.WriteLine($"读取 {settingsPath} 失败（忽略）：{ex.Message}"); }
        }

        var cfg = new EffectiveConfig
        {
            Port = o.Port ?? file?.WebPort ?? 8088,
            Code = o.Code ?? file?.WebAccessCode ?? "",
            TlsMode = o.Tls ?? file?.EffectiveTlsMode ?? 0,
            AllowLan = o.Lan ?? file?.WebAllowLan ?? true,
            AllowExternal = o.External ?? file?.WebAllowExternal ?? false,
            TotpSecret = (o.TotpSecret ?? file?.EffectiveTotpSecret ?? "").Trim().ToUpperInvariant(),
            TotpHours = o.TotpHours ?? file?.WebTotpSessionHours ?? 12,
            ReadOnly = o.ReadOnly ?? file?.WebReadOnly ?? false,
        };
        if (cfg.Port < 1024 || cfg.Port > 65535) cfg.Port = 8088;
        return cfg;
    }

    private sealed class EffectiveConfig
    {
        public int Port { get; set; }
        public string Code { get; set; } = "";
        public int TlsMode { get; set; }
        public bool AllowLan { get; set; }
        public bool AllowExternal { get; set; }
        public string TotpSecret { get; set; } = "";
        public int TotpHours { get; set; } = 12;
        public bool ReadOnly { get; set; }
    }

    private sealed class Options
    {
        public string DataDir = DefaultDataDir();
        public string SettingsFile = "";
        public int? Port;
        public string? Code;
        public int? Tls;
        public bool? Lan;
        public bool? External;
        public string? TotpSecret;
        public int? TotpHours;
        public bool? ReadOnly;
        public bool Help;
        public bool GenTotp;
        public string? PrintCode;

        public static string DefaultDataDir()
        {
            var env = Environment.GetEnvironmentVariable("GALQUOTE_DATA");
            if (!string.IsNullOrWhiteSpace(env)) return env!;
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GalQuoteCollector");
        }

        public static Options Parse(string[] a)
        {
            var o = new Options();
            for (int i = 0; i < a.Length; i++)
            {
                string Next() => i + 1 < a.Length ? a[++i] : "";
                switch (a[i])
                {
                    case "-h" or "--help": o.Help = true; break;
                    case "--gen-totp": o.GenTotp = true; break;
                    case "--print-code": o.PrintCode = Next().Trim().ToUpperInvariant(); break;
                    case "--data": o.DataDir = Path.GetFullPath(Next()); break;
                    case "--settings": o.SettingsFile = Path.GetFullPath(Next()); break;
                    case "--port": if (int.TryParse(Next(), out var p)) o.Port = p; break;
                    case "--code": o.Code = Next(); break;
                    case "--tls": if (int.TryParse(Next(), out var t)) o.Tls = t; break;
                    case "--totp": o.TotpSecret = Next(); break;
                    case "--totp-hours": if (int.TryParse(Next(), out var h)) o.TotpHours = h; break;
                    case "--lan": o.Lan = true; break;
                    case "--no-lan": o.Lan = false; break;
                    case "--external": o.External = true; break;
                    case "--no-external": o.External = false; break;
                    case "--no-code": o.Code = ""; break;
                    case "--no-totp": o.TotpSecret = ""; break;
                    case "--read-only": o.ReadOnly = true; break;
                    case "--no-read-only": o.ReadOnly = false; break;
                    default:
                        Console.WriteLine($"未知参数：{a[i]}（用 --help 看用法）");
                        break;
                }
            }
            return o;
        }
    }

    private static void PrintHelp() => Console.WriteLine("""
galquote-server —— Gal Quote Tool 无界面版（Linux / Armbian / NAS / 树莓派跑网页服务）

用法：galquote-server [选项]
  --data <目录>        数据目录（含 quotes.db、截图、证书；默认 $GALQUOTE_DATA 或 ~/.local/share/GalQuoteCollector）
  --settings <文件>    直接读某份 settings.json（不指定就看数据目录里的那份）
  --port <端口>        网页端口（默认 8088）
  --code <访问码>      访问码；--no-code 清空
  --tls <0|1|2>        0=HTTP/HTTPS 自动兼容（默认） 1=仅 HTTP 2=仅 HTTPS
  --lan / --no-lan     是否允许局域网设备访问
  --external / --no-external
                       是否允许公网（tunnel / 反向代理）访问；默认关
  --totp <密钥>        两步验证密钥（Base32）；--no-totp 关闭
  --totp-hours <小时>  通过验证后免验证时长（默认 12）
  --read-only / --no-read-only
                       强制整个网页只读（连局域网也不能改）。放盒子/NAS 上当只读镜像时用，
                       保证同一时刻只有一个写入方，避免 SQLite 两边同时写。
  --gen-totp           生成一个两步验证密钥并打印 otpauth 链接
  --print-code <密钥>  打印当前 6 位动态码（核对手机用）
  -h, --help           帮助

例：
  galquote-server --data /var/lib/galquote --lan                 # 只给局域网看
  galquote-server --data /var/lib/galquote --external --totp XXX # 给公网看（要访问码 + 动态码）
""");
}
