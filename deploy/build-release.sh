#!/usr/bin/env bash
# Builds ONE deployable folder/zip: the .NET API with the Angular app inside wwwroot (no containers, no nginx).
#   ./deploy/build-release.sh [linux-x64|win-x64]          -> artifacts/smartmeter-<rid>.zip
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
RID="${1:-linux-x64}"
OUT="$ROOT/artifacts/smartmeter-$RID"
rm -rf "$OUT" && mkdir -p "$OUT"

echo ">> building Angular app"
( cd "$ROOT/frontend" && npm ci --no-audit --no-fund && npx ng build --configuration production )

echo ">> publishing API ($RID, framework-dependent)"
dotnet publish "$ROOT/backend/src/SmartMeter.Api/SmartMeter.Api.csproj" -c Release -r "$RID" --self-contained false -o "$OUT"

echo ">> embedding SPA into wwwroot"
mkdir -p "$OUT/wwwroot"
cp -r "$ROOT/frontend/dist/frontend/browser/." "$OUT/wwwroot/"
cp "$ROOT/deploy/appsettings.Production.template.json" "$OUT/appsettings.Production.template.json"
cp -r "$ROOT/database" "$OUT/database"

( cd "$ROOT/artifacts" && rm -f "smartmeter-$RID.zip" && (command -v zip >/dev/null && zip -qr "smartmeter-$RID.zip" "smartmeter-$RID" || python3 -m zipfile -c "smartmeter-$RID.zip" "smartmeter-$RID") )
echo ">> done: artifacts/smartmeter-$RID.zip"
