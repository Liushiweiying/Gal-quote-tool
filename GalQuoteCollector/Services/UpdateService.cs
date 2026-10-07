using System.Collections.Generic;
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

        /// <summary>检测到的部署形态（界面显示用；用户可以在界面上改成别的形态）。</summary>
        public InstallForm DetectedForm { get; set; } = InstallForm.Installer;

        /// <summary>这个 release 里真正存在的、各部署形态可用的资产（形态 → 资产名）。</summary>
        public Dictionary<InstallForm, string> AvailableAssets { get; } = new();

        /// <summary>资产名 → (下载地址, sha256 digest)。</summary>
        internal Dictionary<string, (string url, string digest)> AssetMap { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public bool CanUse(InstallForm form) => AvailableAssets.ContainsKey(form);

        /// <summary>切换到某个部署形态对应的资产（用户在界面上手动选的时候调）。</summary>
        public bool UseForm(InstallForm form)
        {
            if (!AvailableAssets.TryGetValue(form, out var name)) return false;
            if (!AssetMap.TryGetValue(name, out var a)) return false;
            Form = form;
            AssetName = name;
            AssetUrl = a.url;
            Digest = a.digest;
            FellBackToSetup = false;
            return true;
        }
    }

    private const string LatestApi =
        "https://api.github.com/repos/Liushiweiying/Gal-quote-tool/releases/latest";
    /// <summary>含预发布的列表端点（GitHub 的 /releases/latest 不含预发布，所以「接收测试版」要走这个）。</summary>
    private const string ReleasesApi =
        "https://api.github.com/repos/Liushiweiying/Gal-quote-tool/releases?per_page=20";

    public static string FormLabel(InstallForm form) => form switch
    {
        InstallForm.Installer => "安装版（Setup 升级）",
        InstallForm.SingleFile => "单文件版（替换 exe）",
        _ => "文件夹版（覆盖目录）"
    };

    /// <summary>
    /// 哪种资产对应这种部署形态。
    /// 单文件版要注意**自包含版**必须下 `_selfcontained`（否则替换完没有 .NET 运行时可能起不来）：
    /// 光看文件名不够（用户可能把它改名成 Gal-quote-tool.exe），所以再按体积判断（FDD ~27MB / SCD ~181MB）。
    /// </summary>
    public static string PreferredAssetName(InstallForm form)
    {
        var exe = Environment.ProcessPath ?? "";
        long size = 0;
        try { if (File.Exists(exe)) size = new FileInfo(exe).Length; } catch { }
        return PreferredAssetName(form, Path.GetFileName(exe), size);
    }

    /// <summary>
    /// 同上，但显式传入 exe 名字与体积（抽出来是为了能单测：
    /// **改名后的自包含版必须仍然下 `_selfcontained`**，否则替换完没有 .NET 运行时可能起不来）。
    /// </summary>
    public static string PreferredAssetName(InstallForm form, string exeName, long exeSizeBytes)
    {
        if (form == InstallForm.Installer) return "Gal-quote-tool_Setup.exe";
        if (form == InstallForm.Folder) return "publish-folder.zip";

        var looksSelfContained = (exeName ?? "").Contains("selfcontained", StringComparison.OrdinalIgnoreCase)
                                 || exeSizeBytes > 80L * 1024 * 1024;
        return looksSelfContained ? "Gal-quote-tool_selfcontained.exe" : "Gal-quote-tool.exe";
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
            if (File.Exists(Path.Combine(dir, "unins000.dat"))) return InstallForm.Installer;
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
        string[] uninstallRoots =
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        };
        var roots = new[] { Registry.LocalMachine, Registry.CurrentUser };
        foreach (var root in roots)
        {
            foreach (var sk in subKeys)
            {
                try
                {
                    using var key = root.OpenSubKey(sk);
                    if (MatchesLocation(key?.GetValue("InstallLocation") as string, dir)) return true;
                }
                catch { }
            }

            // 兜底：遍历卸载项按显示名匹配 —— AppId 变过、或者安装目录被搬过（记的是老路径）也能认出来
            foreach (var ur in uninstallRoots)
            {
                try
                {
                    using var un = root.OpenSubKey(ur);
                    if (un == null) continue;
                    foreach (var sub in un.GetSubKeyNames())
                    {
                        using var k = un.OpenSubKey(sub);
                        var dn = k?.GetValue("DisplayName") as string;
                        if (string.IsNullOrEmpty(dn) ||
                            !dn.Contains("Gal Quote", StringComparison.OrdinalIgnoreCase)) continue;
                        if (MatchesLocation(k!.GetValue("InstallLocation") as string, dir)) return true;
                    }
                }
                catch { }
            }
        }
        return false;
    }

    private static bool MatchesLocation(string? location, string dir)
        => !string.IsNullOrWhiteSpace(location)
           && string.Equals(location.TrimEnd(Path.DirectorySeparatorChar), dir, StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns update info when a newer version exists, otherwise null.
    /// <paramref name="includePrerelease"/> = true 时改用 releases 列表端点，把测试版（beta）也算进来。</summary>
    public async Task<UpdateInfo?> CheckAsync(string currentVersion, bool includePrerelease = false, int timeoutSec = 10)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSec) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Gal-quote-tool/{currentVersion}");
        var jsonStr = await http.GetStringAsync(includePrerelease ? ReleasesApi : LatestApi);

        using var doc = JsonDocument.Parse(jsonStr);
        JsonElement root;

        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            // 列表端点：跳过草稿，挑版本号最高的那个——注意要把同号的正式版排在 beta 前面
            JsonElement? best = null;
            string? bestTag = null;
            foreach (var rel in doc.RootElement.EnumerateArray())
            {
                if (rel.TryGetProperty("draft", out var draft) && draft.GetBoolean()) continue;
                var t = rel.TryGetProperty("tag_name", out var te) ? te.GetString() : null;
                if (string.IsNullOrWhiteSpace(t)) continue;
                if (!IsNewer(t!, currentVersion)) continue;
                if (bestTag == null || CompareVersions(t!, bestTag) > 0)
                {
                    best = rel;
                    bestTag = t;
                }
            }
            if (best == null) return null;
            root = best.Value;
            AppLog.Write($"update: 含测试版检查 → 选中 {bestTag}（当前 {currentVersion}）");
        }
        else
        {
            root = doc.RootElement;
        }

        var tag = root.GetProperty("tag_name").GetString();
        if (string.IsNullOrWhiteSpace(tag)) return null;
        if (!IsNewer(tag, currentVersion)) return null;

        var body = root.TryGetProperty("body", out var bodyEl) ? bodyEl.GetString() ?? "" : "";
        var assets = new List<(string name, string url, string digest)>();
        if (root.TryGetProperty("assets", out var assetsEl) && assetsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assetsEl.EnumerateArray())
            {
                assets.Add((
                    a.GetProperty("name").GetString() ?? "",
                    a.GetProperty("browser_download_url").GetString() ?? "",
                    a.TryGetProperty("digest", out var d) ? d.GetString() ?? "" : ""));
            }
        }
        return BuildUpdateInfo(tag, body, assets, DetectInstallForm());
    }

    /// <summary>
    /// 从 release 的资产列表里挑出与部署形态匹配的那个。
    /// （抽成纯函数是为了能单测：直接喂「资产列表 + 形态」，不依赖本机环境。）
    /// 规则：优先当前形态对应的包；没有就退回安装包并标记 <see cref="UpdateInfo.FellBackToSetup"/>；
    /// 同时把各形态可用的资产登记进 <see cref="UpdateInfo.AvailableAssets"/>，界面据此让用户手动切换。
    /// </summary>
    public static UpdateInfo BuildUpdateInfo(string tag, string body,
        IEnumerable<(string name, string url, string digest)> assets, InstallForm form)
    {
        var info = new UpdateInfo { Tag = tag, Body = body, DetectedForm = form, Form = form };
        foreach (var a in assets)
            if (!string.IsNullOrWhiteSpace(a.name))
                info.AssetMap[a.name] = (a.url, a.digest);

        // 登记各形态可用的资产（单文件版有两种可能的名字，看 release 里实际有哪个）
        foreach (var f in new[] { InstallForm.Installer, InstallForm.Folder, InstallForm.SingleFile })
        {
            var preferred = PreferredAssetName(f);
            if (info.AssetMap.ContainsKey(preferred)) { info.AvailableAssets[f] = preferred; continue; }
            if (f != InstallForm.SingleFile) continue;
            foreach (var alt in new[] { "Gal-quote-tool.exe", "Gal-quote-tool_selfcontained.exe" })
                if (info.AssetMap.ContainsKey(alt)) { info.AvailableAssets[f] = alt; break; }
        }

        if (info.UseForm(form)) return info;

        if (info.UseForm(InstallForm.Installer))
        {
            info.FellBackToSetup = form != InstallForm.Installer;
            AppLog.Write($"update: '{PreferredAssetName(form)}' 在 release 里不存在，改用 {info.AssetName}");
        }
        else
        {
            AppLog.Write($"update: release 里没有任何可用资产（tag={tag}）");
        }
        return info;
    }

    public static bool IsNewer(string latestTag, string currentVersion) => CompareVersions(latestTag, currentVersion) > 0;

    /// <summary>
    /// 比较两个版本标签（支持 `v1.3.6`、`1.3.6-beta`、`v1.3.6-beta.2` 这种写法）：
    /// 先比数字，数字相同则**正式版 &gt; 预发布**（"1.3.6" 比 "1.3.6-beta" 新），
    /// 两个都是预发布就按后缀字符串比（beta &lt; beta.2）。返回 &gt;0 表示 a 更新。
    /// </summary>
    public static int CompareVersions(string a, string b)
    {
        var pa = ParseRelease(a);
        var pb = ParseRelease(b);
        if (pa == null || pb == null) return 0;
        var cmp = pa.Value.num.CompareTo(pb.Value.num);
        if (cmp != 0) return cmp;
        var sa = pa.Value.pre;
        var sb = pb.Value.pre;
        if (sa.Length == 0 && sb.Length == 0) return 0;
        if (sa.Length == 0) return 1;   // 正式版 > 同号预发布
        if (sb.Length == 0) return -1;
        return string.Compare(sa, sb, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>把 "v1.3.6-beta.2+abc" 拆成 (1.3.6, "beta.2")；解析不了返回 null。</summary>
    private static (Version num, string pre)? ParseRelease(string v)
    {
        v = (v ?? "").Trim().TrimStart('v', 'V');
        if (v.Length == 0) return null;
        var plus = v.IndexOf('+');
        if (plus >= 0) v = v[..plus];
        var dash = v.IndexOf('-');
        var numPart = dash >= 0 ? v[..dash] : v;
        var pre = dash >= 0 ? v[(dash + 1)..] : "";
        return Version.TryParse(numPart, out var ver) ? (ver, pre) : null;
    }

    private static Version? TryParseVersion(string v)
    {
        v = v.Trim().TrimStart('v', 'V');
        return Version.TryParse(v, out var ver) ? ver : null;
    }

    /// <summary>
    /// 下载更新包到**独立的临时目录**，返回下载到的文件路径（sha256 校验在下载后立即做）。
    ///
    /// ⚠ 千万不要下到运行目录或「下载」文件夹里的同名文件上：单文件版常常就跑在
    /// `C:\Users\…\Downloads\Gal-quote-tool.exe`，而资产名恰好也是 `Gal-quote-tool.exe`，
    /// 于是 File.Create 的目标就是**正在运行的 exe** → Windows 文件锁 →
    /// “The process cannot access the file … because it is being used by another process”（2026-10-07 用户实测）。
    /// 替换/解压统一交给 <see cref="StartApply"/> 的 PowerShell 助手，在本进程退出后再做。
    /// </summary>
    public async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "gal-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, info.AssetName);

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
                # 清理下载用的临时目录（'run' 时不删：安装器还在跑，它自己从那儿启动的）
                if ($Action -ne 'run') {
                  try {
                    $tmpDir = Split-Path -Parent $Source
                    if ($tmpDir -and (Test-Path -LiteralPath $tmpDir) -and $tmpDir -like '*gal-update-*') {
                      Remove-Item -LiteralPath $tmpDir -Recurse -Force -ErrorAction SilentlyContinue
                    }
                  } catch {}
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
