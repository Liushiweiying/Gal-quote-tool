#!/usr/bin/env bash
# Gal Quote Tool 无界面版 —— Armbian / Debian 一键安装脚本
#
# 用法（在盒子上，把 galquote-server 和本脚本放在同一个目录）：
#   chmod +x install-armbian.sh
#   sudo ./install-armbian.sh                       # 装到 /opt/galquote，只允许局域网访问
#   sudo ./install-armbian.sh --external            # 允许公网（Cloudflare tunnel / 反代）访问
#   sudo ./install-armbian.sh --user pi --data /srv/galquote
#
# 装完：
#   systemctl status galquote-server
#   journalctl -u galquote-server -f
#   浏览器打开 http://<盒子IP>:8088/

set -euo pipefail

APP_DIR=/opt/galquote
DATA_DIR="$APP_DIR/data"
RUN_USER="${SUDO_USER:-armbian}"
EXTRA_ARGS="--lan"
BIN_SRC="$(cd "$(dirname "$0")" && pwd)/galquote-server"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --user) RUN_USER="$2"; shift 2 ;;
    --data) DATA_DIR="$2"; shift 2 ;;
    --dir)  APP_DIR="$2"; shift 2 ;;
    --external) EXTRA_ARGS="--lan --external"; shift ;;
    --lan-only) EXTRA_ARGS="--lan"; shift ;;
    --port) EXTRA_ARGS="$EXTRA_ARGS --port $2"; shift 2 ;;
    *) echo "未知参数：$1"; exit 1 ;;
  esac
done

[[ $EUID -eq 0 ]] || { echo "请用 sudo 运行"; exit 1; }
[[ -f "$BIN_SRC" ]] || { echo "找不到 galquote-server（要和本脚本放在同一目录）"; exit 1; }

echo "==> 安装到 $APP_DIR（运行用户：$RUN_USER，数据目录：$DATA_DIR）"
install -d -o "$RUN_USER" -g "$RUN_USER" "$DATA_DIR"
install -m 0755 "$BIN_SRC" "$APP_DIR/galquote-server"

echo "==> 写 systemd 服务"
cat >/etc/systemd/system/galquote-server.service <<EOF
[Unit]
Description=Gal Quote Tool Server (headless web UI for the quote library)
After=network-online.target
Wants=network-online.target

[Service]
User=$RUN_USER
Group=$RUN_USER
ExecStart=$APP_DIR/galquote-server --data $DATA_DIR $EXTRA_ARGS
WorkingDirectory=$APP_DIR
Restart=always
RestartSec=5
StandardOutput=journal
StandardError=journal
SyslogIdentifier=galquote-server
NoNewPrivileges=true
PrivateTmp=true

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable --now galquote-server
sleep 2
systemctl --no-pager --full status galquote-server | head -20

IP=$(hostname -I | awk '{print $1}')
PORT=$(grep -oP '(?<=--port )\d+' /etc/systemd/system/galquote-server.service || echo 8088)
echo
echo "==> 完成。浏览器打开： http://$IP:$PORT/"
echo "    数据目录：$DATA_DIR  （把电脑上的 quotes.db、截图、settings.json 放进来即可）"
echo "    看日志：  journalctl -u galquote-server -f"
echo "    生成两步验证密钥： $APP_DIR/galquote-server --gen-totp"
echo "    改启动参数：  sudo nano /etc/systemd/system/galquote-server.service && sudo systemctl restart galquote-server"
