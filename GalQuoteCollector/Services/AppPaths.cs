using System;
using System.IO;

namespace GalQuoteCollector.Services;

/// <summary>
/// 数据目录解析（v1.3.6 起可配置，方便双系统 / 便携 / 放 NAS）：
///   ① 环境变量 <c>GALQUOTE_DATA</c>（用户级或系统级都行）
///   ② exe 同级的 <c>data\</c> 目录里**有标记文件</c>.galquote-portable</c> → 便携模式
///      （只认标记，不认"文件夹是否存在" —— 否则一个碰巧叫 data 的无关文件夹就会把数据目录
///        悄悄搬走，用户会以为"语录全丢了"。设置界面里的「切换数据目录…」会自动建这个标记）
///   ③ 默认 <c>%LOCALAPPDATA%\GalQuoteCollector</c>
/// 整个进程里只解析一次（启动后不会变），所以可以放心缓存。
/// </summary>
public static class AppPaths
{
    /// <summary>便携模式的标记文件名（放在 &lt;exe&gt;\data\ 里）。</summary>
    public const string PortableMarkerName = ".galquote-portable";

    private static string? _dataDir;

    /// <summary>实际使用的数据目录（quotes.db / settings.json / usage.json / web-cert.pfx / startup.log 都在这）。</summary>
    public static string DataDirectory => _dataDir ??= Resolve();

    /// <summary>这个目录是怎么定下来的（设置界面里显示给用户看）。</summary>
    public static string Source { get; private set; } = "";

    /// <summary>默认位置：%LOCALAPPDATA%\GalQuoteCollector</summary>
    public static string DefaultDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GalQuoteCollector");

    /// <summary>重命名前的旧目录（GalgameQuoteCollector），只读一次做迁移。</summary>
    public static string LegacyDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GalgameQuoteCollector");

    /// <summary>便携位置：exe 旁边的 data\</summary>
    public static string PortableDataDirectory
    {
        get
        {
            try { return Path.Combine(AppContext.BaseDirectory, "data"); }
            catch { return ""; }
        }
    }

    /// <summary>便携模式的标记文件完整路径。</summary>
    public static string PortableMarkerPath
    {
        get
        {
            var dir = PortableDataDirectory;
            return dir.Length == 0 ? "" : Path.Combine(dir, PortableMarkerName);
        }
    }

    /// <summary>把一个目录设成便携数据目录：建好文件夹 + 写标记文件。</summary>
    public static bool EnablePortableMode(out string message)
    {
        try
        {
            var dir = PortableDataDirectory;
            if (dir.Length == 0) { message = "取不到程序目录"; return false; }
            Directory.CreateDirectory(dir);
            File.WriteAllText(PortableMarkerPath,
                "这个文件告诉 Gal Quote Tool：把数据放在同级的 data 文件夹里（便携模式）。\r\n删除它就会回到默认的 %LOCALAPPDATA%\\GalQuoteCollector。\r\n");
            message = $"已开启便携模式：数据将存放在 {dir}（重启程序后生效）";
            return true;
        }
        catch (Exception ex)
        {
            message = "开启便携模式失败：" + ex.Message;
            return false;
        }
    }

    /// <summary>把 GALQUOTE_DATA 写到当前用户的环境变量里（重启程序后生效）。</summary>
    public static bool SetEnvironmentDataDirectory(string dir, out string message)
    {
        try
        {
            Environment.SetEnvironmentVariable(EnvironmentVariableName, dir, EnvironmentVariableTarget.User);
            message = $"已设置用户环境变量 {EnvironmentVariableName} = {dir}（重启程序后生效）";
            return true;
        }
        catch (Exception ex)
        {
            message = "设置环境变量失败：" + ex.Message;
            return false;
        }
    }

    public static string EnvironmentVariableName => "GALQUOTE_DATA";

    /// <summary>给界面用的一句话说明。</summary>
    public static string Describe() => $"{DataDirectory}（{Source}）";

    private static string Resolve()
    {
        // ① 环境变量优先
        try
        {
            var env = Environment.GetEnvironmentVariable(EnvironmentVariableName);
            if (!string.IsNullOrWhiteSpace(env))
            {
                var full = Path.GetFullPath(env.Trim().Trim('"'));
                Source = $"来自环境变量 {EnvironmentVariableName}";
                return full;
            }
        }
        catch { }

        // ② 便携模式：exe 旁边的 data\ 里必须有标记文件（只认标记，见类注释）
        try
        {
            var portable = PortableDataDirectory;
            if (portable.Length > 0 && File.Exists(Path.Combine(portable, PortableMarkerName)))
            {
                Source = "程序目录旁的 data\\ 文件夹（便携模式）";
                return portable;
            }
        }
        catch { }

        // ③ 默认
        Source = "默认位置（%LOCALAPPDATA%）";
        return DefaultDataDirectory;
    }
}
