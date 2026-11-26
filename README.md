# Smart Meter Communication & Health Monitoring GIS Platform

Production-grade platform to ingest, analyse and **navigate** the communication health of very large
smart-meter fleets (designed for 15M+ meters and ~465M daily log rows) - from the whole country down to a zone,
a building and a single meter - with sub-second map and dashboard responses.

| Tier | Technology |
|---|---|
| Database | **SQL Server 2022+** (port **1433**, manage with **SSMS**) - daily partitioned columnstore log, spatial index, columnstore pre-aggregations |
| API | **.NET 10** (LTS) Web API, Clean Architecture, CQRS (MediatR), FluentValidation, Dapper, SqlBulkCopy, Hangfire, SignalR, Serilog, OpenTelemetry, HybridCache + Redis |
| Web | **Angular 22** (standalone, zoneless, Signals, OnPush, lazy routes), **OpenLayers 10** (open-source, BSD-2) on open basemaps, **Apache ECharts 6** |
| Ops | **No Docker**: one self-contained release folder (API + SPA) run as a **Windows Service** or **systemd** unit, GitLab CI on shell runners, health checks, JWT/OIDC with a dev toggle, k6 load tests |

> .NET 8 reached end of support on 10 Nov 2026, so the solution targets **.NET 10 LTS** (one line in
> `backend/Directory.Build.props`). The existing `src/` MeterMap app is kept untouched - see [`src/README.md`](src/README.md).

---

## Contents
1. [What you get](#1-what-you-get)
2. [Deployment (no Docker)](#2-deployment-no-docker)
3. [Local development](#3-local-development)
4. [Architecture](#4-architecture)
5. [Database design (SQL Server)](#5-database-design-sql-server)
6. [Data ingestion](#6-data-ingestion)
7. [API reference](#7-api-reference)
8. [Front-end guide](#8-front-end-guide)
9. [Security](#9-security)
10. [Observability & operations](#10-observability--operations)
11. [Performance, scale & load testing](#11-performance-scale--load-testing)
12. [Repository layout](#12-repository-layout)

---

## 1. What you get

**Navigate every level of the fleet** - *Fleet → Zone → Building → Meter*:
- **Map dashboard**: never sends raw meters to the browser. Every pan/zoom requests server-side aggregated bubbles for the
  viewport - a 5-level spatial grid pyramid when zoomed out, one bubble per building at street level (zoom ≥ 15).
  Bubble size = meter count; colour = failure rate on the selected day - **green 0-20 %**, **amber 21-79 %**, **red 80-100 %**.
  Click a cluster to zoom in, click a building to open the drill-down drawer.
- **Explorer**: zone cards → sortable/searchable building list (real buildings *and* "localities" for meters without
  a BuildingId) → full building page → full meter page, with breadcrumbs and "show on map" at every level.
- **Building drill-down**: totals, success/fail, NSP-wise and manufacturer-wise breakdowns, interactive 30-day trend
  (hover = exact date, count and %; click = select that day everywhere), MDM energy consumption (monthly total, building
  daily average, per-meter daily average over communicating meters) and a server-side paginated, searchable,
  CSV-exportable meter list with a 30-day strip per meter.
- **Single meter search** (index-seek prefix search) that flies to the meter and opens its card with address and daily
  timeline **without touching the active filters**.

**KPIs & filters**: total meters, communication success count & %, failure count & % for the selected day or the
30/31-day average; cascading multi-selects (Zone, NSP, Manufacturer - each counted under the other selections),
installation-date range (new meter lots), date picker; health quick-filters **All-day comm**, **Last 7 days comm**,
**Fail on selected day**, **Dead**, **Intermittent** with live counts.

**Field-operations tools**: **Failure Cluster Finder** (DBSCAN over high-failure buildings, ranked pockets with NSP
breakdown and convex hulls drawn on the map), **Lasso / polygon / box selection** (exact meter-level spatial
containment, independent of building ids) and the **SIM / NSP switch opportunity finder**.

**Ingestion**: streaming Excel (`.xlsx/.xlsb/.xls`, multi-worksheet) and CSV upload, chunked & resumable, row-level
validation report, set-based merge, live progress over SignalR, automatic coalesced analytics refresh.

---

## 2. Deployment (no Docker)

Nothing in this platform needs containers. Production is **three things on servers you already run**:

| Piece | Where | How |
|---|---|---|
| `SmartMeterDb` | your existing SQL Server instance (port 1433) | `database/deploy.sh` / `deploy.ps1` (sqlcmd) or open the scripts in SSMS |
| API + web app (one process) | Windows Server or Linux app server | release zip → `deploy/install-windows-service.ps1` or `deploy/install-linux.sh` |
| (optional) Redis | any Redis 6+ | shared L2 cache + SignalR backplane when you run 2+ API nodes |

### 2.1 Database - a separate `SmartMeterDb` on the existing instance
The platform keeps **all** of its objects in its own database, `SmartMeterDb`. The deploy scripts never connect to,
read, alter, seed or clean up any other database on the instance (your existing Users / DISCOMs / servers / tasks /
history / audit database is untouched). Demo seeding is **off** unless you explicitly pass `SEED_DEMO_METERS`.

```bash
cd database
# deploy login needs dbcreator (first run) and db_owner on SmartMeterDb - it is NOT the application login
SQL_SERVER='sqlprod01,1433' SQL_USER='<deploy login>' SQL_PASSWORD='<...>' APP_DB_PASSWORD='<app password>' ./deploy.sh
#   Windows:  .\deploy.ps1 -Server 'sqlprod01,1433' -User '<deploy login>' -Password '<...>' -AppPassword '<app password>'
#   SSMS:     open 00..05 and execute in order, then 06_security.sql in SQLCMD mode (Query > SQLCMD Mode)
```
Re-running is safe (every script is idempotent; RCSI is only switched on if it is off, so re-deploys never kick
sessions). `06_security.sql` creates/updates the least-privilege `smartmeter_app` login the API uses.

### 2.2 Application - one folder, no web server needed
```bash
./deploy/build-release.sh win-x64      # or linux-x64 ->  artifacts/smartmeter-<rid>.zip
```
The zip contains the framework-dependent .NET 10 API **with the Angular app inside `wwwroot/`** (Kestrel serves the
SPA with Brotli/Gzip, immutable caching for hashed bundles and `no-cache` for `index.html` / `config.json`), the
database scripts and `appsettings.Production.template.json`. Prerequisite on the app server: the
**ASP.NET Core 10 Runtime** (Windows: *Hosting Bundle*).

**Windows Server** (as Administrator):
```powershell
Expand-Archive smartmeter-win-x64.zip C:\SmartMeter\release
.\deploy\install-windows-service.ps1 -Source C:\SmartMeter\release\smartmeter-win-x64 -Port 8080
# first run creates C:\SmartMeter\app\appsettings.Production.json from the template: set the connection string
# (or use the ConnectionStrings__SmartMeterDb machine env var) + Auth, restrict the file ACL, then Start-Service SmartMeterApi
```
Installs the `SmartMeterApi` Windows Service (auto start, restart on failure, firewall rule). Upgrades stop the
service, mirror the new files and **keep** `appsettings.Production.json` and `wwwroot\config.json`.
To publish through **IIS** instead, point an IIS site at the same folder (the Hosting Bundle's ASP.NET Core Module
runs it in-process) - no other changes needed.

**Linux** (as root):
```bash
unzip smartmeter-linux-x64.zip -d /tmp/release
sudo ./deploy/install-linux.sh /tmp/release/smartmeter-linux-x64
# secrets go into /etc/smartmeter/smartmeter.env (mode 600), e.g. ConnectionStrings__SmartMeterDb=...
```
Installs `/opt/smartmeter/app` + the hardened `smartmeter.service` systemd unit (`Type=notify`, dedicated user).

Put the corporate reverse proxy / load balancer (IIS ARR, F5, HAProxy, nginx if you already have one) in front for
TLS; list its addresses under `ReverseProxy:KnownProxies` so the API trusts its `X-Forwarded-For/Proto` headers.

### 2.3 GitLab CI (company GitLab, `Test-Branch`)
[`.gitlab-ci.yml`](.gitlab-ci.yml) runs every server operation as a pipeline job on **shell runners** (no Docker
executor, no `image:`):

| Stage | Job | Runs |
|---|---|---|
| test | `backend:test`, `frontend:test` | every MR / `Test-Branch` / default-branch pipeline |
| package | `package:linux`, `package:windows` | builds the release zips (job artifacts, 30 days) |
| deploy | `db:deploy` (manual, protected) | `database/deploy.sh` against `SmartMeterDb` only, `SEED_DEMO_METERS=0` |
| deploy | `app:deploy:linux` / `app:deploy:windows` (manual, protected) | installer on the app server, then waits for `/health/ready` |
| operate | `analytics:refresh`, `smoke:test` (manual, protected) | `EXEC sm.usp_RefreshAnalytics`, post-deploy smoke test |

Secrets exist **only** as *protected + masked* CI/CD variables (`SM_SQL_SERVER`, `SM_SQL_DEPLOY_USER`,
`SM_SQL_DEPLOY_PASSWORD`, `SM_APP_DB_PASSWORD`, `SM_BASE_URL`, `SM_API_TOKEN`), so they are only exposed to
pipelines on protected branches such as `Test-Branch`. Runner tags: `smartmeter-build` (.NET 10 SDK, Node 22),
`smartmeter-db` (sqlcmd), `smartmeter-app` / `smartmeter-app-windows` (the app servers). `resource_group`s
serialise database and app deployments.

---

## 3. Local development

> **Step-by-step for Windows + VS Code** (install, database, F5 to run, loading your Excel files): see
> [`docs/local-setup.md`](docs/local-setup.md). The repository ships VS Code tasks and launch configurations in `.vscode/`.

Prerequisites: .NET 10 SDK, Node 22, SQL Server 2022+ (Developer edition is free) reachable on 1433, `sqlcmd` or SSMS.

```bash
# 1. database (sqlcmd) - or open database/00..06 in SSMS and execute in order (06 in SQLCMD mode)
cd database
SQL_PASSWORD='<sa password>' APP_DB_PASSWORD='<app password>' SEED_DEMO_METERS=300000 ./deploy.sh
#   Windows:  .\deploy.ps1 -Password '<sa password>' -AppPassword '<app password>' -SeedDemoMeters 300000

# 2. API  (http://localhost:5080, Scalar UI at /scalar)
cd ../backend
dotnet user-secrets --project src/SmartMeter.Api set "ConnectionStrings:SmartMeterDb" \
  "Server=localhost,1433;Database=SmartMeterDb;User Id=smartmeter_app;Password=<app password>;TrustServerCertificate=True"
dotnet run --project src/SmartMeter.Api --launch-profile http
dotnet test                                   # 61 unit tests

# 3. Web  (http://localhost:4200, proxies /api and /hubs to :5080)
cd ../frontend && npm ci && npm start
npx ng test --watch=false                     # Vitest unit tests
```

* `SEED_DEMO_METERS=300000` generates a realistic synthetic fleet (12 zones in 4 cities, outage pockets, NSP coverage
  holes, dead/intermittent meters, new installation lots) - for development databases only.
* **SSMS**: connect to `localhost,1433`; database `SmartMeterDb`.
* Jobs dashboard: `/hangfire` (Development, or authenticated `admin` role).

---

## 4. Architecture

```mermaid
flowchart LR
  subgraph Browser
    A[Angular 22 SPA<br/>Signals · OnPush · lazy routes]
    M[OpenLayers 10<br/>vector bubbles · draw tools]
    E[ECharts]
  end
  T[(Basemap tiles<br/>OSM · EOX Sentinel-2<br/>or your own tile server)]
  subgraph API[ASP.NET Core .NET 10 - Windows Service / systemd]
    S[Static SPA<br/>wwwroot + fallback] 
    C[Controllers] --> MD[MediatR pipeline<br/>Perf → Validation → Cache]
    MD --> H[Query / command handlers]
    H --> R[Dapper read repositories]
    H --> MDM[IMdmConsumptionService<br/>mock / HTTP + Polly]
    HF[Hangfire workers<br/>ingestion · refresh · nightly]
    SR[SignalR hub /hubs/live]
  end
  subgraph Data
    SQL[(SQL Server 2022<br/>SmartMeterDb)]
    RD[(Redis<br/>L2 cache · SignalR backplane)]
  end
  A <-->|REST + JSON (Brotli)| C
  A <-->|WebSocket| SR
  A -->|index.html, bundles| S
  M -->|XYZ tiles| T
  R --> SQL
  HF -->|SqlBulkCopy + merge procs| SQL
  MD <-->|HybridCache keyed by data version| RD
  HF --> SR
```

**Clean Architecture** (`backend/src`):

| Project | Responsibility |
|---|---|
| `SmartMeter.Domain` | Pure model: `CommWindow` (bit/column mapping), health classes & bands, map resolution, bounding boxes, geo math, grid-indexed **DBSCAN**. No dependencies. |
| `SmartMeter.Application` | CQRS queries/commands + handlers, DTOs, FluentValidation, pipeline behaviours (performance metrics, validation, version-keyed caching), ingestion row parsers, repository/service abstractions. |
| `SmartMeter.Infrastructure` | Dapper/SqlClient repositories, `FleetSource` SQL builder (summary vs meter-level), SqlBulkCopy ingestion worker, Sylvan CSV/Excel readers, upload store, Hangfire jobs, MDM clients with resilience, HybridCache/Redis. |
| `SmartMeter.Api` | Controllers, SignalR hub, auth (JWT/OIDC + dev toggle), ProblemDetails, health checks, Serilog, OpenTelemetry, compression, rate limiting, OpenAPI. |

**Request path for a map pan** (typical p50 ≈ 10-60 ms): viewport bbox is snapped outward to ¼-tile so neighbouring
pans share a cache key → `CachingBehavior` (HybridCache L1 + Redis L2, key prefixed with the analytics **data
version**) → on miss, one columnstore aggregate over `sm.GridSummary` (or `sm.BuildingSummary` at street zoom) →
≤ 5 000 bubbles returned. When a refresh publishes a new generation the version changes, every cache key changes,
and SignalR tells open dashboards to reload.

---

## 5. Database design (SQL Server)

Scripts in [`database/`](database) (idempotent; run in order, or `deploy.sh` / `deploy.ps1`):

| Script | Content |
|---|---|
| `00_create_database.sql` | Database, compat level 160, **READ_COMMITTED_SNAPSHOT** (readers never block loads/refreshes), Query Store, FULL recovery |
| `01_schema.sql` | Dimensions, CIS master `sm.Meter`, partition function/scheme, `sm.CommLog`, state, grid levels, ingestion tracking |
| `02_partitions.sql` | Daily partition SPLIT ahead of loads, sliding-window retention (TRUNCATE partitions + MERGE RANGE), day statistics |
| `03_analytics.sql` | `sm.usp_RefreshAnalytics` - builds the pre-aggregated generation and swaps it in |
| `04_ingestion.sql` | Staging tables, CIS / MDM set-based merge procedures, unknown-meter report |
| `05_demo_data.sql` | `sm.usp_GenerateDemoData` synthetic fleet |
| `06_security.sql` | Least-privilege `smartmeter_app` login (SQLCMD variables) |
| `queries/*.sql` | Reference queries for SSMS: KPIs & trend, map bubbles, failure clusters (pure T-SQL), polygon selection, SIM switch, meter timeline |

### Core tables
* **`sm.Meter`** (CIS master, 1 row/meter): `MeterSk int` surrogate, `MeterId` (unique), `BuildingId` (nullable),
  `Latitude/Longitude`, persisted `geometry` point **`Geom`** (SRID 4326) with a **spatial index**, small-int
  `ZoneId/NspId/ManufacturerId`, `InstallationDate`, `DtId` (distribution transformer, ready for loss mapping),
  and a persisted **`BuildingKey`**: the BuildingId, or a **~33 m grid cell** (`G:<y>:<x>`) for regions without
  building ids - those meters still group into "virtual buildings" (shown as *localities*). Composite index on
  (Zone, NSP, Manufacturer, InstallationDate, BuildingKey).
* **`sm.CommLog`** (MDM logs, rolling 30/31 days ≈ 465M rows at 15M meters): `(LogDate, MeterSk, IsCommunicating)`,
  **RANGE RIGHT partitioned by day**, **clustered columnstore** (~1-2 bytes/row) plus a unique `(MeterSk, LogDate)`
  B-tree for the single-meter timeline. Boundaries are always created through `LogDate + 1`, so the right-most
  partition stays empty (columnstore only allows SPLIT of empty partitions). Retention = `TRUNCATE ... WITH (PARTITIONS)`.

### Pre-aggregated generation (rebuilt by `sm.usp_RefreshAnalytics`)
| Table | Grain | Used by |
|---|---|---|
| `sm.MeterHealth` (CCI) | 1 row / meter: **31-bit communication mask**, eligibility mask, comm/eligible days, last comm, health class, active-last-7 | meter lists, installation-lot (meter-level) queries, polygon stats |
| `sm.Building` | 1 row / building: centroid, dominant zone, NSP count | drill-down headers, explorer |
| `sm.BuildingSummary` (CCI, ordered by Lat) | building × zone × NSP × manufacturer × health × last7, with per-day counters **E01..E31 / C01..C31** | building bubbles, drill-down, clusters, SIM switch |
| `sm.GridSummary` (CCI, ordered by Level, CellY) | same cube aggregated into a **5-level grid pyramid** (1°, 0.25°, 0.05°, 0.0125°, 0.003°) | zoomed-out bubbles (server-side clustering) |
| `sm.KpiCube` | zone × NSP × manufacturer × health × last7 (~1K rows) | KPI bar, chips, cascading options, trend, zones |

*Conventions*: mask bit *i* = day `WindowEnd − i`; column `Ekk/Ckk` = day `WindowStart + kk − 1`. `E` counts meters
**expected** to communicate that day (installed, or actually communicated), `C` those that **did** - so new installation
lots are never reported as failing for days before they existed. Health class: **Dead** = 0 comm days,
**All-day** = communicated on every eligible day, **Intermittent** = otherwise. Selecting a day reads 2 compressed
columns; the 30-day average reads 2 totals.

**Zero-downtime refresh**: every table is rebuilt as `sm.<Table>__next` while dashboards keep reading the live
generation, then swapped with `sp_rename` in one short transaction (`LOCK_TIMEOUT` + retry), `DataVersion` is bumped
and the old generation dropped. Concurrent refreshes are serialised with `sp_getapplock`; DDL runs inside procedures
declared `WITH EXECUTE AS OWNER`, so the app login only needs `EXECUTE`.

Useful SSMS snippets:
```sql
SELECT * FROM sm.AnalyticsState;                       -- window, data version, last refresh duration + row counts
SELECT * FROM sm.vCommLogPartitions ORDER BY LogDate;   -- partitions, rows per day, load time
EXEC sm.usp_RefreshAnalytics;                           -- rebuild now (also queued after every ingestion + nightly)
UPDATE sm.AnalyticsState SET ConfigWindowDays = 31;     -- 30/31-day window (applies at next refresh)
```

---

## 6. Data ingestion

UI: **Data ingestion** page (drag & drop). API: chunked or single-shot.

| File | Required columns (aliases recognised, case/space-insensitive) | Optional |
|---|---|---|
| **CIS master** (`kind=cis`) | `MeterId` (Meter No, MSN …) | `BuildingId`, `Latitude`, `Longitude`, `Address`, `NSP` (SIM Operator …), `Manufacturer` (Make …), `Zone` (Area, Circle …), `InstallationDate`, `DtId` |
| **MDM logs** (`kind=mdm`) | `MeterId`, `LogDate` (Date, Read Date …), `IsCommunicating` (1/0, true/false, yes/no, success/fail, …) | |

Pipeline (Hangfire job, constant memory): Sylvan streaming reader → per-row validation (`ValidatingDataReader`
feeds only valid rows to **SqlBulkCopy**, errors are captured with row number, column, value, message) → per-job
staging heap → set-based merge (CIS: batched upsert, unchanged rows skipped, new zones/NSPs/manufacturers added;
MDM: one partition-day at a time, a new day takes the bulk `TABLOCK` path into compressed row groups) → unknown
MeterIds reported → analytics refresh enqueued (coalesced: a refresh that started later already covers the change).

Validation highlights: dates `yyyy-MM-dd`, `dd/MM/yyyy`, `dd-MMM-yyyy`, Excel date cells and serials; Excel numeric
ids are not mangled (`1.2E+11`); swapped latitude/longitude columns are corrected; `0,0` is treated as unknown;
coordinates outside `Ingestion:ServiceArea` (India by default) are rejected; empty NSP/manufacturer/zone → `Unknown`.
Workbooks larger than one sheet (1,048,576 rows) are read across all worksheets.

```bash
# single-shot streaming upload (scripts / schedulers)
curl --data-binary @cis_master.csv -H "Content-Type: application/octet-stream" \
     "http://localhost:8080/api/v1/ingestion/files?kind=cis&fileName=cis_master.csv"
# chunked (what the UI does): POST /uploads -> PUT /uploads/{id}/chunks/{n} (any order, parallel) -> POST /uploads/{id}/complete
curl http://localhost:8080/api/v1/ingestion/jobs                 # status, counts, progress
curl http://localhost:8080/api/v1/ingestion/jobs/{id}/errors     # CSV error report
```

---

## 7. API reference

All under `/api/v1`. Global filters as query string: `zone=1&zone=4&nsp=2&mfr=3&installedFrom=2026-11-01&installedTo=…&health=dead|intermittent|allDay|last7Days|todayFail&date=2026-11-15&metric=day|window`.

| Method & path | Purpose |
|---|---|
| `GET meta` | window, data version, dimensions, grid levels, band thresholds |
| `GET dashboard/kpis` | KPI bar + health chip counts |
| `GET dashboard/trend` | daily success over the window |
| `GET dashboard/filter-options` | cascading Zone / NSP / Manufacturer options with counts |
| `GET map/bubbles?bbox=minLon,minLat,maxLon,maxLat&zoom=12` | viewport bubbles (grid or building mode) |
| `GET zones`, `GET zones/{id}/buildings?search=&sort=failureRate&page=` | explorer levels 1-2 |
| `GET buildings/{key}` · `/meters` · `/meters/export` · `/consumption` | drill-down, paged meter list, CSV, MDM consumption |
| `GET meters/search?q=` · `GET meters/{meterId}` | meter search & card with 30-day timeline |
| `POST analytics/failure-clusters` | `{minMeters, minFailurePct, epsMeters, minPoints, bbox?}` |
| `POST analytics/polygon` | `{geometry: GeoJSON Polygon}` → meters, health, NSP & manufacturer breakdowns |
| `GET analytics/sim-switch?minStrongSuccessPct=85&minWeakFailurePct=70&basis=window` | SIM swap candidates |
| `POST ingestion/uploads` · `PUT …/chunks/{n}` · `POST …/complete` · `POST ingestion/files` | uploads |
| `GET ingestion/jobs[/{id}[/errors]]` · `GET ingestion/templates/{cis|mdm}` · `POST ingestion/refresh` | jobs, templates, refresh |
| `GET /health` · `/health/ready` · `/health/live` | health (SQL Server, Redis, analytics freshness) |
| SignalR `/hubs/live` | `jobUpdated`, `dataVersionChanged` |

Errors are RFC 7807 problem details with a `traceId`.

---

## 8. Front-end guide

* **Runtime configuration** (`wwwroot/config.json` on the server, never cached, kept across upgrades): `apiBaseUrl`
  (empty = same origin), map centre/zoom, locale (`en-IN` digit grouping by default) and optional `basemaps` overrides.
* **Map = OpenLayers** (open-source, BSD-2, no API key, no vendor SDK). Four basemaps, all keyless and verified to
  serve real tiles:

  | Basemap | Default source | Licence / attribution |
  |---|---|---|
  | Streets | OpenStreetMap standard tiles | © OpenStreetMap contributors (ODbL) |
  | Satellite | EOX **Sentinel-2 cloudless 2016** (to zoom 15) | CC BY 4.0, attribution shown on the map |
  | Dark Gray / Light Gray | OpenStreetMap tiles through a CSS filter on the basemap canvas only | © OpenStreetMap contributors |

  `tile.openstreetmap.org` is a community service with a [usage policy](https://operations.osmfoundation.org/policies/tiles/);
  for heavy production use (or air-gapped networks) point the basemaps at your own tile server or a provider, without
  rebuilding the app:
  ```json
  { "basemaps": {
      "streets":    { "url": "https://tiles.company.local/osm/{z}/{x}/{y}.png", "attribution": "© OpenStreetMap contributors" },
      "light-gray": { "url": "https://tiles.company.local/osm/{z}/{x}/{y}.png", "filter": "grayscale(1) brightness(1.06) contrast(0.78)" } } }
  ```
* State: `FilterStore` (Signals; URL-synced on the dashboard → shareable links like `/dashboard?zone=2&health=dead&building=B:BLD-0001234`),
  `MetaStore` (reloads on `dataVersionChanged`), `MapCommands` (fly-to, highlights, draw tools). Data loading uses
  `rxResource` so in-flight requests are cancelled when inputs change.
* Map: one OpenLayers vector layer of server-aggregated bubbles (area-proportional radius, band colour, labels
  decluttered so overlapping cells never garble counts), a highlight layer (cluster hulls, selected building, meter pin)
  and `Draw` interactions for free-hand lasso, polygon and box. Bubbles render even if the tile server is unreachable.
* Charts follow a single-axis rule (no dual axes): success % lines with counts in tooltips, kWh bars from zero.

---

## 9. Security

* **Auth**: `Auth:Enabled=false` is the **dev toggle** (anonymous, logged as a warning). Production: `Auth:Enabled=true`,
  `Auth:Mode=Oidc`, `Auth:Authority`/`Audience` (Entra ID, Keycloak, Okta, …). Policies: *Viewer* (any authenticated
  user) and *Operator* (`ingestion-operator` or `admin` role) for uploads and refresh. The SPA ships an Authorization
  Code + PKCE flow; `Auth:Mode=Dev` issues local signed tokens (`POST /api/v1/auth/dev-token`, never in Production).
* **Least privilege**: the API connects as `smartmeter_app` (`db_datareader`, `db_datawriter`, `EXECUTE` on `sm`,
  insert into `stage`); all DDL happens in `EXECUTE AS OWNER` procedures; staging table names are validated server-side.
* No secrets in config files: connection strings, MDM API key and signing key come from environment / user-secrets /
  your vault / protected GitLab CI variables. SQL passwords are passed as sqlcmd variables, never defaulted in scripts.
* Parameterised SQL everywhere (dynamic SQL only interpolates whitelisted identifiers/column indexes), CSV
  formula-injection guarding, upload size/extension limits, per-user rate limiting + an analytics concurrency limiter,
  security headers (`nosniff`, `SAMEORIGIN`, origin-only referrer), Windows Service / systemd unit running under a
  dedicated low-privilege account (systemd unit hardened with `ProtectSystem=full`, `NoNewPrivileges`, …).

---

## 10. Observability & operations

* **Logs**: Serilog structured JSON (console) with request logging; slow CQRS requests (> 1 s) are warned.
* **Metrics & traces**: OpenTelemetry - ASP.NET Core, HttpClient, SqlClient, runtime, plus custom meter `SmartMeter`
  (`smartmeter.request.duration`, `smartmeter.ingestion.rows`, `smartmeter.ingestion.rejected`, `smartmeter.refresh.duration`).
  Set `OTEL_EXPORTER_OTLP_ENDPOINT` to export (Grafana, Jaeger, Azure Monitor, …).
* **Health**: `/health/live` (process), `/health/ready` (SQL Server, Redis, analytics freshness - *Degraded* when the
  last refresh is > 36 h old or the newest MDM day is > 3 days old).
* **Jobs**: Hangfire on SQL Server (`HangFire` schema): queues `ingestion`, `analytics`; recurring
  `analytics-nightly-refresh` (02:15 Asia/Kolkata) which also applies log retention (`RetentionDays`, default 45).
  Scale out by running more API replicas; set `Jobs:WorkersEnabled=false` on API-only nodes and run worker replicas.
* **Daily runbook**: MDM file lands → upload (UI/API/scheduler with `curl`) → job merges the day → refresh runs
  automatically → dashboards update live. Back up `SmartMeterDb` like any OLTP database (FULL + log backups);
  the analytics tables can always be rebuilt from `sm.Meter` + `sm.CommLog`.

---

## 11. Performance, scale & load testing

See [`docs/performance.md`](docs/performance.md) for measured numbers (300K and 2M meter fleets on a 4 vCPU box with
native SQL Server 2025: cached responses 2-13 ms, cold map/KPI queries < 250 ms, 0 errors under 30-50 concurrent
users) and sizing guidance for 15M+ meters. Run the load test yourself:

```bash
# k6 is a single native binary (https://k6.io) - no container needed
BASE_URL=http://localhost:8080 VUS=50 HOLD=3m k6 run loadtest/k6-dashboard.js
```

---

## 12. Repository layout

```
database/        SQL Server scripts, deploy.sh / deploy.ps1, reference queries
backend/         .NET 10 solution (Domain / Application / Infrastructure / Api + UnitTests)
frontend/        Angular 22 + OpenLayers + ECharts app
deploy/          release build scripts, Windows Service + systemd installers, production settings template
loadtest/        k6 scenarios
scripts/         post-deployment smoke test, folder upload helper (upload-folder.ps1)
data/            your own Excel/CSV files (git-ignored)
.vscode/         VS Code tasks, launch configurations, recommended extensions
docs/            performance & scaling notes
.gitlab-ci.yml   company GitLab pipeline (shell runners);  .github/workflows/ci.yml  build + test only
src/, tests/, MeterMap.slnx   legacy MeterMap.NET application (unchanged)
```
