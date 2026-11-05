# Installs the patched ScreenConnect.Server.dll plus the strong-named SQL fixer.
# Run elevated. Edit the two paths if your files are elsewhere.

param(
    [string]$PatchedServer = '.\ScreenConnect.Server.patched.dll',
    [string]$Fixer         = '.\ScreenConnect.SqlFixer.dll',
    [string]$Bin           = 'C:\Program Files (x86)\ScreenConnect\Bin',
    [string]$Url           = 'http://localhost:8040/'
)

$ErrorActionPreference = 'Stop'
$svcs = @('ScreenConnect Web Server','ScreenConnect Session Manager',
          'ScreenConnect Security Manager','ScreenConnect Relay')
$order = @('ScreenConnect Security Manager','ScreenConnect Session Manager',
           'ScreenConnect Relay','ScreenConnect Web Server')

foreach ($p in @($PatchedServer, $Fixer)) {
    if (-not (Test-Path $p)) { throw "missing file: $p" }
}

# The patched assembly must match the build that is actually installed,
# otherwise the service dies with an assembly version mismatch.
$installed = (Get-Item (Join-Path $Bin 'ScreenConnect.Server.dll')).VersionInfo.FileVersion
$patched   = (Get-Item $PatchedServer).VersionInfo.FileVersion
Write-Output "installed: $installed   patched: $patched"
if ($installed -ne $patched) {
    throw "version mismatch - patch the DLL from THIS installation ($installed), not $patched"
}

foreach ($s in $svcs) { try { Stop-Service $s -Force -ErrorAction Stop } catch {} }
Start-Sleep -Seconds 8
if (Get-Process ScreenConnect.Service -ErrorAction SilentlyContinue) {
    throw 'ScreenConnect.Service.exe is still running - cannot replace the assembly'
}

$dll = Join-Path $Bin 'ScreenConnect.Server.dll'
if (-not (Test-Path "$dll.orig")) { Copy-Item $dll "$dll.orig" -Force; Write-Output 'backup: ScreenConnect.Server.dll.orig' }
Copy-Item $PatchedServer $dll -Force
Copy-Item $Fixer (Join-Path $Bin 'ScreenConnect.SqlFixer.dll') -Force
Write-Output 'installed patched assembly + fixer'

foreach ($s in $order) { try { Start-Service $s -ErrorAction Stop } catch { Write-Output "start failed: $s" }; Start-Sleep -Seconds 2 }
Start-Sleep -Seconds 12

Get-CimInstance Win32_Service -Filter "Name like 'ScreenConnect%'" |
    Select-Object Name, State, ProcessId | Format-Table -Auto | Out-String | Write-Output

try { $code = (Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 60).StatusCode }
catch { $code = $_.Exception.Message }
Write-Output "site: $code"

Write-Output ''
Write-Output 'Rollback: stop all four services, restore ScreenConnect.Server.dll.orig,'
Write-Output 'delete ScreenConnect.SqlFixer.dll from Bin, start the services again.'
