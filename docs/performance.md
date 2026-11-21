# Performance, scale & load testing

All numbers below were **measured** (not estimated) on a deliberately small box, so production hardware will be faster:

* one Linux VM, **4 vCPU (Xeon 2.1 GHz), 15 GB RAM**, shared with everything else below
* **SQL Server 2025** (RTM-CU11, Developer = Enterprise feature set) installed **natively** (no containers), default
  memory settings, MAXDOP 0
* one API instance from the **release bundle** (.NET 10, Release, framework-dependent) serving the Angular app itself,
  no Redis (HybridCache L1 only), authentication off for the run
* synthetic fleets from `sm.usp_GenerateDemoData` (12 zones / 4 cities, outage pockets, NSP coverage holes)

## Measured results

### Data volumes and build times (2M meters)

| | Value |
|---|---|
| Fleet | 2,000,000 meters in 90,909 sites, 227,863 buildings / localities |
| `sm.CommLog` | 58.7M daily rows (30 days), **1.6 GB** incl. the `(MeterSk, LogDate)` B-tree |
| `sm.Meter` / `sm.MeterHealth` | 569 MB (incl. spatial index) / 302 MB |
| Pre-aggregations | `BuildingSummary` 665,380 rows (41 MB), `GridSummary` 582,760 rows (36 MB), `KpiCube` 1,152 rows |
| Synthetic generation | meters 118 s, 30 days of logs 407 s |
| **Full analytics refresh** (`sm.usp_RefreshAnalytics`) | **115 s** for 2M meters (300K meters: 16 s) - readers never blocked |

### Single requests - API latency (end to end, ms)

*Cold* = first call after an API restart (empty cache, so it includes the SQL query); *warm* = the same request
again (HybridCache hit).

| Request | 300K cold | 300K warm | **2M cold** | **2M warm** |
|---|---:|---:|---:|---:|
| KPI bar | 195 | 8 | 216 | 10 |
| 30-day trend | 66 | 2 | 63 | 2 |
| Cascading filter options | 47 | 2 | 44 | 2 |
| KPIs with zone + NSP + health filters | 24 | 2 | 28 | 2 |
| Map bubbles, country (zoom 5) | 192 | 3 | 76 | 3 |
| Map bubbles, city (zoom 11) | 71 | 11 | 94 | 13 |
| Map bubbles, street (zoom 16, buildings) | 54 | 4 | 43 | 9 |
| Map bubbles, zoom 12 with NSP + past date | 61 | 5 | 24 | 5 |
| Zones / zone building list | 37 / 123 | 2 / 4 | 32 / 236 | 2 / 3 |
| Building drill-down header | 63 | 3 | 51 | 3 |
| Building meter list, page 1 / page 10 / address search | 73 / 11 / 484 | 7 / 3 / 7 | 56 / 7 / 8 | 7 / 4 / 6 |
| Building consumption (MDM) | 36 | 3 | 47 | 2 |
| Meter search (prefix) / meter card | 24 / 64 | 4 / 2 | 20 / 42 | 4 / 2 |
| Failure Cluster Finder (DBSCAN, whole fleet) | 338 | 5 | 928 | 8 |
| Lasso over 434 km² (**380,920 meters**, exact) | 275 | 101 | 430 | 275 |
| SIM / NSP switch finder | 335 | 4 | 773 | 4 |

### Concurrent users (same request mix as `loadtest/k6-dashboard.js`)

Each simulated user loads KPIs + trend + filter options with random filters, then pans/zooms the map four times
across the four cities (zoom 5-17, random day), opens buildings at street level, searches meters; a separate client
issues 6 heavy analytics calls per minute (clusters / polygon / SIM switch).

| Fleet | Users | Duration | Requests | Failed | dashboard p95 | map p95 | building p95 | meter list p95 | analytics p95 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 300K | 50 | 120 s | 10,761 (90/s) | **0** | 7 ms | 37 ms | 5 ms | 8 ms | 358 ms |
| 2M | 30 | 120 s | 6,391 (53/s) | **0** | 11 ms | 147 ms | 10 ms | 7 ms | 1.3 s |

The default per-client rate limit (`RateLimiting:RequestsPerMinute = 3000`) was raised for these runs because every
simulated user came from the same IP; with real users the limit applies per authenticated user.

### Bugs these measurements found and fixed

* **varchar key parameters**: Dapper sends .NET strings as `nvarchar`; against the `varchar` `MeterId` /
  `BuildingKey` columns that forced `CONVERT_IMPLICIT` on the column and turned index seeks into scans. Building meter
  lists went from ~550 ms to 7 ms and meter search from ~390 ms to 4 ms at 2M meters (`SqlText.Key`).
* **Lasso / polygon**: the forced spatial-index plan took minutes for a city-sized lasso; it now scans the
  `sm.MeterHealth` columnstore by the polygon's envelope and runs the exact point-in-polygon test only on candidates
  (12 s → 0.28 s for 380K meters, identical counts). The analytics tools also now honour the health quick-filter.
* **Shared cache cancellation**: one client aborting a request no longer fails other users' identical in-flight requests.


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
| Lasso / polygon | envelope range scan on the `sm.MeterHealth` columnstore + exact point-in-polygon on candidates only | ~0.3 s for 380K meters |
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
# k6 is a single native binary (https://k6.io). 50 concurrent map users + 6 heavy analytics calls/minute
BASE_URL=http://appserver:8080 VUS=50 HOLD=3m k6 run loadtest/k6-dashboard.js
# with authentication on: add TOKEN=<bearer token>
```

The scenario mixes KPI + options + trend loads, four random pans/zooms per iteration across the four cities
(zoom 5 → 17, random filters, random day), building drill-downs at street level, meter searches and a trickle of
failure-cluster / polygon / SIM-switch analytics. Thresholds: error rate < 1 %, dashboard p95 < 500 ms,
map p95 < 800 ms, analytics p95 < 5 s.

What to watch while it runs: API `smartmeter.request.duration` histogram (OpenTelemetry), SQL Server
`sys.dm_exec_query_stats` / Query Store top consumers, `sys.dm_os_wait_stats`, cache hit ratio (Redis `INFO stats`).
