#!/usr/bin/env bash
# Post-deployment smoke test: exercises every read endpoint and reports status + latency.
#   BASE_URL=http://localhost:8080 [TOKEN=...] ./scripts/smoke-test.sh
set -uo pipefail
BASE="${BASE_URL:-http://localhost:8080}"
API="$BASE/api/v1"
AUTH=()
[[ -n "${TOKEN:-}" ]] && AUTH=(-H "Authorization: Bearer $TOKEN")
fail=0
call() {
  local name=$1; shift
  local out
  out=$(curl -s -o /tmp/smoke.$$ -w "%{http_code} %{time_total}" "${AUTH[@]}" "$@")
  local code=${out%% *} secs=${out##* }
  printf "%-28s %s  %6.0f ms\n" "$name" "$code" "$(echo "$secs * 1000" | bc)"
  [[ $code == 2* ]] || { fail=1; head -c 300 /tmp/smoke.$$; echo; }
}
call health            "$BASE/health/ready"
call meta              "$API/meta"
BUILDING=$(curl -s "${AUTH[@]}" "$API/map/bubbles?bbox=68,6,98,37&zoom=16" | python3 -c "import sys,json; b=json.load(sys.stdin)['bubbles']; print(b[0]['buildingKey'] if b else '')")
METER=$(curl -s "${AUTH[@]}" "$API/meters/search?q=SM" | python3 -c "import sys,json; r=json.load(sys.stdin); print(r[0]['meterId'] if r else '')" 2>/dev/null)
call kpis              "$API/dashboard/kpis"
call kpis-dead-window  "$API/dashboard/kpis?health=dead&metric=window"
call trend             "$API/dashboard/trend"
call filter-options    "$API/dashboard/filter-options"
call bubbles-z5        "$API/map/bubbles?bbox=68,6,98,37&zoom=5"
call bubbles-z12       "$API/map/bubbles?bbox=77.0,28.4,77.4,28.8&zoom=12"
call zones             "$API/zones"
call zone-buildings    "$API/zones/1/buildings?pageSize=10"
if [[ -n "$BUILDING" ]]; then
  enc=$(python3 -c "import urllib.parse,sys; print(urllib.parse.quote(sys.argv[1], safe=''))" "$BUILDING")
  call building        "$API/buildings/$enc"
  call building-meters "$API/buildings/$enc/meters?pageSize=25"
  call consumption     "$API/buildings/$enc/consumption"
fi
[[ -n "$METER" ]] && call meter "$API/meters/$METER"
call clusters          -X POST -H 'Content-Type: application/json' -d '{"minMeters":300,"minFailurePct":25,"epsMeters":400}' "$API/analytics/failure-clusters"
call polygon           -X POST -H 'Content-Type: application/json' -d '{"geometry":{"type":"Polygon","coordinates":[[[77.19,28.50],[77.25,28.50],[77.25,28.55],[77.19,28.55],[77.19,28.50]]]}}' "$API/analytics/polygon"
call sim-switch        "$API/analytics/sim-switch?pageSize=20"
rm -f /tmp/smoke.$$
exit $fail
