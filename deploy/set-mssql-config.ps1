<#
    Point ScreenConnect's web.config at SQL Server.

    Two attributes have to change for each connection string. Changing only
    connectionString leaves providerName="SQLite", so ScreenConnect keeps using
    its embedded SQLite engine, treats the SQL Server data source as a file name
    and throws "SQLiteException: unable to open database file". Procmon shows it
    as CreateFile C:\WINDOWS\system32\<server>\<instance> -> PATH NOT FOUND.

    The four ScreenConnect services share one process and hold web.config open,
    so they are stopped, the file is written to a temporary name and then moved
    into place (a direct write can leave a zero-byte web.config), and the
    services are started again.

    Example:
      .\set-mssql-config.ps1 `
        -SessionConnectionString  "Data Source=.\SQLEXPRESS;Initial Catalog=ScreenConnect;Integrated Security=True;TrustServerCertificate=True" `
        -SecurityConnectionString "Data Source=.\SQLEXPRESS;Initial Catalog=ScreenConnect_Security;Integrated Security=True;TrustServerCertificate=True"

    Hybrid - SQL Server for sessions, the original SQLite file for security
    (this is the configuration that runs without patching ScreenConnect.Server.dll):

      .\set-mssql-config.ps1 `
        -SessionConnectionString "Data Source=.\SQLEXPRESS;Initial Catalog=ScreenConnect;Integrated Security=True;TrustServerCertificate=True" `
        -SecuritySqlite
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$SessionConnectionString,
    [string]$SecurityConnectionString,
    [switch]$SecuritySqlite,
    [string]$SqliteSecurityDataSource = '|DataDirectory|/Security.db',
    [string]$WebConfig = 'C:\Program Files (x86)\ScreenConnect\web.config',
    [string]$ProviderName = 'System.Data.SqlClient',
    [string]$SqliteProviderName = 'SQLite',
    [switch]$KeepServicesStopped,
    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'
$services = @(
    'ScreenConnect Session Manager',
    'ScreenConnect Security Manager',
    'ScreenConnect Relay',
    'ScreenConnect Web Server'
)

function Stop-ScreenConnect {
    foreach ($n in $services) { Stop-Service -Name $n -Force -ErrorAction SilentlyContinue }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt 90) {
        $running = @(Get-CimInstance Win32_Service |
            Where-Object { $_.PathName -like '*\Bin\ScreenConnect.Service.exe*' -and $_.State -eq 'Running' })
        if ($running.Count -eq 0) { return }
        Start-Sleep -Milliseconds 400
    }
    Get-Process -Name 'ScreenConnect.Service' -ErrorAction SilentlyContinue |
        ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 3
}

function Start-ScreenConnect {
    foreach ($n in $services) { Start-Service -Name $n -ErrorAction SilentlyContinue; Start-Sleep -Seconds 3 }
}

if (-not (Test-Path $WebConfig)) { throw "web.config not found: $WebConfig" }
if (-not $SecuritySqlite -and -not $SecurityConnectionString) {
    throw 'pass -SecurityConnectionString, or -SecuritySqlite to keep the security database on SQLite'
}
if ($SecuritySqlite) {
    # Same string ScreenConnect installs: ticks for datetime, WAL, foreign keys.
    $SecurityConnectionString = "Data Source=$SqliteSecurityDataSource; DateTimeKind=Utc; Foreign Keys=true; Page Size=4096; Journal Mode=WAL; BaseSchemaName=; Cache Size=1000; Memory Mapped Size=10000000000; DateTimeFormat=Ticks"
    $securityProvider = $SqliteProviderName
} else {
    $securityProvider = $ProviderName
}

$timestamp =Get-Date -Format 'yyyyMMdd-HHmmss'
$backup = "$WebConfig.bak-$timestamp"

Write-Host "[i] web.config: $WebConfig"
if (-not $WhatIf) {
    Stop-ScreenConnect
    Copy-Item $WebConfig $backup -Force
    Write-Host "[i] backup: $backup"
}

[xml]$xml = Get-Content $WebConfig -Raw
$changed = 0
foreach ($entry in $xml.configuration.connectionStrings.add) {
    $value = switch ($entry.name) {
        'SessionDatabase'  { $SessionConnectionString }
        'SecurityDatabase' { $SecurityConnectionString }
        default { $null }
    }
    if (-not $value) { continue }
    Write-Host ("[i] {0}" -f $entry.name)
    Write-Host ("      connectionString : {0}" -f $entry.connectionString)
    $targetProvider = if ($entry.name -eq 'SecurityDatabase') { $securityProvider } else { $ProviderName }
    Write-Host ("      providerName     : {0}  ->  {1}" -f $entry.providerName, $targetProvider)
    Write-Host ("      value            : {0}" -f $value)
    $entry.connectionString = $value
    $entry.providerName = $targetProvider
    $changed++
}

if ($changed -ne 2) {
    if (-not $WhatIf) { Copy-Item $backup $WebConfig -Force; Start-ScreenConnect }
    throw "expected 2 connection strings named SessionDatabase and SecurityDatabase, found $changed"
}

if ($WhatIf) {
    Write-Host '[i] -WhatIf: nothing written'
    exit 0
}

$settings = New-Object System.Xml.XmlWriterSettings
$settings.Indent = $true
$settings.Encoding = New-Object System.Text.UTF8Encoding($false)
$temp = "$WebConfig.new"
$writer = [System.Xml.XmlWriter]::Create($temp, $settings)
$xml.Save($writer)
$writer.Close()
Move-Item -Path $temp -Destination $WebConfig -Force

Write-Host "[i] wrote $WebConfig"
if (-not $KeepServicesStopped) {
    Start-ScreenConnect
    Start-Sleep -Seconds 20
    try {
        $r = Invoke-WebRequest 'http://127.0.0.1:8040/' -UseBasicParsing -TimeoutSec 30
        Write-Host ("[i] web / -> {0}" -f [int]$r.StatusCode)
    } catch {
        Write-Host ("[!] web / -> {0}" -f $_.Exception.Message)
    }
    Get-CimInstance Win32_Service |
        Where-Object { $_.PathName -like '*\Bin\ScreenConnect.Service.exe*' } |
        ForEach-Object { Write-Host ("[i] {0} = {1}" -f $_.Name, $_.State) }
}
Write-Host "[i] rollback: Copy-Item '$backup' '$WebConfig' -Force"
