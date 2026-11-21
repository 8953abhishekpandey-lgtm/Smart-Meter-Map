#!/usr/bin/env bash
# Deploys the SmartMeterDb schema to SQL Server (default localhost,1433) with sqlcmd.
#   SQL_PASSWORD=... ./deploy.sh                       # schema only
#   SQL_PASSWORD=... SEED_DEMO_METERS=300000 ./deploy.sh  # + synthetic demo fleet
set -euo pipefail
cd "$(dirname "$0")"
SERVER="${SQL_SERVER:-localhost,1433}"
USER="${SQL_USER:-sa}"
: "${SQL_PASSWORD:?set SQL_PASSWORD}"
SQLCMD="${SQLCMD:-sqlcmd}"
APP_PASSWORD="${APP_DB_PASSWORD:-}"

run() { "$SQLCMD" -S "$SERVER" -U "$USER" -P "$SQL_PASSWORD" -C -I -b "$@"; }

for f in 00_create_database.sql 01_schema.sql 02_partitions.sql 03_analytics.sql 04_ingestion.sql 05_demo_data.sql; do
  echo ">> $f"
  run -i "$f"
done
if [[ -n "$APP_PASSWORD" ]]; then
  echo ">> 06_security.sql"
  run -v AppPassword="$APP_PASSWORD" -i 06_security.sql
fi
if [[ "${SEED_DEMO_METERS:-0}" -gt 0 ]]; then
  if [[ "$(run -d SmartMeterDb -h -1 -W -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM sm.Meter")" == "0" ]]; then
    echo ">> generating demo fleet of ${SEED_DEMO_METERS} meters"
    run -d SmartMeterDb -t 0 -Q "EXEC sm.usp_GenerateDemoData @Meters = ${SEED_DEMO_METERS};"
  fi
fi
echo ">> refreshing analytics"
run -d SmartMeterDb -t 0 -Q "EXEC sm.usp_RefreshAnalytics;" | grep -v "^Caution" || true
echo "done."
