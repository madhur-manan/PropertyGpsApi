<#
    Diagnoses a 500.30 on the deployed PropertyGpsApi.

    Run ON THE SERVER (172.31.0.112) in an ELEVATED PowerShell.

    500.30 means IIS did its job: it found the app, loaded AspNetCoreModuleV2
    and started dotnet. The failure is inside the application's own startup.
    The browser will never show why - the real exception goes to the Windows
    event log and to the stdout log, and this script reads both.
#>

[CmdletBinding()]
param([string] $Alias = 'propertygps')

$ErrorActionPreference = 'Continue'
Import-Module WebAdministration

Write-Host "`n=== Where does '$Alias' actually point? ===" -ForegroundColor Cyan

$apps = Get-WebApplication | Where-Object { $_.Path -like "*$Alias*" }
$sites = Get-Website | Where-Object { $_.Name -like "*$Alias*" }

foreach ($a in $apps) {
    [PSCustomObject]@{
        Kind         = 'Application'
        Path         = $a.Path
        PhysicalPath = $a.PhysicalPath
        AppPool      = $a.ApplicationPool
    } | Format-List
}
foreach ($s in $sites) {
    [PSCustomObject]@{
        Kind         = 'Site'
        Name         = $s.Name
        State        = $s.State
        PhysicalPath = $s.PhysicalPath
        AppPool      = $s.ApplicationPool
        Bindings     = ($s.Bindings.Collection | ForEach-Object { $_.bindingInformation }) -join ', '
    } | Format-List
}

$target = @($apps) + @($sites) | Select-Object -First 1
if (-not $target) { Write-Host "No site or application matching '$Alias' found." -ForegroundColor Red; exit 1 }

$root = $target.PhysicalPath
$pool = $target.ApplicationPool
Write-Host "Physical path : $root"
Write-Host "App pool      : $pool"

Write-Host "`n=== Is that folder the published output? ===" -ForegroundColor Cyan
foreach ($f in 'PropertyGpsApi.dll','web.config','appsettings.json') {
    $p = Join-Path $root $f
    if (Test-Path $p) { Write-Host "  present  $f" -ForegroundColor Green }
    else              { Write-Host "  MISSING  $f" -ForegroundColor Red }
}

Write-Host "`n=== App pool runtime mode ===" -ForegroundColor Cyan
$rt = (Get-ItemProperty "IIS:\AppPools\$pool").managedRuntimeVersion
if ([string]::IsNullOrEmpty($rt)) { Write-Host "  ok  No Managed Code" -ForegroundColor Green }
else { Write-Host "  WRONG  managedRuntimeVersion = '$rt' - must be empty (No Managed Code)" -ForegroundColor Red }
Write-Host "  identity: $((Get-ItemProperty "IIS:\AppPools\$pool").processModel.identityType)"

Write-Host "`n=== The actual startup exception (event log) ===" -ForegroundColor Cyan
# AspNetCoreModuleV2 writes the unhandled startup exception here even when
# stdout logging is off. This is usually the whole answer.
$events = Get-WinEvent -FilterHashtable @{ LogName = 'Application' } -MaxEvents 300 -ErrorAction SilentlyContinue |
          Where-Object { $_.ProviderName -match 'IIS AspNetCore|AspNetCore' } |
          Select-Object -First 5
if (-not $events) {
    Write-Host "  Nothing from AspNetCoreModule in the last 300 Application events." -ForegroundColor Yellow
} else {
    foreach ($e in $events) {
        Write-Host ("`n--- {0}  [{1}] ---" -f $e.TimeCreated, $e.LevelDisplayName) -ForegroundColor Yellow
        Write-Host $e.Message
    }
}

Write-Host "`n=== stdout log ===" -ForegroundColor Cyan
# Turn this on if the event log was not enough. It captures everything the app
# wrote before it died, including the startup-check output.
$cfg = Join-Path $root 'web.config'
if (Test-Path $cfg) {
    $xml = [xml](Get-Content $cfg -Raw)
    $node = $xml.SelectSingleNode('//aspNetCore')
    if ($node) {
        Write-Host "  stdoutLogEnabled = $($node.stdoutLogEnabled)"
        Write-Host "  stdoutLogFile    = $($node.stdoutLogFile)"
        Write-Host "  hostingModel     = $($node.hostingModel)"
    }
}
$logDir = Join-Path $root 'logs'
if (Test-Path $logDir) {
    $latest = Get-ChildItem $logDir -Filter 'stdout*' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($latest) {
        Write-Host "`n--- $($latest.Name) ---" -ForegroundColor Yellow
        Get-Content $latest.FullName -Tail 60
    } else { Write-Host "  logs\ exists but is empty." -ForegroundColor Yellow }
} else {
    Write-Host "  No logs\ folder. To capture it:" -ForegroundColor Yellow
    Write-Host "    New-Item -ItemType Directory '$logDir'"
    Write-Host "    then set stdoutLogEnabled=`"true`" in web.config, retry the URL, and re-run this script."
}

Write-Host "`n=== Run it by hand ===" -ForegroundColor Cyan
# The fastest answer of all. Note this does NOT read web.config, so the
# environment variables set there are absent - which is itself informative:
# if it fails here with the same message, the setting is genuinely missing.
Write-Host "  cd '$root'; dotnet PropertyGpsApi.dll"
