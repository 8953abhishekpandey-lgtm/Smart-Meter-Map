<#  Uploads every Excel / CSV file in a folder to the Smart Meter API, CIS master files first, then MDM logs,
    and waits for each ingestion job to finish (PowerShell 5.1 or 7, Windows / Linux / macOS).

      .\scripts\upload-folder.ps1                                  # .\data, API on http://localhost:5080
      .\scripts\upload-folder.ps1 -Folder D:\mdm\2026-11 -BaseUrl https://smartmeter.company.local -Token "<bearer token>"

    The file type is taken from the file name: names containing "cis" or "master" are CIS master files,
    names containing "mdm", "comm" or "log" are MDM communication logs. Anything else is skipped with a warning;
    rename it, or pass -Kind cis|mdm to treat every file in the folder as one type. #>
param(
    [string]$Folder = (Join-Path $PSScriptRoot "..\data"),
    [string]$BaseUrl = "http://localhost:5080",
    [string]$Token = "",
    [ValidateSet("", "cis", "mdm")][string]$Kind = "",
    [int]$TimeoutMinutes = 120
)
$ErrorActionPreference = "Stop"
$api = $BaseUrl.TrimEnd("/") + "/api/v1/ingestion"
$headers = @{}
if ($Token) { $headers["Authorization"] = "Bearer $Token" }

$extensions = ".xlsx", ".xlsb", ".xls", ".csv", ".txt"
$files = @(Get-ChildItem -LiteralPath $Folder -File | Where-Object { $extensions -contains $_.Extension.ToLowerInvariant() } | Sort-Object Name)
if ($files.Count -eq 0) { throw "No .xlsx/.xlsb/.xls/.csv/.txt files found in $Folder" }

function KindOf([IO.FileInfo]$f) {
    if ($Kind) { return $Kind }
    $n = $f.BaseName.ToLowerInvariant()
    if ($n -match "cis|master") { return "cis" }
    if ($n -match "mdm|comm|log") { return "mdm" }
    return ""
}

$plan = @()
foreach ($f in $files) {
    $k = KindOf $f
    if (-not $k) { Write-Warning "Skipping '$($f.Name)': name says neither CIS nor MDM (use -Kind, or put cis/mdm in the name)."; continue }
    $plan += [pscustomobject]@{ File = $f; Kind = $k; Order = $(if ($k -eq "cis") { 0 } else { 1 }) }
}
$plan = @($plan | Sort-Object Order, { $_.File.Name })

$failed = 0
foreach ($p in $plan) {
    $f = $p.File
    $size = "{0:N1} MB" -f ($f.Length / 1MB)
    Write-Host ""
    Write-Host ">> $($p.Kind.ToUpperInvariant())  $($f.Name)  ($size)"
    $url = "$api/files?kind=$($p.Kind)&fileName=$([uri]::EscapeDataString($f.Name))"
    $job = Invoke-RestMethod -Method Post -Uri $url -InFile $f.FullName -ContentType "application/octet-stream" -Headers $headers
    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    do {
        Start-Sleep -Seconds 2
        $job = Invoke-RestMethod -Method Get -Uri "$api/jobs/$($job.jobId)" -Headers $headers
        Write-Host ("   {0,-10} {1,5:N0}%   read {2:N0}  valid {3:N0}  rejected {4:N0}" -f $job.status, [double]$job.progressPct, $job.rowsRead, $job.rowsValid, $job.rowsRejected)
    } while ($job.status -notin @("completed", "failed") -and (Get-Date) -lt $deadline)

    if ($job.status -ne "completed") {
        $failed++
        Write-Warning "Job $($job.jobId) ended as '$($job.status)': $($job.message)"
    }
    else {
        Write-Host ("   done: inserted {0:N0}, updated {1:N0}, rejected {2:N0}" -f $job.rowsInserted, $job.rowsUpdated, $job.rowsRejected)
    }
    if ($job.rowsRejected -gt 0) {
        $report = Join-Path $f.DirectoryName ("{0}.errors.csv" -f $f.BaseName)
        Invoke-WebRequest -Uri "$api/jobs/$($job.jobId)/errors" -Headers $headers -OutFile $report | Out-Null
        Write-Host "   row-level error report: $report"
    }
}
Write-Host ""
Write-Host "Uploaded $($plan.Count - $failed) of $($plan.Count) file(s). The analytics refresh runs automatically; the dashboard updates when it finishes."
if ($failed -gt 0) { exit 1 }
