# scpatch — ScreenConnect on SQL Server

ScreenConnect installs with both of its databases in SQLite. This repository moves them to
SQL Server, in whichever shape fits:

| Layout | Session database | Security database | Patch needed |
|---|---|---|---|
| **Hybrid — first choice** | SQL Server | SQLite | **No** |
| Full SQL Server | SQL Server | SQL Server | Yes |
| Reports on a SQL Server session database | SQL Server | either | Yes, for the report SQL |

The **hybrid** comes first below and is where most installations should stop: the session
database — the one that grows with events and connections — moves to SQL Server, while the
security database stays on the SQLite file the installer created. Nothing in the product is
modified, so a repair or upgrade cannot undo it.

The **patch** exists for the two things the hybrid cannot do:

- **Put the security database on SQL Server.** ScreenConnect generates SQL with bare column
  names, and `Key` is reserved in T-SQL, so `ServerCryptoManager..ctor()` can never load the
  application keys and the whole site answers HTTP 500:

  ```
  Msg 156, Level 15: Incorrect syntax near the keyword 'Key'.
  ```

- **Run ScreenConnect reports against a SQL Server session database.** The report engine
  emits SQL that SQLite accepts and T-SQL rejects (`Msg 164`, `Msg 8117`).

Both layouts share the same web.config and migration steps. Those come first, the patch last.

## Hybrid: SQL Server for sessions, SQLite for security (recommended)

This configuration needs **no patch at all**. The reserved-word problem (`Key` in
`SecurityConfiguration`) lives in the security database, so leaving security on the original
SQLite file avoids it, while the large database — sessions and events — moves to SQL Server.

```xml
<connectionStrings>
  <add name="SessionDatabase"
       connectionString="Data Source=.\SQLEXPRESS;Initial Catalog=ScreenConnect;User ID=CWControl;Password=CHANGE-ME;MultipleActiveResultSets=True;TrustServerCertificate=True"
       providerName="System.Data.SqlClient" />
  <add name="SecurityDatabase"
       connectionString="Data Source=|DataDirectory|/Security.db; DateTimeKind=Utc; Foreign Keys=true; Page Size=4096; Journal Mode=WAL; BaseSchemaName=; Cache Size=1000; Memory Mapped Size=10000000000; DateTimeFormat=Ticks"
       providerName="SQLite" />
</connectionStrings>
```

`deploy\set-mssql-config.ps1 -SecuritySqlite` writes exactly that, keeping the SQLite string
byte-for-byte as ScreenConnect installs it:

```powershell
.\set-mssql-config.ps1 -SecuritySqlite `
  -SessionConnectionString "Data Source=.\SQLEXPRESS;Initial Catalog=ScreenConnect;User ID=CWControl;Password=CHANGE-ME;MultipleActiveResultSets=True;TrustServerCertificate=True"
```

Copy `App_Data\Session.db` into SQL Server first with `sqlite2mssql.exe`; the security file
stays where it is and needs no migration.

Verified on a running install with the **stock, unpatched** `ScreenConnect.Server.dll`:
`/` 200, `/Login` 200, relay up across the full tamper-check interval with zero restarts,
21 agents connected, sessions updating.

Things to keep in mind:

- `App_Data\Security.db` is load-bearing again — it holds the DPAPI keys and the
  `SecurityConfiguration` rows. Back it up, and copy it if you move the installation.
- The SQL Server database used for security (`ScreenConnect_Security`) is no longer touched
  and can be dropped.
- After switching, the SQLite `Session.db` is ignored; keep it as a rollback.
- Reports that query the session database still hit the shapes described under
  "SQL Server report fixes" (`Msg 164`, `Msg 8117`), because those queries now run on SQL
  Server. If you depend on those reports, patch; for agent, relay and web operation you do
  not need to.

## Configuring web.config

Both connection strings need a matching `providerName`. `System.Data.SqlClient` is the
invariant name of the built-in SQL Server provider:

```xml
<connectionStrings>
  <add name="SessionDatabase"
       connectionString="Data Source=.\SQLEXPRESS;Initial Catalog=ScreenConnect;User ID=CWControl;Password=CHANGE-ME;MultipleActiveResultSets=True;TrustServerCertificate=True"
       providerName="System.Data.SqlClient" />
  <add name="SecurityDatabase"
       connectionString="Data Source=.\SQLEXPRESS;Initial Catalog=ScreenConnect_Security;User ID=CWControl;Password=CHANGE-ME;MultipleActiveResultSets=True;TrustServerCertificate=True"
       providerName="System.Data.SqlClient" />
</connectionStrings>
```

### The SQL Server login

Use SQL authentication rather than `Integrated Security=True`. The four services run as
`LocalSystem`, so integrated security authenticates as the machine account (`MACHINE$`), which
then needs a login of its own on the instance; a SQL login is independent of the service
identity and keeps working if the services later run under another account.

```powershell
# once, as a sysadmin - change the password in the script first
sqlcmd -S .\SQLEXPRESS -E -i .\create-sql-login.sql
```

`deploy\create-sql-login.sql` creates the login, maps it into `ScreenConnect` and
`ScreenConnect_Security`, and adds it to `db_owner` on both. It creates the databases too; if
you would rather let `sqlite2mssql.exe --create-database` do that, grant the login `dbcreator`
instead (the commented line in the script).

Connection-string options that matter:

- `MultipleActiveResultSets=True` — ScreenConnect's data layer issues concurrent commands on a
  single connection; this is part of the tested configuration.
- `TrustServerCertificate=True` — needed when the instance uses a self-signed certificate (the
  default) and the client negotiates encryption.
- The password sits in plain text in `web.config`; restrict that file's ACL to Administrators
  and the service account.

`deploy\set-mssql-config.ps1` performs that edit safely. The four ScreenConnect services share
one process and hold `web.config` open, so it stops them, writes a timestamped backup, writes
to a temporary file and moves it into place (a direct write can leave a zero-byte
`web.config`), restarts, and reports health:

```powershell
.\set-mssql-config.ps1 -WhatIf `
  -SessionConnectionString  "Data Source=.\SQLEXPRESS;Initial Catalog=ScreenConnect;User ID=CWControl;Password=CHANGE-ME;MultipleActiveResultSets=True;TrustServerCertificate=True" `
  -SecurityConnectionString "Data Source=.\SQLEXPRESS;Initial Catalog=ScreenConnect_Security;User ID=CWControl;Password=CHANGE-ME;MultipleActiveResultSets=True;TrustServerCertificate=True"
```

Drop `-WhatIf` to apply. `-KeepServicesStopped` leaves them down for a follow-up step.

## Migrating an existing SQLite database

`tools\SqliteToMssql\sqlite2mssql.exe` copies `App_Data\Session.db` and
`App_Data\Security.db` into SQL Server. It reads SQLite through the provider ScreenConnect
ships inside `ScreenConnect.Server.dll` (loaded by reflection), so there is nothing to
download and no `sqlite3.dll` to install — only an existing ScreenConnect installation.

```powershell
# stop the four services first
.\sqlite2mssql.exe --db "C:\Program Files (x86)\ScreenConnect\App_Data\Session.db" `
    --target "Data Source=.\SQLEXPRESS;Initial Catalog=ScreenConnect;User ID=CWControl;Password=CHANGE-ME;MultipleActiveResultSets=True;TrustServerCertificate=True" `
    --create-database

.\sqlite2mssql.exe --db "C:\Program Files (x86)\ScreenConnect\App_Data\Security.db" `
    --target "Data Source=.\SQLEXPRESS;Initial Catalog=ScreenConnect_Security;User ID=CWControl;Password=CHANGE-ME;MultipleActiveResultSets=True;TrustServerCertificate=True" `
    --create-database
```

It creates the database when missing, creates missing tables with SQL Server types
(`nvarchar(max)` for free text, `nvarchar(450)` / `nvarchar(200)` for primary-key columns
because SQL Server cannot index `nvarchar(max)`, `datetime2`, `varbinary(max)`), copies with
`SqlBulkCopy`, then adds the foreign keys with `ON DELETE CASCADE`, and finishes with a
row-count comparison per table:

```
table                    sqlite    mssql   ok
Session                       60       60   yes
SessionConnection           3219     3219   yes
SessionCustomProperty        480      480   yes
SessionEvent                6672     6672   yes
```

Other switches: `--truncate` clears existing rows first, `--dry-run` prints the plan without
changing anything, `--batch <n>` tunes the bulk-copy batch (default 2000), `--server-dll`
points at a ScreenConnect installation in a non-default location.

Notes:

- ScreenConnect stores datetimes as **.NET ticks** in SQLite; the tool converts them to
  `datetime2`. `0001-01-01` sentinels survive, which `datetime` (1753 minimum) would reject.
- The two `SecurityConfiguration` rows are DPAPI blobs bound to the machine. Migrate on the
  machine that will run the server, otherwise the encrypted settings cannot be read.
- `tools\SqliteToMssql\build.ps1` rebuilds the executable with the in-box .NET Framework
  compiler if you prefer to compile it yourself.

## Full SQL Server: putting the security database on SQL Server

This is where the patch is needed. With the security database on SQL Server, every statement
that touches `SecurityConfiguration.Key` is rejected (`Msg 156`), `ServerCryptoManager..ctor()`
cannot load the application keys, and the site answers HTTP 500. The tool fixes that at the
binary level and works on any ScreenConnect build with the same data layer.

Follow the same web.config and migration steps as above, then patch before starting:

1. `set-mssql-config.ps1` with both connection strings on `System.Data.SqlClient`.
2. `sqlite2mssql.exe` for `Session.db` **and** `Security.db`.
3. `scpatch hook` against the installed `ScreenConnect.Server.dll`, and copy the result plus
   `ScreenConnect.SqlFixer.dll` into `Bin` (see Deployment below).
4. Start the services and check `/` and `/Login`.

If you only need the report SQL rewritten and the security database can stay on SQLite, stay
with the hybrid and skip this section.


### How it works

Two pieces, both dropped into `Bin`:

1. **`ScreenConnect.Server.dll` (patched)** — one `call` is injected at the top of
   `ScreenConnect.DbExtensions.CreateCommand(DbConnection, string, object[])`. In this
   assembly that method contains the *only* `DbCommand.CommandText` assignment, so every
   SQL statement in the product passes through it.

2. **`ScreenConnect.SqlFixer.dll` (helper)** — takes that SQL text and brackets reserved
   identifiers (`Key`, `Value`) when — and only when — the connection is a `SqlConnection`.
   It skips string literals, `@parameters`, already-bracketed names, qualified names
   (`a.Key`), comments, and the `PRIMARY KEY` / `FOREIGN KEY` syntax.

Bracketing is done on the **text that is executed**, not in the schema definitions. That
detail matters: ScreenConnect compares `ColumnDefinition.ColumnName` against the live
database to decide whether to `ALTER TABLE`. If the definitions said `[Key]` while SQL
Server reports `Key`, it would try to add a duplicate column (`Msg 2705`). Keeping the
definitions plain keeps that comparison happy.

### Requirements

- .NET 8 runtime to run the tool (`dotnet scpatch.dll`)
- .NET Framework 4.x on the target (any ScreenConnect 26.x)
- Mono's `mcs` or the .NET Framework compiler to rebuild the fixer (a prebuilt signed
  `ScreenConnect.SqlFixer.dll` is included)

### Usage

```
dotnet scpatch.dll report <in.dll> <out.tsv>
dotnet scpatch.dll hook   <in.dll> <out.dll> --fixer <ScreenConnect.SqlFixer.dll>
dotnet scpatch.dll patch  <in.dll> <out.dll> [--verbose] [--extra-ident NAME]
dotnet scpatch.dll il     <dll> [type] [method] [count]
```

- `report` — decrypts the obfuscated string table and dumps every call site
  (`type`, `method`, `kind`, `index`, `key`, `plain`). Useful to confirm the build is
  recognisable before changing anything.
- `hook` — **the recommended mode.** Injects the `SqlFixer.Fix(connection, sql)` call.
- `patch` — legacy mode: rewrites the decrypted SQL strings themselves. Keeps schema
  definitions intact but cannot reach SQL that is assembled at runtime from column
  definitions (e.g. the `SELECT` column list), so `hook` is preferred.
- `il` — dumps the first N IL instructions of a method plus the module's assembly refs.
  Handy to verify the injection.

#### Patching a different ScreenConnect version

1. Get the version that is **actually installed** — the patched file must match it or the
   service fails with `FileLoadException`:

   ```powershell
   (Get-Item 'C:\Program Files (x86)\ScreenConnect\Bin\ScreenConnect.Server.dll').VersionInfo.FileVersion
   ```

2. Copy that DLL to a work folder.
3. `dotnet scpatch.dll report ScreenConnect.Server.dll sites.tsv` — you should see the
   `SecurityDatabase::.cctor` rows and ` WHERE Key = @A`; if the table resolves, the build
   uses the same obfuscator.
4. `dotnet scpatch.dll hook ScreenConnect.Server.dll out.dll --fixer ScreenConnect.SqlFixer.dll`
5. Deploy (below).

The tool locates the code by structure, not by offsets: it finds the `Lazy<string[]>[]`
string table, the decryptor call sites, and `CreateCommand` by signature. That is what makes
it portable across builds.

### Deployment

`deploy/install-patch.ps1` is a template — edit the two source paths at the top, then run it
elevated. In short:

```powershell
# stop all four (they share ONE process)
'ScreenConnect Web Server','ScreenConnect Session Manager',
'ScreenConnect Security Manager','ScreenConnect Relay' | Stop-Service -Force
Start-Sleep 8

Copy-Item .\ScreenConnect.Server.dll  'C:\Program Files (x86)\ScreenConnect\Bin\' -Force
Copy-Item .\ScreenConnect.SqlFixer.dll 'C:\Program Files (x86)\ScreenConnect\Bin\' -Force

# start again (one process serves all four roles)
'ScreenConnect Security Manager','ScreenConnect Session Manager',
'ScreenConnect Relay','ScreenConnect Web Server' | Start-Service
```

The patched assembly is loaded from `Bin` with the same identity (name, version, public key
token) as the original, so nothing else needs to change.

## SQL Server checklist

Two things outside the DLL are easy to miss, and both fail in ways that look unrelated.

**1. `providerName` must change with the connection string.**

Each connection string in `web.config` carries a `providerName` attribute. Rewriting only
`connectionString` leaves `providerName="SQLite"`, so ScreenConnect keeps using its embedded
SQLite engine and treats the SQL Server data source as a *file*. Procmon shows it plainly:

```
CreateFile  C:\WINDOWS\system32\127.0.0.1\CWControl   PATH NOT FOUND   (325x)
```

and the service log fills with `SQLiteException: unable to open database file`. Set both
entries to `providerName="System.Data.SqlClient"`.

**2. ScreenConnect checks its own file hashes, and the relay acts on the result.**

`ScreenConnect.ServerExtensions.HaveFilesBeenTamperedWith()` compares the installed files
against an encrypted manifest shipped with the product. A patched `ScreenConnect.Server.dll`
always mismatches. The web tier ignores that; the relay does not — it ends its socket run and
restart-loops with `Service run ended prematurely`, so the relay listens but no agent can
connect. The check runs on a timer, so the loop may not start until several minutes after
launch.

`scpatch hook` therefore also rewrites `HaveFilesBeenTamperedWith` to call
`ScreenConnect.SqlFixer.NotTampered()` (which returns `Task.FromResult(false)`). The relay
then stays up. Because the check is timer-driven, test for at least five minutes before
declaring a build good.

## Putting web and relay on port 443

If agents are blocked on outbound 8041, the built-in (and unsupported) Router service can put the
web server and the relay behind a single port 443, splitting connections by sniffing the TLS
ClientHello versus the ScreenConnect relay hello. It is a `web.config` section plus a service that
ships disabled — the usual reason it "doesn't work" is that the service was never re-enabled, or
that the web server was left on plain HTTP so the router forwards TLS at an HTTP listener.

Full walkthrough, including the certificate binding, the SNI caveat and the rollback:
[docs/screenconnect-router-443.md](docs/screenconnect-router-443.md), with a tested script at
[deploy/setup-router-443.ps1](deploy/setup-router-443.ps1).

## Notes and caveats

- **Strong name.** `ScreenConnect.Server.dll` is strong-named; rewriting it invalidates the
  signature. .NET Framework skips strong-name *verification* for full-trust assemblies
  loaded from the application directory, so it loads — that is what makes this work.
  Keep the original as `ScreenConnect.Server.dll.orig` for rollback.
- **The helper must be strong-named.** A strong-named assembly cannot reference an unsigned
  one here; without a key the CLR fails with
  `Could not load ... ScreenConnect.SqlFixer ... A strongly-named assembly is required`
  (`0x80131044`) and the service crashes at startup. `fixer.snk` is included; rebuild with
  `mcs -target:library -sdk:4.5 -keyfile:fixer.snk -out:ScreenConnect.SqlFixer.dll Fixer.cs`.
  Changing the key changes the public key token, which then requires re-running `hook`.
- **Re-run after every upgrade.** A ScreenConnect update replaces both the DLL and its
  version; re-patch the new file.
- **Schema.** The `SecurityConfiguration` table must already exist on SQL Server with a
  usable primary key (`[Key] nvarchar(450) NOT NULL PRIMARY KEY`), because ScreenConnect's
  own DDL uses `nvarchar(MAX)` as a key, which SQL Server rejects (`Msg 1919`).
- **Keys.** `ApplicationDecryptionKey` / `ApplicationValidationKey` in `SecurityConfiguration`
  are DPAPI blobs bound to the machine. When moving from SQLite, copy the two rows across as
  they are; if they are regenerated, previously encrypted configuration can no longer be read.
- **Diagnostics.** The fixer logs every rewrite to `C:\Temp\scsqlfix.log`, but only when the
  marker file `C:\Temp\scsqlfix.enable` exists. Delete the marker to go quiet.

## What the tamper check actually does

It does **not** report anything to ConnectWise. The reaction is entirely local:

1. `HaveFilesBeenTamperedWith()` hashes `ScreenConnect.*.dll` against the encrypted manifest
   shipped with the install and caches the answer in an `AsyncLazy<bool>`. The only strings in
   that method are `ScreenConnect.*.dll` and `-`.
2. `SessionManager.RunTamperingCheckAsync` runs on a timer (`ServerConstants.CheckTamperingInterval`,
   a few minutes). When the check trips it calls `LicenseManager.RemoveLicense` for every loaded
   license, and raises a `SessionManagerFaultType.TamperingCheck` service fault. `RemoveLicense`
   goes to `AlterLicensesAndSave` → `LoadLicenseEnvelopes` / `GetLicenseFilePath` / `SerializeXml`
   — the local license file, no network.
3. The check gates four local entry points: the relay accept loop, the generic socket server
   accept loop, the installer handler and the launch handler. They all consult the same cached
   boolean and decline to serve.

The single hard-coded external host in the server assembly is
`https://api.labtechsoftware.com/v1/licensing/screenconnect/validate` (LabTech/Automate
licensing). The other outbound code is licensing, cloud configuration, extension hosting and
activity tracing. The license telemetry that does leave the machine carries
`LicenseIDHash`, `LicenseIDHashSignature`, `EncryptedFingerprint`,
`SignatureCertificateChainPemString` and `SignatureHashAlgorithmName` — license identity, never
file hashes and never a tampered flag. The revocation check sends `InstanceCode`,
`LicenseIDHash` and `ClientSecret`.

So the practical consequence of a patched DLL is that the server **deletes its own licence** and
records a fault, which is exactly what `scpatch hook` neutralises. The only indirect signal is
that a licence the server removed locally stops validating — a consequence of the local
removal, not a report about modified files. In the hybrid layout above nothing fires at all,
because the DLL is stock.

## Verified on

- ScreenConnect 26.6.6.9747, Windows Server 2025
- SQL Server 2019 Express, security database `ScreenConnect_Security`
- `/` 200, `/Login` 200, `/Administration` 302, keys loaded from SQL Server

## SQL Server report fixes

ScreenConnect's built-in report engine generates SQL that SQLite accepts and SQL Server
rejects. Two shapes are rewritten by the fixer (`fixer/SqlRewrite.cs`), and only when the
exact pattern is present, so ordinary queries pass through untouched.

**1. Aggregate/subquery expressions in `GROUP BY`** (`Msg 164`)

The engine reuses the select expressions as group keys. For computed report columns such as
`SessionName` or `ConnectedTime` those expressions are correlated scalar subqueries:

```sql
GROUP BY ParticipantName,
         (SELECT Name FROM Session WHERE SessionID = SessionConnection.SessionID),
         (SELECT MIN(Time) FROM SessionEvent WHERE ... EventType = 10)
```

The rewrite hoists the select list into a derived table and groups on its plain columns:

```sql
SELECT ParticipantName, SessionName, ConnectedTime, SUM(__scagg3) AS TotalDurationSeconds
FROM (SELECT ParticipantName AS ParticipantName,
             (SELECT Name FROM Session ...) AS SessionName,
             (SELECT MIN(Time) FROM SessionEvent ... EventType = 10) AS ConnectedTime,
             DATEDIFF_BIG(microsecond, ...) / 1000000 AS __scagg3
      FROM SessionConnection WHERE ProcessType=1) AS __sc
GROUP BY ParticipantName, SessionName, ConnectedTime
ORDER BY ParticipantName, SessionName, ConnectedTime
```

**2. Duration arithmetic written for SQLite ticks** (`Msg 8117`)

`TotalDurationSeconds` is emitted as `(MAX(Time) - MIN(Time)) / 10000000`. That only works
when `Time` is stored as .NET ticks, which is what SQLite does. On SQL Server the column is
`datetime2` and `DateTimeConverter` calls `dataRecord.GetDateTime()`, so the column type is
correct and the *arithmetic* is wrong. It is rewritten to
`DATEDIFF_BIG(microsecond, MIN, MAX) / 1000000`, which reproduces the tick division exactly
(verified against live data: 26789 s for a session whose stored duration is 26789 s).

Both rewrites are logged to `C:\Temp\scsqlfix.log` when the marker file
`C:\Temp\scsqlfix.enable` exists. A rewrite that does not parse cleanly is abandoned and the
original SQL is returned unchanged.
