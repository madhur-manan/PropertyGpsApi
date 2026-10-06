<#
    Runs the published app exactly as IIS would, but in a console where the
    startup error is visible.

    Run ON THE SERVER (172.31.0.112).

    Why this exists: "dotnet PropertyGpsApi.dll" does NOT read web.config, so it
    fails with a DIFFERENT error than IIS does - one about missing configuration
    that is missing only because the console never loaded it. That sends you
    chasing the wrong problem. This script reads the <environmentVariables>
    block out of web.config first, applies it to this process, and only then
    starts the app. What you see is what IIS sees.

    Nothing is modified. Ctrl+C to stop.
#>

[CmdletBinding()]
param([string] $Root = 'C:\inetpub\propertyGpsApi')

$ErrorActionPreference = 'Stop'

$dll = Join-Path $Root 'PropertyGpsApi.dll'
$cfg = Join-Path $Root 'web.config'
if (-not (Test-Path $dll)) { throw "PropertyGpsApi.dll not found in $Root" }
if (-not (Test-Path $cfg)) { throw "web.config not found in $Root" }

Write-Host "Reading environment from $cfg" -ForegroundColor Cyan
$xml = [xml](Get-Content $cfg -Raw)

$vars = $xml.SelectNodes('//aspNetCore/environmentVariables/environmentVariable')
if (-not $vars -or $vars.Count -eq 0) {
    Write-Host "  No <environmentVariables> found - the app will start with nothing configured." -ForegroundColor Yellow
}

foreach ($v in $vars) {
    $name  = $v.name
    $value = $v.value
    Set-Item -Path "Env:$name" -Value $value

    # Never print secrets. Show enough to spot a placeholder or a typo, no more.
    $shown = if ($name -match 'Key|Password|Secret|Pwd') {
        "<set, $($value.Length) chars>"
    } elseif ($name -match 'Database__') {
        # Connection strings carry the password in them. Show only the server
        # and database, which is what you actually need to eyeball.
        (($value -split ';' | Where-Object { $_ -match '^\s*(Server|Data Source|Database|Initial Catalog)\s*=' }) -join '; ')
    } else { $value }

    "{0,-34} {1}" -f $name, $shown
}

# Things worth flagging before the app even starts, because each produces a
# confusing failure later rather than an obvious one now.
Write-Host "`nSanity checks" -ForegroundColor Cyan

$conn = @($env:Database__Master, $env:Database__B2A)
if ($conn -match '\\') {
    Write-Host "  WARN  a connection string contains a doubled backslash (\). XML attributes do not escape" -ForegroundColor Yellow
    Write-Host "        backslashes, so 'HOST\INSTANCE' reaches SQL Server literally and the instance is not found." -ForegroundColor Yellow
}
if ($env:Jwt__Key -and $env:Jwt__Key.Length -lt 32) {
    Write-Host "  WARN  Jwt__Key is under 32 bytes - HS256 validation will reject it at startup." -ForegroundColor Yellow
}
if ($env:Otp__Sender -eq 'SmsGateway' -and -not $env:Sms__ApiUrl) {
    Write-Host "  WARN  Otp__Sender=SmsGateway but no Sms__* settings are present. SmsOptions is ValidateOnStart," -ForegroundColor Yellow
    Write-Host "        so the app will throw OptionsValidationException before it serves anything." -ForegroundColor Yellow
}
if ($env:Media__PublicBaseUrl -match 'your-public-host|localhost|example') {
    Write-Host "  WARN  Media__PublicBaseUrl still looks like a placeholder. It is concatenated into document" -ForegroundColor Yellow
    Write-Host "        URLs that are written PERMANENTLY into the database - fix it before any survey is submitted." -ForegroundColor Yellow
}
if ($env:Media__RootPath -and -not (Test-Path (Split-Path $env:Media__RootPath -Qualifier))) {
    Write-Host "  WARN  Media__RootPath is on drive $(Split-Path $env:Media__RootPath -Qualifier) which does not exist on this server." -ForegroundColor Yellow
}

Write-Host "`nStarting: dotnet $dll" -ForegroundColor Cyan
Write-Host "(Ctrl+C to stop. A clean exit with code 1 is the startup-check failure - read the message above it.)`n" -ForegroundColor DarkGray

Push-Location $Root
try { & dotnet $dll } finally { Pop-Location }
Write-Host "`nExited with code $LASTEXITCODE" -ForegroundColor Cyan
