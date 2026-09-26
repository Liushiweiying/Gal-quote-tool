# Gal Quote Tool · 无界面版（Armbian / Linux / NAS）

手机、平板、电视盒子的浏览器打开就能看语录。**不需要装 .NET、不需要电脑开机**：这个版本把
电脑端那套网页服务单独拎出来，用同一份源码编译成 Linux 可执行文件，扔到 Armbian 盒子上常驻。

```
Windows 电脑（采集端）                      Armbian 盒子 / NAS（常驻端）
  WPF 应用：截图 / OCR / 回想 / 管理          galquote-server：网页 / API / 两步验证
        │                                            │
        └─────────── 同步 quotes.db + 截图 ──────────┘
                     （Syncthing / rclone / U 盘）
```

## 和电脑端的关系

- **同一份源码**：`WebServerService` / `WebPage` / `StorageService` / `TotpService` 等文件是
  用 `<Compile Include="..\GalQuoteCollector\...">` **链接编译**进来的，没有第二份拷贝。
  改电脑端 = 盒子端一起改，网页、API、访问码、两步验证、导入导出的行为逐字一致。
- **只有一处不同**：`/api/shot/{id}?bars=1|2`（服务端裁黑边）用的是 WPF + System.Drawing，
  在 Linux 上跑不了。无界面版用一个替身 `Shims/BlackBarCropper.Linux.cs` 返回"没有黑边"，
  于是照原图返回。想在电视上裁黑边，后续把这段搬到浏览器端 canvas（PC / 手机 / 盒子就统一了）。
- 设置读取用 `Shims/SettingsService.Linux.cs`：直接读数据目录里的 `settings.json`，
  所以**把电脑上的那份 settings.json 拷过来**，端口 / 访问码 / 两步验证 / 截图目录都会跟着走。

## 编译与安装

```bash
# 在 Windows 电脑上交叉编译（不需要 Linux 环境）；--self-contained 连 .NET 运行时都不用装
dotnet publish GalQuoteCollector.Server/GalQuoteCollector.Server.csproj \
  -c Release -r linux-arm64 --self-contained true -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -o publish-server-linux-arm64
```

产物是单个可执行文件（约 76 MB，arm64）。32 位系统用 `-r linux-arm`（约 66 MB）。
**注意**：优先刷 64 位（arm64）Armbian 镜像；32 位能跑但性能差一截。

把它和 `deploy/install-armbian.sh` 一起拷到盒子上：

```bash
sudo ./install-armbian.sh                 # 装到 /opt/galquote，只允许局域网访问
sudo ./install-armbian.sh --external      # 允许公网（Cloudflare tunnel / 反向代理）
journalctl -u galquote-server -f          # 看日志
```

不想用脚本就手动：拷到 `/opt/galquote/`，写个 systemd 服务（`deploy/galquote-server.service` 可参考）。

## 数据目录

默认 `~/.local/share/GalQuoteCollector`（也就是 `$GALQUOTE_DATA`），命令行 `--data` 可指定。
里面就是电脑端那几样东西：

| 文件 | 说明 |
|---|---|
| `quotes.db` | 语录库（SQLite，和电脑端同一个格式） |
| `settings.json` | 设置（可直接从电脑拷） |
| `web-cert.pfx` | 自签 HTTPS 证书（用 HTTPS 时自动生成） |
| `startup.log` | 日志 |
| 截图目录 | 由 settings.json 里的 `ScreenshotDirectory` 指定；为空则用 `~/Pictures/GalQuoteCollector` |

## 命令行选项（`--help` 看全部）

| 选项 | 作用 |
|---|---|
| `--data <目录>` | 数据目录 |
| `--port <端口>` | 端口（默认 8088） |
| `--code <访问码>` / `--no-code` | 访问码 |
| `--tls 0\|1\|2` | 0=HTTP/HTTPS 自动兼容（默认）1=仅 HTTP 2=仅 HTTPS |
| `--lan` / `--no-lan` | 是否允许局域网设备访问 |
| `--external` / `--no-external` | 是否允许公网（tunnel / 反向代理）访问，**默认关** |
| `--totp <密钥>` / `--no-totp` | 两步验证密钥；`--print-code` 打印当前动态码 |
| `--read-only` | **强制整个网页只读**（连局域网也不能改）——当只读镜像时用 |
| `--gen-totp` | 生成两步验证密钥 + otpauth 链接 |

安全默认与电脑端一致：局域网、公网、两步验证**都要显式打开**；公网一律只读 + 要访问码（+动态码）。

## 同步怎么做（重要）

SQLite 文件**不能两个进程同时写**，否则会冲突/损坏。所以规则只有一条：**同一时刻只有一个写入方**。

| 方案 | 谁写 | 同步方式 | 适合 |
|---|---|---|---|
| **A. 盒子只读镜像**（推荐先用这个） | 只有电脑 | Syncthing：电脑侧文件夹设 **Send Only**，盒子侧 **Receive Only**；盒子用 `--read-only` 启动 | 电脑关机也能看（看的是最后一次同步的内容）；最安全，不会冲突 |
| B. 盒子为主，电脑采集后推送 | 只有盒子 | 电脑侧网页/脚本把新语录 `POST /api/import`（ZIP）或同步推给盒子 | 想直接在手机/电视上改 |
| C. 双向 Syncthing（截图目录） | — | 截图**只增不改**，双向同步安全；`quotes.db` 仍然只走单向 | 截图太多、想在两边都留一份 |
| D. 云盘（OneDrive / WebDAV / NAS） | 一个写者 | rclone 定时 `copy`（截图）+ `copyto`（quotes.db，单向覆盖） | "云同步"的诉求；本质是 A 的云版本 |

- **不要**让 Syncthing 双向同步 `quotes.db`：冲突时它只保留一个版本，另一边会进 `.stversions`，
  容易出现"少了几条"的错觉。
- 截图目录几十 GB 时，盒子存不下就只同步**缩略图**（后续可以加个"只同步前 N 张/指定日期"的选项）。
- 双向同步状态下想临时在盒子上改：先停电脑端（或把电脑端也设成 `--read-only`），改完再同步回来。
