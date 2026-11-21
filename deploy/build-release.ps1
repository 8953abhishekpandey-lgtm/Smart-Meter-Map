<# Builds ONE deployable folder/zip: the .NET API with the Angular app inside wwwroot (no containers, no web server needed).
   .\deploy\build-release.ps1 [-Rid win-x64]    -> artifacts\smartmeter-win-x64.zip #>
param([string]$Rid = "win-x64")
$ErrorActionPreference = "Stop"
$root = Resolve-Path "$PSScriptRoot\.."
$out = Join-Path $root "artifacts\smartmeter-$Rid"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Path $out | Out-Null

Write-Host ">> building Angular app"
Push-Location (Join-Path $root "frontend"); npm ci --no-audit --no-fund; npx ng build --configuration production; Pop-Location
if ($LASTEXITCODE -ne 0) { throw "frontend build failed" }

Write-Host ">> publishing API ($Rid)"
dotnet publish (Join-Path $root "backend\src\SmartMeter.Api\SmartMeter.Api.csproj") -c Release -r $Rid --self-contained false -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Write-Host ">> embedding SPA into wwwroot"
Copy-Item (Join-Path $root "frontend\dist\frontend\browser\*") (New-Item -ItemType Directory -Force -Path (Join-Path $out "wwwroot")) -Recurse -Force
Copy-Item (Join-Path $root "deploy\appsettings.Production.template.json") $out
Copy-Item (Join-Path $root "database") (Join-Path $out "database") -Recurse

$zip = Join-Path $root "artifacts\smartmeter-$Rid.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path $out -DestinationPath $zip
Write-Host ">> done: $zip"
