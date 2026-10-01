<#
    Read-only survey of a NEW app server before PropertyGpsApi is installed on it.

    Run ON THE SERVER in an ELEVATED PowerShell (right-click PowerShell -> Run as administrator):

        powershell -ExecutionPolicy Bypass -File .\Check-NewServer.ps1

    It changes nothing. It reports what the API needs and whether each is already there,
    so the install can do exactly what is missing and no more. Paste the whole output back.
#>
[CmdletBinding()]
param(
    [string] $DbServer = '10.40.119.74',
    [int]    $DbPort   = 1433
)
$ErrorActionPreference = 'Continue'
function Ok($m)   { Write-Host "  ok       $m" -ForegroundColor Green }
function Miss($m) { Write-Host "  MISSING  $m" -ForegroundColor Red }
function Note($m) { Write-Host "  note     $m" -ForegroundColor Yellow }
function Head($m) { Write-Host "`n== $m ==" -ForegroundColor Cyan }

Head 'Machine'
$os = Get-CimInstance Win32_OperatingSystem
"  {0}  (build {1})" -f $os.Caption, $os.BuildNumber
"  name {0}" -f $env:COMPUTERNAME
Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
    Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' } |
    ForEach-Object { "  ip   {0}  ({1})" -f $_.IPAddress, $_.InterfaceAlias }
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if ($isAdmin) { Ok 'running as administrator' } else { Miss 'not elevated - re-run PowerShell as administrator for complete results' }

Head 'Drives (photos and documents are stored on disk)'
Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3' | ForEach-Object {
    "  {0}  {1,7:N1} GB free of {2,7:N1} GB" -f $_.DeviceID, ($_.FreeSpace / 1GB), ($_.Size / 1GB)
}

Head 'IIS'
$iisFeatures = 'Web-Server','Web-WebServer','Web-Default-Doc','Web-Static-Content','Web-Http-Errors','Web-Http-Logging','Web-Request-Monitor','Web-Filtering','Web-Stat-Compression','Web-Mgmt-Console'
if (Get-Command Get-WindowsFeature -ErrorAction SilentlyContinue) {
    foreach ($f in $iisFeatures) {
        $w = Get-WindowsFeature -Name $f -ErrorAction SilentlyContinue
        if ($w -and $w.Installed) { Ok "$f" } else { Miss "$f" }
    }
} else {
    Note 'Get-WindowsFeature not available (not Windows Server?) - checking the IIS service instead'
}
$w3 = Get-Service W3SVC -ErrorAction SilentlyContinue
if ($w3) { Ok "IIS service W3SVC is $($w3.Status)" } else { Miss 'IIS service W3SVC (IIS is not installed)' }

Head 'ASP.NET Core 10 Hosting Bundle (the API targets .NET 10)'
# Ask IIS rather than guessing a path: the Hosting Bundle installs the module under
# %ProgramFiles%\IIS\Asp.Net Core Module\V2, not System32\inetsrv.
$ancm = $null
if (Get-Module -ListAvailable WebAdministration) { Import-Module WebAdministration; $ancm = Get-WebGlobalModule | Where-Object Name -eq 'AspNetCoreModuleV2' }
if ($ancm) { Ok "AspNetCoreModuleV2 registered ($([Environment]::ExpandEnvironmentVariables($ancm.Image)))" } else { Miss 'AspNetCoreModuleV2 - install the ASP.NET Core 10 Hosting Bundle' }
$dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
if (Test-Path $dotnet) {
    $rt = & $dotnet --list-runtimes 2>$null
    $rt | ForEach-Object { "  runtime  $_" }
    # Both are needed: ASP.NET Core 10 runs on top of the base .NET 10 runtime.
    foreach ($fw in 'Microsoft.NETCore.App', 'Microsoft.AspNetCore.App') {
        if ($rt -match ('^' + [regex]::Escape($fw) + ' 10\.')) { Ok "$fw 10 installed" } else { Miss "$fw 10.x - install the ASP.NET Core 10 Hosting Bundle" }
    }
} else { Miss "dotnet not found at $dotnet - install the ASP.NET Core 10 Hosting Bundle" }

Head "Database server $DbServer`:$DbPort"
$t = Test-NetConnection $DbServer -Port $DbPort -WarningAction SilentlyContinue
if ($t.TcpTestSucceeded) { Ok "reachable from this server (source $($t.SourceAddress.IPAddress))" } else { Miss "cannot reach $DbServer`:$DbPort - ask the network team to open it from this server" }

Head 'Ports this server would serve on'
foreach ($p in 80, 443, 8080) {
    $l = Get-NetTCPConnection -State Listen -LocalPort $p -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($l) { "  port {0}  in use by {1}" -f $p, (Get-Process -Id $l.OwningProcess -ErrorAction SilentlyContinue).ProcessName } else { "  port {0}  free" -f $p }
}
$fw = Get-NetFirewallRule -Direction Inbound -Enabled True -Action Allow -ErrorAction SilentlyContinue |
      Where-Object { $_.DisplayName -match 'World Wide Web|HTTP|IIS' } | Select-Object -ExpandProperty DisplayName -Unique
if ($fw) { $fw | ForEach-Object { Ok "firewall allows: $_" } } else { Note 'no inbound HTTP firewall rule found yet (IIS adds one when installed)' }

Head 'Existing IIS sites and app pools'
if (Get-Module -ListAvailable WebAdministration) {
    Import-Module WebAdministration
    Get-Website | ForEach-Object { "  site  {0,-24} {1,-8} {2}  -> {3}" -f $_.Name, $_.State, (($_.Bindings.Collection | ForEach-Object { $_.bindingInformation }) -join ','), $_.PhysicalPath }
    Get-ChildItem IIS:\AppPools | ForEach-Object { "  pool  {0,-24} {1,-8} .NET CLR '{2}'" -f $_.Name, $_.State, $_.managedRuntimeVersion }
} else { Note 'WebAdministration module not available (IIS management not installed)' }

Write-Host "`nDone. Nothing was changed. Copy everything above and paste it back." -ForegroundColor Cyan
