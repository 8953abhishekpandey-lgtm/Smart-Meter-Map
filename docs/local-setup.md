# Run the platform on your own PC with VS Code

This guide takes a Windows PC from nothing to the running app in VS Code, connected to your own SQL Server and
loaded with your own Excel files. Allow about 30 minutes the first time (mostly installs).

## 1. Install the tools (once)

**Quickest: one script installs everything on drive D:** (SQL Server 2022 Developer in `D:\SQLServer`; sqlcmd, the
.NET 10 SDK and Node.js 22 in `D:\Tools`, added to the Windows PATH). Open PowerShell with **Run as administrator**:

```powershell
Set-ExecutionPolicy -Scope Process Bypass -Force
D:\Smart-Meter-Map\scripts\setup-windows-dev.ps1          # add -Root E:\ for another drive, -SkipSqlServer for tools only
```

It skips anything already installed and ends with a check of every tool. Then open a **new** PowerShell / VS Code window
so the PATH changes apply. If your network blocks the SQL Server download, download *SQL Server 2022 Developer* from
https://www.microsoft.com/sql-server/sql-server-downloads and run the script again with `-SqlInstaller <path to SQL2022-SSEI-Dev.exe>`.

Or install the tools yourself:

| Tool | Version | How to get it (Windows) |
| --- | --- | --- |
| Git | any | `winget install Git.Git` |
| .NET SDK | 10.0 | `winget install Microsoft.DotNet.SDK.10` |
| Node.js | 22 LTS | `winget install OpenJS.NodeJS.LTS` |
| SQL Server | 2022 or later | use the instance you already have, or install **SQL Server 2022 Developer** (free) |
| sqlcmd | any | `winget install sqlcmd` |
| SSMS | any | optional, to look at the database |
| VS Code | latest | `winget install Microsoft.VisualStudioCode` |

Open a **new** terminal after installing so the tools are on the PATH. Check with:

```powershell
git --version; dotnet --version; node --version; sqlcmd -?
```

SQL Server must accept TCP connections: open *SQL Server Configuration Manager* → *SQL Server Network Configuration*
→ *Protocols for MSSQLSERVER* → enable **TCP/IP**, then restart the SQL Server service. (SQL Server Express names
its instance `SQLEXPRESS`; use `localhost\SQLEXPRESS` wherever this guide says `localhost`.)

## 2. Get the code

From the zip:

```powershell
Expand-Archive Smart-Meter-Grid-full.zip C:\dev
code C:\dev\Smart-Meter-Grid
```

Or, once the branch is on GitHub/GitLab:

```powershell
git clone -b claude/smart-meter-gis-platform-ncl2td https://github.com/tanishqgithub7i9/Smart_Meter_com.git C:\dev\Smart-Meter-Grid
code C:\dev\Smart-Meter-Grid
```

When VS Code asks, **trust the folder** and **install the recommended extensions** (C# Dev Kit, Angular Language
Service, SQL Server (mssql), PowerShell). Then run the task **web: install packages** once
(*Terminal → Run Task…*), which downloads the web app's packages.

## 3. Create the database

The platform keeps everything in its own database, `SmartMeterDb`; no other database on the server is touched.

In VS Code: *Terminal → Run Task…* → **db: deploy (empty database)** and accept `localhost` (or type your server
name exactly as in SSMS). This uses your Windows login, like SSMS.

Want sample data to explore first? Run **db: deploy + 300K demo meters** instead (about 2 minutes). Do this only on a
test database: your real files will be added to whatever is already there.

Same thing from a terminal:

```powershell
.\database\deploy.ps1                                   # Windows authentication, localhost
.\database\deploy.ps1 -Server "localhost\SQLEXPRESS"    # named instance
.\database\deploy.ps1 -Server "sql01,1433" -User sa -Password "<password>"   # SQL login
```

## 4. Connect the API to the database

Nothing to do if SQL Server runs on the same PC as the default instance and you use Windows authentication: the
development settings already use `Server=localhost;Database=SmartMeterDb;Integrated Security=True`.

Otherwise, store your own connection string once (it is kept outside the project, never committed):

```powershell
cd backend
dotnet user-secrets --project src/SmartMeter.Api set "ConnectionStrings:SmartMeterDb" `
  "Server=localhost\SQLEXPRESS;Database=SmartMeterDb;Integrated Security=True;TrustServerCertificate=True"
# SQL login instead:  "Server=sql01,1433;Database=SmartMeterDb;User Id=<login>;Password=<password>;TrustServerCertificate=True"
```

## 5. Run it

Press **F5** with **Full stack: API + web app (Edge)** (or *Chrome*) selected in the *Run and Debug* view. VS Code
builds and starts the API on `http://localhost:5080`, starts the web app on `http://localhost:4200` and opens it in
the browser. Breakpoints work in both the C# and the TypeScript code. Sign-in is switched off in development.

| Address | What |
| --- | --- |
| http://localhost:4200 | the web app (map dashboard, Explorer, Data ingestion) |
| http://localhost:5080/scalar | API reference, try any endpoint |
| http://localhost:5080/hangfire | background jobs (ingestion, refresh) |
| http://localhost:5080/health | health check |

## 6. Load your Excel files

The project's `data` folder is a **watch folder** while the app runs from VS Code: copy files into it and they are
imported automatically within about a minute. Files in `data` are never committed to git.

1. Put **`cis`** or **`master`** in the name (or folder) of customer-master files and **`mdm`**, **`comm`** or
   **`log`** in daily communication files. A daily file can carry its day only in its name, so a sheet with just
   *Meter No* and *Status* columns is fine:

    ```
    data/cis/cis_master.xlsx
    data/mdm/mdm_2026-11-18.xlsx
    data/mdm/mdm_2026-11-19.xlsx
    data/mdm/comm 20-11-2026.xlsx
    ```

2. Open **Data ingestion** in the browser. The *Watch folder* panel lists every file as imported, waiting, failed (with
   the reason) or "name not recognised", and **Scan now** imports immediately instead of waiting for the next minute.
3. Master files are imported first, then daily files in date order. Saving a corrected file imports it again.
4. The dashboards refresh automatically after each scan that imported something.

You can also drag a single file onto the Data ingestion page, or upload a whole folder to any server with the task
**data: upload files in ./data** (`scripts/upload-folder.ps1`).

**Columns.** CIS master: `MeterId` is required; `BuildingId`, `Latitude`, `Longitude`, `Address`, `NSP`,
`Manufacturer`, `Zone`, `InstallationDate`, `DtId` are optional. MDM logs: `MeterId`, `IsCommunicating` (1/0,
true/false, yes/no, success/fail, Communicating / Non-Communicating, online/offline) and `LogDate` unless the day is in
the file name. Common alternative headers are recognised (*Meter No*, *MSN*, *SIM Operator*, *Make*, *Circle*,
*Area*, *Read Date*, *Comm Status*, …). Download empty templates from the Data ingestion page.

**Typical rejections** (see the error report): coordinates outside India (the allowed area is
`Ingestion:ServiceArea` in `appsettings.json`), dates in an unknown format, and MDM rows for meters that are not in
the CIS master yet; load the CIS file first.

**Your own folder.** To watch another folder (for example where the MDM system drops its daily export), set it in
`backend/src/SmartMeter.Api/appsettings.Development.json`, or on a server in `appsettings.Production.json`:

```json
"Ingestion": { "WatchFolder": "D:\\MDM\\daily", "WatchFolderScanMinutes": 1 }
```

The API only reads the files; it never moves or deletes them. On a server, give the service account read access to
the folder.

## 7. Combine several days (date range)

On the dashboard switch **Selected day** to **Date range** and pick **From** and **To**, or a quick pick (*Last day*,
*Last 7 days*, *Last 14 days*, *All 30 days*). The KPI cards, map colours, zone and building lists, the building
drill-down, the Cluster Finder and the SIM-switch finder then show the **average day of that range**, built from the
daily files of every day in it. Percentages are exact (counted over all meter-days in the range). The range must lie
inside the analytics window, the latest 30 days of loaded data. The browser address keeps the range, so a link opens the
same view.

## 8. Everyday tasks

| Task (Terminal → Run Task…) | Does |
| --- | --- |
| api: build / api: test | build the .NET solution / run its unit tests |
| web: test | run the web app's unit tests |
| db: refresh analytics | rebuild the dashboard tables now (normally automatic) |
| release: build win-x64 zip | the deployable zip for a Windows server (see the README, section 2) |

## 9. When something does not work

| Symptom | Fix |
| --- | --- |
| `sqlcmd was not found` | `winget install sqlcmd`, then open a new terminal |
| `A network-related or instance-specific error` | TCP/IP is off, the SQL Server service is stopped, or the server name is wrong (Express: `localhost\SQLEXPRESS`) |
| `Login failed for user` | your Windows account has no login on that SQL Server; use a SQL login in step 4, or ask the DBA |
| `The certificate chain was issued by an authority that is not trusted` | add `TrustServerCertificate=True` to the connection string |
| API stops at start with a SQL timeout | SQL Server was still starting; press F5 again |
| Port 5080 or 4200 already in use | close the other program, or stop the earlier run (Shift+F5) |
| The map shows bubbles but no background | your network blocks `tile.openstreetmap.org`; point `basemaps` in `frontend/public/config.json` at an allowed tile server |
| PowerShell refuses to run the scripts | the tasks already use `-ExecutionPolicy Bypass`; in a terminal run `Set-ExecutionPolicy -Scope CurrentUser RemoteSigned` once |
