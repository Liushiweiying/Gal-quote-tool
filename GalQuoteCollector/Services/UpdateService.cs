using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace GalQuoteCollector.Services;

/// <summary>How this copy of the app was deployed — updates must keep the same form.</summary>
public enum InstallForm
{
    /// <summary>Inno Setup install (unins000.exe / registered InstallLocation) → in-place upgrade via Setup.exe.</summary>
    Installer,
    /// <summary>Single-file exe (no managed dll beside it) → replace the exe in place.</summary>
    SingleFile,
    /// <summary>Extracted folder build (dll + deps.json beside the exe) → replace the folder contents.</summary>
    Folder
}

/// <summary>
/// GitHub Releases based update check &amp; download. The asset is chosen to match the
/// current deployment form so every install keeps its existing upgrade style:
/// installer → Setup.exe (same AppId, same directory, data untouched),
/// single file → that exe, folder build → publish-folder.zip.
/// Downloads are verified against the asset's sha256 digest when GitHub provides one.
/// </summary>
public class UpdateService
{
    private const string AppId = "{B4F8C1A2-3D5E-4F67-9A0B-1C2D3E4F5G6H}";

    public class UpdateInfo
    {
        public string Tag { get; set; } = "";
        public string Body { get; set; } = "";
        public string AssetUrl { get; set; } = "";
        public string AssetName { get; set; } = "";
        public string Digest { get; set; } = ""; // "sha256:…" or empty
        public InstallForm Form { get; set; } = InstallForm.Installer;
        /// <summary>True when the form-matching asset was missing and Setup.exe was used instead.</summary>
        public bool FellBackToSetup { get; set; }
    }

    private const string LatestApi =
        "https://api.github.com/repos/Liushiweiying/Gal-quote-tool/releases/latest";

    public static string FormLabel(InstallForm form) => form switch
    {
        InstallForm.Installer => "安装版（Setup 升级）",
        InstallForm.SingleFile => "单文件版（替换 exe）",
        _ => "文件夹版（覆盖目录）"
    };

    /// <summary>Which release asset this deployment should update from.</summary>
    public static string PreferredAssetName(InstallForm form)
    {
        if (form == InstallForm.Installer) return "Gal-quote-tool_Setup.exe";
        if (form == InstallForm.Folder) return "publish-folder.zip";

        var exeName = Path.GetFileName(Environment.ProcessPath ?? "Gal-quote-tool.exe");
        return exeName.Contains("selfcontained", StringComparison.OrdinalIgnoreCase)
            ? "Gal-quote-tool_selfcontained.exe"
            : "Gal-quote-tool.exe";
    }

    /// <summary>
    /// Detect how the running copy was installed. Installer first (the installer deploys a
    /// folder layout too, so unins000.exe / the registry entry is the distinguishing mark),
    /// then folder build, otherwise a single-file exe.
    /// </summary>
    public static InstallForm DetectInstallForm()
    {
        try
        {
            var dir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            if (File.Exists(Path.Combine(dir, "unins000.exe"))) return InstallForm.Installer;
            if (IsRegisteredInstallLocation(dir)) return InstallForm.Installer;

            if (File.Exists(Path.Combine(dir, "Gal-quote-tool.dll")) &&
                File.Exists(Path.Combine(dir, "Gal-quote-tool.deps.json")))
                return InstallForm.Folder;
        }
        catch { }

        return InstallForm.SingleFile;
    }

    private static bool IsRegisteredInstallLocation(string dir)
    {
        string[] subKeys =
        {
            $@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{AppId}_is1",
            $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{AppId}_is1"
        };
        var roots = new[] { Registry.LocalMachine, Registry.CurrentUser };
        foreach (var root in roots)
        {
            foreach (var sk in subKeys)
            {
                try
                {
                    using var key = root.OpenSubKey(sk);
                    var loc = key?.GetValue("InstallLocation") as string;
                    if (string.IsNullOrWhiteSpace(loc)) continue;
                    if (string.Equals(loc.TrimEnd(Path.DirectorySeparatorChar), dir,
                            StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch { }
            }
        }
        return false;
    }

    /// <summary>Returns update info when a newer version exists, otherwise null.</summary>
    public async Task<UpdateInfo?> CheckAsync(string currentVersion, int timeoutSec = 10)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSec) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Gal-quote-tool/{currentVersion}");
        var jsonStr = await http.GetStringAsync(LatestApi);

        using var doc = JsonDocument.Parse(jsonStr);
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString();
        if (string.IsNullOrWhiteSpace(tag)) return null;
        if (!IsNewer(tag, currentVersion)) return null;

        var form = DetectInstallForm();
        var preferred = PreferredAssetName(form);
        var info = new UpdateInfo { Tag = tag, Form = form };
        info.Body = root.TryGetProperty("body", out var bodyEl) ? bodyEl.GetString() ?? "" : "";

        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            string? fallbackUrl = null, fallbackName = null, fallbackDigest = null;
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.GetProperty("name").GetString() ?? "";
                var url = a.GetProperty("browser_download_url").GetString() ?? "";
                var digest = a.TryGetProperty("digest", out var d) ? d.GetString() ?? "" : "";

                if (string.Equals(name, preferred, StringComparison.OrdinalIgnoreCase))
                {
                    info.AssetUrl = url;
                    info.AssetName = name;
                    info.Digest = digest;
                    break;
                }
                if (name.EndsWith("_Setup.exe", StringComparison.OrdinalIgnoreCase))
                {
                    fallbackUrl = url;
                    fallbackName = name;
                    fallbackDigest = digest;
                }
            }

            // Form-matching asset missing (older release) → fall back to the installer
            if (string.IsNullOrEmpty(info.AssetUrl) && fallbackUrl != null)
            {
                info.AssetUrl = fallbackUrl;
                info.AssetName = fallbackName!;
                info.Digest = fallbackDigest ?? "";
                info.FellBackToSetup = form != InstallForm.Installer;
                AppLog.Write($"update: '{preferred}' not found, falling back to {info.AssetName}");
            }
        }
        return info;
    }

    public static bool IsNewer(string latestTag, string currentVersion)
    {
        var l = TryParseVersion(latestTag);
        var c = TryParseVersion(currentVersion);
        return l != null && c != null && l > c;
    }

    private static Version? TryParseVersion(string v)
    {
        v = v.Trim().TrimStart('v', 'V');
        return Version.TryParse(v, out var ver) ? ver : null;
    }

    /// <summary>
    /// Download the asset to <paramref name="destDir"/> with progress reporting,
    /// verifying the sha256 digest when the release provides one.
    /// </summary>
    public async Task<string> DownloadAsync(UpdateInfo info, string destDir, IProgress<double>? progress = null)
    {
        Directory.CreateDirectory(destDir);
        var dest = Path.Combine(destDir, info.AssetName);

        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Gal-quote-tool/update");
        using var resp = await http.GetAsync(info.AssetUrl, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();

        long total = resp.Content.Headers.ContentLength ?? 0;
        long readTotal = 0;
        using (var fs = File.Create(dest))
        using (var stream = await resp.Content.ReadAsStreamAsync())
        {
            var buffer = new byte[81920];
            int n;
            while ((n = await stream.ReadAsync(buffer)) > 0)
            {
                await fs.WriteAsync(buffer, 0, n);
                readTotal += n;
                if (total > 0) progress?.Report((double)readTotal / total);
            }
        }
        progress?.Report(1.0);

        // Verify the sha256 digest when GitHub provides one
        if (info.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            var expected = info.Digest["sha256:".Length..].Trim().ToLowerInvariant();
            var actual = ComputeSha256(dest);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(dest); } catch { }
                throw new InvalidDataException("下载文件校验失败（SHA256 不匹配），已删除");
            }
        }
        return dest;
    }

    private static string ComputeSha256(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }

    /// <summary>
    /// Start the upgrade for the detected form. A small PowerShell helper waits for this
    /// process to exit (so file locks are gone), then performs the form-appropriate step:
    ///   Installer → run Setup.exe (same AppId / same folder → in-place upgrade)
    ///   SingleFile → replace the running exe and relaunch it
    ///   Folder → extract the zip over the install folder and relaunch
    /// </summary>
    public static bool StartApply(InstallForm form, string downloadedPath)
    {
        try
        {
            var exePath = Environment.ProcessPath ?? "";
            var dir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

            string action = form switch
            {
                InstallForm.Installer => "run",
                InstallForm.SingleFile => "replace",
                _ => "extract"
            };

            var script = """
                param([int]$WaitPid,[string]$Action,[string]$Source,[string]$Target,[string]$Exe)
                try { Wait-Process -Id $WaitPid -Timeout 120 -ErrorAction SilentlyContinue } catch {}
                Start-Sleep -Seconds 2
                switch ($Action) {
                  'run' { Start-Process -FilePath $Source }
                  'replace' {
                    $old = $Target + '.old'
                    if (Test-Path -LiteralPath $old) { Remove-Item -LiteralPath $old -Force -ErrorAction SilentlyContinue }
                    if (Test-Path -LiteralPath $Target) { Move-Item -LiteralPath $Target -Destination $old -Force }
                    Copy-Item -LiteralPath $Source -Destination $Target -Force
                    Start-Process -FilePath $Target
                    Start-Sleep -Seconds 2
                    Remove-Item -LiteralPath $old -Force -ErrorAction SilentlyContinue
                  }
                  'extract' {
                    Expand-Archive -LiteralPath $Source -DestinationPath $Target -Force
                    Start-Process -FilePath $Exe
                  }
                }
                Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
                """;

            var scriptPath = Path.Combine(Path.GetTempPath(), $"gal-update-{Guid.NewGuid():N}.ps1");
            File.WriteAllText(scriptPath, script);

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(scriptPath);
            psi.ArgumentList.Add("-WaitPid");
            psi.ArgumentList.Add(Environment.ProcessId.ToString());
            psi.ArgumentList.Add("-Action");
            psi.ArgumentList.Add(action);
            psi.ArgumentList.Add("-Source");
            psi.ArgumentList.Add(downloadedPath);
            psi.ArgumentList.Add("-Target");
            psi.ArgumentList.Add(form == InstallForm.SingleFile ? exePath : dir);
            psi.ArgumentList.Add("-Exe");
            psi.ArgumentList.Add(exePath);

            Process.Start(psi);
            AppLog.Write($"update: apply started (form={form}, action={action}, source={downloadedPath})");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Write($"update: apply failed: {ex}");
            return false;
        }
    }
}
