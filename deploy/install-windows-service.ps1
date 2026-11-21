<# Installs or upgrades the platform as a Windows Service (run as Administrator on the app server).
   .\install-windows-service.ps1 -Source .\smartmeter-win-x64 [-Target C:\SmartMeter\app] [-Port 8080]
   Prerequisite: ASP.NET Core 10 Runtime (Hosting Bundle) on the server. #>
param(
    [Parameter(Mandatory = $true)][string]$Source,
    [string]$Target = "C:\SmartMeter\app",
    [string]$ServiceName = "SmartMeterApi",
    [int]$Port = 8080
)
$ErrorActionPreference = "Stop"
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svc -and $svc.Status -ne "Stopped") { Stop-Service $ServiceName; $svc.WaitForStatus("Stopped", "00:01:00") }

New-Item -ItemType Directory -Force -Path $Target | Out-Null
# keep the server's own settings across upgrades: appsettings.Production.json and the SPA's wwwroot\config.json
$spaConfig = Join-Path $Target "wwwroot\config.json"
$savedSpaConfig = if (Test-Path $spaConfig) { Get-Content $spaConfig -Raw } else { $null }
robocopy $Source $Target /MIR /XF appsettings.Production.json /NFL /NDL /NJH /NJS | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE)" }
if ($savedSpaConfig) { Set-Content -Path $spaConfig -Value $savedSpaConfig -NoNewline -Encoding UTF8 }
if (-not (Test-Path (Join-Path $Target "appsettings.Production.json"))) {
    Copy-Item (Join-Path $Target "appsettings.Production.template.json") (Join-Path $Target "appsettings.Production.json")
    Write-Warning "Created appsettings.Production.json from the template - edit the connection string and auth settings, then start the service."
}

$exe = Join-Path $Target "SmartMeter.Api.exe"
if (-not $svc) {
    New-Service -Name $ServiceName -BinaryPathName "`"$exe`" --urls http://0.0.0.0:$Port" -DisplayName "Smart Meter GIS Platform" `
        -Description "Smart meter communication & health monitoring API + web app" -StartupType Automatic | Out-Null
    sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Out-Null
    New-NetFirewallRule -DisplayName "Smart Meter GIS ($Port)" -Direction Inbound -Protocol TCP -LocalPort $Port -Action Allow -ErrorAction SilentlyContinue | Out-Null
}
[Environment]::SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Production", "Machine")
Start-Service $ServiceName
Write-Host "Service '$ServiceName' running. Check: Invoke-WebRequest http://localhost:$Port/health/ready"
