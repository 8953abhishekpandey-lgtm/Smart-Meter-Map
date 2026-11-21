# Performance, scale & load testing

All numbers below were **measured** (not estimated) on a deliberately small box, so production hardware will be faster:

* one container host, **4 vCPU, 15 GB RAM**, local overlay disk
* SQL Server 2022 Developer (RTM-CU28) in Docker, **memory capped at 6 GB**
* one API instance (.NET 10, Release), no Redis (HybridCache L1 only), Angular served by `ng serve`
* synthetic fleets from `sm.usp_GenerateDemoData` (12 zones / 4 cities, outage pockets, NSP coverage holes)

<!-- MEASUREMENTS -->

## Why it stays fast as the fleet grows

| Concern | Design decision | Cost at query time |
|---|---|---|
| 465M daily log rows (15M meters x 31 days) | never read by dashboards; read once per refresh, partition-pruned, batch-mode columnstore | 0 |
| Per-meter 30-day health | one 31-bit mask per meter (`sm.MeterHealth`) | bit ops |
| KPIs / chips / cascading options / trend | `sm.KpiCube` (zone x NSP x make x health x last7, ~1K rows) | < 10 ms |
| Map, zoomed out | 5-level grid pyramid (`sm.GridSummary`), ordered columnstore, bbox -> cell range | only cells in view |
| Map, street level | `sm.BuildingSummary` ordered by latitude (segment elimination) | only buildings in view |
| Selected day vs 30-day average | per-day counter columns `E01..E31/C01..C31` and window totals | 2 compressed columns |
| Repeated pans / users on the same view | bbox snapped to 1/4 tile + HybridCache (L1 + Redis) keyed by data version | ~1 ms on hit |
| Exact installation-day lots | meter-level fallback on `sm.MeterHealth` (CCI ordered by InstallationDate) | proportional to the lot |
| Refresh while users are working | build `__next` generation, swap with `sp_rename` under `LOCK_TIMEOUT` + retry, RCSI | no reader blocking |

## Sizing guidance for 15M+ meters

**SQL Server** (the only stateful tier):
* Enterprise edition is recommended at this scale: Standard limits batch-mode parallelism (DOP 2) and the
  columnstore object pool (32 GB). 16-32 vCores, 128-256 GB RAM, fast SSD/NVMe for data + tempdb (8 tempdb files).
* Storage estimate: `sm.CommLog` 465M rows ≈ 1.5-3 GB columnstore + ≈ 8-12 GB for the `(MeterSk, LogDate)` B-tree
  (PAGE compressed); `sm.Meter` 15M ≈ 4-5 GB incl. spatial index; analytics generation ≈ 5-8 GB (held twice during a
  refresh). Plan ~60 GB data + log + backups headroom.
* Refresh scales linearly with meters x days. Run it after each daily MDM load (automatic) and nightly (recurring job).
  If the daily refresh window ever becomes tight, the masks can be rolled forward incrementally
  (`((mask << 1) | today) & fullMask`) so a new day costs O(meters) instead of O(meters x days) - the schema already
  supports this; full rebuilds remain the correctness baseline.
* Read scale-out: put dashboards on an Always On **readable secondary** (`ApplicationIntent=ReadOnly`) - every
  analytics table is a normal logged table, so it replicates; ingestion and refresh stay on the primary.
* Keep Query Store on (enabled by `00_create_database.sql`) and watch `sys.dm_db_column_store_row_group_physical_stats`
  for open/delta row groups after corrections (an `ALTER INDEX ... REORGANIZE` on `CCI_CommLog` compacts them).

**API**: stateless - run 2+ replicas behind a load balancer with **Redis** configured (shared L2 cache and SignalR
backplane). Run Hangfire workers on dedicated replicas (`Jobs:WorkersEnabled=true`) and disable them on the web-facing
ones. Keep `Max Pool Size` ≥ 2x the expected concurrent queries per instance.

**Ingestion**: a 15M-row daily MDM CSV (~600 MB) streams through Sylvan + SqlBulkCopy at hundreds of thousands of rows
per second into an indexed-free heap, then lands in one fresh daily partition via a `TABLOCK` insert (compressed row
groups, minimal logging under `BULK_LOGGED`). Upload chunks are 8 MB and resumable; files up to 4 GB by default.

## Running the load test

```bash
# against the compose stack (nginx on :8080) - 50 concurrent map users + 6 heavy analytics calls/minute
docker run --rm -i --network host -e BASE_URL=http://localhost:8080 -e VUS=50 -e HOLD=3m grafana/k6 run - < loadtest/k6-dashboard.js
```

The scenario mixes KPI + options + trend loads, four random pans/zooms per iteration across the four cities
(zoom 5 → 17, random filters, random day), building drill-downs at street level, meter searches and a trickle of
failure-cluster / polygon / SIM-switch analytics. Thresholds: error rate < 1 %, dashboard p95 < 500 ms,
map p95 < 800 ms, analytics p95 < 5 s.

What to watch while it runs: API `smartmeter.request.duration` histogram (OpenTelemetry), SQL Server
`sys.dm_exec_query_stats` / Query Store top consumers, `sys.dm_os_wait_stats`, cache hit ratio (Redis `INFO stats`).
