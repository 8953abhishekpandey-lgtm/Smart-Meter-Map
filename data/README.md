# Your data files (the watch folder in development)

Copy your Excel (`.xlsx`, `.xlsb`, `.xls`) and CSV files into this folder **while the API is running**: within about a
minute they are imported automatically, and the dashboards refresh. Nothing needs to be uploaded by hand. Everything
in this folder except this README is ignored by git, so customer data is never committed.

(In development the API watches this folder: `Ingestion:WatchFolder` = `../../../data` in
`backend/src/SmartMeter.Api/appsettings.Development.json`. On a server you set your own folder; see `docs/local-setup.md`.)

## How files are recognised

| Put in the file name or its folder | Loaded as | Columns |
| --- | --- | --- |
| `cis` or `master` (`cis_master_nov.xlsx`, `cis/meters.xlsx`) | CIS customer master: one row per meter | `MeterId` required; optional `BuildingId`, `Latitude`, `Longitude`, `Address`, `NSP`, `Manufacturer`, `Zone`, `InstallationDate`, `DtId` |
| `mdm`, `comm` or `log` (`mdm_2026-11-20.xlsx`, `mdm/2026-11/…`) | MDM communication log: one row per meter per day | `MeterId`, `IsCommunicating`, and `LogDate` **unless the day is in the file or folder name** |

A suggested layout for date-wise files:

```
data/
  cis/
    cis_master.xlsx            <- the meter list; replace it when meters change
  mdm/
    mdm_2026-11-18.xlsx        <- one file per day; the date in the name is enough
    mdm_2026-11-19.xlsx
    comm 20-11-2026.xlsx       <- dd-MM-yyyy, yyyy-MM-dd, yyyyMMdd, 20 Nov 2026 … all work
```

- Master files are imported first, then daily files in date order.
- A file is imported once; if you correct it and save it again, it is imported again (rows are updated, not doubled).
- `IsCommunicating` accepts 1/0, true/false, yes/no, success/fail, Communicating / Non-Communicating, online/offline.
- The **Data ingestion** page shows every file in the folder: imported, waiting, failed (with the reason) or "name not
  recognised", and has a **Scan now** button. Rows that fail validation are listed in each job's error report.

## Seeing several days together

On the dashboard choose **Date range** and pick *From* and *To* (or a quick pick such as *Last 7 days*). KPIs, map
colours, zones, buildings, clusters and the SIM-switch finder then show the average day of those days, combining the
daily files of every day in the range. Any range inside the analytics window (the latest 30 days of data) works.

You can still upload a single file by hand on the Data ingestion page, or run `.\scripts\upload-folder.ps1` against a
server that has no watch folder.
