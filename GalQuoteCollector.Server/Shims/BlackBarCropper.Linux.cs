using System.Drawing;

namespace GalQuoteCollector.Services;

/// <summary>
/// Linux / 非 Windows 端的 <see cref="BlackBarCropper"/> 替身。
/// 真身依赖 WPF 的 Rect 与 System.Drawing 的像素操作，在 Linux 上跑不了（System.Drawing 在
/// .NET 8 的 Linux 上直接抛 PlatformNotSupportedException）。无界面版不需要在服务端改图：
///   · 网页请求 <c>/api/shot/{id}?bars=1|2</c> 时返回"没有黑边"，于是照原图返回；
///   · 想在电视上裁黑边，交给浏览器端（后续把黑边处理搬到前端 canvas，PC / 手机 / 盒子就都一致了）。
/// 只有被链接的源码里用到的那个重载需要存在，所以这里只提供 <c>Detect(Bitmap)</c>。
/// </summary>
public static class BlackBarCropper
{
    /// <summary>非 Windows 端：永远返回"整张图都是内容"（= 没有黑边）。</summary>
    public static Rectangle Detect(Bitmap bmp, int darkThreshold = 34, double darkRatio = 0.985)
        => new(0, 0, Math.Max(0, bmp.Width - 1), Math.Max(0, bmp.Height - 1));
}
