using System.Security.Cryptography;
using System.Text;

namespace GalQuoteCollector.Services;

/// <summary>
/// 两步验证（TOTP，RFC 6238：和 Microsoft / Google Authenticator、Aegis 等验证器 App 通用）。
/// 只用 .NET 自带的 HMAC，不需要任何第三方库，也不需要联网。
/// 密钥用 Base32 保存（验证器 App 手动输入用的就是它）。
/// </summary>
public static class TotpService
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567"; // RFC 4648 Base32
    private const int Step = 30;   // 30 秒一步（所有验证器 App 的默认值）
    private const int Digits = 6;

    /// <summary>生成新的随机密钥（20 字节 = 160 位，Google 推荐长度）。</summary>
    public static string NewSecret(int bytes = 20)
    {
        var buf = RandomNumberGenerator.GetBytes(bytes);
        return Base32Encode(buf);
    }

    public static string Base32Encode(byte[] data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int bits = 0, value = 0;
        foreach (var b in data)
        {
            value = (value << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Alphabet[(value >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) sb.Append(Alphabet[(value << (5 - bits)) & 31]);
        return sb.ToString();
    }

    public static byte[] Base32Decode(string s)
    {
        var clean = new string((s ?? "").ToUpperInvariant().Where(c => Alphabet.IndexOf(c) >= 0).ToArray());
        var outBytes = new List<byte>(clean.Length * 5 / 8);
        int bits = 0, value = 0;
        foreach (var c in clean)
        {
            value = (value << 5) | Alphabet.IndexOf(c);
            bits += 5;
            if (bits >= 8)
            {
                outBytes.Add((byte)((value >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }
        return outBytes.ToArray();
    }

    /// <summary>当前（或指定时刻）的 6 位动态码。</summary>
    public static string Code(string secret, DateTimeOffset? at = null, int step = Step, int digits = Digits)
    {
        var key = Base32Decode(secret);
        if (key.Length == 0) return "";
        long counter = (at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() / step;
        var msg = new byte[8];
        for (int i = 7; i >= 0; i--) { msg[i] = (byte)(counter & 0xFF); counter >>= 8; }
        using var hmac = new HMACSHA1(key);
        var hash = hmac.ComputeHash(msg);
        int off = hash[^1] & 0x0F;
        int bin = ((hash[off] & 0x7F) << 24) | ((hash[off + 1] & 0xFF) << 16)
                | ((hash[off + 2] & 0xFF) << 8) | (hash[off + 3] & 0xFF);
        int mod = 1;
        for (int i = 0; i < digits; i++) mod *= 10;
        return (bin % mod).ToString(new string('0', digits));
    }

    /// <summary>校验动态码；window=1 表示允许前后各 30 秒（手机时钟略偏也能过）。</summary>
    public static bool Verify(string secret, string code, int window = 1, DateTimeOffset? at = null)
    {
        var given = (code ?? "").Trim().Replace(" ", "");
        if (given.Length != Digits || !given.All(char.IsDigit)) return false;
        var now = at ?? DateTimeOffset.UtcNow;
        for (int i = -window; i <= window; i++)
            if (FixedEquals(Code(secret, now.AddSeconds(i * Step)), given)) return true;
        return false;
    }

    /// <summary>给验证器 App 的链接（二维码里装的就是这个字符串）。</summary>
    public static string OtpauthUri(string secret, string account, string issuer = "Gal Quote Tool")
    {
        var label = Uri.EscapeDataString($"{issuer}:{account}");
        return $"otpauth://totp/{label}?secret={secret}&issuer={Uri.EscapeDataString(issuer)}"
             + $"&algorithm=SHA1&digits={Digits}&period={Step}";
    }

    /// <summary>把密钥按 4 位一组排开，方便手动输入到手机里。</summary>
    public static string Group(string secret, int size = 4)
    {
        var s = secret ?? "";
        var parts = new List<string>();
        for (int i = 0; i < s.Length; i += size) parts.Add(s.Substring(i, Math.Min(size, s.Length - i)));
        return string.Join(" ", parts);
    }

    // ── 登录会话（验证通过后不用每次输入动态码）──

    private static byte[] SessionKey(string secret) =>
        SHA256.HashData(Encoding.UTF8.GetBytes("galquote-totp-session|" + secret));

    /// <summary>生成会话令牌：{过期时间戳}.{HMAC}。服务端不存状态，重启也有效。</summary>
    public static string SessionToken(string secret, TimeSpan lifetime)
    {
        long exp = DateTimeOffset.UtcNow.Add(lifetime).ToUnixTimeSeconds();
        using var hmac = new HMACSHA256(SessionKey(secret));
        var mac = hmac.ComputeHash(Encoding.UTF8.GetBytes("session:" + exp));
        return exp + "." + Convert.ToHexString(mac).ToLowerInvariant();
    }

    public static bool ValidateSessionToken(string secret, string? token)
    {
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(token)) return false;
        var dot = token.IndexOf('.');
        if (dot <= 0) return false;
        if (!long.TryParse(token[..dot], out var exp)) return false;
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > exp) return false;
        using var hmac = new HMACSHA256(SessionKey(secret));
        var mac = hmac.ComputeHash(Encoding.UTF8.GetBytes("session:" + exp));
        return FixedEquals(Convert.ToHexString(mac).ToLowerInvariant(), token[(dot + 1)..].Trim().ToLowerInvariant());
    }

    /// <summary>定长比较，避免时序侧信道。</summary>
    public static bool FixedEquals(string a, string b)
    {
        var x = Encoding.UTF8.GetBytes(a ?? "");
        var y = Encoding.UTF8.GetBytes(b ?? "");
        return x.Length == y.Length && CryptographicOperations.FixedTimeEquals(x, y);
    }
}
