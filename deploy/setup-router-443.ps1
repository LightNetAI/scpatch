<#
.SYNOPSIS
    Puts the ScreenConnect web server and relay behind the built-in Router
    service so both are reachable on TCP 443.

.DESCRIPTION
    Runs the setup described on winsysadminblog.com ("ScreenConnect Router
    Service Setup"), corrected for 26.x and made idempotent:

      * adds the <screenconnect.routing> section to web.config
      * moves the web server to https://+:8043/ and keeps http://+:8040/
        as an alternate prefix so an existing reverse proxy keeps working
      * forwards TLS  -> https://127.0.0.1:8043/
      * forwards relay -> relay://127.0.0.1:8041/
      * binds a certificate to 0.0.0.0:8043 via http.sys
      * enables and starts the "ScreenConnect Router" service

    The certificate is the one thing this script cannot invent. Either pass
    -PfxPath (recommended) or let it create a self-signed placeholder with
    -SelfSigned, which works but makes browsers complain.

.PARAMETER PfxPath
    Path to a .pfx containing the public certificate for -HostName.

.PARAMETER PfxPassword
    Password for the .pfx (plain string; use a secure string in production).

.PARAMETER SelfSigned
    Create a self-signed certificate for -HostName instead of importing one.

.PARAMETER HostName
    Public DNS name the certificate is for. Default help.dark.net.au.

.PARAMETER NoRouter
    Only reconfigure the web server and certificate; leave the Router alone.

.PARAMETER Rollback
    Restore the most recent web.config backup and remove the sslcert binding.

.EXAMPLE
    .\setup-router-443.ps1 -PfxPath C:\Temp\help.dark.net.au.pfx -PfxPassword hunter2

.EXAMPLE
    .\setup-router-443.ps1 -SelfSigned
#>
[CmdletBinding()]
param(
    [string] $HostName = 'help.dark.net.au',
    [string] $PfxPath,
    [string] $PfxPassword,
    [switch] $SelfSigned,
    [switch] $NoRouter,
    [switch] $Rollback
)

$ErrorActionPreference = 'Stop'

$root       = 'C:\Program Files (x86)\ScreenConnect'
$webConfig  = Join-Path $root 'web.config'
$serviceExe = Join-Path $root 'Bin\ScreenConnect.Service.exe'
$routerName = 'ScreenConnect Router'
$sslPort    = 8043
$stamp      = Get-Date -Format 'yyyyMMdd-HHmmss'

function Write-Step($text) { Write-Host "`n=== $text ===" -ForegroundColor Cyan }
function Write-Ok($text)   { Write-Host "  [ok] $text" -ForegroundColor Green }
function Write-Warn2($text){ Write-Host "  [!]  $text" -ForegroundColor Yellow }

# ---------------------------------------------------------------- rollback
if ($Rollback) {
    Write-Step 'Rollback'
    $bak = Get-ChildItem "$webConfig.router-*.bak" -ErrorAction SilentlyContinue |
           Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $bak) { throw "No web.config.router-*.bak found" }
    Copy-Item $bak.FullName $webConfig -Force
    Write-Ok "web.config restored from $($bak.Name)"

    Stop-Service $routerName -Force -ErrorAction SilentlyContinue
    Set-Service  $routerName -StartupType Disabled -ErrorAction SilentlyContinue
    Write-Ok 'router service stopped and disabled'

    netsh http delete sslcert ipport=0.0.0.0:$sslPort | Out-Null
    Write-Ok "ssl binding on $sslPort removed"

    Restart-Service 'ScreenConnect Web Server' -Force
    Write-Ok 'web server restarted on the old configuration'
    return
}

if (-not (Test-Path $webConfig)) { throw "web.config not found at $webConfig" }
if (-not (Test-Path $serviceExe)) { throw "ScreenConnect.Service.exe not found at $serviceExe" }

# ------------------------------------------------------------- certificate
Write-Step "Certificate for $HostName"

$cert = Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.Subject -like "*CN=$HostName" } |
        Sort-Object NotAfter -Descending | Select-Object -First 1

if ($PfxPath) {
    if (-not (Test-Path $PfxPath)) { throw "PFX not found: $PfxPath" }
    $sec = ConvertTo-SecureString $PfxPassword -AsPlainText -Force
    $cert = Import-PfxCertificate -FilePath $PfxPath -CertStoreLocation Cert:\LocalMachine\My `
                                  -Password $sec -Exportable
    Write-Ok "imported $($cert.Thumbprint) from $PfxPath"
}
elseif ($SelfSigned) {
    if (-not $cert) {
        $cert = New-SelfSignedCertificate -DnsName $HostName -CertStoreLocation Cert:\LocalMachine\My `
                                          -NotAfter (Get-Date).AddYears(2) -KeyExportPolicy Exportable `
                                          -KeyLength 2048
        Write-Ok "created self-signed $($cert.Thumbprint)"
    } else {
        Write-Ok "reusing existing $($cert.Thumbprint)"
    }
}
elseif ($cert) {
    Write-Ok "reusing existing $($cert.Thumbprint) (valid to $($cert.NotAfter))"
}
else {
    throw "No certificate for $HostName in LocalMachine\My. Pass -PfxPath or -SelfSigned."
}

if (-not $cert.HasPrivateKey) { throw "Certificate $($cert.Thumbprint) has no private key" }

netsh http delete sslcert ipport=0.0.0.0:$sslPort | Out-Null
$add = netsh http add sslcert ipport=0.0.0.0:$sslPort certhash=$($cert.Thumbprint) `
        appid='{00000000-0000-0000-0000-000000000000}' certstorename=MY
if ($LASTEXITCODE -ne 0) { throw "netsh http add sslcert failed: $add" }
Write-Ok "bound $($cert.Thumbprint) to 0.0.0.0:$sslPort"

# -------------------------------------------------------------- web.config
Write-Step 'web.config'

$bak = "$webConfig.router-$stamp.bak"
Copy-Item $webConfig $bak -Force
Write-Ok "backup: $bak"

$xml = New-Object System.Xml.XmlDocument
$xml.PreserveWhitespace = $true
$xml.Load($webConfig)
$cfg = $xml.DocumentElement

# <configSections> has to be the first child element
$sections = $cfg.SelectSingleNode('configSections')
if (-not $sections) {
    $sections = $xml.CreateElement('configSections')
    if ($cfg.FirstChild) { $cfg.InsertBefore($sections, $cfg.FirstChild) | Out-Null }
    else                 { $cfg.AppendChild($sections) | Out-Null }
    Write-Ok 'added <configSections>'
}
if (-not $sections.SelectSingleNode("section[@name='screenconnect.routing']")) {
    $sec = $xml.CreateElement('section')
    $sec.SetAttribute('name', 'screenconnect.routing')
    $sec.SetAttribute('type', 'ScreenConnect.RoutingConfigurationHandler, ScreenConnect.Server')
    $sections.AppendChild($sec) | Out-Null
    Write-Ok 'declared the screenconnect.routing section'
}

# <screenconnect.routing> - TLS to the web server, relay to the relay
$existing = $cfg.SelectSingleNode('screenconnect.routing')
if ($existing) { $cfg.RemoveChild($existing) | Out-Null }

$fragment = @"
<screenconnect.routing>
  <listenUris>
    <listenUri>tcp://+:443/</listenUri>
  </listenUris>
  <rules>
    <rule schemeExpression="ssl" actionType="forwardPayload" actionData="https://127.0.0.1:$sslPort/" />
    <rule schemeExpression="relay" actionType="forwardPayload" actionData="relay://127.0.0.1:8041/" />
  </rules>
</screenconnect.routing>
"@
$tmp = New-Object System.Xml.XmlDocument
$tmp.LoadXml($fragment)
$cfg.AppendChild($xml.ImportNode($tmp.DocumentElement, $true)) | Out-Null
Write-Ok 'routing section written (listen 443, ssl -> 8043, relay -> 8041)'

# appSettings
$appSettings = $cfg.SelectSingleNode('appSettings')
if (-not $appSettings) { throw 'web.config has no <appSettings> element' }

function Set-Setting($parent, $key, $value) {
    $node = $parent.SelectSingleNode("add[@key='$key']")
    if ($node) { $node.SetAttribute('value', $value) }
    else {
        $node = $parent.OwnerDocument.CreateElement('add')
        $node.SetAttribute('key', $key)
        $node.SetAttribute('value', $value)
        $parent.AppendChild($node) | Out-Null
    }
    Write-Ok "$key = $value"
}

Set-Setting $appSettings 'WebServerListenUri'          "https://+:$sslPort/"
Set-Setting $appSettings 'WebServerAlternateListenUri' 'http://+:8040/'
Set-Setting $appSettings 'RelayListenUri'              'relay://+:8041/'

$xml.Save($webConfig)

# --------------------------------------------------------------- services
Write-Step 'Services'
Restart-Service 'ScreenConnect Web Server' -Force
Write-Ok 'web server restarted'
Start-Sleep -Seconds 10

if (-not $NoRouter) {
    sc.exe config $routerName start= auto | Out-Null
    reg.exe add "HKLM\SYSTEM\CurrentControlSet\Services\$routerName" /v DelayedAutostart `
              /t REG_DWORD /d 0 /f | Out-Null
    Start-Service $routerName
    Start-Sleep -Seconds 5
    Write-Ok 'router service enabled and started'
}

# ------------------------------------------------------------ verification
Write-Step 'Verification'

$listening = Get-NetTCPConnection -State Listen |
             Where-Object { $_.LocalPort -in 443, 8040, 8041, $sslPort } |
             Select-Object -ExpandProperty LocalPort -Unique | Sort-Object
Write-Host "  listening ports: $($listening -join ', ')"

foreach ($probe in @(
    @{ Name = "https direct :$sslPort"; Url = "https://127.0.0.1:$sslPort/" },
    @{ Name = 'http alternate :8040';   Url = 'http://127.0.0.1:8040/' },
    @{ Name = 'https via router :443';  Url = "https://$HostName/" }
)) {
    try {
        $code = & curl.exe -sS -k -o NUL -w '%{http_code}' --resolve "$($HostName):443:127.0.0.1" $probe.Url
        Write-Ok "$($probe.Name) -> $code"
    } catch {
        Write-Warn2 "$($probe.Name) -> failed"
    }
}

Write-Host "`nDone. Remember:" -ForegroundColor Cyan
Write-Host '  * Router listeners only match TLS with SNI - browse by hostname, never by IP.'
Write-Host '  * Relay clients still use relay://<host>:8041/ until you set'
Write-Host '    RelayAddressableUri=relay://<host>:443/ and move public 443 to this host.'
Write-Host '  * Port 80 is left alone (IIS usually owns it); add tcp://+:80/ to listenUris'
Write-Host '    only after freeing the port.'
