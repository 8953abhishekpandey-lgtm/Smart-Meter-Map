<#  Deploys SmartMeterDb to SQL Server with sqlcmd (Windows). Equivalent SSMS steps: open and execute
    00_ .. 05_ in order (06_security.sql in SQLCMD Mode), then: EXEC sm.usp_RefreshAnalytics;
    .\deploy.ps1 -Server "localhost,1433" -User sa -Password "..." [-SeedDemoMeters 300000] [-AppPassword "..."] #>
param(
    [string]$Server = "localhost,1433",
    [string]$User = "sa",
    [Parameter(Mandatory = $true)][string]$Password,
    [int]$SeedDemoMeters = 0,
    [string]$AppPassword = ""
)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
function Run([string[]]$extra) { & sqlcmd -S $Server -U $User -P $Password -C -I -b @extra; if ($LASTEXITCODE -ne 0) { throw "sqlcmd failed" } }
foreach ($f in "00_create_database.sql","01_schema.sql","02_partitions.sql","03_analytics.sql","04_ingestion.sql","05_demo_data.sql") {
    Write-Host ">> $f"; Run @("-i", $f)
}
if ($AppPassword) { Write-Host ">> 06_security.sql"; Run @("-v", "AppPassword=$AppPassword", "-i", "06_security.sql") }
if ($SeedDemoMeters -gt 0) { Write-Host ">> demo fleet"; Run @("-d", "SmartMeterDb", "-t", "0", "-Q", "IF NOT EXISTS (SELECT 1 FROM sm.Meter) EXEC sm.usp_GenerateDemoData @Meters = $SeedDemoMeters;") }
Write-Host ">> refreshing analytics"; Run @("-d", "SmartMeterDb", "-t", "0", "-Q", "EXEC sm.usp_RefreshAnalytics;")
