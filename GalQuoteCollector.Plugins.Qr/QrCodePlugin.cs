using GalQuoteCollector.Plugins;
using QRCoder;

namespace GalQuoteCollector.Plugins.Qr;

/// <summary>
/// 二维码插件（可选）：引用 QRCoder 把任意文本（这里是两步验证的 otpauth:// 链接）画成 PNG。
/// 主程序不依赖这个 DLL 也能跑——只是设置里「显示二维码」按钮会提示插件没装。
/// </summary>
public sealed class QrCodePlugin : IQrCodePlugin
{
    public string Name => "二维码生成";
    public string Description => "给两步验证（TOTP）生成二维码，手机扫一下就能添加，不用手输密钥。";
    public string Version => "1.0.0";

    public byte[] RenderPng(string text, int moduleSize)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<byte>();
        var size = Math.Clamp(moduleSize, 1, 40);
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data);
        return png.GetGraphic(size);
    }
}
