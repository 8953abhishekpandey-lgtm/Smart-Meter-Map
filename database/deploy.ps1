<#  Deploys SmartMeterDb to SQL Server with sqlcmd (Windows, PowerShell 5.1 or 7).
    Equivalent SSMS steps: open and execute 00_ .. 05_ in order (06_security.sql in SQLCMD Mode), then
    EXEC sm.usp_RefreshAnalytics;

    Windows authentication (your own login, as in SSMS) - leave out -Password:
      .\deploy.ps1                                         # localhost, empty database
      .\deploy.ps1 -Server "localhost\SQLEXPRESS"          # named instance (e.g. SQL Server Express)
      .\deploy.ps1 -SeedDemoMeters 300000                  # + synthetic demo fleet (development only)
    SQL authentication:
      .\deploy.ps1 -Server "sqlprod01,1433" -User sa -Password "..." [-AppPassword "..."]

    -AppPassword creates/updates the least-privilege smartmeter_app login the API uses in production.
    Requires sqlcmd: `winget install sqlcmd` (or the ODBC sqlcmd that ships with SQL Server tools). #>
param(
    [string]$Server = "localhost,1433",
    [string]$User = "",
    [string]$Password = "",
    [int]$SeedDemoMeters = 0,
    [string]$AppPassword = ""
)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

if (-not (Get-Command sqlcmd -ErrorAction SilentlyContinue)) {
    throw "sqlcmd was not found. Install it with 'winget install sqlcmd' (or SQL Server command line tools) and open a new terminal."
}
# no password = Windows (trusted) authentication with the current user, like SSMS 'Windows Authentication'
$auth = if ($Password) { @("-U", $(if ($User) { $User } else { "sa" }), "-P", $Password) } else { @("-E") }
Write-Host ("Server: {0}  Authentication: {1}" -f $Server, $(if ($Password) { "SQL login '$($auth[1])'" } else { "Windows (current user)" }))

function Run([string[]]$extra) {
    & sqlcmd -S $Server @auth -C -I -b @extra
    if ($LASTEXITCODE -ne 0) { throw "sqlcmd failed (exit code $LASTEXITCODE)" }
}

foreach ($f in "00_create_database.sql", "01_schema.sql", "02_partitions.sql", "03_analytics.sql", "04_ingestion.sql", "05_demo_data.sql") {
    Write-Host ">> $f"; Run @("-i", $f)
}
if ($AppPassword) { Write-Host ">> 06_security.sql"; Run @("-v", "AppPassword=$AppPassword", "-i", "06_security.sql") }
if ($SeedDemoMeters -gt 0) {
    Write-Host ">> generating demo fleet of $SeedDemoMeters meters (only if sm.Meter is empty)"
    Run @("-d", "SmartMeterDb", "-t", "0", "-Q", "IF NOT EXISTS (SELECT 1 FROM sm.Meter) EXEC sm.usp_GenerateDemoData @Meters = $SeedDemoMeters;")
}
Write-Host ">> refreshing analytics"
Run @("-d", "SmartMeterDb", "-t", "0", "-Q", "SET NOCOUNT ON; EXEC sm.usp_RefreshAnalytics;")
Write-Host "done."
