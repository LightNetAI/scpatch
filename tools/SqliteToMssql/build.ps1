# Rebuild sqlite2mssql.exe with the .NET Framework compiler that ships with Windows.
$ErrorActionPreference = 'Stop'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw 'csc.exe not found - .NET Framework 4.x is required' }
Push-Location $PSScriptRoot
try {
    & $csc /nologo /optimize+ /target:exe /r:System.Data.dll /out:sqlite2mssql.exe SqliteToMssql.cs
    if ($LASTEXITCODE -ne 0) { throw "csc failed with $LASTEXITCODE" }
    Write-Host '[i] built sqlite2mssql.exe'
} finally { Pop-Location }
