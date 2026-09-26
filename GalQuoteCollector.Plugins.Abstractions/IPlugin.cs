namespace GalQuoteCollector.Plugins;

/// <summary>
/// 插件契约。主程序【不】直接引用任何插件，只是启动时扫描两个目录：
///   ① 程序目录\plugins\     ② %LOCALAPPDATA%\GalQuoteCollector\plugins\（单文件版用这个）
/// 把实现下面接口的 DLL 丢进去、重启程序就会自动加载；没有插件程序也照常工作。
/// 想要自己写插件：建一个类库，引用 GalQuoteCollector.Plugins.Abstractions.dll + QRCoder 之类的依赖即可。
/// </summary>
public interface IGalQuotePlugin
{
    /// <summary>插件名（显示在日志/设置里）。</summary>
    string Name { get; }
    /// <summary>一句话说明这个插件干什么。</summary>
    string Description { get; }
    /// <summary>版本号（自己维护）。</summary>
    string Version { get; }
}

/// <summary>
/// 可选能力：把一段文本画成二维码 PNG。
/// 主程序用它来显示两步验证（TOTP）的 otpauth 链接——没装插件时只能手动输入密钥。
/// </summary>
public interface IQrCodePlugin : IGalQuotePlugin
{
    /// <summary>把 text 画成二维码，返回 PNG 字节。moduleSize = 每个小方块的像素（建议 6~10）。</summary>
    byte[] RenderPng(string text, int moduleSize);
}
