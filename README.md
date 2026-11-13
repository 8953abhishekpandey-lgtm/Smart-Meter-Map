# Smart Meter Communication & Monitoring System (MeterMap.NET)

[![.NET Version](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![C#](https://img.shields.io/badge/C%23-13.0-239120?logo=csharp&logoColor=white)](https://learn.microsoft.com/en-us/dotnet/csharp/)
[![Database](https://img.shields.io/badge/Database-SQL%20Server-CC292B?logo=microsoftsqlserver&logoColor=white)](https://www.microsoft.com/sql-server)
[![Mapping](https://img.shields.io/badge/Map-MapLibre%20GL-396BFE?logo=maplibre&logoColor=white)](https://maplibre.org/)
[![Testing](https://img.shields.io/badge/Tests-xUnit-blue?logo=xunit)](https://xunit.net/)

An enterprise-grade .NET solution for **Advanced Metering Infrastructure (AMI)** and **Smart Energy Meter** telemetry monitoring, communication health analysis, geospatial mapping, and automated fault diagnosis.

---

## 📌 Overview

**MeterMap.NET** is designed to process and analyze massive volumes of interval telemetry data produced by electrical smart meters. It continuously evaluates fleet health, measures communication quality across both recent and historical windows, detects power losses and mass outages, and renders meter statuses in real time on an interactive, high-performance geospatial map.

### Key Highlights
- **Automated Interval Inference**: Identifies reporting intervals (5, 10, 15, 30, or 60 min) per meter automatically using statistical distribution of timestamp gaps.
- **Dual-Window Health Scoring**: Calculates both 24-hour recent communication quality and lifetime date-range communication percentage.
- **Intelligent Root Cause Diagnostics**: Classifies silent or non-communicating meters into actionable categories (e.g., *Power loss*, *Mass outage*, *Reading fault*, *Intermittent signal*, *Overheating*, *Sudden stop*).
- **Spatial Clustering & Hierarchy**: Automatically groups nearby meters (within 25m) into buildings and spatial zones (2km radius) even when geographical hierarchy is missing from the database.
- **High-Performance Data Pipeline**: Built on Dapper and ADO.NET with parallelized SQL queries designed for minimal latency over large telemetry tables.
- **Real-Time Interactive Dashboard**: Built-in dark-mode MapLibre GL frontend with KPI cards, outage waffles, failure breakdown analytics, and granular filtering.

---

## 🏛️ Architecture & Project Structure

The solution adheres to Clean Architecture principles, isolating pure business and domain calculations from database queries and HTTP transport layers:

```
METER_COM.NET/
├── MeterMap.slnx                     # Modern XML-based Solution file
├── src/
│   ├── MeterMap.Core/                # Domain & Business Logic (No external DB dependencies)
│   │   ├── MeterLogic.cs             # Gap inference, comm calculation, reason classification, spatial clustering
│   │   └── MeterMap.Core.csproj
│   ├── MeterMap.Data/                # Data Access & SQL Pipeline
│   │   ├── DatabaseOptions.cs        # Configuration models, profiles, table/column mappings
│   │   ├── SqlConnectionFactory.cs   # Resilient SQL connection creation and profile management
│   │   ├── Sql.cs                    # Safe identifier escaping and SQL utilities
│   │   ├── MeterData.cs              # Dapper repositories, parallel queries, and aggregation service
│   │   └── MeterMap.Data.csproj
│   └── MeterMap.Api/                 # ASP.NET Core Minimal API & UI Hosting
│       ├── Program.cs                # Dependency injection, route endpoints, query validations
│       ├── appsettings.json          # Database profiles, parameter IDs, and table mappings
│       ├── wwwroot/
│       │   └── index.html            # Dark-theme MapLibre GL web visualization dashboard
│       └── MeterMap.Api.csproj
└── tests/
    └── MeterMap.Tests/               # Unit Test Suite
        ├── Logictest.cs              # Unit tests for interval detection, comm scoring, reason heuristics
        └── MeterMap.Tests.csproj
```

---

## ⚙️ Core Modules & Logic

### 1. Telemetry Interval Detection (`IntervalInference`)
Different meters report at different frequencies (e.g., 15-minute feeder meters vs. 30-minute residential meters). Rather than requiring manual metadata entry, `IntervalInference`:
- Computes delta timestamps between consecutive readings.
- Filters standard interval candidates: `[5, 10, 15, 30, 60]` minutes.
- Selects the smallest interval that accounts for at least 5% of observed gaps.
- Falls back to fleet-wide consensus or default standard (30 min) for meters with sparse historical data.

### 2. Communication Quality Engine (`CommCalculator`)
Tracks meter freshness and reliability:
- **Hours Silent**: Time elapsed since the last valid telemetry reading relative to `statusRef`.
- **Stale Limit**: Configurable threshold (e.g., 24h) clamped to a minimum of two reporting intervals.
- **Recent Communication % (`CommPct`)**: Ratio of valid readings received vs. expected in the latest 24-hour window.
- **Lifetime Communication % (`LifeCommPct`)**: Ratio of total valid readings received vs. theoretical maximum across the entire selected range.

### 3. Outage & Fault Classifier (`ReasonClassifier`)
When a meter is marked **Not communicating**, the diagnostic classifier evaluates telemetry history using a deterministic priority hierarchy:

| Reason | Diagnostic Condition |
| :--- | :--- |
| **`Reading fault`** | Database rows are actively arriving within the freshness window, but telemetry parameters (voltage/current/power) are empty or corrupt. |
| **`Silent before range`** | No records present in the query window; last known communication predates the window. |
| **`Never communicated`** | Meter exists in equipment inventory but has zero recorded telemetry in history. |
| **`Mass outage`** | Multiple meters (≥10 meters and ≥20% of offline meters) ceased communication within the identical 3-hour time bucket. |
| **`Power loss`** | Minimum voltage dropped below dead threshold (< 20V) on the last valid packet before stopping. |
| **`Overheating`** | Temperature recorded ≥ 85°C on the last valid packet before stopping. |
| **`Intermittent signal`** | Lifetime communication quality is erratic (< 70%). |
| **`Sudden stop`** | Normal voltage, temperature, and historical reliability, with telemetry abruptly ending without prior warnings. |

### 4. Geospatial Clustering & Building Derivation (`MeterMapping`)
- Robust coordinate parsing for formats like `lat,lon`, `lon,lat`, `(lon, lat)`, and WKT `POINT(lon lat)`, with automatic detection and correction of inverted latitude/longitude.
- **Greedy Spatial Clustering (25m)**: Groups meters in close proximity into virtual Buildings (`B00001`, `B00002`, etc.) when physical structure IDs are unassigned.
- **Zone Grid Partitioning (2km)**: Partitions geographical regions into structured "Location" zones for spatial filtering.

---

## 🚀 REST API Reference

The API is built using ASP.NET Core Minimal APIs for ultra-low latency:

### `GET /api/ping`
Health check endpoint.
```json
{
  "ok": true,
  "time": "2026-11-13T17:00:00.0000000+05:30"
}
```

### `GET /api/gps-meters`
Retrieves all mapped meters with geographical coordinates, assigned buildings, and locations.

### `GET /api/meter-status`
Retrieves comprehensive communication metrics, status flags, and failure classifications for all meters.

**Query Parameters:**
| Parameter | Type | Default | Description |
| :--- | :--- | :--- | :--- |
| `days` | `int` | `7` | Relative historical lookback window in days (clamped 1–400). |
| `from` | `date` | `null` | Start date filter (`yyyy-MM-dd`). |
| `to` | `date` | `null` | End date filter (`yyyy-MM-dd`). Maximum window of 366 days. |
| `today` | `bool` | `true` | If `true`, status freshness is judged against current time. If `false`, judged against `dataMax`. |
| `stale` | `int` | `24` | Inactivity threshold in hours before a meter is marked offline. |

**Sample Response Structure:**
```json
{
  "refTime": "2026-11-13T17:00:00",
  "statusRef": "2026-11-13T17:00:00",
  "dataMax": "2026-11-13T16:45:00",
  "from": "2026-11-06T17:00:00",
  "to": "2026-11-13T17:00:00",
  "total": 1250,
  "communicating": 1180,
  "buildings": 84,
  "areas": 6,
  "warnings": [],
  "meters": [
    {
      "meterId": "MTR-100234",
      "intervalMinutes": 15,
      "commPct": 98.5,
      "lifeCommPct": 99.2,
      "hoursSilent": 0.2,
      "status": "Communicating",
      "reason": "-",
      "lastComm": "2026-11-13T16:45:00",
      "vMin": 234.2,
      "temp": 38.5,
      "buildingId": "B00012",
      "buildingName": "Building 12",
      "area": "Location 1",
      "lat": 28.6139,
      "lon": 77.2090,
      "manufacturer": "Secure",
      "nsp": "NSP-A"
    }
  ]
}
```

---

## 🔧 Configuration

All database profiles, schema definitions, and electrical parameter mappings are managed via `src/MeterMap.Api/appsettings.json`:

```json
{
  "DatabaseOptions": {
    "ActiveProfile": "Primary",
    "Profiles": {
      "Primary": {
        "Server": ".\\SQLEXPRESS",
        "Database": "MeterDB",
        "AuthMode": "Windows",
        "User": "",
        "Password": "",
        "LatLonOrder": "lonlat",
        "EquipmentTable": "dbo.Equipment",
        "DeviceLocationTable": "dbo.device_location",
        "DeviceLinkTable": "dbo.devloc_device_link",
        "IntervalTable": "dbo.interval_data",
        "ParameterTable": "dbo.parameter_master"
      }
    }
  },
  "ParameterIds": {
    "VB": 388,
    "VR": 395,
    "VY": 404,
    "IR": 236,
    "IY": 243,
    "IB": 201,
    "IntKvahI": 211,
    "IntKwhI": 217
  }
}
```

### Telemetry Parameter IDs
The parameter IDs correspond to rows in your `parameter_master` table:
- **Voltages**: `VR` (Phase R), `VY` (Phase Y), `VB` (Phase B)
- **Currents**: `IR`, `IY`, `IB`
- **Active / Apparent Energy**: `IntKwhI`, `IntKvahI`

---

## 🏁 Getting Started

### Prerequisites
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Microsoft SQL Server (2016 or higher, Express, or Azure SQL)

### 1. Clone & Restore
```bash
git clone https://github.com/your-org/METER_COM.NET.git
cd METER_COM.NET
dotnet restore
```

### 2. Configure Database Connection
Edit `src/MeterMap.Api/appsettings.json` with your SQL Server connection details and database table names.

### 3. Run the Web Application
```bash
dotnet run --project src/MeterMap.Api
```
Once started, the API and map dashboard will be available at:
- **Map Dashboard**: `http://localhost:5158/index.html` (or `https://localhost:7158/index.html`)
- **Healthcheck**: `http://localhost:5158/api/ping`

---

## 🧪 Running Unit Tests

The test suite validates interval inference, communication quality edge cases, reason classification rules, and spatial clustering algorithms:

```bash
dotnet test
```

To run tests with detailed output:
```bash
dotnet test tests/MeterMap.Tests/MeterMap.Tests.csproj --logger "console;verbosity=detailed"
```

---

## 📊 Dashboard UI Features

The built-in web frontend (`src/MeterMap.Api/wwwroot/index.html`) provides a dark-mode command center:
- **Interactive Geospatial Map**: Renders colored meter nodes with MapLibre GL vector tiles.
- **Dynamic Filtering**:
  - Date range selector (preset days or exact start/end dates).
  - Stale inactivity threshold slider.
  - Filter by status (`Communicating` vs `Not communicating`), building, area, manufacturer, or failure reason.
- **Analytics Visuals**:
  - Live Fleet Health gauge (percentage communicating).
  - Outage cause breakdown and counts.
  - 100-tile status waffle visualization.
  - Drill-down inspector for individual meters showing latest telemetry, voltage, and silent duration.

---

## 🛡️ License

This project is licensed under the [MIT License](LICENSE).
