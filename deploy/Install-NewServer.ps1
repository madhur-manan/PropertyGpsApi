<#
    Installs (or updates) PropertyGpsApi on a new app server, as the IIS application
    "propertygps" under Default Web Site - the same layout as the old server, so the public
    address stays https://testapps.bbmpgov.in/propertygps.

    Run ON THE SERVER in an ELEVATED PowerShell, from the folder that contains .\app:

        powershell -ExecutionPolicy Bypass -File .\Install-NewServer.ps1

    What it does, in order, stopping at the first problem:
      1. checks prerequisites (admin, IIS, AspNetCoreModuleV2, ASP.NET Core 10 runtime, .\app)
      2. stops the app pool if it already exists (a redeploy), copies .\app to -AppRoot
      3. creates the app pool (No Managed Code, always running) and the IIS application
      4. locks the folder down: web.config holds the database password and the JWT key, so
         only Administrators, SYSTEM and the app pool can read it - never "Everyone"
      5. starts the pool and calls /health/live and /health/ready

    Re-running it is safe: existing pool and application are updated, not duplicated.
    The media folder is never emptied, and logs\ is never overwritten.
#>
[CmdletBinding()]
param(
    # No default here: with -File, $PSScriptRoot is still empty while defaults are evaluated
    # (Windows PowerShell 5.1), so it is filled in below instead.
    [string] $Source,
    [string] $AppRoot   = 'D:\inetpub\propertyGpsApi',
    [string] $MediaRoot = 'D:\PropertyGpsMedia',
    [string] $SiteName  = 'Default Web Site',
    [string] $Alias     = 'propertygps',
    [string] $PoolName  = 'propertyGPSAPI'
)
$ErrorActionPreference = 'Stop'
if (-not $Source) { $Source = Join-Path $PSScriptRoot 'app' }
function Ok($m)   { Write-Host "  ok    $m" -ForegroundColor Green }
function Step($m) { Write-Host "`n== $m ==" -ForegroundColor Cyan }
function Fail($m) { Write-Host "`nFAIL  $m" -ForegroundColor Red; exit 1 }

Step '1. Prerequisites'
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Fail 'Run PowerShell as administrator.' }
Ok 'running as administrator'

if (-not (Get-Service W3SVC -ErrorAction SilentlyContinue)) { Fail 'IIS is not installed.' }
Import-Module WebAdministration
Ok 'IIS present'

if (-not (Get-WebGlobalModule | Where-Object Name -eq 'AspNetCoreModuleV2')) {
    Fail 'AspNetCoreModuleV2 is not registered. Install the ASP.NET Core 10 Hosting Bundle, run iisreset, then re-run.'
}
Ok 'AspNetCoreModuleV2 registered'

$dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
if (-not (Test-Path $dotnet) -or -not ((& $dotnet --list-runtimes) -match 'Microsoft\.AspNetCore\.App 10\.')) {
    Fail 'ASP.NET Core 10 runtime not found. Install the ASP.NET Core 10 Hosting Bundle.'
}
Ok 'ASP.NET Core 10 runtime installed'

foreach ($f in 'PropertyGpsApi.dll', 'web.config') {
    if (-not (Test-Path (Join-Path $Source $f))) { Fail "$f not found in $Source - copy the whole package folder, including app\." }
}
# Never print the exception text here: it can quote parts of web.config, which holds secrets.
try { $doc = New-Object System.Xml.XmlDocument; $doc.Load((Convert-Path (Join-Path $Source 'web.config'))) }
catch {
    $e = $_.Exception.InnerException
    if ($e -is [System.Xml.XmlException]) { Fail "web.config in $Source is not valid XML (line $($e.LineNumber), position $($e.LinePosition))." }
    Fail "web.config in $Source could not be read."
}
Ok "package found in $Source"

# The package's web.config holds the database password and the JWT key, and D:\ lets every
# local user (every app pool included) read what is under it. Lock the package folder first.
$src = (Resolve-Path $Source).ProviderPath.TrimEnd('\')
if ([IO.Path]::GetPathRoot($src).TrimEnd('\') -eq $src) { Fail "-Source must be the package's app folder, not a drive root ($src)." }
icacls $src /inheritance:r /grant:r "*S-1-5-32-544:(OI)(CI)F" "*S-1-5-18:(OI)(CI)F" | Out-Null
if ($LASTEXITCODE -ne 0) { Fail "could not restrict $src (icacls exit code $LASTEXITCODE)." }
Ok "$src (holds the secrets) readable only by Administrators and SYSTEM"

if (-not (Get-Website -Name $SiteName)) { Fail "IIS site '$SiteName' not found." }
Ok "site '$SiteName' exists"

Step '2. Copy files'
$poolPath = "IIS:\AppPools\$PoolName"
if (Test-Path $poolPath) {
    if ((Get-WebAppPoolState -Name $PoolName).Value -ne 'Stopped') {
        Stop-WebAppPool -Name $PoolName
        for ($i = 0; $i -lt 30 -and (Get-WebAppPoolState -Name $PoolName).Value -ne 'Stopped'; $i++) { Start-Sleep -Seconds 1 }
    }
    Ok "existing pool $PoolName stopped for the update"
}
foreach ($d in $AppRoot, (Join-Path $AppRoot 'logs'), $MediaRoot) {
    if (-not (Test-Path $d)) { New-Item -ItemType Directory -Path $d | Out-Null }
}
# /E copies everything; /XD logs leaves the log folder alone; no /MIR, so nothing is deleted.
robocopy $Source $AppRoot /E /XD logs /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { Fail "robocopy failed with code $LASTEXITCODE." }
Ok "files copied to $AppRoot"

Step '3. App pool and application'
if (-not (Test-Path $poolPath)) { New-WebAppPool -Name $PoolName | Out-Null; Ok "created pool $PoolName" }
Set-ItemProperty $poolPath -Name managedRuntimeVersion -Value ''          # No Managed Code
Set-ItemProperty $poolPath -Name managedPipelineMode -Value 'Integrated'
# Keeps the worker process up. The app itself still loads on the first request after a
# start or recycle (no Application Initialization feature here); the health check below is
# that first request.
Set-ItemProperty $poolPath -Name startMode -Value 'AlwaysRunning'
Set-ItemProperty $poolPath -Name processModel.idleTimeout -Value ([TimeSpan]::Zero)
Set-ItemProperty $poolPath -Name processModel.identityType -Value 'ApplicationPoolIdentity'
Ok "pool $PoolName configured (No Managed Code, always running)"

$app = Get-WebApplication -Site $SiteName -Name $Alias
if ($app) {
    Set-ItemProperty "IIS:\Sites\$SiteName\$Alias" -Name physicalPath -Value $AppRoot
    Set-ItemProperty "IIS:\Sites\$SiteName\$Alias" -Name applicationPool -Value $PoolName
    Ok "application /$Alias updated"
} else {
    New-WebApplication -Site $SiteName -Name $Alias -PhysicalPath $AppRoot -ApplicationPool $PoolName | Out-Null
    Ok "application /$Alias created"
}

Step '4. Permissions'
$identity = "IIS AppPool\$PoolName"
# A new pool's virtual account is registered asynchronously: wait until Windows knows it, then
# grant by SID. icacls does not stop the script on failure by itself, so check every call.
$sid = $null
for ($i = 0; $i -lt 30 -and -not $sid; $i++) {
    try { $sid = ([Security.Principal.NTAccount]$identity).Translate([Security.Principal.SecurityIdentifier]).Value }
    catch { Start-Sleep -Seconds 1 }
}
if (-not $sid) { Fail "Windows cannot resolve $identity yet. Run iisreset, then re-run this script." }

# Stop inheriting (D:\ grants Users read), then grant exactly what is needed.
icacls $AppRoot /inheritance:r /grant:r "*S-1-5-32-544:(OI)(CI)F" "*S-1-5-18:(OI)(CI)F" "*${sid}:(OI)(CI)RX" | Out-Null
if ($LASTEXITCODE -ne 0) { Fail "icacls on $AppRoot failed (exit $LASTEXITCODE) - the folder is NOT locked down." }
icacls (Join-Path $AppRoot 'logs') /grant "*${sid}:(OI)(CI)M" | Out-Null
if ($LASTEXITCODE -ne 0) { Fail "icacls on $AppRoot\logs failed (exit $LASTEXITCODE)." }
icacls $MediaRoot /inheritance:r /grant:r "*S-1-5-32-544:(OI)(CI)F" "*S-1-5-18:(OI)(CI)F" "*${sid}:(OI)(CI)M" | Out-Null
if ($LASTEXITCODE -ne 0) { Fail "icacls on $MediaRoot failed (exit $LASTEXITCODE)." }
Ok "$AppRoot and $MediaRoot readable only by Administrators, SYSTEM and $identity"
Ok "$identity can write logs\ and $MediaRoot"

Step '5. Start and health check'
# A pool created a moment ago is already Started by IIS, and starting a started pool is an
# error - so only start it when it is not running (a redeploy, which stopped it in step 2).
$state = (Get-WebAppPoolState -Name $PoolName).Value
if ($state -eq 'Stopping') {
    for ($i = 0; $i -lt 30 -and (Get-WebAppPoolState -Name $PoolName).Value -ne 'Stopped'; $i++) { Start-Sleep -Seconds 1 }
    $state = (Get-WebAppPoolState -Name $PoolName).Value
}
if ($state -ne 'Started' -and $state -ne 'Starting') { Start-WebAppPool -Name $PoolName }
for ($i = 0; $i -lt 30 -and (Get-WebAppPoolState -Name $PoolName).Value -ne 'Started'; $i++) { Start-Sleep -Seconds 1 }
if ((Get-WebAppPoolState -Name $PoolName).Value -ne 'Started') { Fail "App pool $PoolName did not start." }
Ok "pool $PoolName started"
$base = "http://localhost/$Alias"
$healthy = $false
foreach ($path in 'health/live', 'health/ready') {
    $result = $null
    for ($i = 0; $i -lt 20; $i++) {
        try {
            $r = Invoke-WebRequest "$base/$path" -UseBasicParsing -TimeoutSec 10
            $result = "$($r.StatusCode) $($r.Content)"; break
        } catch {
            $result = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { $_.Exception.Message }
            Start-Sleep -Seconds 2
        }
    }
    if ("$result" -like '200*') { Ok "$base/$path -> $result"; $healthy = $true } else { Write-Host "  FAIL  $base/$path -> $result" -ForegroundColor Red; $healthy = $false }
}

if (-not $healthy) {
    Write-Host "`nThe app did not come up. Last lines of its stdout log:" -ForegroundColor Yellow
    $log = Get-ChildItem (Join-Path $AppRoot 'logs') -Filter 'stdout*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
    if ($log) { Get-Content $log.FullName -Tail 40 | Where-Object { $_ -notmatch 'OTP for mobile' } }
    else { Write-Host '  (no stdout log yet - check Event Viewer > Windows Logs > Application, source "IIS AspNetCore Module V2")' }
    exit 1
}

Write-Host "`nInstalled. Public address: https://testapps.bbmpgov.in/$Alias" -ForegroundColor Green
Write-Host "OTPs are written to $AppRoot\logs\stdout_*.log (search for 'OTP for mobile ending')." -ForegroundColor Green
