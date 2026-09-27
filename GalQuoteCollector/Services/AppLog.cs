using System.IO;

namespace GalQuoteCollector.Services;

/// <summary>
/// Shared append-only diagnostic log (data dir\startup.log —— 默认 %LOCALAPPDATA%\GalQuoteCollector，
/// 或 GALQUOTE_DATA / exe 旁 data\ 指向的位置)，trimmed to its tail once it grows past 1 MB.
/// </summary>
public static class AppLog
{
    public static readonly string LogPath = Path.Combine(AppPaths.DataDirectory, "startup.log");

    public static void Write(string msg)
    {
        try
        {
            var dir = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var fi = new FileInfo(LogPath);
            if (fi.Exists && fi.Length > 1_000_000)
            {
                const int keepBytes = 200_000;
                var tail = new byte[keepBytes];
                using var fs = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                fs.Position = Math.Max(0, fs.Length - keepBytes);
                int read = fs.Read(tail, 0, keepBytes);
                File.WriteAllBytes(LogPath, tail[..read]);
            }
            File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {msg}\n");
        }
        catch { }
    }
}
