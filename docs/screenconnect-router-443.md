# Putting the web server and relay on port 443 with the ScreenConnect Router

This is a corrected, tested version of the setup described at
<https://www.winsysadminblog.com/2024/12/screenconnect-router-service-setup/>,
verified against ScreenConnect **26.6.6.9747** on Windows Server 2025.

The short version: the blog's `web.config` snippet is correct and still valid for 26.x, but it
leaves out four things that decide whether it works. They are listed under
[What the blog gets wrong](#what-the-blog-gets-wrong) — check those first if you already tried it
and nothing happened.

---

## What this gives you

| Port | Protocol | Destination |
|---|---|---|
| 443 | TLS (web) | `https://127.0.0.1:8043/` — the ScreenConnect web server |
| 443 | ScreenConnect relay | `relay://127.0.0.1:8041/` — the ScreenConnect relay |
| 80 | HTTP | optional redirect to `https://…` (needs IIS out of the way) |

One port, two protocols, sorted out by the Router service: it sniffs the first bytes of every
connection, parses the TLS ClientHello or the relay hello, and forwards the raw payload to the
right place. Nothing is decrypted by the router, so the TLS certificate still belongs to the web
server (bound to 8043 with `netsh`).

The Router is an **unsupported** service — ConnectWise uses it in their own hosted environment but
will not support you running it. It has shipped unchanged for years.

---

## What the blog gets wrong

1. **The service is disabled after install.** `HKLM\SYSTEM\CurrentControlSet\Services\ScreenConnect Router`
   exists on a default install with `Start = 4` (Disabled). The blog's "export the Relay key and
   rename it" gives you a key, but it never says to set the startup type back to Automatic and to
   `DelayedAutostart = 0`. If you skip that, the router silently never runs and port 443 is
   simply closed. This is the most common reason the whole thing "doesn't work".

2. **The web server must actually speak TLS on the forwarded port.** The rule forwards to
   `https://localhost:8043/`, so the web server has to listen with an `https://` prefix *and* a
   certificate must be bound to `0.0.0.0:8043`. If you leave the web server on `http://+:8040/`,
   the router forwards raw TLS bytes at an HTTP listener and every connection dies with a
   handshake error.

3. **The router only routes TLS that carries SNI.** Connections made by IP address have no
   server name, the handshake parser returns no URI, no rule matches, and the connection is
   dropped. Always test with the DNS name
   (`curl --resolve help.example.com:443:10.0.0.5 https://help.example.com/`), never
   `https://10.0.0.5/`.

4. **Port 80 is usually already taken.** On a server with IIS, `HTTP://*:80/` is bound by
   `DefaultAppPool` and the router cannot listen there. Add `tcp://+:80/` only after stopping
   IIS or moving its binding, or skip the HTTP→HTTPS redirect entirely.

Two smaller points: the "allow edge traversal" step in the blog only matters for Teredo/IPv6 —
plain inbound allow rules for TCP 80/443/8041/8043 are what you actually need; and the
`<configSections>` block must be the **first child** of `<configuration>` or .NET refuses to load
the file.

---

## Prerequisites

* ScreenConnect installed at `C:\Program Files (x86)\ScreenConnect`
* A certificate for the public DNS name. Either a PFX you can import, or Let's Encrypt, or a
  temporary self-signed certificate (browsers will warn — fine for proving the plumbing, not for
  production).
* Ports 443 (and 80 if you use the redirect) allowed inbound in Windows Firewall and forwarded to
  this host by whatever NAT/firewall sits in front.

> **If a reverse proxy already owns 443** (nginx/openresty in front of the box, terminating TLS and
> proxying to `http://server:8040/`), leave it alone. Either move the public 443 to this host and
> let the router take over, or keep the proxy for the web and use the router only on an internal
> port. Do not point a proxy and the router at the same public port.

---

## Automated setup

```powershell
# copy deploy\setup-router-443.ps1 to the server, then:
.\setup-router-443.ps1 -PfxPath C:\Temp\help.example.com.pfx -PfxPassword 'secret'
# or, to prove the wiring first:
.\setup-router-443.ps1 -SelfSigned
# undo everything:
.\setup-router-443.ps1 -Rollback
```

The script is idempotent, takes a timestamped backup of `web.config` before touching it, and
verifies each listener at the end.

## Manual setup

### 1. Certificate

```powershell
# import a real certificate
$cert = Import-PfxCertificate -FilePath C:\Temp\help.example.com.pfx `
        -CertStoreLocation Cert:\LocalMachine\My -Password (Read-Host -AsSecureString)
$cert.Thumbprint

# or a throwaway self-signed one
$cert = New-SelfSignedCertificate -DnsName help.example.com `
        -CertStoreLocation Cert:\LocalMachine\My -NotAfter (Get-Date).AddYears(2)
```

Bind it to the port the router will forward to:

```cmd
netsh http delete sslcert ipport=0.0.0.0:8043
netsh http add sslcert ipport=0.0.0.0:8043 certhash=<thumbprint> ^
      appid={00000000-0000-0000-0000-000000000000} certstorename=MY
netsh http show sslcert ipport=0.0.0.0:8043
```

### 2. web.config

Back it up first. Three edits:

```xml
<configuration>
  <!-- must be the very first child -->
  <configSections>
    <section name="screenconnect.routing"
             type="ScreenConnect.RoutingConfigurationHandler, ScreenConnect.Server" />
  </configSections>

  <!-- ... existing <location>, system.web, ... -->

  <screenconnect.routing>
    <listenUris>
      <listenUri>tcp://+:443/</listenUri>
      <!-- <listenUri>tcp://+:80/</listenUri> only if nothing else owns 80 -->
    </listenUris>
    <rules>
      <rule schemeExpression="ssl"   actionType="forwardPayload" actionData="https://127.0.0.1:8043/" />
      <rule schemeExpression="relay" actionType="forwardPayload" actionData="relay://127.0.0.1:8041/" />
      <!-- optional, only with a tcp://+:80/ listener above:
      <rule schemeExpression="http"  actionType="issueRedirect" actionData="https://$HOST/" /> -->
    </rules>
  </screenconnect.routing>

  <appSettings>
    <add key="WebServerListenUri"          value="https://+:8043/" />
    <add key="WebServerAlternateListenUri" value="http://+:8040/" />
    <add key="RelayListenUri"              value="relay://+:8041/" />
    <!-- <add key="RelayAddressableUri"     value="relay://help.example.com:443/" />  see cutover -->
  </appSettings>
</configuration>
```

`WebServerAlternateListenUri` is the safety net: it keeps the old plain-HTTP listener alive on
8040, so an existing reverse proxy in front of the box keeps working while you test 443.

The rule fields the blog does not mention all exist and all work the same way:
`hostExpression`, `portExpression`, `pathExpression`, `queryExpression`, and the action types
`none`, `issueRedirect`, `forwardPayload`, `httpCallback`.

### 3. Router service

```cmd
sc config "ScreenConnect Router" start= auto
reg add "HKLM\SYSTEM\CurrentControlSet\Services\ScreenConnect Router" ^
    /v DelayedAutostart /t REG_DWORD /d 0 /f
```

### 4. Restart

```powershell
Restart-Service 'ScreenConnect Web Server' -Force
Start-Service   'ScreenConnect Router'
```

The web server must be up first — the router only proxies to it.

---

## Verification

```powershell
Get-NetTCPConnection -State Listen |
  Where-Object LocalPort -in 443,8040,8041,8043 |
  Select-Object LocalAddress,LocalPort,OwningProcess | Sort-Object LocalPort
```

Expect `443` and `8041` owned by `ScreenConnect.Service.exe`, and `8040`/`8043` owned by `System`
(http.sys).

```powershell
curl.exe -sS -k -o NUL -w "%{http_code}" https://127.0.0.1:8043/     # 200  web server direct
curl.exe -sS -k -o NUL -w "%{http_code}" http://127.0.0.1:8040/      # 200  alternate listener
curl.exe -sS -k -o NUL -w "%{http_code}" --resolve help.example.com:443:127.0.0.1 https://help.example.com/
# ^ 200 means the router parsed the ClientHello and forwarded it
```

Relay through 443:

```powershell
# watch for a connection appearing on 8041 while a client uses 443
Get-NetTCPConnection -State Established -LocalPort 8041 |
  Select-Object RemoteAddress,RemotePort
```

If the TLS probe returns `000` / "failed to receive handshake", the cause is almost always one of:
no SNI in the URL, no certificate bound to 8043, or the web server still listening on plain HTTP.

Logs: Application event log (provider `ScreenConnect`), and the router writes its own trace lines
when run from a console — `ScreenConnect.Service.exe` started as `ScreenConnect Router` writes to
the standard service log.

---

## Cutover: moving the relay to 443

The router listens for relay traffic on 443 from the moment it starts, but clients are only told to
use it when `RelayAddressableUri` says so:

```xml
<add key="RelayAddressableUri" value="relay://help.example.com:443/" />
```

Do this **only** once public 443 actually reaches this host (NAT/port-forward moved off the front
proxy, or the proxy reconfigured to pass non-HTTP traffic through). Until then, clients would
follow the new address into the proxy and lose the relay.

Also update, if present, `WebServerAddressableUri` (it normally already holds the public
`https://` name).

## Optional: HTTP→HTTPS redirect on port 80

Only after freeing port 80 — for example `Stop-Service W3SVC; Set-Service W3SVC -StartupType Disabled`
if IIS serves nothing else — add:

```xml
<listenUri>tcp://+:80/</listenUri>
<rule schemeExpression="http" actionType="issueRedirect" actionData="https://$HOST/" />
```

`$HOST` is substituted with the request's Host header by `ResolveActionAsync`, so the redirect
keeps the requested name.

---

## Rollback

```powershell
.\setup-router-443.ps1 -Rollback
```

or by hand:

```powershell
Copy-Item 'C:\Program Files (x86)\ScreenConnect\web.config.router-<stamp>.bak' `
          'C:\Program Files (x86)\ScreenConnect\web.config' -Force
Stop-Service  'ScreenConnect Router'
Set-Service   'ScreenConnect Router' -StartupType Disabled
netsh http delete sslcert ipport=0.0.0.0:8043
Restart-Service 'ScreenConnect Web Server' -Force
```

The web server returns to `http://+:8040/`, the router stops, and nothing else is affected.

---

## Notes and caveats

* The Router is unsupported by ConnectWise. Feature changes to it are rare, but it is not covered
  by support.
* TLS terminates in the web server, not the router, so all the usual http.sys limits apply
  (SNI-based certificate selection, no HTTP/3).
* Binding a new certificate later means repeating the `netsh http add sslcert` step and restarting
  the web server; the router does not need to be touched.
* The router's `schemeExpression` values are regular expressions matched against the URI scheme
  that the handshake parser produces: `http`, `ssl`, `relay`.
* If you already patch `ScreenConnect.Server.dll` (see the main README), none of this interacts
  with the tamper check — `web.config` and the router service are not part of the signed manifest.
