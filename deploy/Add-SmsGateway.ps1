<#
    Switches OTP delivery from the server log to BBMP's SMS gateway, in the package's
    app\web.config. Run it in the package folder, then install the package as usual.

        powershell -ExecutionPolicy Bypass -File .\Add-SmsGateway.ps1

    It asks for the gateway details (the same account the LayoutKhata API uses) and writes
    them as Sms__* environment variables, plus Otp__Sender=SmsGateway. The password and the
    secret key are typed hidden, and no value is ever printed. The message the officer will
    receive is shown with a sample code, so it can be compared with the DLT-registered text.

    A copy of web.config is kept as web.config.before-sms first; it holds the same secrets.
    Re-running is safe: the variables are replaced, not duplicated.

    Run it ON THE SERVER for the connection check to mean anything: it tests the gateway
    from whichever computer runs it.
#>
[CmdletBinding()]
param(
    [string] $ApplicationName = 'BtoA Application'
)
$ErrorActionPreference = 'Stop'

$webConfig = Join-Path $PSScriptRoot 'app\web.config'
if (-not (Test-Path $webConfig)) { Write-Host "FAIL  $webConfig not found." -ForegroundColor Red; exit 1 }

[xml] $xml = Get-Content -LiteralPath $webConfig -Raw
$vars = $xml.SelectSingleNode('//aspNetCore/environmentVariables')
if (-not $vars) { Write-Host 'FAIL  no <environmentVariables> section in web.config.' -ForegroundColor Red; exit 1 }

function Ask([string] $label) {
    do { $v = (Read-Host "  $label").Trim() } while (-not $v)
    return $v
}

function AskHidden([string] $label) {
    do {
        $secure = Read-Host "  $label (hidden)" -AsSecureString
        $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
        try { $v = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr).Trim() }
        finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
    } while (-not $v)
    return $v
}

Write-Host "`nSMS gateway details (B2A_WEBAPI web.config key in brackets):" -ForegroundColor Cyan
$apiUrl      = Ask 'API URL [SMSBBMPurl]'
$username    = Ask 'Username [SMSBBMPUsername]'
$password    = AskHidden 'Password [SMSBBMPPassword]'
$senderId    = Ask 'Sender ID [SMSBBMPPSenderId]'
$secretKey   = AskHidden 'Service key [SMSBBMPserviceKey]'
$serviceType = Ask 'Service type [SMSBBMPPSmsServiceTypeForOTP]'
$templateId  = Ask 'DLT template ID [SendOTPTemplateId]'

$uri = $null
if (-not [Uri]::TryCreate($apiUrl, [UriKind]::Absolute, [ref] $uri) -or $uri.Scheme -notin 'http', 'https') {
    Write-Host 'FAIL  the API URL must be a full http:// or https:// address.' -ForegroundColor Red; exit 1
}
# smsmobile1.karnataka.gov.in speaks the protocol B2A_WEBAPI uses; the mgov gateway the other.
$protocol = if ($uri.Host -like 'smsmobile*.karnataka.gov.in') { 'SmsMobile' } else { 'Mgov' }
Write-Host "  ok    protocol $protocol (from the address)" -ForegroundColor Green
if ($uri.Scheme -eq 'http') {
    Write-Host '  WARN  the gateway is plain http: the password travels unencrypted, as it does for B2A today.' -ForegroundColor Yellow
}

$defaultTemplate = 'Your OTP for logging in to the {#var1#} is {#var2#}. Please do not share this OTP with anyone. Regards - BBMP'
Write-Host "`nThe SMS text must match the text registered for template $templateId exactly." -ForegroundColor Cyan
Write-Host "  Current: $defaultTemplate"
Write-Host '  {#var1#} is the application name, {#var2#} is the OTP.'
$template = (Read-Host '  Press Enter to keep it, or paste the registered text (with {#var1#} and {#var2#})').Trim()
if (-not $template) { $template = $defaultTemplate }
if ($template -notlike '*{#var1#}*' -or $template -notlike '*{#var2#}*') {
    Write-Host 'FAIL  the text must contain both {#var1#} and {#var2#}.' -ForegroundColor Red; exit 1
}

Write-Host "`nConnection to the gateway from this computer:" -ForegroundColor Cyan
$port = $uri.Port
if (Test-NetConnection -ComputerName $uri.Host -Port $port -InformationLevel Quiet -WarningAction SilentlyContinue) {
    Write-Host "  ok    $($uri.Host):$port reachable" -ForegroundColor Green
    if ($uri.Scheme -eq 'https') {
        # The API checks the gateway's certificate (the old LayoutKhata helper switched that
        # off), so a certificate this computer does not trust would fail every OTP.
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        try {
            Invoke-WebRequest -Uri $apiUrl -Method Head -UseBasicParsing -TimeoutSec 20 | Out-Null
            Write-Host '  ok    secure connection trusted' -ForegroundColor Green
        } catch [Net.WebException] {
            if ($_.Exception.Response) { Write-Host '  ok    secure connection trusted' -ForegroundColor Green }
            else { Write-Host "  WARN  secure connection failed ($($_.Exception.Status)). OTPs will fail until the gateway's certificate is trusted here." -ForegroundColor Yellow }
        }
    }
} else {
    Write-Host "  WARN  $($uri.Host):$port not reachable from here. If this is the server, ask for the firewall to allow it." -ForegroundColor Yellow
}

Copy-Item -LiteralPath $webConfig -Destination "$webConfig.before-sms" -Force

function Set-Var([string] $name, [string] $value) {
    foreach ($old in @($vars.SelectNodes("environmentVariable[@name='$name']"))) { [void] $vars.RemoveChild($old) }
    $node = $xml.CreateElement('environmentVariable')
    $node.SetAttribute('name', $name)
    $node.SetAttribute('value', $value)
    [void] $vars.AppendChild($node)
    Write-Host "  ok    $name set" -ForegroundColor Green
}

Write-Host "`nweb.config:" -ForegroundColor Cyan
Set-Var 'Otp__Sender'          'SmsGateway'
Set-Var 'Sms__Protocol'        $protocol
Set-Var 'Sms__ApiUrl'          $apiUrl
Set-Var 'Sms__Username'        $username
Set-Var 'Sms__Password'        $password
Set-Var 'Sms__SenderId'        $senderId
Set-Var 'Sms__SecretKey'       $secretKey
Set-Var 'Sms__ServiceType'     $serviceType
Set-Var 'Sms__TemplateId'      $templateId
Set-Var 'Sms__MessageTemplate' $template
Set-Var 'Sms__ApplicationName' $ApplicationName
$xml.Save($webConfig)
Remove-Variable password, secretKey

$preview = $template.Replace('{#var1#}', $ApplicationName).Replace('{#var2#}', '123456')
Write-Host "`nOfficers will receive (with their own code):" -ForegroundColor Cyan
Write-Host "  $preview"
Write-Host "`nDone. Install the package as usual. OTPs no longer appear in the server log." -ForegroundColor Cyan
Write-Host 'web.config holds the SMS credentials too: copy this folder only to the server.'
