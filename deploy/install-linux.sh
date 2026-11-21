#!/usr/bin/env bash
# Installs or upgrades the platform on a Linux app server (systemd). Run as root.
#   ./install-linux.sh <unzipped smartmeter-linux-x64 folder>
# Prerequisite: ASP.NET Core 10 runtime (e.g. apt install aspnetcore-runtime-10.0).
set -euo pipefail
SRC="${1:?usage: install-linux.sh <release folder>}"
APP=/opt/smartmeter/app
id smartmeter >/dev/null 2>&1 || useradd --system --home /opt/smartmeter --shell /usr/sbin/nologin smartmeter
mkdir -p "$APP" /var/smartmeter/uploads /etc/smartmeter
systemctl stop smartmeter 2>/dev/null || true
# replace the binaries but keep the server's own settings: appsettings.Production.json and wwwroot/config.json
KEEP="$(mktemp -d)"
for f in appsettings.Production.json wwwroot/config.json; do
  [[ -f "$APP/$f" ]] && { mkdir -p "$KEEP/$(dirname "$f")"; cp -p "$APP/$f" "$KEEP/$f"; }
done
find "$APP" -mindepth 1 -delete
cp -a "$SRC/." "$APP/"
cp -a "$KEEP/." "$APP/" && rm -rf "$KEEP"
[[ -f "$APP/appsettings.Production.json" ]] || cp "$APP/appsettings.Production.template.json" "$APP/appsettings.Production.json"
sed -i 's#D:\\\\SmartMeter\\\\uploads#/var/smartmeter/uploads#' "$APP/appsettings.Production.json"
[[ -f /etc/smartmeter/smartmeter.env ]] || install -m 600 /dev/null /etc/smartmeter/smartmeter.env
chown -R smartmeter:smartmeter /opt/smartmeter /var/smartmeter
cp "$(dirname "$0")/smartmeter.service" /etc/systemd/system/smartmeter.service
systemctl daemon-reload
systemctl enable --now smartmeter
echo "installed; check: curl -s http://localhost:8080/health/ready"
