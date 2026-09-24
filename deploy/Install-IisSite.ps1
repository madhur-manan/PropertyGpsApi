<#
    Creates the IIS site for PropertyGpsApi on the deployment server.

    Run this ON THE SERVER (172.31.0.112), in an ELEVATED PowerShell.

    Publishing the files into C:\inetpub does not create a site - IIS has to be
    told about them. Without this, every request lands on the Default Web Site
    and returns IIS's own 404 rather than the application's JSON envelope.

    The site is created standalone on its own port rather than as an application
    under Default Web Site. A sub-application would prefix every route
    (/propertyGpsApi/v1/api/...), and that prefix has to be mirrored in
    Media__PublicBaseUrl - which is baked permanently into stored document URLs.
    One less thing to get wrong.
#>

[CmdletBinding()]
param(
    [string] $SiteName    = 'PropertyGpsApi',
    [string] $PhysicalPath = 'C:\inetpub\propertyGpsApi',
    [int]    $Port         = 8080,
    [string] $MediaRoot    = 'D:\PropertyGpsMedia'
)

$ErrorActionPreference = 'Stop'
Import-Module WebAdministration

function Say($m) { Write-Host "  $m" }
function Fail($m) { Write-Host "FAIL  $m" -ForegroundColor Red; exit 1 }
function Ok($m)  { Write-Host "ok    $m" -ForegroundColor Green }

Write-Host "`n== Checks ==" -ForegroundColor Cyan

# 1. The published output is actually there.
if (-not (Test-Path (Join-Path $PhysicalPath 'PropertyGpsApi.dll'))) {
    Fail "PropertyGpsApi.dll not found in $PhysicalPath - publish output is missing or in a subfolder."
}
Ok "publish output found in $PhysicalPath"

# 2. web.config must parse. A raw '<' in an attribute value (e.g. a placeholder
#    left as value="<generate - see below>") makes the file invalid XML, and IIS
#    answers 500.19 for every request before the app is ever loaded.
$webConfig = Join-Path $PhysicalPath 'web.config'
if (-not (Test-Path $webConfig)) { Fail "web.config not found in $PhysicalPath" }
try { [xml](Get-Content $webConfig -Raw) | Out-Null; Ok 'web.config is valid XML' }
catch { Fail "web.config is not valid XML: $($_.Exception.Message)" }

# 3. The ASP.NET Core Hosting Bundle. Without it AspNetCoreModuleV2 does not
#    exist and IIS answers 500.19/500.21 - which looks like a config error but
#    is a missing install.
$modules = (Get-WebGlobalModule).Name
if ($modules -notcontains 'AspNetCoreModuleV2') {
    Fail 'AspNetCoreModuleV2 is not registered. Install the ASP.NET Core Hosting Bundle, then run "iisreset" and try again.'
}
Ok 'AspNetCoreModuleV2 is registered'

# 4. Nothing else on the port.
$taken = Get-WebBinding | Where-Object { $_.bindingInformation -match ":$Port:" }
if ($taken -and (Get-WebBinding -Name $SiteName -ErrorAction SilentlyContinue) -eq $null) {
    Fail "Port $Port is already bound by another site. Pick a different -Port."
}
Ok "port $Port is free (or already ours)"

Write-Host "`n== Application pool ==" -ForegroundColor Cyan

# managedRuntimeVersion must be empty - "No Managed Code" in the GUI. ASP.NET
# Core runs outside the CLR pipeline; leaving this at v4.0 is a classic cause of
# a site that is configured correctly and still will not start.
if (-not (Test-Path "IIS:\AppPools\$SiteName")) {
    New-WebAppPool -Name $SiteName | Out-Null
    Say "created app pool $SiteName"
} else {
    Say "app pool $SiteName already exists"
}
Set-ItemProperty "IIS:\AppPools\$SiteName" -Name managedRuntimeVersion -Value ''
Set-ItemProperty "IIS:\AppPools\$SiteName" -Name startMode              -Value 'AlwaysRunning'
Set-ItemProperty "IIS:\AppPools\$SiteName" -Name processModel.idleTimeout -Value '00:00:00'
Ok 'app pool set to No Managed Code, always running, no idle timeout'

Write-Host "`n== Site ==" -ForegroundColor Cyan

if (-not (Test-Path "IIS:\Sites\$SiteName")) {
    New-Website -Name $SiteName -Port $Port -PhysicalPath $PhysicalPath -ApplicationPool $SiteName | Out-Null
    Ok "created site $SiteName on port $Port"
} else {
    Set-ItemProperty "IIS:\Sites\$SiteName" -Name physicalPath     -Value $PhysicalPath
    Set-ItemProperty "IIS:\Sites\$SiteName" -Name applicationPool  -Value $SiteName
    Ok "site $SiteName already exists - path and pool refreshed"
}
Start-Website -Name $SiteName

Write-Host "`n== Permissions ==" -ForegroundColor Cyan

$identity = "IIS AppPool\$SiteName"

# Read on the publish folder.
& icacls $PhysicalPath /grant "${identity}:(OI)(CI)(RX)" /T /C /Q | Out-Null
Ok "read+execute on $PhysicalPath"

# Write on the media root. Survey photos and documents are written here, and a
# permission failure only surfaces when the first officer submits - long after
# anyone is watching the deployment.
if (-not (Test-Path $MediaRoot)) {
    New-Item -ItemType Directory -Path $MediaRoot -Force | Out-Null
    Say "created $MediaRoot"
}
& icacls $MediaRoot /grant "${identity}:(OI)(CI)(M)" /T /C /Q | Out-Null
Ok "modify on $MediaRoot"

Write-Host "`n== Firewall ==" -ForegroundColor Cyan

$rule = "PropertyGpsApi HTTP $Port"
if (-not (Get-NetFirewallRule -DisplayName $rule -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -DisplayName $rule -Direction Inbound -Protocol TCP -LocalPort $Port -Action Allow | Out-Null
    Ok "opened inbound TCP $Port"
} else {
    Ok "inbound TCP $Port already open"
}

Write-Host "`n== Result ==" -ForegroundColor Cyan
Get-Website -Name $SiteName | Format-List Name, State, PhysicalPath, ApplicationPool
Get-WebBinding -Name $SiteName | Select-Object -ExpandProperty bindingInformation

$url = "http://localhost:$Port/health/live"
Write-Host "`nProbing $url ..." -ForegroundColor Cyan
try {
    $r = Invoke-WebRequest -Uri $url -TimeoutSec 20 -UseBasicParsing
    Write-Host "HTTP $($r.StatusCode)  $($r.Content)" -ForegroundColor Green
    Write-Host "`nThe API is serving. Reachable from the network at http://$($env:COMPUTERNAME):$Port/ and http://172.31.0.112:$Port/" -ForegroundColor Green
} catch {
    Write-Host "Probe failed: $($_.Exception.Message)" -ForegroundColor Yellow
    Write-Host @"

The site now exists, so the failure is the application itself, not IIS routing.
To see the real reason, set stdoutLogEnabled="true" in web.config, create a
'logs' folder beside PropertyGpsApi.dll, re-run this probe, and read the file
that appears in logs\. The startup checks name the exact missing setting.
"@ -ForegroundColor Yellow
}
