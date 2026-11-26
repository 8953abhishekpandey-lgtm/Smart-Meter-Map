<#
  One-time developer setup on Windows with everything on drive D: (or the drive you choose).

  Run in PowerShell opened with "Run as administrator":
      Set-ExecutionPolicy -Scope Process Bypass -Force
      D:\Smart-Meter-Map\scripts\setup-windows-dev.ps1

  Installs (anything already there is skipped, so it is safe to run again):
    * SQL Server 2022 Developer edition (free)  -> D:\SQLServer   (default instance, TCP 1433, your Windows login is sysadmin)
    * sqlcmd                                     -> D:\Tools\sqlcmd
    * .NET 10 SDK                                -> D:\Tools\dotnet
    * Node.js 22 LTS                             -> D:\Tools\node
  and puts D:\Tools\sqlcmd, D:\Tools\dotnet and D:\Tools\node on the machine PATH, so `sqlcmd`, `dotnet`, `node` and `npm`
  work in every NEW terminal. The npm and NuGet package caches are moved to D:\Tools as well.

  Note: SQL Server's setup always keeps about 1 GB of shared components on C:; the instance, its databases and logs go to D:.

  Options:
    -Root E:\                       use another drive
    -SkipSqlServer                  only the developer tools
    -SqlInstaller <path>            use a SQL2022-SSEI-Dev.exe you downloaded yourself (if the automatic download is blocked)
#>
param(
    [string]$Root = "D:\",
    [switch]$SkipSqlServer,
    [string]$SqlInstaller = ""
)
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"          # Invoke-WebRequest is many times faster without the progress bar
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Please run this in PowerShell opened with 'Run as administrator' (SQL Server and the machine PATH need it)."
}
if (-not (Test-Path $Root)) { throw "Drive or folder '$Root' was not found." }

$tools = Join-Path $Root "Tools"
$sqlRoot = Join-Path $Root "SQLServer"
$downloads = Join-Path $tools "_downloads"
New-Item -ItemType Directory -Force -Path $tools, $downloads | Out-Null

function Add-ToMachinePath([string]$dir, [switch]$First) {
    $parts = @([Environment]::GetEnvironmentVariable("Path", "Machine") -split ";" | Where-Object { $_ -and $_ -ne $dir })
    $parts = if ($First) { @($dir) + $parts } else { $parts + @($dir) }
    [Environment]::SetEnvironmentVariable("Path", ($parts -join ";"), "Machine")
    $env:Path = (@($dir) + @($env:Path -split ";" | Where-Object { $_ -and $_ -ne $dir })) -join ";"   # usable in this window too
    Write-Host "   on PATH: $dir"
}

function Get-File([string]$url, [string]$file) {
    Write-Host "   downloading $url"
    Invoke-WebRequest -Uri $url -OutFile $file -UseBasicParsing
}

# ---------------------------------------------------------------- sqlcmd
Write-Host ">> sqlcmd"
$sqlcmdDir = Join-Path $tools "sqlcmd"
if (Test-Path (Join-Path $sqlcmdDir "sqlcmd.exe")) { Write-Host "   already installed" }
else {
    $zip = Join-Path $downloads "sqlcmd-windows-amd64.zip"
    Get-File "https://github.com/microsoft/go-sqlcmd/releases/latest/download/sqlcmd-windows-amd64.zip" $zip
    Expand-Archive -Path $zip -DestinationPath $sqlcmdDir -Force
}
Add-ToMachinePath $sqlcmdDir

# ---------------------------------------------------------------- .NET 10 SDK
Write-Host ">> .NET 10 SDK"
$dotnetDir = Join-Path $tools "dotnet"
$dotnetExe = Join-Path $dotnetDir "dotnet.exe"
$hasSdk10 = (Test-Path $dotnetExe) -and (@(& $dotnetExe --list-sdks) -match '^10\.').Count -gt 0
if ($hasSdk10) { Write-Host "   already installed" }
else {
    $installer = Join-Path $downloads "dotnet-install.ps1"
    Get-File "https://dot.net/v1/dotnet-install.ps1" $installer
    & $installer -Channel 10.0 -InstallDir $dotnetDir -NoPath
}
[Environment]::SetEnvironmentVariable("DOTNET_ROOT", $dotnetDir, "Machine"); $env:DOTNET_ROOT = $dotnetDir
[Environment]::SetEnvironmentVariable("DOTNET_CLI_TELEMETRY_OPTOUT", "1", "Machine")
$nuget = Join-Path $tools "nuget-packages"
[Environment]::SetEnvironmentVariable("NUGET_PACKAGES", $nuget, "Machine"); $env:NUGET_PACKAGES = $nuget
Add-ToMachinePath $dotnetDir -First        # first, so it wins over an older C:\Program Files\dotnet

# ---------------------------------------------------------------- Node.js 22 LTS
Write-Host ">> Node.js 22 LTS"
$nodeDir = Join-Path $tools "node"
if (Test-Path (Join-Path $nodeDir "node.exe")) { Write-Host "   already installed" }
else {
    $index = Invoke-RestMethod "https://nodejs.org/dist/index.json"     # one array object: enumerate it before filtering
    $release = $index | Where-Object { $_.version -like "v22.*" -and $_.lts } | Select-Object -First 1
    if (-not $release) { throw "Could not find a Node.js 22 LTS release on nodejs.org." }
    $v = $release.version
    $zip = Join-Path $downloads "node-$v-win-x64.zip"
    Get-File "https://nodejs.org/dist/$v/node-$v-win-x64.zip" $zip
    Expand-Archive -Path $zip -DestinationPath $downloads -Force
    Move-Item -Path (Join-Path $downloads "node-$v-win-x64") -Destination $nodeDir
}
Add-ToMachinePath $nodeDir -First
& (Join-Path $nodeDir "npm.cmd") config set cache (Join-Path $tools "npm-cache") --global | Out-Null

# ---------------------------------------------------------------- SQL Server 2022 Developer
if ($SkipSqlServer) {
    Write-Host ">> SQL Server: skipped (-SkipSqlServer)"
}
elseif (Get-Service -Name MSSQLSERVER -ErrorAction SilentlyContinue) {
    Write-Host ">> SQL Server: the default instance (MSSQLSERVER) is already installed"
}
else {
    Write-Host ">> SQL Server 2022 Developer -> $sqlRoot"
    $media = Join-Path $downloads "sqlserver-media"
    New-Item -ItemType Directory -Force -Path $media | Out-Null
    if (-not $SqlInstaller) {
        $SqlInstaller = Join-Path $downloads "SQL2022-SSEI-Dev.exe"
        if (-not (Test-Path $SqlInstaller)) {
            try { Get-File "https://go.microsoft.com/fwlink/p/?linkid=2215158&clcid=0x409&culture=en-us&country=us" $SqlInstaller }
            catch {
                throw ("Could not download the SQL Server installer ($($_.Exception.Message)). Download 'SQL Server 2022 Developer' from " +
                    "https://www.microsoft.com/sql-server/sql-server-downloads and run this script again with -SqlInstaller <path to SQL2022-SSEI-Dev.exe>.")
            }
        }
    }

    $package = Get-ChildItem -Path $media -Filter "SQLServer2022-DEV-*.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $package) {
        Write-Host "   downloading the installation media (about 1.5 GB, several minutes)..."
        $p = Start-Process -FilePath $SqlInstaller -ArgumentList "/ACTION=Download", "/MEDIAPATH=`"$media`"", "/MEDIATYPE=CAB", "/QUIET" -Wait -PassThru
        if ($p.ExitCode -ne 0) { throw "Downloading the SQL Server media failed (exit code $($p.ExitCode))." }
        $package = Get-ChildItem -Path $media -Filter "SQLServer2022-DEV-*.exe" | Select-Object -First 1
        if (-not $package) { throw "The SQL Server media was not found in $media." }
    }

    $setupDir = Join-Path $media "Setup"
    if (-not (Test-Path (Join-Path $setupDir "setup.exe"))) {
        Write-Host "   extracting the media..."
        $p = Start-Process -FilePath $package.FullName -ArgumentList "/q", "/x:`"$setupDir`"" -Wait -PassThru
        if ($p.ExitCode -ne 0 -or -not (Test-Path (Join-Path $setupDir "setup.exe"))) { throw "Extracting the SQL Server media failed (exit code $($p.ExitCode))." }
    }

    $me = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    $setupArgs = @(
        "/Q", "/ACTION=Install", "/IACCEPTSQLSERVERLICENSETERMS", "/SUPPRESSPRIVACYSTATEMENTNOTICE", "/ENU",
        "/FEATURES=SQLENGINE", "/INSTANCENAME=MSSQLSERVER",
        "/INSTANCEDIR=`"$sqlRoot`"",
        "/SQLUSERDBDIR=`"$sqlRoot\Data`"", "/SQLUSERDBLOGDIR=`"$sqlRoot\Log`"",
        "/SQLTEMPDBDIR=`"$sqlRoot\TempDB`"", "/SQLTEMPDBLOGDIR=`"$sqlRoot\TempDB`"", "/SQLBACKUPDIR=`"$sqlRoot\Backup`"",
        "/SQLSVCACCOUNT=`"NT Service\MSSQLSERVER`"", "/SQLSVCSTARTUPTYPE=Automatic",
        "/SQLSYSADMINACCOUNTS=`"$me`" `"BUILTIN\Administrators`"",
        "/TCPENABLED=1", "/UPDATEENABLED=False", "/USESQLRECOMMENDEDMEMORYLIMITS"
    )
    Write-Host "   installing (5-15 minutes)..."
    $p = Start-Process -FilePath (Join-Path $setupDir "setup.exe") -ArgumentList $setupArgs -Wait -PassThru
    if ($p.ExitCode -notin 0, 3010) {
        throw "SQL Server setup failed (exit code $($p.ExitCode)). Details: C:\Program Files\Microsoft SQL Server\160\Setup Bootstrap\Log\Summary.txt"
    }
    if ($p.ExitCode -eq 3010) { Write-Warning "SQL Server is installed; Windows asks for a restart before first use." }
}

# ---------------------------------------------------------------- check
Write-Host ""
Write-Host ">> check"
Write-Host ("   dotnet  {0}" -f (& $dotnetExe --version))
Write-Host ("   node    {0}" -f (& (Join-Path $nodeDir "node.exe") --version))
if (-not $SkipSqlServer) {
    $svc = Get-Service -Name MSSQLSERVER -ErrorAction SilentlyContinue
    if ($svc -and $svc.Status -ne "Running") { Start-Service MSSQLSERVER }
    try {
        $version = & (Join-Path $sqlcmdDir "sqlcmd.exe") -S localhost -E -C -h -1 -W -Q "SET NOCOUNT ON; SELECT CAST(SERVERPROPERTY('ProductVersion') AS varchar(20)) + ' ' + CAST(SERVERPROPERTY('Edition') AS varchar(60));"
        Write-Host "   SQL Server $version"
    }
    catch { Write-Warning "SQL Server did not answer yet: $($_.Exception.Message)" }
}
Write-Host ""
Write-Host "Done. Open a NEW PowerShell or VS Code window so the PATH changes apply, then:"
Write-Host "   cd <project folder>; powershell -ExecutionPolicy Bypass -File .\database\deploy.ps1 -Server localhost -SeedDemoMeters 300000"
Write-Host "SSMS (optional, to browse the database): https://aka.ms/ssms"
